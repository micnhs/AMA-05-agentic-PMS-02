using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class CaseService
{
    public async Task<SafetyCase> RecordSpecimenPlan(string id, RecordSpecimenPlanCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "SpecimenPlanRecorded", input.Reason, actor, input);
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        RequireSpecimenCase(c, input.ExpectedVersion);
        SpecimenInvestigationRules.RequireReference(input.Reference, "Untersuchungsreferenz");
        SpecimenInvestigationRules.RequireReference(input.SpecimenReference, "Asservatenreferenz");
        SpecimenInvestigationRules.RequireReference(input.CustodyReference, "Verwahrungs-/Verantwortungsreferenz");
        Guard.Text(input.Reason, "Begruendung von Auftrag und Beweissicherung", 80, 4000);
        Guard.Against(input.MayCompromiseCauseEvaluation is null, "specimen_impact",
            "Moegliche Beeintraechtigung der Ursachenbewertung muss ausdruecklich menschlich eingeordnet werden.");
        Guard.Against(input.Language is not ("de" or "en"), "specimen_language", "Explizit Deutsch oder Englisch waehlen.");
        Guard.Against(input.ReceivedAt == default || input.ReceivedAt > command.At, "specimen_received",
            "Expliziter, nicht zukuenftiger synthetischer Asservateneingang erforderlich.");
        var origin = Guard.Text(input.OriginDescription, "Herkunft, Uebergabe und bekannte Verwahrungsluecken", 80, 4000);
        var condition = Guard.Text(input.ConditionBefore, "Dokumentierter Ausgangszustand", 80, 4000);
        var measures = Guard.Text(input.PreservationMeasures, "Beweissicherung vor Untersuchung", 80, 4000);
        var traceability = Guard.Text(input.TraceabilityAssessment, "Land-/Chargen-/UDI-Zuordnung und Luecken", 80, 4000);
        var method = Guard.Text(input.Method, "Fragestellung und Untersuchungsauftrag", 80, 4000);
        var impact = Guard.Text(input.ImpactRationale, "Moegliche Beeintraechtigung der Ursachenbewertung", 80, 4000);
        var noticeText = Guard.Text(input.AuthorityNoticeText, "Minimale vorherige Unterrichtung",
            input.MayCompromiseCauseEvaluation == true ? 80 : 0, 4000);
        var investigationId = "specimen-" + Canonical.Hash(new { id, input.Reference })[..32];
        var previous = (c.Investigations ?? []).SingleOrDefault(i => i.Id == investigationId);
        if (previous is not null) SpecimenInvestigationRules.RequireIntegrity(previous);
        Guard.Against(previous?.Status is "SimulatedExamined" or "Cancelled", "specimen_immutable",
            "Abgeschlossene Untersuchung oder begruendeter Verzicht bleibt unveraendert. Einen neuen Auftrag separat referenzieren.", 409);
        var revision = (previous?.Revision ?? 0) + 1;
        var originalHash = Canonical.Hash(c);
        var source = SpecimenSource(c, await store.Audit(c.Id, ct));
        var evidenceId = "preservation-" + investigationId;
        var preservationText = SpecimenInvestigationRules.PreservationText(input.Reference, revision,
            SpecimenInvestigationRules.Context(c), input.SpecimenReference, input.ReceivedAt, origin,
            condition, input.CustodyReference, measures, traceability, actor.Id, command.At);
        var evidenceHash = await objects.Put(preservationText, ct);
        var evidence = new Evidence(evidenceId, revision, c.ProductId,
            "Synthetische Asservatensicherung " + input.Reference, "SpecimenPreservation",
            preservationText, evidenceHash, "cohors:specimen:" + investigationId, "Originaler Sicherungsbeleg",
            "Synthetic source, not a clinical validation", "Product", command.At, null, command.At, true, true, evidenceHash);
        var sources = c.Evidence.Where(e => e.Id != evidenceId).Append(evidence).ToArray();
        var next = new SpecimenInvestigation(investigationId, revision, input.Reference,
            source, SpecimenInvestigationRules.Context(c with { Evidence = sources }),
            policies.Device(c.ProductId), new(input.SpecimenReference, input.ReceivedAt, origin, condition,
                input.CustodyReference, measures, traceability, evidence), sources, method,
            input.MayCompromiseCauseEvaluation == true, impact, noticeText,
            ReportTargets.For(c.Jurisdiction).Authority, SpecimenInvestigationRules.Recipient(c.Jurisdiction),
            input.Language, actor.Id, command.At, "", [], "Draft", null, null, null, null);
        next = next with { ScopeHash = SpecimenInvestigationRules.Scope(next) };
        SpecimenInvestigationRules.RequireIntegrity(next);
        await ReadSpecimenObjects(next, ct);
        return await store.Execute<SafetyCase>(command, value =>
        {
            var current = Require(value);
            Guard.Against(Canonical.Hash(current) != originalHash, "specimen_stale",
                "Ausgangsfall geaendert; Sicherung und Auftrag neu binden.", 409);
            return Invalidate(current) with
            {
                Version = current.Version + 1, Evidence = sources, Stage = CaseStage.AwaitingMedicalReview,
                Investigations = [.. (current.Investigations ?? []).Where(i => i.Id != investigationId), next]
            };
        }, ct);
    }

    public async Task<SafetyCase> ApproveSpecimen(string id, string investigationId, VersionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "SpecimenPlanApproved", input.Reason,
            actor, new { investigationId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        RequireSpecimenCase(c, input.ExpectedVersion);
        var plan = RequireSpecimen(c, investigationId);
        Guard.Against(plan.Status is not ("Draft" or "MedicalApproved"), "specimen_state",
            "Nur ein aktueller offener Untersuchungsplan kann intern freigegeben werden.", 409);
        RequireCurrentSpecimen(c, plan);
        var role = plan.Status == "Draft" ? Roles.Medical : Roles.Regulatory;
        actor.RequireHuman(role);
        Guard.Against(plan.Approvals.Any(a => a.ActorId == actor.Id || a.Role == role), "four_eyes",
            "Zwei getrennte berechtigte Personen und Rollen muessen genau diesen Untersuchungsplan pruefen.", 403);
        var reason = Guard.Text(input.Reason, "Interne Pruefbegruendung", 80, 4000);
        await ReadSpecimenObjects(plan, ct);
        return await store.Execute<SafetyCase>(command, value =>
        {
            var current = Require(value);
            RequireCurrentSpecimen(current, plan);
            var next = plan with
            {
                Approvals = [.. plan.Approvals, new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name,
                    role, plan.ScopeHash, command.At, SpecimenInvestigationRules.ApprovalMeaning, reason, null)],
                Status = role == Roles.Medical ? "MedicalApproved" : "Approved",
                FrozenJson = role == Roles.Regulatory ? Canonical.Serialize(SpecimenInvestigationRules.Package(plan)) : null
            };
            return UpdateSpecimen(current, next);
        }, ct);
    }

    public async Task<SafetyCase> InformSpecimenAuthority(string id, string investigationId, VersionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Regulatory);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "SpecimenAuthorityInformed", input.Reason,
            actor, new { investigationId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        RequireSpecimenCase(c, input.ExpectedVersion);
        var plan = RequireSpecimen(c, investigationId);
        Guard.Against(!plan.MayCompromiseCauseEvaluation || plan.Status != "Approved" || plan.PriorNotice is not null,
            "specimen_notice_state", "Dieser Simulatorweg braucht eine beeintraechtigende, zweifach intern freigegebene Prueffassung ohne bereits belegte Vorabunterrichtung.", 409);
        RequireCurrentSpecimen(c, plan);
        SpecimenInvestigationRules.RequireFrozen(plan);
        Guard.Text(input.Reason, "Begruendung der vorherigen simulierten Unterrichtung", 80, 4000);
        await ReadSpecimenObjects(plan, ct);
        return await store.Execute<SafetyCase>(command, value =>
        {
            var current = Require(value);
            RequireCurrentSpecimen(current, plan);
            return UpdateSpecimen(current, plan with
            {
                Status = "AuthorityInformed",
                PriorNotice = new("DEMO-SPECIMEN-NOTICE-" + plan.ScopeHash[..20], plan.Recipient, plan.ScopeHash,
                    command.At, actor.Id, current.Version + 1, SpecimenInvestigationRules.NoticeMeaning, true)
            });
        }, ct);
    }

    public async Task<SafetyCase> SimulateSpecimen(string id, string investigationId, SimulateSpecimenCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "SpecimenExaminationSimulated", input.Reason,
            actor, new { investigationId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        RequireSpecimenCase(c, input.ExpectedVersion);
        var plan = RequireSpecimen(c, investigationId);
        RequireCurrentSpecimen(c, plan);
        SpecimenInvestigationRules.RequireReadyToSimulate(plan);
        Guard.Text(input.Reason, "Begruendung der reinen Simulation", 80, 4000);
        var findings = Guard.Text(input.Findings, "Synthetische technische Befunde und Gegenhypothesen", 80, 5000);
        var remaining = Guard.Text(input.RemainingState, "Verbleibender Zustand und Beweissicherung", 80, 4000);
        var consequences = Guard.Text(input.ConsequencesAndGaps, "Auswirkungen auf Ursachenbewertung und Luecken", 80, 5000);
        await ReadSpecimenObjects(plan, ct);
        var text = SpecimenInvestigationRules.ResultText(plan, findings, remaining, consequences, command.At, actor.Id);
        var hash = await objects.Put(text, ct);
        var evidence = new Evidence("finding-" + plan.Id, plan.Revision, c.ProductId,
            "Synthetisches Untersuchungsergebnis " + plan.Reference, "SpecimenFinding",
            text, hash, "cohors:specimen:" + plan.Id, "Simulierter technischer Befund mit erhaltenem Ausgangszustand",
            "Synthetic source, not a clinical validation", "Product", command.At, null, command.At, true, true, hash);
        return await store.Execute<SafetyCase>(command, value =>
        {
            var current = Require(value);
            RequireCurrentSpecimen(current, plan);
            var next = plan with
            {
                Status = "SimulatedExamined",
                Outcome = new(findings, remaining, consequences, evidence, command.At, actor.Id, current.Version + 1, true)
            };
            return UpdateSpecimen(Invalidate(current), next) with
            {
                Evidence = [.. current.Evidence, evidence], Stage = CaseStage.AwaitingMedicalReview,
                Alerts = [.. current.Alerts, "Synthetischer Untersuchungsbefund erfasst: Assessment und offene Berichte erneut pruefen. Keine Kausalitaets-, Risiko- oder Fristentscheidung getroffen."]
            };
        }, ct);
    }

    public async Task<SafetyCase> CancelSpecimen(string id, string investigationId, VersionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "SpecimenPlanCancelled", input.Reason,
            actor, new { investigationId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        RequireSpecimenCase(c, input.ExpectedVersion);
        var plan = RequireSpecimen(c, investigationId);
        SpecimenInvestigationRules.RequireIntegrity(plan);
        Guard.Against(plan.Status is "Cancelled" or "SimulatedExamined", "specimen_immutable",
            "Abschluss/Verzicht ist bereits dokumentiert und bleibt unveraendert.", 409);
        var reason = Guard.Text(input.Reason, "Begruendeter Verzicht und weitere Ursachenabklaerung", 80, 4000);
        await ReadSpecimenObjects(plan, ct);
        return await store.Execute<SafetyCase>(command, value => UpdateSpecimen(Require(value), plan with
        {
            Status = "Cancelled", Cancellation = new(reason, actor.Id, command.At)
        }), ct);
    }

    public async Task<SpecimenInvestigationExport> ExportSpecimen(string id, string investigationId, Actor actor,
        int? caseVersion = null, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory, Roles.Quality, Roles.Auditor);
        var c = await Get(id, actor, ct);
        var history = await store.History<SafetyCase>("case", [c.ProductId], ct);
        var audit = await store.Audit(id, ct);
        var snapshot = caseVersion is { } version
            ? history.SingleOrDefault(v => v.Id == id && v.Version == version)
                ?? throw new DomainException("specimen_history", "Die angeforderte Fallfassung fehlt.", 404)
            : c;
        var plan = RequireSpecimen(snapshot, investigationId);
        SpecimenInvestigationRules.RequireIntegrity(plan);
        var source = history.SingleOrDefault(v => v.Id == plan.CaseSource.Id && v.Version == plan.CaseSource.Version)
            ?? throw new DomainException("specimen_source_history", "Die gebundene Ausgangsfallfassung fehlt.", 409);
        Guard.Against(SpecimenSource(source, audit) != plan.CaseSource, "specimen_source_history",
            "Der Originalhash der gebundenen Ausgangsfallfassung weicht ab.", 409);
        await ReadSpecimenObjects(plan, ct);
        return new("cohors-specimen-investigation-export-1", SpecimenInvestigationRules.Boundary,
            SpecimenSource(snapshot, audit), plan);
    }

    private static VersionReference SpecimenSource(SafetyCase c, AuditEvent[] audit)
    {
        // Reserializing an older case can add optional fields; bind its stored original payload hash.
        var version = audit.SingleOrDefault(a => a.AggregateId == c.Id && a.Version == c.Version)
            ?? throw new DomainException("specimen_source_audit", "Auditbindung der konkreten Fallfassung fehlt.", 409);
        return new(c.Id, c.Version, version.PayloadHash);
    }

    private static void RequireSpecimenCase(SafetyCase c, int expectedVersion)
    {
        Guard.Against(c.Version != expectedVersion, "version_conflict", "Fall wurde geaendert; aktuelle Fassung neu laden.", 409);
        Guard.Against(!c.Synthetic || c.Stage == CaseStage.Closed, "specimen_case",
            "Untersuchungen sind nur fuer offene synthetische Faelle vorgesehen.", 409);
    }

    private static SpecimenInvestigation RequireSpecimen(SafetyCase c, string investigationId) =>
        (c.Investigations ?? []).SingleOrDefault(i => i.Id == investigationId)
        ?? throw new DomainException("specimen_missing", "Untersuchungsauftrag fehlt.", 404);

    private void RequireCurrentSpecimen(SafetyCase c, SpecimenInvestigation plan)
    {
        SpecimenInvestigationRules.RequireCurrent(c, plan);
        Guard.Against(Canonical.Hash(plan.Device) != Canonical.Hash(policies.Device(c.ProductId)) ||
            plan.AuthorityName != ReportTargets.For(c.Jurisdiction).Authority, "specimen_policy_stale",
            "Produktmetadaten oder Empfaengerprofil geaendert; Untersuchungsplan neu binden und freigeben.", 409);
    }

    private static SafetyCase UpdateSpecimen(SafetyCase c, SpecimenInvestigation next)
    {
        SpecimenInvestigationRules.RequireIntegrity(next);
        return c with
        {
            Version = c.Version + 1,
            Investigations = (c.Investigations ?? []).Select(i => i.Id == next.Id ? next : i).ToArray()
        };
    }

    private async Task ReadSpecimenObjects(SpecimenInvestigation plan, CancellationToken ct)
    {
        var evidence = plan.Outcome is { } outcome ? plan.Sources.Append(outcome.Evidence) : plan.Sources;
        foreach (var source in evidence.DistinctBy(e => e.Hash))
            await EvidenceService.ReadOriginal(source, objects, ct);
    }
}
