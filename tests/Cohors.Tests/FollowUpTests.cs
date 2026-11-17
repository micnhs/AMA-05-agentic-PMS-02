using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class FollowUpTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    [Fact]
    public async Task T14_PromisedDatesSurviveUnknownReplayEscalateAndFinalOnlyCompletesBoundJurisdiction()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Respond(); await s.Assess(); await s.Prepare();
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version,
            s.MirFacts() with { NextReportDate = new(2026, 11, 22) },
            "Sonntag ausdruecklich als festen synthetischen Folgetermin zusagen, nicht als neue gesetzliche Frist."),
            SqlFixture.Medical, CaseScenario.Key());
        await s.Approve();
        var initial = s.Case.CurrentReport!;
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Unknown",
            "Moeglichen Empfang simulieren; vor Abgleich noch keine Pflicht erledigen."), SqlFixture.Ra, CaseScenario.Key());
        Assert.Null(s.Case.FollowUps);
        Assert.All(s.Case.Deadlines, d => Assert.Equal("Open", d.Status));
        var reconcile = new VersionCommand(s.Case.Version, "Korrelationsgebundenen simulierten Empfang menschlich abgleichen.");
        var reconcileKey = CaseScenario.Key();
        s.Case = await s.Service.Reconcile(s.Case.Id, reconcile, SqlFixture.Ra, reconcileKey);
        var first = Assert.Single(Assert.IsType<FollowUpObligation[]>(s.Case.FollowUps));
        Assert.Equal(initial.ScopeHash, first.SourceReportHash);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T23:59:59.9999999Z"), first.Schedule.DueAt);
        Assert.Equal("Europe/Dublin", first.Schedule.TimeZone);
        Assert.Equal(initial.Mir!.FollowUpSchedule!.CalendarVersion, first.Schedule.CalendarVersion);
        Assert.Equal(first.Schedule.DueAt.AddHours(-12), first.Schedule.SafetyDue);
        Assert.Equal("Open", first.Status);
        Assert.All(s.Case.Deadlines, d => Assert.Equal("SimulatedReported", d.Status));

        s.Clock.Now = DateTimeOffset.Parse("2026-11-23T00:01:00Z");
        var operations = new OperationsService(fixture.Store, fixture.Store, s.Clock);
        var tick = await operations.Tick();
        Assert.Equal(1, tick.OpenFollowUps);
        s.Case = await s.Service.Get(s.Case.Id, SqlFixture.Ra);
        Assert.Contains(s.Case.Alerts, a => a.StartsWith($"FOLLOWUP:{first.Id}:", StringComparison.Ordinal));
        var versionAfterAlert = s.Case.Version;
        await operations.Tick();
        s.Case = await s.Service.Get(s.Case.Id, SqlFixture.Ra);
        Assert.Equal(versionAfterAlert, s.Case.Version);

        var northernIrelandDeadline = DeadlineCalculator.Calculate("NI", s.Case.Seriousness, s.Case.FirstAwarenessAt, fixture.Policies.Calendar("NI"));
        s.Case = await fixture.Store.Execute<SafetyCase>(new(s.Case.Id, "case", s.Case.ProductId, s.Case.Version,
            CaseScenario.Key(), "AdditionalJurisdictionTestFixture",
            "Expliziter synthetischer Zwei-Jurisdiktions-Teststand, kein automatisches Routing durch eine Dublettenquelle.",
            SqlFixture.Ra, northernIrelandDeadline, s.Clock.Now), current => current! with
            { Version = current.Version + 1, Deadlines = [.. current.Deadlines, northernIrelandDeadline] });
        Assert.Contains(s.Case.Deadlines, d => d.Jurisdiction == "NI");
        s.Case = await s.Service.Prepare(s.Case.Id, new(s.Case.Version, "Initial", "NI", "en", true,
            "Getrennte synthetische nordirische Berichtspflicht.",
            "Zweite Jurisdiktion ausdruecklich und mit eigenem Empfaengerscope bearbeiten."), SqlFixture.Pms, CaseScenario.Key());
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version, s.MirFacts(),
            "Synthetische Angaben fuer die getrennte nordirische Empfangssimulation."), SqlFixture.Medical, CaseScenario.Key());
        await s.Approve();
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept",
            "Eigenstaendige nordirische Meldung ausschliesslich simuliert annehmen."), SqlFixture.Ra, CaseScenario.Key());
        var northernIreland = s.Case.FollowUps!.Single(f => f.Jurisdiction == "NI");

        await s.Prepare("FollowUp"); await s.Approve();
        var followupReport = s.Case.CurrentReport!;
        Assert.Equal([first.Id], Assert.IsType<string[]>(followupReport.FollowUpObligationIds));
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept",
            "Irischen Folgebericht mit neuer menschlich freigegebener Terminzusage simulieren."), SqlFixture.Ra, CaseScenario.Key());
        var next = s.Case.FollowUps!.Single(f => f.Jurisdiction == "EU-IE" && f.Status == "Open");
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(followupReport.ScopeHash, next.SourceReportHash);
        Assert.Equal(followupReport.ScopeHash, s.Case.FollowUps!.Single(f => f.Id == first.Id).CompletedByReportHash);

        await s.Service.Reconcile(s.Case.Id, reconcile, SqlFixture.Ra, reconcileKey);
        s.Case = await s.Service.Get(s.Case.Id, SqlFixture.Ra);
        Assert.Equal(3, s.Case.FollowUps!.Length);
        Assert.Equal("Open", s.Case.FollowUps!.Single(f => f.Id == next.Id).Status);
        Assert.Equal("Open", s.Case.FollowUps!.Single(f => f.Id == northernIreland.Id).Status);

        await s.Prepare("Final"); await s.Approve();
        Assert.Equal([next.Id], Assert.IsType<string[]>(s.Case.CurrentReport!.FollowUpObligationIds));
        s.Case = await s.Service.Submit(s.Case.Id, new(s.Case.Version, "Accept",
            "Irischen Abschlussbericht mit genau einer gebundenen offenen Folgepflicht simulieren."), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("SimulatedReported", s.Case.FollowUps!.Single(f => f.Id == next.Id).Status);
        Assert.Equal("Open", s.Case.FollowUps!.Single(f => f.Id == northernIreland.Id).Status);
        Assert.Equal(3, s.Case.FollowUps!.Length);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.Close(s.Case.Id,
            new(s.Case.Version, "Offene nordirische Folgepflicht verhindert den fallweiten Abschluss."),
            SqlFixture.Medical, CaseScenario.Key()));
        await fixture.Store.Manifest(s.Case.Id);
    }
}
