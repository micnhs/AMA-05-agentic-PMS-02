using Cohors.Domain;

namespace Cohors.Application;

public sealed class OutboxProcessor(IStateStore store, CaseService cases, IKnowledgeSearch search, EudamedService? eudamed = null)
{
    public async Task<OutboxDispatchResult> Process(string id, CancellationToken ct = default)
    {
        const string consumer = "cohors-domain-events-v1";
        if (await store.HasReceipt(id, consumer, ct)) return new(id, "Handled", null);
        var owner = Guid.NewGuid().ToString("N");
        var e = await store.ClaimEvent(id, owner, ct);
        if (e is null) return new(id, "Unavailable", "leased_completed_or_backoff");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        try
        {
            if (e.Type == "AssessmentRequested")
            {
                var requested = Canonical.Read<SafetyCase>(e.Payload);
                await cases.ProcessAssessment(requested.Id, requested.AgentRuns.Last().Id, timeout.Token);
            }
            if (e.Type == "InformationDispatchQueued")
            {
                var requested = Canonical.Read<SafetyCase>(e.Payload);
                var dispatch = requested.InformationRequests.SelectMany(r => (r.Lifecycle?.Dispatches ?? [])
                    .Where(d => d.QueuedCaseVersion == requested.Version).Select(d => new { RequestId = r.Id, Dispatch = d })).Single();
                await cases.ProcessInformationDispatch(requested.Id, dispatch.RequestId, dispatch.Dispatch.Id, timeout.Token);
            }
            if (e.Type == "EudamedActorSubmitted")
            {
                var requested = Canonical.Read<EudamedActorApplication>(e.Payload);
                await (eudamed ?? throw new DomainException("eudamed_worker_missing", "Actor-Worker ist nicht registriert.", 503))
                    .ProcessActor(requested.Id, requested.Version, timeout.Token);
            }
            if (e.Type == "DomibusConnectionCheckRequested")
                throw new DomainException("domibus_diagnostics_not_enabled",
                    "Die externe Backenddiagnose ist in diesem Auslieferungsschnitt noch nicht freigeschaltet.", 503);
            if (e.Type is "EvidenceSeeded" or "PublicSourceApproved" or "EvidenceVersionPublished")
                await search.Rebuild(timeout.Token);
            await store.CompleteEvent(id, consumer, owner, null, timeout.Token);
            return new(id, "Handled", null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var code = error is DomainException domain ? domain.Code : error.GetType().Name;
            using var recordTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await store.CompleteEvent(id, consumer, owner, code, recordTimeout.Token);
            return new(id, e.Attempts >= 5 ? "Poisoned" : "Retry", code);
        }
    }
}
