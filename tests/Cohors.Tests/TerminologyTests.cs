using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

[Collection("SQL pilot")]
public sealed class TerminologyTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private AnalyticsService Analytics() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    private static FreezePsurCommand Period() => new("ortho", "ortho", new(2025, 11, 15), new(2026, 11, 14), "Worldwide");
    private RecordCodingCommand Input(SafetyCase c, string[] codes, DateOnly? historical = null) =>
        new(c.Version, fixture.Policies.Terminology.Reference.ReleaseId, codes,
            c.Evidence.Where(e => e.Kind != "Coding").Select(e => e.Id).ToArray(),
            "SYNTHETISCHE TESTKODIERUNG: Originalhinweise und Anwendbarkeit menschlich geprueft; keine klinische Kausalitaet oder reale Todesfallfeststellung.",
            historical);

    [Fact]
    public void T16_FdaMappingMembershipIsSeparateFromRetirementAndAmbiguousHierarchy()
    {
        var catalog = fixture.Policies.Terminology;
        Assert.Equal("Death", catalog.Select("F02", null).Source.Term);
        Assert.NotEqual("Death", catalog.Select("F01", null).Source.Term);
        Assert.Equal("terminology_unknown", Assert.Throws<DomainException>(() => catalog.Select("A999999", null)).Code);
        Assert.Equal("terminology_unknown", Assert.Throws<DomainException>(() => catalog.Select("F30 ", null)).Code);
        Assert.Equal("F30", catalog.Select("F30", null).Source.Code);
        foreach (var code in new[] { "D1401", "D1402" })
        {
            Assert.Equal("terminology_retired", Assert.Throws<DomainException>(() => catalog.Select(code, null)).Code);
            Assert.False(catalog.Select(code, new(2025, 1, 1)).Source.CurrentSelectable);
        }
        foreach (var code in new[] { "C1901", "C1902" })
        {
            var term = catalog.Select(code, null);
            Assert.True(term.Source.CurrentSelectable);
            Assert.False(term.Source.AutomaticAggregationEligible);
            Assert.Null(term.Source.Level);
            Assert.Null(term.GroupCode);
            Assert.Equal(3, term.Source.SourceLevelColumn);
            Assert.Equal(2, term.Source.Hierarchy.Length);
        }
    }

    [Fact]
    public async Task T16_HumanCodingInvalidatesApprovalAndFreezesUniqueGroupAndExplicitDeathSources()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        await s.Assess();
        await s.Prepare();
        await s.Approve();
        var old = await Analytics().Freeze(Period(), SqlFixture.Pms, CaseScenario.Key());
        var oldBytes = Canonical.Serialize(old);
        Assert.Empty(old.Snapshot.CodingTables!.Single(t => t.Period == "N" && t.Region == "Worldwide").ExplicitDeathCases);
        var request = Input(s.Case, ["A010101", "A010102", "F02"]);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.RecordCoding(s.Case.Id, request, SqlFixture.Pms, CaseScenario.Key()))).Status);
        s.Case = await s.Service.RecordCoding(s.Case.Id, request, SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal(ReportState.Superseded, s.Case.CurrentReport!.State);
        Assert.NotEqual(s.Case.CurrentAssessment!.EvidenceHash, Canonical.Hash(s.Case.Evidence));
        Assert.Equal("F02", s.Case.Coding!.Codes.Single(c => c.Source.Annex == "F").Source.Code);
        var p = await Analytics().Freeze(Period(), SqlFixture.Pms, CaseScenario.Key());
        var table = p.Snapshot.CodingTables!.Single(t => t.Period == "N" && t.Region == "Worldwide");
        var group = Assert.Single(table.Rows, r => r.Annex == "A");
        Assert.Equal("A0101", group.Code);
        Assert.Equal("Patient-Device Incompatibility", group.Term);
        Assert.Equal(["A010101", "A010102"], group.OriginalCodes);
        Assert.Equal(PortfolioRules.Reference(s.Case), Assert.Single(group.Cases));
        Assert.Equal(PortfolioRules.Reference(s.Case), Assert.Single(table.ExplicitDeathCases));
        Assert.Contains("[metric:N:Worldwide:deathCoded=1]", p.Draft);
        Assert.Equal(fixture.Policies.Terminology.Hash, p.Snapshot.SourceManifest!.Cases.Single().Coding!.ReferenceHash);
        Assert.DoesNotContain(p.Findings, f => f.Severity is "Critical" or "Major");
        Assert.Equal(oldBytes, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", old.Id)));
        await fixture.Store.Manifest(p.Id, kind: "psur");
    }

    [Fact]
    public async Task T16_HistoricalCodesStaySeparateAndUnresolvedCurrentHierarchyBlocksApproval()
    {
        var historical = new CaseScenario(fixture);
        historical.Case = await historical.Service.Register(historical.Input with {
            OccurredAt = DateTimeOffset.Parse("2025-06-01T00:00:00Z"),
            AwarenessAt = DateTimeOffset.Parse("2025-06-02T00:00:00Z")
        }, SqlFixture.Pms, CaseScenario.Key());
        historical.Case = await historical.Service.RecordCoding(historical.Case.Id,
            Input(historical.Case, ["D1401"], new(2025, 7, 1)), SqlFixture.Medical, CaseScenario.Key());
        var historicalBytes = Canonical.Serialize(historical.Case.Coding);
        var current = new CaseScenario(fixture);
        await current.Create();
        current.Case = await current.Service.RecordCoding(current.Case.Id,
            Input(current.Case, ["C1901", "F01"]), SqlFixture.Medical, CaseScenario.Key());
        var p = await Analytics().Freeze(Period(), SqlFixture.Pms, CaseScenario.Key());
        var currentTable = p.Snapshot.CodingTables!.Single(t => t.Period == "N" && t.Region == "Worldwide");
        var blocked = Assert.Single(currentTable.Rows, r => r.Annex == "C");
        Assert.Equal("C1901", blocked.Code);
        Assert.Equal("BlockedHierarchy", blocked.Status);
        Assert.Empty(currentTable.ExplicitDeathCases);
        var previous = p.Snapshot.CodingTables!.Single(t => t.Period == "N2" && t.Region == "Worldwide");
        Assert.Contains(previous.Rows, r => r.Historical && r.OriginalCodes.Contains("D1401"));
        Assert.Equal(historicalBytes, Canonical.Serialize(p.Snapshot.SourceManifest!.Cases
            .Single(c => c.Reference.Id == historical.Case.Id).Coding));
        Assert.Contains(p.Findings, f => f.Code == "psur_coding_hierarchy" && f.Severity == "Major");
        var error = await Assert.ThrowsAsync<DomainException>(() => Analytics().Approve(p.Id, new(p.Version,
            "Eine widerspruechliche Hierarchie darf nicht durch eine freigebende Unterschrift repariert werden."),
            SqlFixture.Medical, CaseScenario.Key()));
        Assert.Equal("psur_assurance", error.Code);
    }

    [Fact]
    public async Task T16_SelfConsistentForgedSnapshotCodingCannotReplaceItsActualSqlSource()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        s.Case = await s.Service.RecordCoding(s.Case.Id, Input(s.Case, ["F01"]), SqlFixture.Medical, CaseScenario.Key());
        var p = await Analytics().Freeze(Period(), SqlFixture.Pms, CaseScenario.Key());
        var altered = s.Case.Coding! with { Codes = [fixture.Policies.Terminology.Select("F02", null)] };
        var sources = p.Snapshot.SourceManifest! with {
            Cases = p.Snapshot.SourceManifest!.Cases.Select(c => c with { Coding = altered }).ToArray()
        };
        var snapshot = p.Snapshot with { SourceManifest = sources, CodingTables = CodingRules.Tables(p.Snapshot.Periods, sources.Cases) };
        var forged = p with { Version = p.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot), Findings = [] };
        forged = forged with { ScopeHash = PortfolioRules.PsurScope(forged) };
        await fixture.Store.Execute<Psur>(new(p.Id, "psur", "ortho", p.Version, CaseScenario.Key(), "ForgedCodingFixture",
            "Negativer Test: konsistente eigene Hashes ersetzen keinen originalen SQL-Quellinhalt.", SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_source_content", (await Assert.ThrowsAsync<DomainException>(() => Analytics().Approve(p.Id,
            new(forged.Version, "Vorgebliche F02-Kodierung muss gegen die tatsaechlich gebundene F01-Fallversion geprueft werden."),
            SqlFixture.Medical, CaseScenario.Key()))).Code);
    }
}
