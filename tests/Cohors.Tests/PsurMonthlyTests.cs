using Cohors.Domain;

namespace Cohors.Tests;

public sealed class PsurMonthlyTests
{
    private static readonly DateOnly Start = new(2025, 11, 15);
    private static readonly DateOnly End = new(2026, 11, 14);
    private static readonly DateTimeOffset Freeze = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
    private static readonly Actor Reader = new("monthly-reader", "Synthetic reader", [Roles.Pms], ["ortho", "cardio"], true);

    [Fact]
    public void MonthCountsUseUtcEventDatesPartialMonthsAndUniqueFrozenCasesNotSourcesOrAwareness()
    {
        var first = Case("first", "2025-11-14T23:30:00-01:00", "2025-11-15T01:00:00Z") with { Stage = CaseStage.Closed, Version = 4 };
        var cases = new[]
        {
            first with { Sources = [new("one", "one", "Complaint", "Synthetic one", "cohors:synthetic", Canonical.HashText("one"), Freeze, Freeze, "test"),
                new("two", "two", "Complaint", "Synthetic two", "cohors:synthetic", Canonical.HashText("two"), Freeze, Freeze, "test")] },
            Case("january", "2026-02-01T00:30:00+02:00", "2026-02-01T10:00:00Z"),
            Case("last", "2026-11-14T23:59:59Z", "2026-11-14T23:59:59Z"),
            Case("unknown", null, "2026-04-01T12:00:00Z"),
            Case("carry", "2025-10-10T12:00:00Z", "2025-10-11T12:00:00Z"),
            Case("late", "2026-03-10T12:00:00Z", "2026-11-15T00:00:00Z"),
            Case("after", "2026-11-15T00:00:00Z", "2026-11-16T12:00:00Z"),
            Case("no-history", "2026-06-01T12:00:00Z", "2026-06-01T13:00:00Z")
        };
        var history = cases.Where(c => c.Id != "no-history").Select(c =>
        {
            var old = c with { Version = 1, Stage = CaseStage.Investigating };
            return new AggregateRevision("case", c.Id, 1, c.FirstAwarenessAt, Canonical.Serialize(old), Canonical.Hash(old));
        }).ToArray();
        var report = Report(Snapshot(cases, history: history));
        var bytes = Canonical.Serialize(report);
        var view = PsurMonthly.Read(report, "ortho", Reader);
        Assert.Equal("Available", view.Status);
        Assert.Equal("UTC", view.TimeZone);
        Assert.Equal(13, view.Months!.Length);
        Assert.Equal(Start, view.Months[0].From);
        Assert.Equal(End, view.Months[^1].Through);
        Assert.Equal(4, view.Months.Sum(m => m.Cases.Length));
        Assert.Equal(1, view.Months.Sum(m => m.PostCutoffCases.Length));
        Assert.Equal("first", Assert.Single(view.Months[0].Cases).Id);
        Assert.Equal("january", Assert.Single(view.Months.Single(m => m.Month == new DateOnly(2026, 1, 1)).Cases).Id);
        Assert.Empty(view.Months.Single(m => m.Month == new DateOnly(2026, 2, 1)).Cases);
        Assert.Equal("last", Assert.Single(view.Months[^1].Cases).Id);
        Assert.Equal("late", Assert.Single(view.Months.Single(m => m.Month == new DateOnly(2026, 3, 1)).PostCutoffCases).Id);
        Assert.Equal(1, view.Months[0].OpenAtCutoff);
        Assert.Equal(1, view.Months.Single(m => m.Month == new DateOnly(2026, 6, 1)).UnknownStateAtCutoff);
        Assert.Equal("Closed", view.Cases.Single(c => c.Source.Id == "first").StageAtFreeze);
        Assert.Equal("UnknownEventDate", view.Cases.Single(c => c.Source.Id == "unknown").Selection);
        Assert.True(view.Cases.Single(c => c.Source.Id == "carry").InCarryOverNarrative);
        Assert.Equal("BeforePeriod", view.Cases.Single(c => c.Source.Id == "carry").Selection);
        Assert.Equal("AfterPeriod", view.Cases.Single(c => c.Source.Id == "after").Selection);
        Assert.Equal(bytes, Canonical.Serialize(report));
    }

    [Fact]
    public void LaterEventEditsAndNewKnowledgeCannotRewriteFrozenMonthCounts()
    {
        var source = Case("fixed", "2026-01-20T12:00:00Z", "2026-01-21T12:00:00Z");
        var old = Report(Snapshot([source]));
        var oldBytes = Canonical.Serialize(old);
        var expected = Canonical.Serialize(PsurMonthly.Read(old, "ortho", Reader));
        var changed = source with { Version = 2, OccurredAt = DateTimeOffset.Parse("2026-05-01T12:00:00Z") };
        var newer = Report(Snapshot([changed]));
        Assert.Single(PsurMonthly.Read(newer, "ortho", Reader).Months!.Single(m => m.Month.Month == 5).Cases);
        var restored = Canonical.Read<Psur>(oldBytes);
        Assert.Equal(expected, Canonical.Serialize(PsurMonthly.Read(restored, "ortho", Reader)));
        Assert.Empty(PsurMonthly.Read(restored, "ortho", Reader).Months!.Single(m => m.Month.Month == 5).Cases);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("date-basis")]
    [InlineData("duplicate-revision")]
    [InlineData("missing-numerator")]
    [InlineData("missing-source-array")]
    public void MissingOrAmbiguousHistoricalBasisHasNoInventedZeroMonths(string problem)
    {
        var snapshot = Snapshot([Case("source", "2026-01-20T12:00:00Z", "2026-01-21T12:00:00Z")]);
        snapshot = problem switch
        {
            "legacy" => snapshot with { SourceManifest = null },
            "date-basis" => snapshot with { DateBasis = "SystemReceivedAt" },
            "missing-source-array" => snapshot with { SourceManifest = snapshot.SourceManifest! with { Cases = null! } },
            "duplicate-revision" => snapshot with { SourceManifest = snapshot.SourceManifest! with { Cases =
                [snapshot.SourceManifest!.Cases[0], snapshot.SourceManifest.Cases[0] with
                    { Reference = snapshot.SourceManifest.Cases[0].Reference with { Version = 2 } }] } },
            _ => snapshot with { Periods = snapshot.Periods.Select(p => p with
                { Regions = p.Regions.Select(r => r with { NumeratorSources = null }).ToArray() }).ToArray() }
        };
        var report = Report(snapshot);
        var before = Canonical.Serialize(report);
        var view = PsurMonthly.Read(report, "ortho", Reader);
        Assert.Equal("Unavailable", view.Status);
        Assert.Null(view.Months);
        Assert.Contains(view.Limitations, message => message.Contains("kein Live-Fallback", StringComparison.Ordinal));
        Assert.Equal(before, Canonical.Serialize(report));
    }

    [Fact]
    public void GroupMembershipAndPermissionsAreFrozenAndFamiliesAreNotPooled()
    {
        var lead = Snapshot([Case("lead", "2026-01-20T12:00:00Z", "2026-01-21T12:00:00Z")]);
        var second = Snapshot([Case("member", "2026-02-20T12:00:00Z", "2026-02-21T12:00:00Z") with { ProductId = "cardio" }], "cardio");
        var governance = WorkspaceTestData.Psur("monthly-group", new(2026, 12, 1)).Snapshot.Series!.Governance with { FirstPeriodStart = Start };
        var secondProfile = governance with { Id = "cardio", ProductId = "cardio", Device = governance.Device with { BasicUdi = "DEMO-CARDIO" } };
        var definition = new PsurGroupDefinition("monthly-group", 1, "ortho", "DEMO-PSUR-GROUP-MONTHLY",
            [PsurGroupRules.Product(governance), PsurGroupRules.Product(secondProfile)], Start,
            WorkspaceTestData.Reason, WorkspaceTestData.Reason, Freeze);
        var grouped = lead with { Group = new(PsurGroupRules.Contract,
            new(definition.Id, definition.Version, Canonical.Hash(definition)), definition,
            [new(PsurGroupRules.Product(governance), governance, lead), new(PsurGroupRules.Product(secondProfile), secondProfile, second)],
            "Separate family counts; no pooled incidence.") };
        var report = Report(grouped);
        Assert.Empty(PsurGroupRules.Check(grouped));
        var member = PsurMonthly.Read(report, "cardio", Reader);
        Assert.Equal(["ortho", "cardio"], member.FrozenMembers);
        Assert.Equal(1, member.GroupSource!.Version);
        Assert.Equal(1, member.Months!.Sum(m => m.Cases.Length));
        Assert.Equal("member", Assert.Single(member.Cases).Source.Id);
        Assert.Equal("product_scope", Assert.Throws<DomainException>(() =>
            PsurMonthly.Read(report, "ortho", Reader with { Products = ["ortho"] })).Code);
        Assert.Equal("psur_snapshot_hash", Assert.Throws<DomainException>(() =>
            PsurMonthly.Read(report with { Snapshot = grouped with { Group = null } }, "ortho", Reader)).Code);
        Assert.Equal("psur_monthly_scope", Assert.Throws<DomainException>(() =>
            PsurMonthly.Read(Report(lead), "cardio", Reader)).Code);
    }

    [Fact]
    public void RegionalCountsExcludeGreatBritainWithoutAddingTheOverlappingWorldwidePopulation()
    {
        var gb = Case("gb", "2026-01-20T12:00:00Z", "2026-01-21T12:00:00Z") with { Country = "GB", Jurisdiction = "GB" };
        var ni = gb with { Id = "ni", Jurisdiction = "NI" };
        var subset = Snapshot([gb, ni], region: "EEA-Turkey-NI");
        var view = PsurMonthly.Read(Report(subset), "ortho", Reader);
        Assert.Equal("Available", view.Status);
        Assert.Equal("ni", Assert.Single(view.Cases).Source.Id);
        Assert.Equal(1, view.Months!.Sum(m => m.Cases.Length));
        Assert.Equal(2, subset.Periods[0].Regions.Single(r => r.Region == "Worldwide").NumeratorSources!.Length);
    }

    private static SafetyCase Case(string id, string? occurred, string awareness) =>
        WorkspaceTestData.Case(id, Freeze) with { OccurredAt = occurred is null ? null : DateTimeOffset.Parse(occurred),
            FirstAwarenessAt = DateTimeOffset.Parse(awareness) };

    private static PsurSnapshot Snapshot(SafetyCase[] cases, string product = "ortho", string region = "Worldwide",
        AggregateRevision[]? history = null) =>
        PortfolioRules.Snapshot(new(product, product, Start, End, region, cases,
            [new("world", 1, product, "Worldwide", Start, End, "DeviceYears", 100, "Synthetic", "Synthetic", true),
             new("subset", 1, product, "EEA-Turkey-NI", Start, End, "DeviceYears", 50, "Synthetic", "Synthetic", true)],
            [], [], [], [], Freeze, history));

    private static Psur Report(PsurSnapshot snapshot) =>
        new("monthly-report", 1, snapshot.ProductId, snapshot, Canonical.Hash(snapshot), "Synthetic draft", "synthetic-scope",
            "Draft", [], null, true, [], AgentMode.Replay, null, null);
}
