using Cohors.Domain;

namespace Cohors.Tests;

public sealed class PsurMonthlyTests
{
    private static readonly DateOnly Start = new(2025, 11, 15);
    private static readonly DateOnly End = new(2026, 11, 14);
    private static readonly DateTimeOffset FreezeAt = DateTimeOffset.Parse("2026-11-16T15:00:00Z");

    [Fact]
    public void MonthsUseUtcEventsUniqueCasesAndHistoricalStatusesNotAwarenessOrFreezeState()
    {
        var source = new SourceReference("source-a", "external-a", "Complaint", "Synthetic source",
            "synthetic://source", new string('a', 64), FreezeAt, FreezeAt, "same incident");
        var early = Case("start", "2025-11-15T00:00:00Z", "2025-11-15T01:00:00Z") with
        { Sources = [source, source with { Id = "source-b", ExternalId = "external-b" }] };
        var closedAfterCutoff = early with { Version = 2, Stage = CaseStage.Closed, ClosedAt = FreezeAt };
        var lateIntake = Case("late-intake", "2026-11-14T23:00:00Z", "2026-11-14T23:59:00Z") with { SystemReceivedAt = FreezeAt };
        SafetyCase[] cases = [
            closedAfterCutoff,
            Case("before", "2025-11-15T00:30:00+01:00", "2025-11-15T02:00:00Z"),
            Case("december", "2026-01-01T00:30:00+01:00", "2026-01-01T02:00:00Z"),
            lateIntake,
            Case("addendum", "2026-10-31T12:00:00Z", "2026-11-15T00:00:00Z"),
            Case("unknown", null, "2026-11-01T12:00:00Z"),
            Case("after", "2026-11-15T12:00:00Z", "2026-11-15T13:00:00Z"),
            Case("other-member", "2026-10-01T12:00:00Z", "2026-10-02T12:00:00Z") with { ProductId = "cardio" },
            Case("other-region", "2026-10-01T12:00:00Z", "2026-10-02T12:00:00Z") with { Country = "GB", Jurisdiction = "GB" }
        ];
        var snapshot = Snapshot(cases, [new("case", early.Id, 1, early.FirstAwarenessAt, Canonical.Serialize(early), Canonical.Hash(early))]);
        var report = Report(snapshot);
        var original = Canonical.Serialize(report);
        var months = PsurMonthlyCounts.Read(report, "ortho");
        Assert.Equal("Available", months.Status);
        Assert.Equal(13, months.Months.Length);
        Assert.Equal(Start, months.Months[0].From);
        Assert.Equal(End, months.Months[^1].Through);
        Assert.Equal(3, months.Months.Sum(m => m.Cases.Length));
        var first = months.Months.Single(m => m.Month == "2025-11");
        Assert.Equal(PortfolioRules.Reference(closedAfterCutoff), Assert.Single(first.Cases));
        Assert.Equal(first.Cases, first.OpenAtCutoff);
        Assert.Empty(first.UnknownStatusAtCutoff);
        Assert.Equal("december", Assert.Single(months.Months.Single(m => m.Month == "2025-12").Cases).Id);
        var last = months.Months[^1];
        Assert.Equal(lateIntake.Id, Assert.Single(last.Cases).Id);
        Assert.Equal(last.Cases, last.UnknownStatusAtCutoff);
        Assert.Empty(last.OpenAtCutoff);
        Assert.Empty(months.Months.Single(m => m.Month == "2026-10").Cases);
        Assert.Equal("unknown", Assert.Single(months.Exclusions.Single(e => e.Kind == "UnknownEventDate").Cases).Id);
        Assert.Equal("addendum", Assert.Single(months.Exclusions.Single(e => e.Kind == "PostCutoffAddendum").Cases).Id);
        Assert.Contains(months.Exclusions.Single(e => e.Kind == "CarryOver").Cases, c => c.Id == "unknown");
        Assert.Equal(["after", "before"], months.Exclusions.Single(e => e.Kind == "OutsidePeriod").Cases.Select(c => c.Id));
        Assert.DoesNotContain(months.Sources, c => c.Reference.Id is "other-member" or "other-region");
        Assert.Equal(PsurSources.EndExclusive(End), months.KnowledgeCutoffExclusiveUtc);
        Assert.Equal(FreezeAt, months.FrozenAt);
        Assert.Equal(original, Canonical.Serialize(report));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedOrDuplicateHistoricalSourcesStayUnavailableWithoutReconstruction(bool duplicate)
    {
        var snapshot = Snapshot([Case("only", "2026-11-01T10:00:00Z", "2026-11-02T10:00:00Z")]);
        snapshot = snapshot with { SourceManifest = duplicate
            ? snapshot.SourceManifest! with { Cases = [.. snapshot.SourceManifest!.Cases,
                snapshot.SourceManifest.Cases[0] with { Reference = snapshot.SourceManifest.Cases[0].Reference with { Version = 2 } }] }
            : null };
        var report = Report(snapshot);
        var original = Canonical.Serialize(report);
        var months = PsurMonthlyCounts.Read(report, "ortho");
        Assert.Equal("Unavailable", months.Status);
        Assert.NotEmpty(months.Issues);
        Assert.Empty(months.Months);
        Assert.Equal(original, Canonical.Serialize(report));
        Assert.Equal("psur_hash", Assert.Throws<DomainException>(() =>
            PsurMonthlyCounts.Read(report with { SnapshotHash = new string('0', 64) }, "ortho")).Code);
    }

    private static SafetyCase Case(string id, string? occurred, string awareness) =>
        new(id, 1, "COH-" + id, "ortho", "Synthetic monthly case", "SYNTHETISCH: keine klinische Vollstaendigkeitsbehauptung.",
            null, null, "IE", "EU-IE", Seriousness.OtherSerious,
            occurred is null ? null : DateTimeOffset.Parse(occurred), DateTimeOffset.Parse(awareness), DateTimeOffset.Parse(awareness),
            null, "monthly", true, CaseStage.Intake, Disposition.Undetermined,
            [], [], [], [], [], [], [], [], [], [], [], []);

    private static PsurSnapshot Snapshot(SafetyCase[] cases, AggregateRevision[]? history = null) =>
        PortfolioRules.Snapshot(new("ortho", "ortho", Start, End, "EEA-Turkey-NI", cases,
            [new("exposure", 1, "ortho", "EEA-Turkey-NI", Start, End, "Implants", 100, "Synthetic", "Monthly test", true)],
            [], [], [], [], FreezeAt, history));

    private static Psur Report(PsurSnapshot snapshot)
    {
        var report = new Psur("monthly", 1, "ortho", snapshot, Canonical.Hash(snapshot),
            "SYNTHETISCH: gebundener historischer Monatsdatenstand.", "", "Draft", [], null, true, [], AgentMode.Replay, null, null);
        return report with { ScopeHash = PortfolioRules.PsurScope(report) };
    }
}
