using System.Text.Json;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class AnalyticsTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private AnalyticsService Service(TimeProvider clock, IAgentRunner? runner = null) =>
        new(fixture.Store, runner ?? new AgentRunner(), fixture.Policies, clock);

    [Fact]
    public async Task T15_TrendBindsUniqueCaseVersionsAndRejectsMismatchedDenominators()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var missingDate = await s.Service.Register(s.Input with { IncidentKey = CaseScenario.Key(), SourceId = CaseScenario.Key(),
            OccurredAt = null }, SqlFixture.Pms, CaseScenario.Key());
        var service = Service(s.Clock);
        var command = new RunTrendCommand("ortho", "Worldwide", new(2025, 11, 15), new(2026, 11, 14), "exposure-ortho-2026-Worldwide");
        var key = CaseScenario.Key();
        var result = await service.Trend(command, SqlFixture.Pms, key);
        Assert.Equal("Candidate", result.Status);
        Assert.Equal(Canonical.Hash(result.Input), result.InputHash);
        Assert.Contains(result.Input.CaseVersions!, c => c.Id == s.Case.Id && c.Version == s.Case.Version);
        Assert.DoesNotContain(missingDate.Id, result.Input.CaseIds);
        Assert.Contains(result.Input.ExcludedUnknownDates!, c => c.Id == missingDate.Id);
        Assert.Equal(result.Input.CaseIds.Distinct().Count(), result.Result.Events);
        Assert.Equal(result.Id, (await service.Trend(command, SqlFixture.Pms, key)).Id);
        Assert.Equal("invalid_denominator", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Trend(command with { Region = "EEA-Turkey-NI" }, SqlFixture.Pms, CaseScenario.Key()))).Code);
        await Assert.ThrowsAsync<DomainException>(() => service.ReviewTrend(result.Id,
            new(result.Version, "Technische Einordnung mit passenden Nennern und offener klinischer Relevanz."), SqlFixture.Pms, CaseScenario.Key()));
        var reviewed = await service.ReviewTrend(result.Id,
            new(result.Version, "Explorative Rate fachlich geprueft. Kausalitaet bleibt offen; Untersuchungen und CAPA werden separat gesteuert."),
            SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("Reviewed", reviewed.Status);
        await fixture.Store.Manifest(result.Id, kind: "trend");
    }

    [Fact]
    public async Task T16_PsurFreezesOpenCasesAndRequiresIndependentVersionBoundReviews()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var runner = new CountingRunner();
        var service = Service(s.Clock, runner);
        var input = new FreezePsurCommand("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide");
        var key = CaseScenario.Key();
        var results = await Task.WhenAll(service.Freeze(input, SqlFixture.Pms, key), service.Freeze(input, SqlFixture.Pms, key));
        var psur = (await fixture.Store.Get<Psur>("psur", results[0].Id))!;
        Assert.Equal(1, runner.Count);
        Assert.Equal("Draft", psur.Status);
        Assert.DoesNotContain(psur.Findings, f => f.Severity is "Critical" or "Major");
        var included = Assert.Single(psur.Snapshot.Cases, c => c.Id == s.Case.Id);
        Assert.False(included.MedicalApproved);
        Assert.NotEmpty(included.OpenQuestions);
        Assert.Equal(Canonical.Hash(psur.Snapshot), psur.SnapshotHash);
        var originalHash = psur.SnapshotHash;
        await s.Service.Register(s.Input with { SourceId = CaseScenario.Key() }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(originalHash, Canonical.Hash((await fixture.Store.Get<Psur>("psur", psur.Id))!.Snapshot));
        const string reason = "Medizinisch-technischer PSUR mit offenen Risiken, passenden Nennern und versionsgebundenen Quellen geprueft.";
        await Assert.ThrowsAsync<DomainException>(() => service.Approve(psur.Id, new(psur.Version, reason), SqlFixture.Ra, CaseScenario.Key()));
        await Assert.ThrowsAsync<DomainException>(() => service.Simulate(psur.Id, new(psur.Version, reason), SqlFixture.Ra, CaseScenario.Key()));
        psur = await service.Approve(psur.Id, new(psur.Version, reason), SqlFixture.Medical, CaseScenario.Key());
        var bothRolesSameIdentity = SqlFixture.Medical with { Roles = [Roles.Medical, Roles.Regulatory] };
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Approve(psur.Id, new(psur.Version, reason), bothRolesSameIdentity, CaseScenario.Key()))).Code);
        var priorScope = psur.ScopeHash;
        psur = await service.Revise(psur.Id, new(psur.Version, psur.Draft + "\nMenschlicher Zusatz: offene Befunde werden separat verfolgt.", reason),
            SqlFixture.Medical, CaseScenario.Key());
        Assert.Empty(psur.Approvals);
        Assert.NotEqual(priorScope, psur.ScopeHash);
        psur = await service.Approve(psur.Id, new(psur.Version, reason), SqlFixture.Medical, CaseScenario.Key());
        psur = await service.Approve(psur.Id, new(psur.Version, reason), SqlFixture.Ra, CaseScenario.Key());
        psur = await service.Simulate(psur.Id, new(psur.Version, reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.True(psur.Simulated);
        Assert.StartsWith("DEMO-BS-", psur.NotifiedBodyReceipt);
        await fixture.Store.Manifest(psur.Id, kind: "psur");
    }

    [Fact]
    public async Task T16_IndependentGeographicalReferenceIsNotAddedTwiceAndMissingPeriodsRemainUnknown()
    {
        using var referenceFile = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "cohors-acceptance-references.json")));
        var reference = referenceFile.RootElement.GetProperty("psurGeographyReference");
        var s = new CaseScenario(fixture);
        await s.Create();
        var occurred = DateTimeOffset.Parse("2025-06-10T10:00:00Z");
        var cases = Enumerable.Range(1, 8).Select(i => s.Case with { Id = "reference-" + i, Country = i <= 5 ? "IE" : "GB",
            Jurisdiction = i <= 5 ? "EU-IE" : "GB", OccurredAt = occurred, FirstAwarenessAt = occurred.AddDays(1) }).ToArray();
        var exposure = new Exposure("worldwide", 1, "ortho", "Worldwide", new(2025, 1, 1), new(2025, 12, 31),
            "DeviceYears", reference.GetProperty("worldwideIncludingSubset").GetProperty("matchedExposureDeviceYears").GetInt32(),
            "Independently curated synthetic reference", "Coordinator reference profile", true);
        var snapshot = PortfolioRules.Snapshot(new("ortho", "ortho", exposure.Start, exposure.End, "Worldwide",
            [.. cases, s.Case with { Id = "open-carry-over", OccurredAt = occurred.AddYears(-2), FirstAwarenessAt = occurred.AddYears(-2) }],
            [exposure, exposure with { Id = "subset", Region = "EEA-Turkey-NI", Count = reference.GetProperty("subset").GetProperty("matchedExposureDeviceYears").GetInt32() }],
            [], [], [], [], s.Clock.Now));
        var n = snapshot.Periods[0];
        Assert.Equal(reference.GetProperty("expectedWorldwideEvents").GetInt32(), n.Regions[0].Events);
        Assert.Equal(reference.GetProperty("expectedWorldwideExposureDeviceYears").GetInt32(), n.Regions[0].Exposure);
        Assert.Equal(reference.GetProperty("worldwideIncludingSubset").GetProperty("expectedRatePer10000").GetDecimal(), n.Regions[0].RatePerThousand * 10);
        Assert.Equal(reference.GetProperty("subset").GetProperty("expectedRatePer10000").GetDecimal(), n.Regions[1].RatePerThousand * 10);
        Assert.NotEqual(reference.GetProperty("forbiddenAdditiveWorldwideEvents").GetInt32(), n.Regions[0].Events);
        Assert.Contains(snapshot.Cases, c => c.Id == "open-carry-over" && c.CarryOver && !c.CurrentPeriodEvent);
        Assert.Equal(["N", "N2", "N3", "N4"], snapshot.PeriodLabels);
        Assert.All(snapshot.Periods.Skip(1).SelectMany(p => p.Regions), r => { Assert.Null(r.Events); Assert.Null(r.Exposure); });
        Assert.Equal(new DateOnly(2024, 1, 1), snapshot.Periods[1].Start);
        Assert.Equal(new DateOnly(2024, 12, 31), snapshot.Periods[1].End);
    }

    [Fact]
    public async Task T05_AnalyticsModelFailureKeepsSnapshotsAndNumbersButNeverCreatesAReplaySuccess()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
        var service = Service(clock);
        var trend = await service.Trend(new("ortho", "Worldwide", new(2025, 11, 15), new(2026, 11, 14),
            "exposure-ortho-2026-Worldwide", AgentMode.Failure), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("AuthoringFailed", trend.Status);
        Assert.Equal("simulated_model_outage", trend.Error);
        Assert.Equal("", trend.Interpretation);
        var psur = await service.Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide", AgentMode.Failure),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("AuthoringFailed", psur.Status);
        Assert.Equal("simulated_model_outage", psur.Error);
        Assert.Equal("", psur.Draft);
        Assert.NotEmpty(psur.Snapshot.Documents);
        Assert.Empty(psur.Approvals);
    }

    [Fact]
    public async Task T16_PsurAssuranceRejectsUnresolvedReferencesAndWrongMetrics()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
        var service = Service(clock);
        var psur = await service.Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Contains(PortfolioRules.CheckPsur(psur.Snapshot, psur.Draft + "\n[case:not-in-snapshot@999]"),
            f => f.Code == "psur_source_invalid" && f.Severity == "Critical");
        var wrong = psur.Draft.Replace("[metric:N:Worldwide:exposure=3000]", "[metric:N:Worldwide:exposure=5200]", StringComparison.Ordinal);
        Assert.NotEqual(wrong, psur.Draft);
        Assert.Contains(PortfolioRules.CheckPsur(psur.Snapshot, wrong), f => f.Code == "psur_numbers" && f.Severity == "Critical");
        Assert.Contains(PortfolioRules.CheckPsur(psur.Snapshot, new string('x', 1000)), f => f.Code == "psur_sources_missing");
    }

    private sealed class CountingRunner : IAgentRunner
    {
        private int count;
        public int Count => count;
        public Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default)
        {
            Interlocked.Increment(ref count);
            return new AgentRunner().Run(input, ct);
        }
    }
}
