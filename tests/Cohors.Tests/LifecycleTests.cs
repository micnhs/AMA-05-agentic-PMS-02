using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class LifecycleTests(SqlFixture fixture)
{
    [Fact]
    public async Task T11_ReclassificationCanShortenButNeverRestartOrExtendTheOriginalDeadline()
    {
        var s = new CaseScenario(fixture);
        s.Case = await s.Service.Register(s.Input with { Seriousness = Seriousness.OtherSerious }, SqlFixture.Pms, CaseScenario.Key());
        await s.Assess();
        var original = s.Case.Deadlines.Single();
        s.Case = await s.Service.Reclassify(s.Case.Id, new(s.Case.Version, Seriousness.PublicHealthThreat,
            "Neue medizinische Dringlichkeit begruendet; urspruengliche Kenntnis bleibt unveraendert."), SqlFixture.Medical, CaseScenario.Key());
        var shortened = s.Case.Deadlines.Single();
        Assert.Equal(original.TriggerAt, shortened.TriggerAt);
        Assert.True(shortened.LegalDue < original.LegalDue);
        var stale = await Assert.ThrowsAsync<DomainException>(() => s.Prepare());
        Assert.Equal("assessment_stale", stale.Code);
        s.Case = await s.Service.Reclassify(s.Case.Id, new(s.Case.Version, Seriousness.OtherSerious,
            "Erneute begruendete Einordnung; keine Verlaengerung bereits geschuetzter Fristen."), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal(shortened.LegalDue, s.Case.Deadlines.Single().LegalDue);
        Assert.Equal(original.TriggerAt, s.Case.FirstAwarenessAt);
    }

    [Fact]
    public async Task T14_ReviewedFinalClosureAndExplicitReopeningPreserveCompletedObligations()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        await s.Respond();
        await s.Assess();
        await s.Prepare();
        await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept", "Synthetische Erstmeldung fuer den Abschlussablauf."), SqlFixture.Ra, CaseScenario.Key());
        await Assert.ThrowsAsync<DomainException>(() => s.Service.Close(s.Case.Id,
            new(s.Case.Version, "Eine Erstmeldung allein darf den Fall noch nicht abschliessen."), SqlFixture.Ra, CaseScenario.Key()));
        await s.Prepare("Final");
        await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept", "Synthetischen Abschluss nach getrennter Pruefung quittieren."), SqlFixture.Ra, CaseScenario.Key());
        s.Case = await s.Service.Close(s.Case.Id, new(s.Case.Version,
            "Untersuchung und Bericht abgeschlossen; fortlaufende Produktbeobachtung und CAPA bleiben eigenstaendig."), SqlFixture.Medical, CaseScenario.Key());
        var completed = s.Case.Deadlines.Single();
        Assert.Equal(CaseStage.Closed, s.Case.Stage);
        s.Case = await s.Service.Register(s.Input with { SourceId = "duplicate-" + CaseScenario.Key() }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(CaseStage.Closed, s.Case.Stage);
        AssertDeadlinePreserved(completed, s.Case.Deadlines.Single());
        await Assert.ThrowsAsync<DomainException>(() => s.Assess());
        s.Case = await s.Service.Reopen(s.Case.Id, new(s.Case.Version,
            "Neue Quelle nach Abschluss verlangt eine explizite erneute Untersuchung, aber keinen Neustart der Originalpflicht."), SqlFixture.Medical, CaseScenario.Key());
        Assert.Null(s.Case.ClosedAt);
        Assert.Equal(CaseStage.AwaitingMedicalReview, s.Case.Stage);
        AssertDeadlinePreserved(completed, s.Case.Deadlines.Single());
        Assert.NotEqual(s.Case.CurrentAssessment!.EvidenceHash, Canonical.Hash(s.Case.Evidence));
    }

    [Fact]
    public async Task T03_AbandonedCaseRunCannotAcceptLateResultsOrStartTheNextModelStage()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var runner = new BlockingRunner();
        var service = new CaseService(fixture.Store, fixture.Objects, fixture.Policies, runner,
            new KnowledgeSearch(fixture.Store, fixture.Objects, fixture.Store), s.Clock, new MirComposer());
        s.Case = await service.RequestAssessment(s.Case.Id, new(s.Case.Version, AgentMode.Replay,
            "Kontrollierter Test eines unklaren Agentenlaufs."), SqlFixture.Pms, CaseScenario.Key());
        var runId = s.Case.AgentRuns.Last().Id;
        var pending = service.ProcessAssessment(s.Case.Id, runId);
        var input = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        s.Case = await service.Get(s.Case.Id, SqlFixture.Quality);
        await Assert.ThrowsAsync<DomainException>(() => service.AbandonRun(s.Case.Id, runId,
            new(s.Case.Version, "Zu frueher Abbruch ist unzulaessig und muss zurueckgewiesen werden."), SqlFixture.Quality, CaseScenario.Key()));
        s.Clock.Now = s.Clock.Now.AddMinutes(11);
        s.Case = await service.AbandonRun(s.Case.Id, runId, new(s.Case.Version,
            "Ungeklaerter Lauf nach kontrollierter Pruefung abbrechen; Ergebnisse nicht nachtraeglich annehmen."), SqlFixture.Quality, CaseScenario.Key());
        var due = s.Case.Deadlines.Single().LegalDue;
        runner.Complete(await new AgentRunner().Run(input));
        var finished = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Abandoned", finished.AgentRuns.Single(r => r.Id == runId).Status);
        Assert.Empty(finished.Assessments);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(due, finished.Deadlines.Single().LegalDue);
        var replay = await service.ProcessAssessment(s.Case.Id, runId);
        Assert.Equal(finished.Version, replay.Version);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task T03_AbandonedInvocationIsNotOverwrittenByALateProviderResponse()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var runner = new BlockingRunner();
        var service = new AgentExecutionService(fixture.Store, runner, s.Clock);
        var operations = new OperationsService(fixture.Store, fixture.Store, s.Clock);
        var key = CaseScenario.Key();
        var pending = service.Run(key, s.Case.ProductId, new(AgentRole.Ingestion, AgentMode.Replay, s.Case,
            null, null, s.Case.Evidence, "Synthetische Quellenzuordnung pruefen.", 0));
        var input = await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var id = "agent-" + Canonical.HashText(key)[..32];
        var invocation = (await fixture.Store.Get<AgentInvocation>("agentInvocation", id))!;
        s.Clock.Now = s.Clock.Now.AddMinutes(11);
        await operations.AbandonInvocation(id, new(invocation.Version,
            "Ungeklaerten Providerauftrag kontrolliert abbrechen; keine verdeckte Wiederholung zulassen."), SqlFixture.Quality, CaseScenario.Key());
        runner.Complete(await new AgentRunner().Run(input));
        var error = await Assert.ThrowsAsync<DomainException>(() => pending);
        Assert.Equal("invocation_abandoned", error.Code);
        invocation = (await fixture.Store.Get<AgentInvocation>("agentInvocation", id))!;
        Assert.Equal("Abandoned", invocation.Status);
        Assert.Null(invocation.OutputJson);
        await Assert.ThrowsAsync<DomainException>(() => service.Run(key, s.Case.ProductId, input));
        Assert.Equal(1, runner.Calls);
    }

    private static void AssertDeadlinePreserved(Deadline before, Deadline after) =>
        Assert.Equal((before.Id, before.Status, before.TriggerAt, before.LegalDue, before.SafetyDue),
            (after.Id, after.Status, after.TriggerAt, after.LegalDue, after.SafetyDue));

    private sealed class BlockingRunner : IAgentRunner
    {
        public TaskCompletionSource<AgentInput> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AgentOutput> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default)
        {
            Calls++;
            Started.TrySetResult(input);
            return completion.Task.WaitAsync(ct);
        }
        public void Complete(AgentOutput output) => completion.TrySetResult(output);
    }
}
