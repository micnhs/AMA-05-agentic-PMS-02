using Cohors.Domain;

namespace Cohors.Application;

public sealed class OperationsService(IStateStore store, IWorkspaceReadStore workspace, TimeProvider clock,
    IOperationsStore operations)
{
    public async Task<AgentInvocation[]> ListInvocations(Actor actor, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        return (await store.List<AgentInvocation>("agentInvocation", actor.Products, ct))
            .Where(i => i.Status is "Running" or "Failed" or "Abandoned")
            .Where(i => i.InputJson is null || Canonical.Read<AgentInput>(i.InputJson).Snapshot is not { } snapshot ||
                PsurGroupRules.CanRead(snapshot, actor)).ToArray();
    }

    public async Task<AgentInvocation> AbandonInvocation(string id, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        Guard.Text(input.Reason, "Begruendung des kontrollierten Modellabbruchs", 40);
        var invocation = await store.Get<AgentInvocation>("agentInvocation", id, ct)
            ?? throw new DomainException("invocation_missing", "Modellauftrag fehlt.", 404);
        actor.RequireProduct(invocation.ProductId);
        if (invocation.InputJson is { } json)
        {
            Guard.Against(Canonical.HashText(json) != invocation.InputHash, "invocation_input_integrity",
                "Persistierter Auftragseingang wurde veraendert; kein scopeverkuerzter Abbruch.", 409);
            AgentBoundary.RequireProducts(Canonical.Read<AgentInput>(json), actor.Products);
        }
        return await store.Execute<AgentInvocation>(new(id, "agentInvocation", invocation.ProductId, input.ExpectedVersion,
            key, "AgentInvocationAbandoned", input.Reason, actor, input, clock.GetUtcNow()), value =>
        {
            Guard.Against(value!.Status != "Running" || clock.GetUtcNow() - value.StartedAt < TimeSpan.FromMinutes(10),
                "invocation_active", "Nur einen seit mindestens zehn Minuten ungeklaerten Auftrag kontrolliert abbrechen.", 409);
            return value with { Version = value.Version + 1, Status = "Abandoned", FinishedAt = clock.GetUtcNow(),
                ErrorCode = "human_abandoned_no_result_acceptance" };
        }, ct);
    }

    public async Task<OutboxRecovery> Recover(string eventId, VersionCommand input, Actor actor, string key, CancellationToken ct = default)
    {
        actor.RequireHuman(Roles.Quality);
        Guard.Text(input.Reason, "Wiederanlaufbegruendung", 30, 4000);
        var issue = (await store.OutboxIssues(actor.Products, ct)).SingleOrDefault(e => e.Id == eventId)
            ?? throw new DomainException("delivery_missing", "Keine offene, berechtigte technische Zustellung gefunden.", 404);
        actor.RequireProduct(issue.ProductId);
        var id = "recovery-" + eventId;
        return await store.Execute<OutboxRecovery>(new(id, "delivery-recovery", issue.ProductId, input.ExpectedVersion,
            key, "DeliveryRecoveryAuthorized", input.Reason, actor, new { eventId, input }, clock.GetUtcNow()), previous =>
                new(id, (previous?.Version ?? 0) + 1, issue.ProductId, eventId, actor.Id, input.Reason, clock.GetUtcNow()), ct);
    }

    public async Task<OperationsHeartbeat> Tick(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cases = await store.List<SafetyCase>("case", CaseService.Workflow.Products, ct);
        var escalations = 0;
        foreach (var c in cases)
        {
            var alerts = c.Deadlines.Where(d => d.Status == "Open")
                .SelectMany(d => new[]
                {
                    d.ImmediateAction ? $"IMMEDIATE:{d.Id}:Sofort an Medical Safety, RA und PRRC eskalieren." : null,
                    now > d.LegalDue ? $"LEGAL:{d.Id}:Gesetzliche Hoechstfrist ueberschritten; keine Rueckdatierung." : null,
                    now > d.SafetyDue ? $"SAFETY:{d.Id}:Interne SafetyDue ueberschritten; Stellvertretung alarmieren." : null
                }).Where(s => s is not null).Cast<string>()
                .Concat(c.InformationRequests.Where(r => r.Status != "SimulatedAnswered" && r.DueAt < now)
                    .Select(r => $"RFI:{r.Id}:Interne Rueckmeldefrist ueberschritten; Nachverfolgung ohne Pause der LegalDue."))
                .Concat((c.FollowUps ?? []).Where(f => f.Status == "Open" && f.Schedule.SafetyDue < now)
                    .Select(f => now > f.Schedule.DueAt
                        ? $"FOLLOWUP:{f.Id}:Im freigegebenen MIR zugesagter Folgetermin ueberschritten; Medical Safety und RA eskalieren. Keine neue Initialfrist."
                        : $"FOLLOWUP-SAFETY:{f.Id}:Interne Vorwarnung zum fest zugesagten Folgeberichtstermin."))
                .Where(a => !c.Alerts.Contains(a)).ToArray();
            if (alerts.Length == 0) continue;
            try
            {
                await operations.Escalate(c, alerts, now, ct);
                escalations += alerts.Length;
            }
            catch (DomainException e) when (e.Code == "version_conflict")
            {
                // Concurrent review won the write; the next independent tick rechecks the persisted deadline.
                continue;
            }
        }
        var psurReviews = await store.List<PsurReview>("psurReview", CaseService.Workflow.Products, ct);
        foreach (var review in psurReviews.Where(r => r.Status is "QuestionOpen" or "ReplyDraft" or "MedicalApproved" or "ReplyApproved"))
        {
            PsurReviewRules.RequireScope(review, CaseService.Workflow);
            var question = review.Questions[^1];
            var alert = $"PSUR-BS:{question.Round}:Vereinbarte Antwortfrist ueberschritten; RA/Medical nachverfolgen. Keine gesetzliche Pauschalfrist und kein Neustart alter Fristen.";
            if (now <= question.DueAt || review.Alerts.Contains(alert)) continue;
            try
            {
                await operations.Escalate(review, alert, now, ct);
                escalations++;
            }
            catch (DomainException error) when (error.Code == "version_conflict")
            {
                // A human transition won; the next tick reads the persisted question again.
                continue;
            }
        }
        var correspondence = await workspace.ReadCorrespondenceSources(CaseService.Workflow.Products, ct);
        foreach (var group in correspondence.GroupBy(s => s.Ledger.Id))
        {
            var ledger = group.First().Ledger;
            var deadlines = group.Select(source =>
            {
                EudamedRules.RequireActorScope(source.CurrentDossier, CaseService.Workflow);
                EudamedRules.RequireActorScope(source.ApprovedDossier, CaseService.Workflow);
                return EudamedDeadlineRules.Project(source);
            }).Where(d => d is not null).Cast<EudamedCorrespondenceDeadline>().ToArray();
            var alerts = deadlines.Select(d => EudamedDeadlineRules.Alert(d, now)).Where(a => a is not null)
                .Cast<EudamedCorrespondenceAlert>().Where(a => !(ledger.CorrespondenceAlerts ?? []).Any(old => old.Id == a.Id))
                .OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
            if (alerts.Length == 0) continue;
            var dependencies = group.SelectMany(source => new[]
            {
                new StoreDependency("eudamedDossier", EudamedContracts.Bind(source.CurrentDossier)),
                new StoreDependency("eudamedInbox", new(source.Inbox.Id, source.Inbox.Version, Canonical.Hash(source.Inbox)))
            }).DistinctBy(d => (d.Kind, d.Source.Id)).ToArray();
            try
            {
                await operations.Escalate(ledger, alerts, dependencies, now, ct);
                escalations += alerts.Length;
            }
            catch (DomainException error) when (error.Code is "version_conflict" or "dependency_version")
            {
                // A received reply or source transition won; re-read the authoritative ledger on the next tick.
                continue;
            }
        }
        var previous = await store.Get<OperationsHeartbeat>("operations", "heartbeat", ct);
        var heartbeat = new OperationsHeartbeat("heartbeat", (previous?.Version ?? 0) + 1, now, now,
            cases.Sum(c => c.Deadlines.Count(d => d.Status == "Open")), escalations, "Running",
            cases.Sum(c => (c.FollowUps ?? []).Count(f => f.Status == "Open")));
        return await operations.RecordHeartbeat(heartbeat, ct);
    }

    public static bool HeartbeatMissing(OperationsHeartbeat? heartbeat, DateTimeOffset now) =>
        heartbeat is null || heartbeat.LastTick > now || now - heartbeat.LastTick > TimeSpan.FromMinutes(3);
}
