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
        var profile = (await fixture.Store.Get<ProductGovernance>("productGovernance", "ortho"))!;
        await fixture.Store.Execute<ProductGovernance>(new(profile.Id, "productGovernance", profile.ProductId, profile.Version,
            CaseScenario.Key(), "IndependentPsurSchedule", "Eigener synthetischer Zulassungs- und Zeitplan fuer die unveraenderte Kalenderjahr-Referenz.",
            SqlFixture.Quality, start, clock.Now), value => value! with { Version = value.Version + 1,
                FirstPeriodStart = start, Device = value.Device with { CertificationDate = start }, FirstSubmissionDate = end.AddDays(30) });
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
        var before = await service.Freeze(input with { PreviousReportId = earlier.Id, PreviousReportVersion = earlier.Version,
            RevisionReason = "Wirksamkeitsvergleich vor der freigegebenen Folgeversion als eigene gebundene Fassung." }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(original.Version, Assert.Single(before.Snapshot.Documents, d => d.Id == original.Id).Version);
        Assert.Contains(before.Snapshot.Directory!.Entries, entry => entry.Kind == "evidence" && entry.Source.Id == revised.Id &&
            entry.Source.Version == revised.Version && entry.Selection == "ExcludedNotYetEffective");
        clock.Now = effective;
        var after = await service.Freeze(input with { PreviousReportId = before.Id, PreviousReportVersion = before.Version,
            RevisionReason = "Wirksamkeitsvergleich ab der freigegebenen Folgeversion als neue gebundene Fassung." }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(revised.Version, Assert.Single(after.Snapshot.Documents, d => d.Id == original.Id).Version);
        Assert.Equal(revised.Hash, after.Snapshot.Documents.Single(d => d.Id == original.Id).Hash);
        Assert.Contains(after.Snapshot.Directory!.Entries, entry => entry.Kind == "evidence" && entry.Source.Id == original.Id &&
            entry.Source.Version == original.Version && entry.Selection == "ExcludedSuperseded");
        Assert.Equal(earlierBytes, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", earlier.Id)));
        Assert.Equal(original.Version, Assert.Single(earlier.Snapshot.Documents, d => d.Id == original.Id).Version);
        await fixture.Store.Manifest(earlier.Id, kind: "psur");
    }

    [Fact]
    public async Task T16_ActionStatesUseActualCutoffVersionsAndExplainClosedExclusionsAndLateAddenda()
    {
        var s = new CaseScenario(fixture);
        s.Clock.Now = DateTimeOffset.Parse("2025-11-08T12:00:00Z");
        s.Case = await s.Service.Register(s.Input with { OccurredAt = s.Clock.Now.AddDays(-2),
            AwarenessAt = s.Clock.Now.AddDays(-1) }, SqlFixture.Pms, CaseScenario.Key());
        clock.Now = s.Clock.Now;
        var quality = new QualityService(fixture.Store, fixture.Objects, clock);
        Task<Capa> Open(string title) => quality.OpenCapa(new(s.Case.Id, "ortho", title,
            "Synthetische dokumentierte Untersuchung mit messbarer Prozesskorrektur.",
            "Offene sicherheitsbezogene Prozessabweichung nachvollziehbar untersuchen.",
            SqlFixture.Pms.Id, "Drei dokumentierte Folgepruefungen ohne die festgestellte Abweichung.", clock.Now.AddYears(2)),
            SqlFixture.Pms, CaseScenario.Key());
        var closedBefore = await Open("Vor der Periode abgeschlossene Massnahme");
        var carryOver = await Open("Am Stichtag offene alte Massnahme");
        clock.Now = DateTimeOffset.Parse("2025-11-12T12:00:00Z");
        closedBefore = await quality.Verify(closedBefore.Id, new(closedBefore.Version,
            "Drei unabhaengig dokumentierte Folgepruefungen belegen die Beseitigung der synthetischen Abweichung.", true,
            "Begruendete unabhhaengige Wirksamkeitsbewertung der synthetischen Massnahme."), SqlFixture.Quality, CaseScenario.Key());
        clock.Now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        carryOver = await quality.Verify(carryOver.Id, new(carryOver.Version,
            "Wirksamkeitsnachweis erst nach dem Berichtsende dokumentiert; frueherer offener Status bleibt erhalten.", true,
            "Nachperiodischen Wirksamkeitsnachweis ausdruecklich als spaetere Fortschreibung speichern."), SqlFixture.Quality, CaseScenario.Key());
        var late = await quality.OpenChange(new(s.Case.Id, "ortho", "ortho-risk-v4", 1,
            "Neue synthetische Information als kontrollierten Risikodatei-Nachtrag pruefen.",
            "Erst nach dem Berichtsende angestossene Aenderung darf keinen alten Status fingieren."), SqlFixture.Pms, CaseScenario.Key());
        clock.Now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
        var p = await Service().Freeze(new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide"), SqlFixture.Pms, CaseScenario.Key());
        var action = p.Snapshot.Actions!.Capas.Single(a => a.CurrentSource.Id == carryOver.Id);
        Assert.Equal("OpenCarryOver", action.Selection);
        Assert.Equal("Open", action.StatusAtCutoff);
        Assert.Equal(1, action.CutoffSource!.Version);
        Assert.Equal("Verified", action.CurrentAtFreeze.Status);
        Assert.Equal(2, action.CurrentSource.Version);
        Assert.DoesNotContain(p.Snapshot.Capas, c => c.Id == closedBefore.Id);
        Assert.Contains(p.Snapshot.Directory!.Entries, e => e.Kind == "capa" && e.Source.Id == closedBefore.Id &&
            e.Selection == "ExcludedClosedBeforePeriod");
        var change = p.Snapshot.Actions.Changes.Single(a => a.CurrentSource.Id == late.Id);
        Assert.Equal("PostCutoffAddendum", change.Selection);
        Assert.Equal("NotRecordedAtCutoff", change.StatusAtCutoff);
        Assert.Null(change.CutoffSource);
        Assert.DoesNotContain(p.Findings, f => f.Severity is "Critical" or "Major");
        Assert.Contains("Status am N-Stichtag Open", p.Draft);
        var approved = await Service().Approve(p.Id, new(p.Version,
            "Historische offene Massnahme, nachperiodischer Abschluss und neue Aenderung getrennt medizinisch geprueft."),
            SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("MedicalApproved", approved.Status);
        var forgedActions = p.Snapshot.Actions with { Capas = p.Snapshot.Actions.Capas.Select(a => a.CurrentSource.Id == carryOver.Id
            ? a with { CutoffSource = a.CurrentSource, AtCutoff = a.CurrentAtFreeze, StatusAtCutoff = "Verified" } : a).ToArray() };
        var snapshot = p.Snapshot with { Actions = forgedActions };
        var forged = p with { Version = approved.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot), Findings = [] };
        forged = forged with { ScopeHash = PortfolioRules.PsurScope(forged) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", "ortho", approved.Version, CaseScenario.Key(), "ForgedActionCutoff",
            "Negativer Test: heutige echte Version darf nicht mit altem Datum zum historischen Stand werden.",
            SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_action_cutoff", (await Assert.ThrowsAsync<DomainException>(() => Service().Approve(p.Id,
            new(forged.Version, "Eine echte spaetere Version darf nicht als historisch erledigter Zustand freigegeben werden."),
            SqlFixture.Medical, CaseScenario.Key()))).Code);
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
