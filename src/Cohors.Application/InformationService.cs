using Cohors.Domain;

namespace Cohors.Application;

public sealed partial class CaseService
{
    private static InformationRequest Request(SafetyCase c, string requestId) =>
        c.InformationRequests.SingleOrDefault(r => r.Id == requestId)
        ?? throw new DomainException("request_missing", "Anfrage fehlt in diesem Fall.", 404);

    private static InformationResponse Response(SafetyCase c, string responseId) =>
        (c.InformationResponses ?? []).SingleOrDefault(r => r.Id == responseId)
        ?? throw new DomainException("response_missing", "Ruecklauf fehlt in diesem Fall.", 404);

    private async Task RequireInformationOriginal(InformationResponse response, CancellationToken ct)
    {
        var original = Canonical.Serialize(new { RequestId = response.DeclaredRequestId, Response = response.Content, response.Envelope });
        Guard.Against(Canonical.HashText(original) != response.OriginalHash || await objects.Read(response.OriginalHash, ct) != original ||
            await objects.Read(response.ContentHash, ct) != response.Content, "response_integrity", "Ruecklauforiginal ist nicht unveraendert lesbar.", 409);
    }

    private static SafetyCase ReplaceRequest(SafetyCase c, InformationRequest request)
    {
        Guard.Against(c.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
        return c with { Version = c.Version + 1,
            InformationRequests = c.InformationRequests.Select(r => r.Id == request.Id ? request : r).ToArray() };
    }

    public async Task<SafetyCase> AddInformationRequest(string id, RequestInformationCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "InformationRequestDrafted", input.Relevance, actor, input);
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        Guard.Text(input.Question, "Fragestellung", 20);
        Guard.Text(input.MinimalData, "Minimaldatensatz", 15, 1500);
        Guard.Text(input.Relevance, "Medizinisch-technische Relevanz", 30);
        Guard.Text(input.ManufacturerBasis, "Hersteller-Rechtsgrundlage", 20);
        Guard.Text(input.DisclosureBasis, "Eigenstaendige Offenlegungsbefugnis", 20);
        Guard.Against(input.MinimalData.Contains("vollstaendige Patientenakte", StringComparison.OrdinalIgnoreCase)
            || input.MinimalData.Contains("vollst\u00e4ndige Patientenakte", StringComparison.OrdinalIgnoreCase)
            || input.MinimalData.Contains("full patient record", StringComparison.OrdinalIgnoreCase),
            "data_minimization", "Pauschale Vollaktenanforderung ist nicht zugelassen.");
        Guard.Against(input.DueAt <= clock.GetUtcNow(), "request_due", "Interne Rueckmeldefrist muss in der Zukunft liegen.");
        var recipient = InformationRules.RequireRecipient(input.Recipient);
        Guard.Against(input.RecipientRole != recipient.Role, "request_recipient", "Empfaengerrolle muss zum benannten Kontakt passen.");
        return await store.Execute<SafetyCase>(command, current =>
        {
            var value = Require(current);
            Guard.Against(value.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
            var requestId = Guid.NewGuid().ToString("N");
            var life = new InformationRequestLifecycle(InformationRules.Contract, value.Id, value.ProductId,
                "RFI-" + requestId, recipient, clock.GetUtcNow(), actor.Id, []);
            var request = new InformationRequest(requestId, input.Question, input.MinimalData, input.Relevance,
                input.RecipientRole, input.ManufacturerBasis, input.DisclosureBasis, input.DueAt, "",
                [], "Draft", null, true, life);
            request = request with { ScopeHash = InformationRules.Scope(request) };
            return value with { Version = value.Version + 1, Stage = CaseStage.AwaitingInformation,
                InformationRequests = [.. value.InformationRequests, request] };
        }, ct);
    }

    public async Task<SafetyCase> ApproveInformation(string id, string requestId, ApprovalCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationRequestApproved",
            input.Reason, actor, new { requestId, input }), current =>
        {
            var value = Require(current);
            var request = Request(value, requestId);
            InformationRules.RequireBound(value, request);
            var role = actor.Roles.Contains(Roles.Medical) ? Roles.Medical : Roles.Regulatory;
            Guard.Against(request.Status != "Draft" || request.Approvals.Any(a => a.Role == role || a.ActorId == actor.Id),
                "request_gate", "Anfrage braucht zwei verschiedene berechtigte Personen und Rollen.");
            if (role == Roles.Regulatory)
                Guard.Against(!request.Approvals.Any(a => a.Role == Roles.Medical && a.ScopeHash == request.ScopeHash),
                    "medical_required", "Medizinische Notwendigkeit zuerst genehmigen.");
            var approval = new Approval(Guid.NewGuid().ToString("N"), actor.Id, actor.Name, role, request.ScopeHash,
                clock.GetUtcNow(), "Minimale Anfrage an genau diesen synthetischen Kontakt genehmigt; noch kein Versand",
                Guard.Text(input.Reason, "Begruendung", 20), null);
            return ReplaceRequest(value, request with { Approvals = [.. request.Approvals, approval],
                Status = role == Roles.Regulatory ? "Approved" : "Draft" });
        }, ct);
    }

    public async Task<SafetyCase> DispatchInformation(string id, string requestId, DispatchInformationCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        Guard.Against(input.Scenario is not ("Accept" or "Fail" or "Unknown"), "simulator_scenario", "Unbekanntes Anfragesimulator-Szenario.");
        Guard.Text(input.Reason, "Begruendung des separaten simulierten Versands", 20);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationDispatchQueued",
            input.Reason, actor, new { requestId, input }), current =>
        {
            var value = Require(current);
            var request = Request(value, requestId);
            InformationRules.RequireApprovals(value, request);
            var life = request.Lifecycle!;
            Guard.Against(request.Status is not ("Approved" or "SendFailed") ||
                life.Dispatches.Any(d => d.Status is "SendPending" or "TransmissionUnknown") ||
                InformationRules.HasConfirmedDelivery(request), "request_delivery_gate",
                "Nur freigegebene, nachweislich noch nicht zugestellte Anfrage senden. Unklaren Versand zuerst abgleichen.", 409);
            var dispatch = new InformationDispatch(Guid.NewGuid().ToString("N"), value.Version + 1, request.ScopeHash,
                life.CorrelationId, Canonical.Hash(life.Recipient), actor.Id, clock.GetUtcNow(), input.Scenario,
                "SendPending", null, null, null, null, null);
            return ReplaceRequest(value, request with { Status = "SendPending",
                Lifecycle = life with { Dispatches = [.. life.Dispatches, dispatch] } });
        }, ct);
    }

    public async Task<SafetyCase> ProcessInformationDispatch(string id, string requestId, string dispatchId, CancellationToken ct = default)
    {
        var c = await Get(id, Workflow, ct);
        var request = Request(c, requestId);
        var life = InformationRules.RequireBound(c, request);
        var dispatch = life.Dispatches.SingleOrDefault(d => d.Id == dispatchId)
            ?? throw new DomainException("request_dispatch_missing", "Gebundener Anfragesendeauftrag fehlt.", 404);
        if (dispatch.Status != "SendPending") return c;
        return await store.Execute<SafetyCase>(Context(c, c.Version, "rfi-dispatch-" + dispatchId,
            "InformationDispatchCompleted", "Ausschliesslich interner Simulator; kein E-Mail-, Telefon- oder Portalzugriff.",
            Workflow, new { requestId, dispatchId }), current =>
        {
            var value = Require(current);
            var currentRequest = Request(value, requestId);
            InformationRules.RequireApprovals(value, currentRequest);
            var currentLife = currentRequest.Lifecycle!;
            Guard.Against(currentRequest.Status != "SendPending" || dispatch.ScopeHash != currentRequest.ScopeHash ||
                dispatch.CorrelationId != currentLife.CorrelationId || dispatch.RecipientHash != Canonical.Hash(currentLife.Recipient),
                "request_dispatch_scope", "Sendeauftrag passt nicht mehr exakt zur freigegebenen Anfrage.", 409);
            var now = clock.GetUtcNow();
            var receipt = dispatch.Scenario == "Fail" ? null : new InformationSimulatorReceipt("DEMO-RFI-" + dispatch.Id,
                requestId, value.Id, value.ProductId, currentLife.CorrelationId, dispatch.RecipientHash, currentRequest.ScopeHash, now);
            var status = dispatch.Scenario switch { "Accept" => "SimulatedSent", "Fail" => "SendFailed",
                "Unknown" => "TransmissionUnknown", _ => throw new DomainException("simulator_scenario", "Gespeichertes Simulatorszenario ist ungueltig.") };
            var finished = dispatch with { Status = status, AttemptedAt = now, CompletedAt = now,
                ReceiptReference = status == "SimulatedSent" ? receipt!.Reference : null, SimulatorReceipt = receipt,
                Error = status == "SendFailed" ? "simulated_not_delivered" : status == "TransmissionUnknown" ? "simulated_acknowledgement_lost" : null };
            return ReplaceRequest(value, currentRequest with { Status = status,
                Lifecycle = currentLife with { Dispatches = currentLife.Dispatches.Select(d => d.Id == dispatchId ? finished : d).ToArray() } });
        }, ct);
    }

    public async Task<SafetyCase> ReconcileInformation(string id, string requestId, string dispatchId, VersionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical, Roles.Regulatory);
        var c = await Get(id, actor, ct);
        Guard.Text(input.Reason, "Begruendung des gezielten Empfangsabgleichs", 20);
        return await store.Execute<SafetyCase>(Context(c, input.ExpectedVersion, key, "InformationDispatchReconciled",
            input.Reason, actor, new { requestId, dispatchId, input }), current =>
        {
            var value = Require(current);
            var request = Request(value, requestId);
            var life = InformationRules.RequireBound(value, request);
            var dispatch = life.Dispatches.SingleOrDefault(d => d.Id == dispatchId)
                ?? throw new DomainException("request_dispatch_missing", "Exakter Sendeauftrag fehlt.", 404);
            Guard.Against(request.Status != "TransmissionUnknown" || dispatch.Status != "TransmissionUnknown" ||
                !InformationRules.ReceiptMatches(request, dispatch), "request_reconciliation",
                "Kein exakt passender Simulator-Empfang; unklarer Zustand bleibt gesperrt.", 409);
            var reconciled = dispatch with { Status = "SimulatedSent", ReceiptReference = dispatch.SimulatorReceipt!.Reference,
                CompletedAt = clock.GetUtcNow(), Error = null };
            return ReplaceRequest(value, request with { Status = "SimulatedSent",
                Lifecycle = life with { Dispatches = life.Dispatches.Select(d => d.Id == dispatchId ? reconciled : d).ToArray() } });
        }, ct);
    }

    public async Task<SafetyCase> RespondInformation(string id, RespondInformationCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "InformationResponseReceived", input.Reason, actor, input);
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        var envelope = input.Envelope ?? throw new DomainException("response_envelope",
            "Originalreferenz, synthetischen Absender und vorhandene Anfrage-/Korrelationsangaben erfassen; nichts aus der letzten Anfrage erraten.");
        Guard.Against(!envelope.Synthetic, "synthetic_only", "Nur ausdruecklich synthetischer Befundruecklauf.");
        InformationRules.RequireRecipient(envelope.Sender);
        if (!string.IsNullOrEmpty(envelope.ProductId)) actor.RequireProduct(envelope.ProductId);
        Guard.Text(envelope.ExternalId, "Originalreferenz", 3, 150);
        Guard.Text(envelope.SourceDescription, "Dokumentierte Ruecklaufherkunft", 10, 500);
        Guard.Text(input.Response, "Synthetischer Originalbefund", 30, 5000);
        Guard.Text(input.Reason, "Eingangsbegruendung", 20);
        if (input.RequestId is null) throw new DomainException("response_reference", "Fehlende Anfrage-ID explizit leer lassen, nicht null.");
        Guard.Against(input.RequestId.Length > 100 ||
            envelope.CaseId?.Length > 100 || envelope.ProductId?.Length > 40 || envelope.CorrelationId?.Length > 100,
            "response_reference", "Referenzen muessen kurze explizite Originalangaben sein; fehlende Werte leer lassen.");
        var original = Canonical.Serialize(new { input.RequestId, input.Response, input.Envelope });
        var originalHash = Canonical.HashText(original);
        if ((c.InformationResponses ?? []).SingleOrDefault(r => r.Envelope.ExternalId == envelope.ExternalId) is { } previous)
        {
            Guard.Against(previous.OriginalHash != originalHash, "response_revision",
                "Gleiche Ruecklaufreferenz mit anderem Originalinhalt; als neue Quellreferenz erfassen, Original nicht ersetzen.", 409);
            return c;
        }
        var contentHash = await objects.Put(input.Response, ct);
        Guard.Against(contentHash != Canonical.HashText(input.Response) || await objects.Put(original, ct) != originalHash,
            "response_integrity", "Ruecklauf-Originalhash stimmt nicht.", 409);
        return await store.Execute<SafetyCase>(command, current =>
        {
            var value = Require(current);
            Guard.Against(value.Stage == CaseStage.Closed, "case_closed", "Abgeschlossenen Fall zuerst begruendet wiederaufnehmen.");
            Guard.Against((value.InformationResponses ?? []).Any(r => r.Envelope.ExternalId == envelope.ExternalId),
                "response_duplicate", "Ruecklauf wurde gleichzeitig bereits erfasst; aktuellen Stand laden.", 409);
            var candidates = InformationRules.Candidates(value, input.RequestId, envelope);
            var exact = candidates.Length == 1 && InformationRules.CompleteReferences(input.RequestId, envelope);
            var response = new InformationResponse(Canonical.Hash(new { value.Id, envelope.ExternalId })[..32],
                input.RequestId, envelope, input.Response, contentHash, originalHash, clock.GetUtcNow(), actor.Id,
                exact ? "Received" : "Held", candidates.Select(r => r.Id).ToArray(),
                exact ? "Anfrage, Korrelation, Fall, Produkt und vollstaendiger Absender passen exakt; menschliche Zuordnung noch offen." :
                $"Zurueckgehalten: fehlende/widerspruechliche Referenzen oder kein bestaetigter Versand. {candidates.Length} exakt mit den vorhandenen Angaben vereinbare Anfrage(n); keine automatische Zuordnung.",
                null, null);
            return value with { Version = value.Version + 1,
                InformationResponses = [.. value.InformationResponses ?? [], response],
                InformationRequests = value.InformationRequests.Select(r => exact && r.Id == candidates[0].Id &&
                    r.Status != "SimulatedAnswered" ? r with { Status = "ResponseReceived" } : r).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> MapInformationResponse(string id, string responseId, MapInformationResponseCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Pms, Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "InformationResponseMapped", input.Reason, actor, new { responseId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        Guard.Text(input.Reason, "Begruendete Zuordnung am Original", 40);
        var source = Response(c, responseId);
        await RequireInformationOriginal(source, ct);
        return await store.Execute<SafetyCase>(command, current =>
        {
            var value = Require(current);
            var response = Response(value, responseId);
            var request = Request(value, input.RequestId);
            var life = InformationRules.RequireBound(value, request);
            Guard.Against(response.Mapping is not null, "response_mapped", "Dieser Ruecklauf ist bereits zugeordnet; keine doppelte Evidenz erzeugen.", 409);
            Guard.Against(input.CorrelationId != life.CorrelationId || input.RecipientHash != Canonical.Hash(life.Recipient) ||
                !InformationRules.Candidates(value, response.DeclaredRequestId, response.Envelope).Any(r => r.Id == request.Id),
                "response_binding", "Anfrage, Korrelation, Fall, Produkt, Absender oder bestaetigter Versand widersprechen dem Original; keine Zuordnung.", 409);
            var missing = !InformationRules.CompleteReferences(response.DeclaredRequestId, response.Envelope);
            Guard.Against(missing && !input.ConfirmMissingReferences, "response_confirmation",
                "Fehlende Originalreferenzen ausdruecklich menschlich anhand der exakten Anfrage bestaetigen; keine Auswahl der letzten Anfrage.");
            var evidenceId = "response-" + response.Id;
            var evidence = new Evidence(evidenceId, 1, value.ProductId, "Synthetischer Befundruecklauf - unbestaetigt",
                "ClinicalFollowUp", response.Content, response.ContentHash, "cohors:simulated-response:" + response.Id,
                $"Original {response.Envelope.ExternalId}; Anfrage {request.Id}; Korrelation {life.CorrelationId}",
                "Synthetic; unconfirmed incoming information", "Product", value.OccurredAt ?? value.FirstAwarenessAt,
                null, response.ReceivedAt, false, true, response.ContentHash);
            var mapping = new InformationResponseMapping(request.Id, life.CorrelationId, request.ScopeHash,
                input.RecipientHash, evidenceId, actor.Id, actor.Name, clock.GetUtcNow(), input.Reason, missing);
            return Invalidate(value) with { Version = value.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                Evidence = [.. value.Evidence, evidence],
                InformationRequests = value.InformationRequests.Select(r => r.Id == request.Id
                    ? r with { Status = "SimulatedAnswered", Response = response.Content } : r).ToArray(),
                InformationResponses = (value.InformationResponses ?? []).Select(r => r.Id == responseId
                    ? r with { Status = "Mapped", Mapping = mapping } : r).ToArray() };
        }, ct);
    }

    public async Task<SafetyCase> ReviewInformationResponse(string id, string responseId, VersionCommand input,
        Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Medical);
        var c = await Get(id, actor, ct);
        var command = Context(c, input.ExpectedVersion, key, "InformationResponseEvidenceReviewed",
            input.Reason, actor, new { responseId, input });
        if (await store.Receipt<SafetyCase>(command, ct) is { } replay) return replay;
        Guard.Text(input.Reason, "Pruefung von Herkunft, Relevanz und verbleibender Befundunsicherheit", 40);
        await RequireInformationOriginal(Response(c, responseId), ct);
        return await store.Execute<SafetyCase>(command, current =>
        {
            var value = Require(current);
            var response = Response(value, responseId);
            var mapping = response.Mapping ?? throw new DomainException("response_not_mapped", "Ruecklauf zuerst exakt zuordnen.");
            Guard.Against(response.Review is not null, "response_reviewed", "Diese Ruecklaufquelle wurde bereits geprueft.", 409);
            var evidence = value.Evidence.Single(e => e.Id == mapping.EvidenceId);
            EvidenceRules.RequireIntegrity(evidence);
            var reviewed = evidence with { Version = evidence.Version + 1, Approved = true, RecordedAt = clock.GetUtcNow(),
                Title = "Synthetischer Befundruecklauf - Quelle menschlich geprueft" };
            return Invalidate(value) with { Version = value.Version + 1, Stage = CaseStage.AwaitingMedicalReview,
                Evidence = value.Evidence.Select(e => e.Id == reviewed.Id ? reviewed : e).ToArray(),
                InformationResponses = (value.InformationResponses ?? []).Select(r => r.Id == responseId
                    ? r with { Review = new(actor.Id, actor.Name, clock.GetUtcNow(), input.Reason, reviewed.Version) } : r).ToArray() };
        }, ct);
    }
}
