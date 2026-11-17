using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class EudamedService
{
    public async Task<EudamedDossierView> DossierView(string id, Actor actor, CancellationToken ct = default)
    {
        var dossier = await GetDossier(id, actor, ct);
        var ledger = await GetLedger(dossier.Package, ct);
        return new(dossier, ledger with
        {
            Entries = ledger.Entries.Where(e => e.DossierId == id).ToArray(),
            CorrespondenceAlerts = ledger.CorrespondenceAlerts?.Where(a => a.DossierId == id &&
                a.Products.All(actor.Products.Contains)).ToArray()
        });
    }

    public async Task<EudamedDossierView> QueueDossier(string id, SubmitEudamedDossierCommand input,
        Actor actor, string key, CancellationToken ct = default, bool requirePreview = false)
    {
        actor.RequireHuman(Roles.Regulatory);
        var dossier = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Begruendete Einreichung des eingefrorenen Pakets", 80, 4000);
        Guard.Against(!EudamedContracts.SimulatorScenarios.Contains(input.Scenario, StringComparer.Ordinal),
            "eudamed_scenario", "Ein bekanntes, ausdrueckliches Simulatorszenario erforderlich.");
        var context = Context(LedgerId(dossier.Package), "eudamedDelivery", dossier.ProductId,
            input.ExpectedLedgerVersion, key, "EudamedDeliveryQueued", input.Reason, actor, new { id, input });
        if (await store.Receipt<EudamedDeliveryLedger>(context, ct) is not null)
        {
            await ProjectDelivery(context.Id, ct);
            return await DossierView(id, actor, ct);
        }
        Guard.Against(dossier.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
        EudamedRules.RequireFrozen(dossier);
        RequireLineage(dossier.Package);
        RequireProjection(dossier.Package);
        Guard.Against(!dossier.Package.Binding.Simulated, "eudamed_external_not_activated",
            "Reale Service-/Onboardingfreigabe fehlt. Kein externer Versand und kein Simulator-Fallback.", 409);
        Guard.Against(input.Scenario == "PeriodMismatch" && dossier.Kind != EudamedDossierKind.PSRP,
            "eudamed_scenario_kind", "Periodenabweichung gehoert zum PSRP-Rueckmeldeabgleich.");
        var channel = await CurrentChannel(dossier.Package.Binding, dossier.ProductId, ct);
        var ledger = await GetLedger(dossier.Package, ct);
        Guard.Against(ledger.Version != input.ExpectedLedgerVersion, "version_conflict", "Versandreservierung wurde geaendert.", 409);
        if (requirePreview || input.Confirmation is not null)
            DispatchPreviews.RequireConfirmation(input.Confirmation, DispatchPreviews.DossierBinding(dossier, ledger.Version));
        RequireNoPending(ledger);
        if (dossier.Package.Content.IntendedAction == "Reply") RequireOpenReplyTarget(dossier.Package, ledger);
        Guard.Against(ledger.Entries.Any(e => e.Attempt.PackageHash == dossier.ScopeHash &&
            e.BusinessStatus is "BusinessAccepted" or "DraftCreated" or "BusinessRejected"), "already_submitted",
            "Diese Fassung hat bereits eine fachliche Antwort. Neue begruendete Fassung und Freigaben statt erneutem POST.", 409);
        EudamedCaseObligation? obligation = null;
        if (dossier.Kind == EudamedDossierKind.MIR && dossier.Package.Content.IntendedAction != "Reply")
        {
            var source = EudamedRules.Source<SafetyCase>(dossier.Package.Sources, "case");
            ReportRules.RequireSendable(source, actor);
            var report = source.CurrentReport!;
            Guard.Against(ledger.Entries.Any(e => e.BusinessStatus == "BusinessAccepted" &&
                e.CaseObligation?.ReportScopeHash == report.ScopeHash), "already_submitted",
                "Diese native Berichtsfassung wurde bereits fachlich angenommen. Keine zweite Einreichung.", 409);
            obligation = new(source.Id, report.ScopeHash, report.Version, report.Jurisdiction,
                ReportRules.CoveredObligations(source, report), FollowUpRules.Covered(source, report));
        }
        Guard.Against(await objects.Put(Canonical.Serialize(dossier), ct) != Canonical.Hash(dossier),
            "eudamed_snapshot_hash", "Freigegebener Versandstand konnte nicht originalgetreu gespeichert werden.", 409);
        var attemptId = "eu-" + Canonical.Hash(new { ActorId = actor.Id, key, DossierId = id })[..32];
        var attempt = new EudamedAttempt(attemptId, "EU-" + Canonical.Hash(new { id, attemptId })[..32],
            attemptId + "@cohors.invalid", dossier.Revision, dossier.ScopeHash, dossier.Package.Binding,
            input.Scenario, EudamedContracts.Simulator, "Queued", ledger.Version + 1,
            clock.GetUtcNow(), null, null, null, null);
        var entry = new EudamedDeliveryEntry(id, EudamedContracts.Bind(dossier), attempt, "Pending", 0)
        { IntendedAction = dossier.Package.Content.IntendedAction, CaseObligation = obligation };
        var dependencies = PackageDependencies(dossier.Package, channel);
        dependencies.Add(new("eudamedDossier", EudamedContracts.Bind(dossier)));
        await store.Execute<EudamedDeliveryLedger>(context with { Dependencies = dependencies.ToArray() }, current =>
            current! with { Version = current.Version + 1, Entries = [.. current.Entries, entry] }, ct);
        await ProjectDelivery(ledger.Id, ct);
        return await DossierView(id, actor, ct);
    }

    public async Task ProcessDelivery(string ledgerId, string attemptId, CancellationToken ct = default)
    {
        var ledger = await Delivery(ledgerId, ct);
        var entry = Entry(ledger, attemptId);
        var dossier = await ApprovedDossier(entry, ct);
        var worker = DossierWorker(dossier);
        Guard.Against(!entry.Attempt.Binding.Simulated, "eudamed_external_not_activated",
            "Dieser Worker fuehrt keine ungepruefte externe Serviceaktion aus.", 409);
        if (entry.Attempt.Status == "Queued")
        {
            EudamedChannel? channel = null;
            string? blocked = null;
            try { channel = await CurrentChannel(entry.Attempt.Binding, ledger.ProductId, ct); }
            catch (DomainException error) when (error.Code is "eudamed_channel_fenced" or "eudamed_not_ready" or "eudamed_channel_missing")
            { blocked = error.Code; }
            var dependencies = channel is null ? [] : PackageDependencies(dossier.Package, channel).ToArray();
            var command = Context(ledger.Id, "eudamedDelivery", ledger.ProductId, ledger.Version,
                $"eu-claim:{attemptId}:{ledger.Version}", "EudamedDeliveryClaimed",
                "Neue Claims sind generationsgebunden. Ein deaktivierter Weg leitet keine alten Pakete um.",
                worker, new { attemptId, blocked });
            try
            {
                ledger = await store.Execute<EudamedDeliveryLedger>(command with { Dependencies = dependencies }, current =>
                    Replace(current!, entry with { Attempt = entry.Attempt with {
                        Status = blocked is null ? "Started" : "Fenced", StartedAt = blocked is null ? clock.GetUtcNow() : null,
                        FinishedAt = blocked is null ? null : clock.GetUtcNow(), ErrorCode = blocked } }), ct);
            }
            catch (DomainException error) when (error.Code == "dependency_version")
            {
                ledger = await store.Execute<EudamedDeliveryLedger>(command with { Action = "EudamedDeliveryFenced",
                    Request = new { attemptId, blocked = "eudamed_source_changed" } }, current =>
                    Replace(current!, entry with { Attempt = entry.Attempt with { Status = "Fenced",
                        FinishedAt = clock.GetUtcNow(), ErrorCode = "eudamed_source_changed" } }), ct);
            }
            entry = Entry(ledger, attemptId);
        }
        if (entry.Attempt.Status == "Started")
        {
            var status = entry.Attempt.Scenario switch
            { "TechnicalFailure" => "TransportFailed", "Unknown" => "TransmissionUnknown", _ => "TransportDelivered" };
            var error = status switch { "TransportFailed" => "simulated_transport_failure", "TransmissionUnknown" => "simulated_delivery_unknown", _ => null };
            var original = Canonical.Serialize(new { Contract = EudamedContracts.Simulator, Simulated = true,
                entry.Attempt.Id, entry.Attempt.MessageId, entry.Attempt.CorrelationId, entry.Attempt.PackageHash,
                entry.Attempt.Binding, entry.Attempt.StartedAt, Status = status, ErrorCode = error,
                Boundary = "Synthetic technical journal only; no EUDAMED business acceptance or CA/NB decision." });
            var receipt = await objects.Put(original, ct);
            var completed = entry with { Attempt = entry.Attempt with { Status = status,
                FinishedAt = entry.Attempt.StartedAt, ReceiptHash = receipt, ErrorCode = error },
                BusinessStatus = status == "TransportFailed" ? "NotSent" : "Pending",
                PlannedResponses = status == "TransportFailed" ? [] : SimulationPlan(dossier, entry) };
            ledger = await store.Execute<EudamedDeliveryLedger>(Context(ledger.Id, "eudamedDelivery", ledger.ProductId,
                ledger.Version, $"eu-transport:{attemptId}:{ledger.Version}", "EudamedTransportResultRecorded",
                "Persistenter lokaler Transportbeleg; fachliche Rueckmeldungen werden getrennt eingelesen.",
                worker, new { attemptId, completed }), current => Replace(current!, completed), ct);
        }
        await ProjectDelivery(ledger.Id, ct);
    }

    public async Task<EudamedDossierView> AdvanceSimulator(string id, AdvanceEudamedSimulatorCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var dossier = await GetDossier(id, actor, ct);
        Guard.Text(input.Reason, "Begruendung des ausdruecklichen Simulatorereignisses", 30, 4000);
        var ledger = await GetLedger(dossier.Package, ct);
        var entry = ledger.Entries.SingleOrDefault(e => e.DossierId == id && e.PlannedResponses.Any(p => p.Id == input.EventId))
            ?? throw new DomainException("eudamed_event_missing", "Persistentes Simulatorereignis fehlt.", 404);
        var planned = entry.PlannedResponses.Single(p => p.Id == input.EventId);
        Guard.Against(!entry.Attempt.Binding.Simulated || planned.DueAt > clock.GetUtcNow(),
            "eudamed_event_not_due", "Nur ein faelliges Ereignis des urspruenglichen Simulatorprofils darf ausgeloest werden.", 409);
        var original = Canonical.Read<EudamedInboxOriginal>(planned.Content);
        var inboxId = InboxId(entry.Attempt.Binding, original.MessageId);
        var context = Context(inboxId, "eudamedInbox", dossier.ProductId, 0, "eu-inbox:" + original.MessageId,
            "EudamedInboxStored", "Synthetisches Original dauerhaft vor fachlicher Verarbeitung gespeichert.",
            DossierWorker(dossier), new { entry.Attempt.Binding, original });
        var inbox = await store.Receipt<EudamedInbox>(context, ct);
        if (inbox is null)
        {
            Guard.Against(dossier.Version != input.ExpectedVersion, "version_conflict", "Dossier wurde geaendert.", 409);
            var originalHash = await objects.Put(planned.Content, ct);
            inbox = await store.Execute<EudamedInbox>(context, _ =>
                new(inboxId, 1, dossier.ProductId, entry.Attempt.Binding, original, originalHash, null,
                    original.MessageId, "Stored", "NotApplicable", null, clock.GetUtcNow(), null), ct);
        }
        else if (input.Duplicate)
        {
            var current = await store.Get<EudamedInbox>("eudamedInbox", inboxId, ct)
                ?? throw new DomainException("eudamed_inbox_missing", "Persistentes Original fehlt.", 409);
            var duplicateKey = "eu-duplicate-" + Canonical.Hash(new { actor.Id, key, inboxId })[..32];
            // The producer key, not a mutable inbox revision, identifies one requested duplicate.
            if (!(await store.Audit(inboxId, ct)).Any(a => a.Action == "EudamedDuplicateObserved" && a.CorrelationId == duplicateKey))
                await store.Execute<EudamedInbox>(Context(inboxId, "eudamedInbox", dossier.ProductId, current.Version,
                    duplicateKey, "EudamedDuplicateObserved", input.Reason, actor, new { input.EventId }),
                    value => value! with { Version = value.Version + 1, DuplicateReceipts = value.DuplicateReceipts + 1 }, ct);
        }
        await ApplyInbox(inbox.Id, ct);
        return await DossierView(id, actor, ct);
    }

    public async Task ApplyInbox(string inboxId, CancellationToken ct = default)
    {
        var inbox = await store.Get<EudamedInbox>("eudamedInbox", inboxId, ct)
            ?? throw new DomainException("eudamed_inbox_missing", "Gespeichertes Original fehlt.", 404);
        var original = inbox.Original ?? throw new DomainException("eudamed_inbox_quarantined", "Technischer Eingang braucht eine kontrollierte Korrelation.", 409);
        Guard.Against(Canonical.HashText(await objects.Read(inbox.OriginalHash, ct)) != inbox.OriginalHash ||
            Canonical.Hash(original) != inbox.OriginalHash, "eudamed_inbox_integrity", "Originalbindung wurde veraendert.", 409);
        var currentDossier = await store.Get<EudamedDossier>("eudamedDossier", original.DossierId, ct)
            ?? throw new DomainException("eudamed_dossier_missing", "Adressiertes Dossier fehlt.", 409);
        var ledger = await GetLedger(currentDossier.Package, ct);
        var entry = Entry(ledger, original.AttemptId);
        var dossier = await ApprovedDossier(entry, ct);
        Guard.Against(original.CorrelationId != entry.Attempt.CorrelationId || original.DossierId != entry.DossierId ||
            original.DossierRevision != entry.Attempt.DossierRevision || original.PackageHash != entry.Attempt.PackageHash ||
            original.Simulated != entry.Attempt.Binding.Simulated || Canonical.Hash(inbox.Binding) != Canonical.Hash(entry.Attempt.Binding) ||
            inbox.MessageId != original.MessageId || original.Sequence < 1,
            "eudamed_response_correlation", "Rueckmeldung passt nicht zum urspruenglichen Kanal, Auftrag und Paket.", 409);
        if (!entry.Responses.Any(r => r.InboxId == inboxId) && !entry.Correspondence.Any(c => c.InboxId == inboxId))
        {
            EudamedDeliveryEntry next;
            if (original.Kind == "BusinessResponse")
            {
                Guard.Against(original.Status is not ("DraftCreated" or "BusinessAccepted" or "BusinessRejected"),
                    "eudamed_business_state", "Nicht unterstuetzter fachlicher Rueckmeldestatus; kein stiller Erfolg.", 409);
                var actionMismatch = entry.IntendedAction == "DraftOnly" && original.Status == "BusinessAccepted";
                var applied = !actionMismatch && entry.BusinessStatus != "ActionMismatch" &&
                    original.Sequence > entry.LastBusinessSequence;
                var status = actionMismatch ? "ActionMismatch" : original.Status;
                if (dossier.Kind == EudamedDossierKind.PSRP && status == "BusinessAccepted" &&
                    (original.ReturnedPeriod?.Start != dossier.Package.Content.Period!.Start ||
                     original.ReturnedPeriod?.End != dossier.Package.Content.Period.End))
                    status = "PeriodMismatch";
                var response = new EudamedBusinessResponse(inboxId, original.MessageId, original.AttemptId,
                    inbox.OriginalHash, original.Sequence, status, original.RemoteDossierCode, original.RemoteDossierVersion,
                    actionMismatch ? [.. original.Errors,
                        "COHORS_INTENDED_ACTION_MISMATCH: Annahme widerspricht dem freigegebenen reinen Entwurfsauftrag; keine Pflichtquittierung."]
                        : original.Errors, original.ReturnedPeriod, inbox.ReceivedAt, original.Simulated, applied);
                next = entry with { Responses = [.. entry.Responses, response],
                    LastBusinessSequence = applied || actionMismatch ? Math.Max(original.Sequence, entry.LastBusinessSequence) : entry.LastBusinessSequence,
                    BusinessStatus = actionMismatch ? "ActionMismatch" : applied ? status : entry.BusinessStatus,
                    Attempt = applied && entry.Attempt.Status == "TransmissionUnknown"
                        ? entry.Attempt with { Status = "TransportDelivered", ErrorCode = null } : entry.Attempt };
            }
            else
            {
                Guard.Against(original.Kind is not ("Comment" or "InformationRequest" or "Evaluation"),
                    "eudamed_correspondence_kind", "Nicht unterstuetzte Korrespondenzart.", 409);
                next = entry with { Correspondence = [.. entry.Correspondence,
                    new("eu-correspondence-" + Canonical.HashText(inboxId)[..32], inboxId, inbox.OriginalHash,
                        original.Issuer, original.Kind, original.Content, original.DossierRevision, inbox.ReceivedAt,
                        original.DueAt, Roles.Regulatory, "Open", null, original.Simulated)] };
            }
            next = next with { PlannedResponses = next.PlannedResponses.Select(p => p.Id == original.MessageId
                ? p with { Status = "Published", PublishedInboxId = inboxId } : p).ToArray() };
            ledger = await store.Execute<EudamedDeliveryLedger>(Context(ledger.Id, "eudamedDelivery", ledger.ProductId,
                ledger.Version, $"eu-response:{Canonical.HashText(inboxId)[..32]}:{ledger.Version}", "EudamedBusinessResponseApplied",
                "Originalgebundene fachliche Antwort; alte Sequenzen bleiben sichtbar und setzen keinen neueren Stand zurueck.",
                DossierWorker(dossier), new { inboxId, inbox.OriginalHash }) with { Dependencies =
                    [new("eudamedInbox", new(inbox.Id, inbox.Version, Canonical.Hash(inbox)))] },
                value =>
                {
                    var updated = Replace(value!, next);
                    return next.IntendedAction == "Reply" && next.Responses.Any(r =>
                        r.InboxId == inboxId && r.Applied && r.Status == "BusinessAccepted")
                        ? CompleteReply(updated, dossier) : updated;
                }, ct);
        }
        await ProjectDelivery(ledger.Id, ct);
        inbox = (await store.Get<EudamedInbox>("eudamedInbox", inboxId, ct))!;
        if (inbox.Status != "Applied")
            await store.Execute<EudamedInbox>(Context(inbox.Id, "eudamedInbox", inbox.ProductId, inbox.Version,
                $"eu-inbox-applied:{Canonical.HashText(inboxId)[..32]}:{inbox.Version}", "EudamedInboxApplied",
                "Geschaeftsstand und zugeordnete Projektionen sind dauerhaft gespeichert.",
                DossierWorker(dossier), new { inboxId, inbox.OriginalHash }),
                value => value! with { Version = value.Version + 1, Status = "Applied" }, ct);
    }

    public async Task ProjectDelivery(string ledgerId, CancellationToken ct = default)
    {
        var ledger = await Delivery(ledgerId, ct);
        foreach (var group in ledger.Entries.GroupBy(e => e.DossierId))
        {
            var dossier = await store.Get<EudamedDossier>("eudamedDossier", group.Key, ct)
                ?? throw new DomainException("eudamed_dossier_missing", "Gebundenes Projektionsziel fehlt.", 409);
            var projected = dossier with { Attempts = group.Select(e => e.Attempt).ToArray(),
                PlannedResponses = group.SelectMany(e => e.PlannedResponses).ToArray(),
                BusinessResponses = group.SelectMany(e => e.Responses).ToArray(),
                Correspondence = group.SelectMany(e => e.Correspondence).ToArray() };
            if (Canonical.Hash(projected) != Canonical.Hash(dossier))
                await store.Execute<EudamedDossier>(Context(dossier.Id, "eudamedDossier", dossier.ProductId, dossier.Version,
                    $"eu-project:{Canonical.HashText(ledger.Id)[..16]}:{ledger.Version}:{dossier.Version}",
                    "EudamedDeliveryProjected", "Versand-/Rueckmeldestand projiziert; freigegebene Paketbytes und Unterschriften unveraendert.",
                    DossierWorker(dossier), new { ledger.Id, ledger.Version, Dossier = dossier.Id }) with { Dependencies = [LedgerDependency(ledger)] },
                    _ => projected with { Version = dossier.Version + 1 }, ct);
            foreach (var entry in group.Where(e => e.BusinessStatus == "BusinessAccepted" && e.CaseObligation is not null &&
                e.IntendedAction is "Submit" or "FollowUp"))
                await CompleteCaseReceipt(ledger, entry, ct);
        }
    }

    private async Task CompleteCaseReceipt(EudamedDeliveryLedger ledger, EudamedDeliveryEntry entry, CancellationToken ct)
    {
        Guard.Against(entry.IntendedAction is not ("Submit" or "FollowUp"),
            "eudamed_receipt_action", "Nur eine ausdruecklich freigegebene Einreichung darf native Meldepflichten quittieren.", 409);
        var receipt = entry.Responses.Where(r => r.Applied && r.Status == "BusinessAccepted").MaxBy(r => r.Sequence)
            ?? throw new DomainException("eudamed_receipt_missing", "Fachlicher Originalbeleg fehlt.", 409);
        var obligation = entry.CaseObligation!;
        var value = await store.Get<SafetyCase>("case", obligation.CaseId, ct)
            ?? throw new DomainException("case_missing", "Gebundener Ursprungsfall fehlt.", 409);
        if (value.Submissions.Any(s => s.Id == entry.Attempt.Id)) return;
        Guard.Against(!entry.Attempt.Binding.Simulated || !value.Synthetic || value.ProductId != ledger.ProductId,
            "eudamed_receipt_scope", "Nur synthetisch gebundene Meldepflichten duerfen hier quittiert werden.", 409);
        var source = await ApprovedDossier(entry, ct);
        var original = EudamedRules.Source<SafetyCase>(source.Package.Sources, "case");
        var report = value.Reports.SingleOrDefault(r => r.Version == obligation.ReportVersion && r.ScopeHash == obligation.ReportScopeHash)
            ?? throw new DomainException("eudamed_receipt_report", "Die urspruenglich signierte Berichtsfassung fehlt.", 409);
        Guard.Against(report.Jurisdiction != obligation.Jurisdiction ||
            !ReportRules.CoveredObligations(value, report).SequenceEqual(obligation.ObligationIds) ||
            !FollowUpRules.Covered(value, report).SequenceEqual(obligation.FollowUpIds) ||
            !original.Approvals.Where(a => a.ScopeHash == report.ScopeHash).All(value.Approvals.Contains),
            "eudamed_receipt_obligation", "Originalpflichten und native Freigaben muessen erhalten sein; EUDAMED-Signaturen ersetzen sie nicht.", 409);
        var reference = "DEMO-EUDAMED-" + receipt.MessageId;
        await store.Execute<SafetyCase>(Context(value.Id, "case", value.ProductId, value.Version,
            $"eu-case-receipt:{entry.Attempt.Id}:{value.Version}", "EudamedSimulatedObligationReceipted",
            "Nur die originalgebundenen Pflichten dieser Sprache/Jurisdiktion werden mit separatem fachlichem Simulationsbeleg quittiert; keine Fristneuberechnung.",
            Worker(value.ProductId, true), new { entry.Attempt.Id, receipt.InboxId, receipt.OriginalHash }) with
            { Dependencies = [LedgerDependency(ledger)] }, current => current! with
            {
                Version = current.Version + 1,
                Submissions = [.. current.Submissions, new(entry.Attempt.Id, report.ScopeHash, report.Version,
                    entry.Attempt.CorrelationId, "SimulatedAccepted", entry.Attempt.Scenario, entry.Attempt.QueuedAt,
                    receipt.At, reference, true, 1, "EudamedBusinessAccepted")],
                Reports = current.Reports.Select(r => r.ScopeHash == report.ScopeHash && r.Version == report.Version
                    ? r with { State = ReportState.SimulatedAccepted } : r).ToArray(),
                FollowUps = FollowUpRules.Acknowledge(current, report, reference, receipt.At),
                Deadlines = current.Deadlines.Select(d => obligation.ObligationIds.Contains(d.Id) &&
                    d.Jurisdiction == obligation.Jurisdiction ? d with { Status = "SimulatedReported" } : d).ToArray()
            }, ct);
    }

    private static EudamedPlannedResponse[] SimulationPlan(EudamedDossier dossier, EudamedDeliveryEntry entry)
    {
        var start = entry.Attempt.StartedAt!.Value;
        var result = new List<EudamedPlannedResponse>();
        void Add(int sequence, string kind, string status, TimeSpan delay, EudamedPeriod? period = null)
        {
            if (kind == "BusinessResponse" && entry.IntendedAction == "DraftOnly" && status == "BusinessAccepted")
                status = "DraftCreated";
            var id = entry.Attempt.Id + "-response-" + sequence;
            var content = kind switch
            {
                "InformationRequest" => "SYNTHETISCH: Die lokale Testbehoerde verlangt eine quellengebundene Praezisierung der technischen Ursachenabgrenzung, offener Befunde und des Wirksamkeitskriteriums. Keine reale Patientenanweisung.",
                "Comment" => "SYNTHETISCH: Der Kommentar der lokalen Testbehoerde verlangt eine getrennt freizugebende sprachliche Praezisierung. Fehlende weitere Kommentare sind keine Sicherheitsfreigabe.",
                _ => "SYNTHETISCH: Fachliche Verarbeitung des konkret gebundenen Testdossiers. Keine medizinische Bewertung, amtliche Annahme oder reale Einreichung."
            };
            var original = new EudamedInboxOriginal(id, entry.Attempt.CorrelationId, entry.Attempt.Id,
                dossier.Id, dossier.Revision, dossier.ScopeHash, sequence, kind, status,
                dossier.Kind == EudamedDossierKind.PSUR ? "DEMO evaluating NB" : "DEMO competent authority", content,
                status == "BusinessRejected" ? null : "DEMO-DTX-" + dossier.Id[^16..], dossier.Revision,
                status == "BusinessRejected" ? ["SIMULATED_BUSINESS_RULE: Quellenzuordnung menschlich praezisieren."] : [],
                period, kind == "InformationRequest" ? start.AddDays(7) : null, true);
            result.Add(new(id, entry.Attempt.Id, sequence, kind, start + delay, "Planned", Canonical.Serialize(original), null));
        }
        var expectedPeriod = dossier.Kind == EudamedDossierKind.PSRP ? dossier.Package.Content.Period : null;
        if (entry.Attempt.Scenario == "OutOfOrder")
        {
            Add(2, "BusinessResponse", "BusinessAccepted", TimeSpan.Zero, expectedPeriod);
            Add(1, "BusinessResponse", "DraftCreated", TimeSpan.FromSeconds(1));
        }
        else
        {
            var status = entry.Attempt.Scenario switch { "Reject" => "BusinessRejected", "DraftOnly" => "DraftCreated", _ => "BusinessAccepted" };
            var period = entry.Attempt.Scenario == "PeriodMismatch" ? expectedPeriod! with
                { Start = expectedPeriod.Start.AddDays(1), End = expectedPeriod.End.AddDays(1) } : expectedPeriod;
            Add(1, "BusinessResponse", status, entry.Attempt.Scenario == "Delayed" ? TimeSpan.FromSeconds(20) : TimeSpan.Zero, period);
            if (entry.Attempt.Scenario == "PeriodMismatch")
                Add(2, "BusinessResponse", "BusinessAccepted", TimeSpan.FromSeconds(1), expectedPeriod);
            if (entry.Attempt.Scenario is "Comment" or "InformationRequest")
                Add(2, entry.Attempt.Scenario, "Open", TimeSpan.FromSeconds(1));
        }
        return result.ToArray();
    }

    private static string InboxId(EudamedBinding binding, string messageId) =>
        "eu-inbox-" + Canonical.Hash(new { binding.Profile.Environment, binding.ProfileHash, messageId });

    private async Task<EudamedDeliveryLedger> Delivery(string id, CancellationToken ct) =>
        await store.Get<EudamedDeliveryLedger>("eudamedDelivery", id, ct)
        ?? throw new DomainException("eudamed_ledger_missing", "Persistenter Versandauftrag fehlt.", 404);

    private static EudamedDeliveryEntry Entry(EudamedDeliveryLedger ledger, string attemptId) =>
        ledger.Entries.SingleOrDefault(e => e.Attempt.Id == attemptId)
        ?? throw new DomainException("eudamed_attempt_missing", "Gebundener Versuch fehlt.", 404);

    private static EudamedDeliveryLedger Replace(EudamedDeliveryLedger ledger, EudamedDeliveryEntry entry) =>
        ledger with { Version = ledger.Version + 1,
            Entries = ledger.Entries.Select(e => e.Attempt.Id == entry.Attempt.Id ? entry : e).ToArray() };

    private static EudamedCorrespondence RequireOpenReplyTarget(EudamedPackage package, EudamedDeliveryLedger ledger)
    {
        RequireLineage(package);
        var parent = package.Sources.Where(s => s.Kind == "eudamedDossier" && s.Source.Id == package.Content.ParentId)
            .Select(s => Canonical.Read<EudamedDossier>(s.Json)).Single();
        var bound = parent.Correspondence.Single(c => c.Id == package.Content.ReplyToCorrespondenceId);
        var matches = ledger.Entries.Where(e => e.DossierId == parent.Id && e.Attempt.DossierRevision == bound.DossierRevision &&
            e.Attempt.PackageHash == parent.ScopeHash && Canonical.Hash(e.Attempt.Binding) == Canonical.Hash(parent.Package.Binding))
            .SelectMany(e => e.Correspondence).Where(c => c.Id == bound.Id).ToArray();
        Guard.Against(matches.Length != 1 || matches[0].Status != "Open" || matches[0].ReplyDossierId is not null ||
            Canonical.Hash(matches[0]) != Canonical.Hash(bound),
            "eudamed_correspondence", "Nur die unveraenderte offene Originalkorrespondenz des gebundenen Versandledgers darf beantwortet werden.", 409);
        return matches[0];
    }

    private static EudamedDeliveryLedger CompleteReply(EudamedDeliveryLedger ledger, EudamedDossier reply)
    {
        var target = RequireOpenReplyTarget(reply.Package, ledger);
        return ledger with { Entries = ledger.Entries.Select(e => e with
        {
            Correspondence = e.Correspondence.Select(c => c.Id == target.Id
                ? c with { Status = "Replied", ReplyDossierId = reply.Id } : c).ToArray()
        }).ToArray() };
    }

    private async Task<EudamedDossier> ApprovedDossier(EudamedDeliveryEntry entry, CancellationToken ct)
    {
        var original = await objects.Read(entry.ApprovedDossier.Hash, ct);
        var dossier = Canonical.Read<EudamedDossier>(original);
        Guard.Against(Canonical.HashText(original) != entry.ApprovedDossier.Hash ||
            EudamedContracts.Bind(dossier) != entry.ApprovedDossier || dossier.Id != entry.DossierId ||
            dossier.ScopeHash != entry.Attempt.PackageHash || dossier.Revision != entry.Attempt.DossierRevision ||
            dossier.Package.Content.IntendedAction != entry.IntendedAction ||
            Canonical.Hash(dossier.Package.Binding) != Canonical.Hash(entry.Attempt.Binding),
            "eudamed_delivery_binding", "Versandreservierung und eingefrorenes Original stimmen nicht ueberein.", 409);
        EudamedRules.RequireFrozen(dossier);
        return dossier;
    }

    private static Actor DossierWorker(EudamedDossier dossier) =>
        Worker(dossier.ProductId, dossier.Package.Simulated) with { Products = EudamedRules.AccessProducts(dossier.Package) };
}
