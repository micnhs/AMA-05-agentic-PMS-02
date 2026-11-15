using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class PsurSeriesTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private AnalyticsService Service() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    private static FreezePsurCommand Initial() => new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide");
    private static FreezePsurCommand Refreeze(Psur prior) => Initial() with { PreviousReportId = prior.Id,
        PreviousReportVersion = prior.Version, RevisionReason = "Kontrollierte neue Datenbasis explizit gegen die gebundene Vorfassung vergleichen." };

    [Fact]
    public async Task T16_ParallelFreezesSerializeTheSeriesAndNeverAllocateDuplicateOrLostReportNumbers()
    {
        var service = Service();
        var calls = new[] {
            Record.ExceptionAsync(() => service.Freeze(Initial(), SqlFixture.Pms, CaseScenario.Key())),
            Record.ExceptionAsync(() => service.Freeze(Initial(), SqlFixture.Medical, CaseScenario.Key()))
        };
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => r is null);
        Assert.Equal("psur_predecessor", Assert.IsType<DomainException>(Assert.Single(results, r => r is not null)).Code);
        var first = Assert.Single(await fixture.Store.List<Psur>("psur", ["ortho"]));
        Assert.Equal("DEMO-PSUR-ORTHO", first.Snapshot.Series!.Reference);
        Assert.Equal(1, first.Snapshot.Series.ReportVersion);
        var bytes = Canonical.Serialize(first);
        var key = CaseScenario.Key();
        var next = await service.Freeze(Refreeze(first), SqlFixture.Pms, key);
        Assert.Equal(2, next.Snapshot.Series!.ReportVersion);
        Assert.Equal("Refreeze", next.Snapshot.Series.Kind);
        Assert.Equal(new(first.Id, first.Version, Canonical.Hash(first)), next.Snapshot.Series.PreviousReport!.Source);
        Assert.Equal(next.Id, (await service.Freeze(Refreeze(first), SqlFixture.Pms, key)).Id);
        Assert.Equal(2, (await fixture.Store.List<Psur>("psur", ["ortho"])).Length);
        Assert.Equal(bytes, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", first.Id)));
        Assert.DoesNotContain(next.Findings, f => f.Severity is "Critical" or "Major");
        await fixture.Store.Manifest(next.Id, kind: "psur");
    }

    [Fact]
    public async Task T16_PeriodsLeadProductPredecessorAndAgreedSubmissionDateAreEnforced()
    {
        var service = Service();
        var first = await service.Freeze(Initial(), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(new DateOnly(2026, 12, 15), first.Snapshot.Series!.SubmissionDate);
        Assert.Equal("psur_predecessor", (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(
            Refreeze(first) with { PreviousReportVersion = first.Version - 1 }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("psur_group", (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(
            Refreeze(first) with { LeadProductId = "cardio" }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        clock.Now = DateTimeOffset.Parse("2027-11-16T15:00:00Z");
        var following = Refreeze(first) with { Start = first.Snapshot.End.AddDays(1), End = first.Snapshot.End.AddYears(1) };
        Assert.Equal("psur_continuity", (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(
            following with { Start = following.Start.AddDays(1) }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("psur_schedule", (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(
            following, SqlFixture.Pms, CaseScenario.Key()))).Code);
        var exposure = new Exposure("next-period-worldwide", 1, "ortho", "Worldwide", following.Start, following.End,
            "Implants", 3100, "Synthetic next-period population", "Explicit synthetic annual exposure source", true);
        await fixture.Store.Execute<Exposure>(new(exposure.Id, "exposure", "ortho", 0, CaseScenario.Key(), "NextPeriodExposure",
            "Freigegebener Testnenner fuer die tatsaechliche Folgeperiode.", SqlFixture.Quality, exposure, clock.Now), _ => exposure);
        var next = await service.Freeze(following with { SubmissionDate = new(2028, 1, 10),
            ScheduleAgreement = "DEMO-BS-EXPLICIT-NEXT-PERIOD-2027" }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("NextPeriod", next.Snapshot.Series!.Kind);
        Assert.Equal(new DateOnly(2028, 1, 10), next.Snapshot.Series.SubmissionDate);
        Assert.Equal(2, next.Snapshot.Series.ReportVersion);
        Assert.Equal(first.Snapshot.Series.Governance.Device.BasicUdi, next.Snapshot.Series.Governance.Device.BasicUdi);
        Assert.DoesNotContain(next.Findings, f => f.Severity is "Critical" or "Major");
    }

    [Fact]
    public async Task T16_LegacySeriesAndForgedPriorSummaryCannotBeSilentlyRepairedOrApproved()
    {
        var service = Service();
        var first = await service.Freeze(Initial(), SqlFixture.Pms, CaseScenario.Key());
        var next = await service.Freeze(Refreeze(first), SqlFixture.Pms, CaseScenario.Key());
        var series = next.Snapshot.Series!;
        var snapshot = next.Snapshot with { Series = series with {
            PreviousReport = series.PreviousReport! with { Status = "SimulatedAccepted" } } };
        var forged = next with { Version = next.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot) };
        forged = forged with { ScopeHash = PortfolioRules.PsurScope(forged) };
        await fixture.Store.Execute<Psur>(new(next.Id, "psur", "ortho", next.Version, CaseScenario.Key(), "ForgedPriorSummary",
            "Negativer Test: selbst konsistente Hashes ersetzen keinen gebundenen Vorberichtsstatus.",
            SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_previous_source", (await Assert.ThrowsAsync<DomainException>(() => service.Approve(forged.Id,
            new(forged.Version, "Die vorgebliche BS-Quittung ist gegen die tatsaechliche SQL-Vorfassung abzugleichen."),
            SqlFixture.Medical, CaseScenario.Key()))).Code);
        var legacySnapshot = first.Snapshot with { Series = null };
        var legacy = first with { Version = first.Version + 1, Snapshot = legacySnapshot, SnapshotHash = Canonical.Hash(legacySnapshot) };
        legacy = legacy with { ScopeHash = PortfolioRules.PsurScope(legacy) };
        await fixture.Store.Execute<Psur>(new(first.Id, "psur", "ortho", first.Version, CaseScenario.Key(), "LegacySeriesFixture",
            "Altformat ohne Serienkennung fuer die kontrollierte Uebernahmegrenze.", SqlFixture.Quality, legacy, clock.Now), _ => legacy);
        Assert.Equal("psur_series_legacy", (await Assert.ThrowsAsync<DomainException>(() => service.Freeze(
            Refreeze(forged), SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal(Canonical.Hash(legacy), Canonical.Hash(await fixture.Store.Get<Psur>("psur", first.Id)));
    }
}
