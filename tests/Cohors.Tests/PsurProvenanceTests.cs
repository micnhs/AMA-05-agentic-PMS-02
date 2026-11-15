using System.Text.Json;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class PsurProvenanceTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private AnalyticsService Service() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    private static JsonDocument References() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cohors-acceptance-references.json")));
    private static DateOnly Day(JsonElement item, string key) => DateOnly.Parse(item.GetProperty(key).GetString()!);

    [Fact]
    public async Task T16_IndependentKnowledgeCutoffUsesOneBasisCaseAndSeparatelyBindsLateKnowledge()
    {
        using var json = References();
        var r = json.RootElement.GetProperty("psurCutoffReference");
        foreach (var item in r.GetProperty("cases").EnumerateArray())
            await SeedCase(item.GetProperty("id").GetString()!, item.GetProperty("version").GetInt32(),
                item.GetProperty("country").GetString()!, item.GetProperty("occurredAt").GetDateTimeOffset(),
                item.GetProperty("firstAwarenessAt").GetDateTimeOffset());
        clock.Now = r.GetProperty("frozenAt").GetDateTimeOffset();
        var psur = await Service().Freeze(new("ortho", "ortho", Day(r, "start"), Day(r, "end"), "Worldwide"), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("Draft", psur.Status);
        var manifest = psur.Snapshot.SourceManifest!;
        Assert.Equal(r.GetProperty("knowledgeCutoffInclusiveUtc").GetDateTimeOffset().AddTicks(10), manifest.KnowledgeCutoffExclusiveUtc);
        var n = psur.Snapshot.Periods[0].Regions.Single(x => x.Region == "Worldwide");
        var numerator = Assert.IsType<VersionReference[]>(n.NumeratorSources);
        Assert.Equal(r.GetProperty("expectedBaselineEvents").GetInt32(), n.Events);
        Assert.Equal(r.GetProperty("expectedBaselineCaseIds").EnumerateArray().Select(x => x.GetString()), n.NumeratorSources!.Select(x => x.Id));
        Assert.Equal(r.GetProperty("expectedPostCutoffCaseIds").EnumerateArray().Select(x => x.GetString()),
            manifest.PostCutoffCases.Where(x => x.Period == "N").Select(x => x.Source.Id));
        var before = psur.Snapshot.Cases.Single(c => c.Id == "known-before-cutoff");
        var after = psur.Snapshot.Cases.Single(c => c.Id == "known-after-cutoff");
        Assert.True(before.CurrentPeriodEvent);
        Assert.Equal("Intake", before.StatusAtCutoff);
        Assert.Equal("Monitoring", before.Stage);
        Assert.False(after.CurrentPeriodEvent);
        Assert.False(after.CarryOver);
        Assert.Equal("PostCutoffAddendum", after.KnowledgeStatus);
        Assert.Equal("NotKnownAtCutoff", after.StatusAtCutoff);
        Assert.DoesNotContain(psur.Findings, f => f.Severity == "Critical");
        var persisted = (await fixture.Store.Get<Psur>("psur", psur.Id))!;
        Assert.Equal(Canonical.Hash(manifest), Canonical.Hash(persisted.Snapshot.SourceManifest));
        Assert.Equal(3, numerator.Single().Version);
        await fixture.Store.Manifest(psur.Id, kind: "psur");
    }

    [Fact]
    public async Task T16_SubregionReportBindsAllEightWorldwideSourceVersionsFromIndependentReference()
    {
        using var json = References();
        var r = json.RootElement.GetProperty("psurGeographyReference");
        foreach (var id in r.GetProperty("expectedWorldwideCaseIds").EnumerateArray().Select(x => x.GetString()!))
            await SeedCase(id, 1, r.GetProperty("expectedSubsetCaseIds").EnumerateArray().Any(x => x.GetString() == id) ? "IE" : "GB",
                DateTimeOffset.Parse("2025-06-10T10:00:00Z"), DateTimeOffset.Parse("2025-06-11T10:00:00Z"));
        var start = new DateOnly(2025, 1, 1);
        var end = new DateOnly(2025, 12, 31);
        await Exposure("reference-worldwide", "Worldwide", start, end, r.GetProperty("expectedWorldwideExposureDeviceYears").GetInt32());
        await Exposure("reference-subset", "EEA-Turkey-NI", start, end, r.GetProperty("subset").GetProperty("matchedExposureDeviceYears").GetInt32());
        var psur = await Service().Freeze(new("ortho", "ortho", start, end, "EEA-Turkey-NI"), SqlFixture.Pms, CaseScenario.Key());
        var n = psur.Snapshot.Periods[0];
        Assert.Equal(r.GetProperty("expectedWorldwideEvents").GetInt32(), n.Regions[0].Events);
        Assert.Equal(r.GetProperty("expectedWorldwideCaseIds").EnumerateArray().Select(x => x.GetString()),
            n.Regions[0].NumeratorSources!.Select(x => x.Id));
        Assert.Equal(r.GetProperty("expectedSubsetCaseIds").EnumerateArray().Select(x => x.GetString()),
            n.Regions[1].NumeratorSources!.Select(x => x.Id));
        Assert.Equal(5, psur.Snapshot.Cases.Length);
        Assert.Equal(8, psur.Snapshot.SourceManifest!.Cases.Length);
        var storedCases = await fixture.Store.History<SafetyCase>("case", ["ortho"]);
        foreach (var source in Assert.IsType<VersionReference[]>(n.Regions[0].NumeratorSources))
            Assert.Equal(Canonical.Hash(storedCases.Single(c => c.Id == source.Id && c.Version == source.Version)), source.Hash);
        var tampered = psur.Snapshot with { Periods = psur.Snapshot.Periods.Select(p => p.Label != "N" ? p :
            p with { Regions = p.Regions.Select(x => x.Region != "Worldwide" ? x : x with { NumeratorSources = x.NumeratorSources![..5] }).ToArray() }).ToArray() };
        Assert.Contains(PortfolioRules.CheckPsur(tampered, psur.Draft), f => f.Code == "psur_numerator_provenance" && f.Severity == "Critical");
    }

    [Fact]
    public async Task T16_ClosedComparisonPeriodCaseRemainsBoundWithoutBecomingAnOpenCurrentCarryOver()
    {
        using var json = References();
        var r = json.RootElement.GetProperty("psurHistoricalNumeratorReference");
        var item = r.GetProperty("closedCase");
        var source = await SeedCase(item.GetProperty("id").GetString()!, item.GetProperty("version").GetInt32(),
            item.GetProperty("country").GetString()!, item.GetProperty("occurredAt").GetDateTimeOffset(),
            item.GetProperty("firstAwarenessAt").GetDateTimeOffset(), item.GetProperty("closedAt").GetDateTimeOffset());
        var previous = r.GetProperty("comparisonPeriod");
        await Exposure("reference-n2-world", "Worldwide", Day(previous, "start"), Day(previous, "end"), 1000);
        var period = r.GetProperty("currentPeriod");
        var psur = await Service().Freeze(new("ortho", "ortho", Day(period, "start"), Day(period, "end"), "Worldwide"), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(r.GetProperty("expectedCurrentPeriodEvents").GetInt32(), psur.Snapshot.Periods[0].Regions[0].Events);
        var n2 = psur.Snapshot.Periods[1].Regions[0];
        var numerator = Assert.IsType<VersionReference[]>(n2.NumeratorSources);
        Assert.Equal(r.GetProperty("expectedComparisonPeriodEvents").GetInt32(), n2.Events);
        Assert.Equal(r.GetProperty("expectedComparisonNumeratorCaseIds").EnumerateArray().Select(x => x.GetString()), n2.NumeratorSources!.Select(x => x.Id));
        Assert.Equal(source.Version, numerator.Single().Version);
        Assert.Equal(Canonical.Hash(source), numerator.Single().Hash);
        Assert.DoesNotContain(psur.Snapshot.Cases, c => c.Id == source.Id);
        Assert.Contains(psur.Snapshot.SourceManifest!.Cases, c => c.Reference.Id == source.Id && c.StatusAtCutoff == "Closed");
        Assert.DoesNotContain(psur.Findings, f => f.Severity == "Critical");
    }

    [Fact]
    public async Task T16_PreviouslyFrozenReportWithoutSourceManifestCannotBeSilentlyReleased()
    {
        var service = Service();
        var p = await service.Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"), SqlFixture.Pms, CaseScenario.Key());
        var oldSnapshot = p.Snapshot with { SourceManifest = null };
        var legacy = p with { Version = p.Version + 1, Snapshot = oldSnapshot, SnapshotHash = Canonical.Hash(oldSnapshot), Findings = [] };
        legacy = legacy with { ScopeHash = PortfolioRules.PsurScope(legacy) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", p.ProductId, p.Version, CaseScenario.Key(), "LegacyPsurTestFixture",
            "Gezielte Altformat-Regression ohne Quellenmanifest; keine echte Freigabe.", SqlFixture.Quality, legacy, clock.Now), _ => legacy);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.Approve(p.Id, new(legacy.Version,
            "Ein vorhandener Freeze-String darf fehlende historische Quellen nicht ersetzen."), SqlFixture.Medical, CaseScenario.Key()));
        Assert.Equal("psur_numerator_provenance", error.Code);
        var stored = (await fixture.Store.Get<Psur>("psur", p.Id))!;
        Assert.Equal(Canonical.Hash(legacy), Canonical.Hash(stored));
        Assert.Empty(stored.Approvals);
    }

    [Fact]
    public async Task T16_DocumentRevisionIsEffectiveAtFreezeAndDoesNotRewriteAnEarlierFrozenSnapshot()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var original = (await fixture.Store.Get<Evidence>("evidence", "ortho-spec-v3"))!;
        var quality = new QualityService(fixture.Store, fixture.Objects, clock);
        var documents = new EvidenceService(fixture.Store, fixture.Objects, clock);
        var service = Service();
        var input = new FreezePsurCommand("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide");
        var earlier = await service.Freeze(input, SqlFixture.Pms, CaseScenario.Key());
        var earlierBytes = Canonical.Serialize(earlier);
        var change = await quality.OpenChange(new(s.Case.Id, "ortho", original.Id, original.Version,
            "Synthetische Spezifikationsfolgeversion mit zukuenftiger Wirksamkeit fuer PSUR pruefen.",
            "Vor- und Folgeversion muessen am Freeze-Stichtag eindeutig getrennt werden."), SqlFixture.Pms, CaseScenario.Key());
        change = await quality.ApproveChange(change.Id, new(change.Version,
            "Unabhaengig gepruefter Aenderungsauftrag mit nachvollziehbarer Wirksamkeitsgrenze.", null), SqlFixture.Quality, CaseScenario.Key());
        var effective = clock.Now.AddDays(1);
        var revised = await documents.Revise(original.Id, new(original.Version, change.Id, original.Title + " / Revision",
            original.Content + "\nSYNTHETISCH: Neuer kontrollierter Spezifikationsnachtrag ab explizitem Wirksamkeitsdatum.",
            effective, "Vorversion bleibt unveraendert lesbar; Folgeversion wird erst ab dem freigegebenen Datum wirksam."),
            SqlFixture.Quality, CaseScenario.Key());
        var before = await service.Freeze(input, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(original.Version, Assert.Single(before.Snapshot.Documents, d => d.Id == original.Id).Version);
        clock.Now = effective;
        var after = await service.Freeze(input, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(revised.Version, Assert.Single(after.Snapshot.Documents, d => d.Id == original.Id).Version);
        Assert.Equal(revised.Hash, after.Snapshot.Documents.Single(d => d.Id == original.Id).Hash);
        Assert.Equal(earlierBytes, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", earlier.Id)));
        Assert.Equal(original.Version, Assert.Single(earlier.Snapshot.Documents, d => d.Id == original.Id).Version);
        await fixture.Store.Manifest(earlier.Id, kind: "psur");
    }

    private async Task<SafetyCase> SeedCase(string id, int version, string country, DateTimeOffset occurred,
        DateTimeOffset awareness, DateTimeOffset? closed = null)
    {
        var current = new SafetyCase(id, 1, "COH-" + id, "ortho", "Independent synthetic PSUR reference",
            "SYNTHETISCH: unabhaengiger Referenzfall fuer Stichtag, zaehlbare Quellversion und historische Nachvollziehbarkeit.",
            null, "DEMO-LOT", country, country == "IE" ? "EU-IE" : "GB", Seriousness.OtherSerious, occurred,
            awareness, awareness, null, "independent-psur-reference", true, CaseStage.Intake, Disposition.Undetermined,
            [], [], [], [], [], [], [], [], [], [], [], []);
        for (var v = 1; v <= version; v++)
        {
            var at = v == 1 ? awareness : closed ?? clock.Now.AddMinutes(v - version);
            var next = current with { Version = v, Stage = v == version && closed is not null ? CaseStage.Closed :
                v == 1 ? CaseStage.Intake : CaseStage.Monitoring, ClosedAt = v == version ? closed : null };
            current = await fixture.Store.Execute<SafetyCase>(new(id, "case", "ortho", v - 1, CaseScenario.Key(),
                "IndependentPsurReferenceSeed", "Unabhaengige synthetische SQL-Referenz, keine klinische Freigabe.", SqlFixture.Pms,
                next, at), _ => next);
        }
        return current;
    }

    private Task<Exposure> Exposure(string id, string region, DateOnly start, DateOnly end, int count)
    {
        var value = new Exposure(id, 1, "ortho", region, start, end, "DeviceYears", count,
            "Independent deliberately complete synthetic reference", "Coordinator reference v2", true);
        return fixture.Store.Execute<Exposure>(new(id, "exposure", "ortho", 0, CaseScenario.Key(), "IndependentExposureSeed",
            "Expliziter synthetischer Nenner fuer die unabhaengige PSUR-Referenz.", SqlFixture.Quality, value, clock.Now), _ => value);
    }
}
