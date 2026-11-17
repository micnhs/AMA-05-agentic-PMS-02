using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class WorkspaceReadRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    private static readonly TimeZoneInfo Berlin = WorkspaceDates.Zone("Europe/Berlin");
    private static readonly Actor Actor = SqlFixture.Pms with { Products = ["ortho"] };

    [Fact]
    public void CohortUsesCaseIntakeNotAwarenessAndNeverInfersRiskOrDeath()
    {
        var c = WorkspaceTestData.Case("inside", Now) with
        {
            FirstAwarenessAt = Now.AddYears(-1), Seriousness = Seriousness.DeathOrSeriousDeterioration,
            Description = "SYNTHETISCH: unbestaetigte Hypothese Tod; daraus keine neue Klassifikation ableiten."
        };
        var outside = c with { Id = "outside", FirstAwarenessAt = Now, SystemReceivedAt = Now.AddMonths(-1) };
        var foreign = c with { Id = "foreign", ProductId = "cardio" };
        var assessed = WorkspaceTestData.Assessed(c with { Id = "assessed" });
        var view = WorkspaceReadService.ProjectCockpit([new(c, 0, 0, null), new(outside, 0, 0, null),
            new(foreign, 0, 0, null), new(assessed, 0, 0, null)], Actor, null, new(2026, 10, 25), new(2026, 10, 25), Berlin, Now);
        Assert.Equal(2, view.TotalCases);
        Assert.Equal(2, Assert.Single(view.Intake).Count);
        Assert.Equal(2, view.ProcessSteps.Sum(p => p.Count));
        Assert.False(view.RiskGroupsAvailable);
        Assert.All(view.RiskGroups, r => Assert.Equal(1, r.Count));
        Assert.All(view.Cases, r => Assert.Equal("Unknown", r.Outcome.Code));
        Assert.Equal("report", view.Cases.Single(r => r.Id == assessed.Id).CurrentStep);
        Assert.Equal("Intake", view.Cases.Single(r => r.Id == assessed.Id).Stage);
        Assert.DoesNotContain(view.Cases, r => r.ProductId == "cardio");
    }

    [Fact]
    public void KnownProcessDrilldownSurvivesItsLastCaseMovingAndAnEmptyAuthorizedCohort()
    {
        var c = WorkspaceTestData.Case("moving", Now);
        CockpitSnapshot Read(CaseProcessInput[] cases, string focus) => WorkspaceReadService.ProjectCockpit(cases,
            Actor, null, new(2026, 10, 25), new(2026, 10, 25), Berlin, Now, focus);
        Assert.Equal(1, Read([new(c, 0, 0, null)], "process:analysis").MatchingCases);

        var assessed = WorkspaceTestData.Assessed(c);
        var moved = Read([new(assessed, 0, 0, null)], "process:analysis");
        Assert.Equal(1, moved.TotalCases);
        Assert.Equal(0, moved.MatchingCases);
        Assert.Empty(moved.Cases);
        Assert.Equal(0, moved.ProcessSteps.Single(p => p.Key == "analysis").Count);
        Assert.Equal(0, Read([new(assessed, 0, 0, null)], "process:medical").MatchingCases);
        var empty = Read([], "process:medical");
        Assert.Equal(0, empty.TotalCases);
        Assert.Empty(empty.Cases);
        Assert.All(empty.ProcessSteps, step => Assert.Equal(0, step.Count));
        Assert.Equal("workspace_focus", Assert.Throws<DomainException>(() => Read([], "process:not-a-step")).Code);
    }

    [Fact]
    public void HumanReplacementAndEvidenceStalenessUseSharedProcessRules()
    {
        var c = WorkspaceTestData.Assessed(WorkspaceTestData.Case("human", Now), "Human");
        c = c with { AgentRuns = [new("failed", AgentRole.CausalityRisk, AgentMode.Replay, "Failed", Now, Now,
            "replay", "test", "", null, [], [], "test-failure", 1, null)] };
        var replaced = new CaseProcessInput(c, 4, 3, null);
        var stillFailed = new CaseProcessInput(c with { Id = "failed-new" }, 4, 5, null);
        var stale = new CaseProcessInput(c with { Id = "stale", Evidence = [WorkspaceTestData.Evidence(Now)] }, 4, 3, null);
        var view = WorkspaceReadService.ProjectCockpit([replaced, stillFailed, stale], Actor, null,
            new(2026, 10, 25), new(2026, 10, 25), Berlin, Now);
        Assert.Equal("HumanDraft", view.Cases.Single(r => r.Id == "human").ReviewState);
        Assert.Equal(2, view.ReviewStates.Single(r => r.Key == "Stale").Count);
        foreach (var input in new[] { replaced, stillFailed, stale })
            Assert.Equal(CaseService.ProjectProcess(input).CurrentStep, view.Cases.Single(r => r.Id == input.Case.Id).CurrentStep);
    }

    [Fact]
    public void SafetyAndPrimaryMarkersCountAsOneObligationAndCompletedOnesDoNotReopen()
    {
        var c = WorkspaceTestData.Case("obligations", Now) with
        {
            Deadlines =
            [
                WorkspaceTestData.Deadline("soon", Now.AddDays(1), Now.AddHours(-1)),
                WorkspaceTestData.Deadline("late", Now.AddHours(-1), Now.AddHours(-13)),
                WorkspaceTestData.Deadline("done", Now.AddDays(-1), Now.AddDays(-2)) with { Status = "SimulatedReported" }
            ],
            FollowUps = [new("follow", "EU-DE", "source-hash", 2, "DEMO-RECEIPT",
                new(new(2026, 10, 28), Now.AddDays(3), Now.AddDays(2), "Europe/Berlin", "test", "human-promised-mir-date-1"),
                "Open", Now.AddDays(-1), null, null)]
        };
        var original = Canonical.Serialize(c);
        var cockpit = WorkspaceReadService.ProjectCockpit([new(c, 0, 0, null)], Actor, null,
            new(2026, 10, 25), new(2026, 10, 25), Berlin, Now);
        Assert.Equal(3, cockpit.Kpis.OpenObligations);
        Assert.Equal(2, cockpit.Kpis.UpcomingObligations);
        Assert.Equal(1, cockpit.Kpis.OverdueObligations);
        var calendar = WorkspaceReadService.ProjectCalendar(new([WorkspaceTestData.CalendarCase(c)], [], []),
            Actor, null, new(2026, 10, 1), Berlin, Now);
        Assert.Equal(8, calendar.TotalEvents);
        Assert.Equal(4, calendar.Obligations);
        Assert.Equal(3, calendar.OpenObligations);
        Assert.Equal(1, calendar.OverduePrimaryObligations);
        Assert.Equal(2, calendar.Events.Count(e => e.ObligationId.EndsWith(":done", StringComparison.Ordinal) && !e.IsOpen));
        Assert.Equal(original, Canonical.Serialize(c));
    }

    [Theory]
    [InlineData("2026-03-29", 23)]
    [InlineData("2026-10-25", 25)]
    public void DisplayDayBoundariesUseTheActualDstDayLength(string day, int hours)
    {
        var date = DateOnly.Parse(day);
        var start = WorkspaceDates.Start(date, Berlin);
        var end = WorkspaceDates.Start(date.AddDays(1), Berlin);
        Assert.Equal(hours, (end - start).TotalHours);
        Assert.Equal(date, WorkspaceDates.Day(start, Berlin));
        Assert.Equal(date, WorkspaceDates.Day(end.AddTicks(-1), Berlin));
        Assert.Equal(date.AddDays(1), WorkspaceDates.Day(end, Berlin));
    }

    [Fact]
    public void CalendarMonthBoundaryChangesForInstantsButNeverForDateOnlyPsurSchedules()
    {
        var c = WorkspaceTestData.Case("edge", Now) with { Deadlines =
            [WorkspaceTestData.Deadline("edge", DateTimeOffset.Parse("2026-10-31T23:30:00Z"), DateTimeOffset.Parse("2026-10-31T22:30:00Z"))] };
        var p = WorkspaceTestData.Psur("report", new(2026, 11, 1));
        var sources = new CalendarSources([WorkspaceTestData.CalendarCase(c)], [p], []);
        var berlin = WorkspaceReadService.ProjectCalendar(sources, Actor, null, new(2026, 11, 1), Berlin, Now);
        var utc = WorkspaceReadService.ProjectCalendar(sources, Actor, null, new(2026, 11, 1), WorkspaceDates.Zone("UTC"), Now);
        Assert.Equal(2, berlin.TotalEvents);
        Assert.Equal(1, utc.TotalEvents);
        var date = Assert.Single(utc.Events);
        Assert.Equal(new DateOnly(2026, 11, 1), date.DueDate);
        Assert.Null(date.DueAt);
        Assert.Null(date.OriginalTimeZone);
        Assert.Equal(date.DisplayDate, berlin.Events.Single(e => e.Kind == "PsurSubmission").DisplayDate);
        Assert.Equal(new DateOnly(2026, 11, 1), berlin.Events.Single(e => e.Kind == "LegalDue").DisplayDate);
        Assert.Equal("Europe/Berlin", berlin.Events.Single(e => e.Kind == "LegalDue").OriginalTimeZone);
    }

    [Fact]
    public void PsurVersionsAndBsRoundsRemainDistinctAndWholeProductScopeIsRequired()
    {
        var old = WorkspaceTestData.Psur("old", new(2026, 10, 26));
        var next = old with { Id = "next", Snapshot = old.Snapshot with { Series = old.Snapshot.Series! with { ReportVersion = 2 } } };
        var wider = WorkspaceTestData.Psur("wider", new(2026, 10, 27)) with { Snapshot = old.Snapshot with
            { RegulatoryHistory = new(PsurRegulatoryRules.Contract, ["ortho", "cardio"], Now, [], "Synthetic wider history") } };
        var review = WorkspaceTestData.Review(old, Now.AddDays(2));
        var answered = review with { Id = "answered", Status = "ReplySubmitted",
            Submissions = [new(1, review.Questions[0], new(review.OriginalReport, "Synthetic response", [], [], "Synthetic no action"),
                "scope", [], "DEMO-REPLY", Now)] };
        var forbidden = review with { Id = "forbidden", Products = ["ortho", "cardio"],
            OriginalReport = review.OriginalReport with { Products = ["ortho", "cardio"] } };
        var view = WorkspaceReadService.ProjectCalendar(new([], [old, next, wider], [review, answered, forbidden]),
            Actor, null, new(2026, 10, 1), Berlin, Now);
        Assert.Equal(4, view.TotalEvents);
        Assert.Equal(2, view.OpenObligations);
        Assert.False(view.Events.Single(e => e.EntityId == "old").IsCurrent);
        Assert.True(view.Events.Single(e => e.EntityId == "next").IsOpen);
        Assert.False(view.Events.Single(e => e.EntityId == "answered").IsOpen);
        Assert.DoesNotContain(view.Events, e => e.EntityId is "wider" or "forbidden");
        var bs = view.Events.Single(e => e.EntityId == review.Id);
        Assert.Equal("UTC", bs.OriginalTimeZone);
        Assert.Equal(1, bs.Round);
        Assert.Equal(old.Id, bs.PsurId);
    }

    [Fact]
    public void PagedDrilldownsAndInvalidProductOrRangeNeverReturnOtherData()
    {
        var cases = Enumerable.Range(0, 27).Select(i => new CaseProcessInput(WorkspaceTestData.Case($"case-{i:D2}", Now), 0, 0, null)).ToArray();
        var page = WorkspaceReadService.ProjectCockpit(cases, Actor, "ortho", new(2026, 10, 25), new(2026, 10, 25), Berlin, Now, "process:analysis", 25);
        Assert.Equal(27, page.MatchingCases);
        Assert.Equal(2, page.Cases.Length);
        Assert.Equal("product_scope", Assert.Throws<DomainException>(() =>
            WorkspaceReadService.ProjectCockpit(cases, Actor, "cardio", new(2026, 10, 25), new(2026, 10, 25), Berlin, Now)).Code);
        Assert.Equal("workspace_focus", Assert.Throws<DomainException>(() =>
            WorkspaceReadService.ProjectCockpit(cases, Actor, null, new(2026, 10, 25), new(2026, 10, 25), Berlin, Now, "risk:high")).Code);
        Assert.Equal("workspace_period", Assert.Throws<DomainException>(() => WorkspaceDates.RequirePeriod(new(2026, 1, 1), new(2027, 1, 2))).Code);
    }

    [Fact]
    public void CorrespondenceUsesItsOriginalLedgerRevisionAndAllHistoricalProductsEvenWhenTheProjectionLags()
    {
        var source = WorkspaceTestData.Correspondence(Now, Now.AddHours(-1).ToOffset(TimeSpan.FromHours(2)));
        var original = Canonical.Hash(source);
        var full = Actor with { Products = ["ortho", "cardio"] };
        var sources = new CalendarSources([], [], [], [source]);
        var calendar = WorkspaceReadService.ProjectCalendar(sources, full, "cardio", new(2026, 10, 1), Berlin, Now);
        var item = Assert.Single(calendar.Events);
        Assert.True(item.IsOpen);
        Assert.True(item.IsCurrent);
        Assert.Equal("Overdue", item.Timing);
        Assert.Equal(source.Inbox.Original!.DueAt, item.DueAt);
        Assert.Equal(source.Inbox.Original.DueAt!.Value.Offset, item.DueAt!.Value.Offset);
        Assert.Null(item.OriginalTimeZone);
        Assert.Equal(source.Entry.ApprovedDossier, item.Correspondence!.ApprovedDossier);
        Assert.Equal(1, item.Correspondence.DossierRevision);
        Assert.Equal(2, source.CurrentDossier.Revision);
        Assert.Empty(source.CurrentDossier.Correspondence);
        Assert.Equal(source.Correspondence.OriginalHash, item.Correspondence.OriginalHash);
        Assert.Equal(source.Entry.Attempt.PackageHash, item.Correspondence.PackageHash);
        Assert.Equal(Roles.Regulatory, item.Correspondence.OwnerRole);
        Assert.Equal(new[] { "cardio", "ortho" }, item.Products);
        Assert.Equal(1, calendar.OverduePrimaryObligations);
        Assert.Empty(WorkspaceReadService.ProjectCalendar(sources, Actor, null, new(2026, 10, 1), Berlin, Now).Events);
        Assert.Equal(original, Canonical.Hash(source));
    }

    [Fact]
    public void MissingAndConflictingResponseSchedulesAreVisibleWithoutInventedDatesOrUndatedCommentObligations()
    {
        var full = Actor with { Products = ["ortho", "cardio"] };
        CalendarSnapshot Read(EudamedCorrespondenceSource source) =>
            WorkspaceReadService.ProjectCalendar(new([], [], [], [source]), full, null, new(2027, 1, 1), Berlin, Now);
        var missing = WorkspaceTestData.Correspondence(Now, null);
        var conflict = WorkspaceTestData.Correspondence(Now, Now.AddDays(1));
        var changed = conflict.Correspondence with { DueAt = Now.AddDays(2) };
        var entry = conflict.Entry with { Correspondence = [changed] };
        conflict = conflict with { Correspondence = changed, Entry = entry, Ledger = conflict.Ledger with { Entries = [entry] } };
        foreach (var source in new[] { missing, conflict })
        {
            var calendar = Read(source);
            Assert.Empty(calendar.Events);
            var clarification = Assert.Single(calendar.UnscheduledEvents);
            Assert.Null(clarification.DueAt);
            Assert.Null(clarification.DueDate);
            Assert.Null(clarification.DisplayDate);
            Assert.True(clarification.IsOpen);
            Assert.Equal("NeedsClarification", clarification.Timing);
            var alert = Assert.IsType<EudamedCorrespondenceAlert>(EudamedDeadlineRules.Alert(
                Assert.IsType<EudamedCorrespondenceDeadline>(EudamedDeadlineRules.Project(source)), Now));
            Assert.Equal("ScheduleClarification", alert.Code);
            Assert.Equal(Roles.Regulatory, alert.OwnerRole);
        }
        foreach (var kind in new[] { "Comment", "Evaluation" })
        {
            var calendar = Read(WorkspaceTestData.Correspondence(Now, null, kind));
            Assert.Empty(calendar.Events);
            Assert.Empty(calendar.UnscheduledEvents);
        }
        var scheduled = Assert.IsType<EudamedCorrespondenceDeadline>(
            EudamedDeadlineRules.Project(WorkspaceTestData.Correspondence(Now, Now.AddHours(1))));
        Assert.Null(EudamedDeadlineRules.Alert(scheduled, Now));
        var first = EudamedDeadlineRules.Alert(scheduled, Now.AddHours(2))!;
        var repeated = EudamedDeadlineRules.Alert(scheduled, Now.AddHours(3))!;
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal("Overdue", first.Code);
        Assert.Equal(scheduled.Source.Correspondence.OriginalHash, first.OriginalHash);
    }

    [Fact]
    public void AClaimedReplyWithoutItsAppliedBusinessReceiptRemainsOpenForClarification()
    {
        var source = WorkspaceTestData.Correspondence(Now, Now.AddHours(-1));
        var claimed = source.Correspondence with { Status = "Replied", ReplyDossierId = "unproven-reply" };
        var entry = source.Entry with { Correspondence = [claimed] };
        source = source with { Correspondence = claimed, Entry = entry, Ledger = source.Ledger with { Entries = [entry] } };
        var deadline = Assert.IsType<EudamedCorrespondenceDeadline>(EudamedDeadlineRules.Project(source));
        Assert.True(deadline.IsOpen);
        Assert.Null(deadline.DueAt);
        Assert.Contains(deadline.Issues, issue => issue.Contains("fachliche Reply-Annahme", StringComparison.Ordinal));
        Assert.Equal("ScheduleClarification", EudamedDeadlineRules.Alert(deadline, Now)!.Code);
    }
}

[Collection("SQL pilot")]
public sealed class WorkspaceReadSqlTests(SqlFixture fixture)
{
    [Fact]
    public async Task CorrespondenceCalendarReadsFrozenSqlHistoryAndLedgerNotTheLaggingDossierProjection()
    {
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        var source = WorkspaceTestData.Correspondence(now, now.AddHours(-1));
        async Task Record<T>(string kind, string id, int expected, T value) => await fixture.Store.Execute<T>(
            new(id, kind, "ortho", expected, CaseScenario.Key(), "SyntheticCalendarSourceRecorded",
                WorkspaceTestData.Reason, SqlFixture.Ra, value!, now), _ => value);
        await Record("eudamedDossier", source.ApprovedDossier.Id, 0, source.ApprovedDossier);
        await Record("eudamedDossier", source.CurrentDossier.Id, 1, source.CurrentDossier);
        await Record("eudamedInbox", source.Inbox.Id, 0, source.Inbox);
        await Record("eudamedDelivery", source.Ledger.Id, 0, source.Ledger);
        var reader = new WorkspaceReadService(fixture.Store, new TestClock(now));
        var calendar = await reader.Calendar(SqlFixture.Pms, "cardio", new(2026, 10, 1), "UTC", "EudamedReply", "open", null, 0);
        var item = Assert.Single(calendar.Events, e => e.EntityId == source.CurrentDossier.Id);
        Assert.Equal(source.Correspondence.Id, item.Correspondence!.CorrespondenceId);
        Assert.Equal(source.Entry.ApprovedDossier, item.Correspondence.ApprovedDossier);
        Assert.Equal(1, item.SourceVersion);
        Assert.Equal("Overdue", item.Timing);
        var limited = SqlFixture.Pms with { Products = ["ortho"] };
        Assert.DoesNotContain((await reader.Calendar(limited, null, new(2026, 10, 1), "UTC", null, null, null, 0)).Events,
            e => e.EntityId == source.CurrentDossier.Id);
        Assert.Equal(Canonical.Hash(source.CurrentDossier),
            Canonical.Hash(await fixture.Store.Get<EudamedDossier>("eudamedDossier", source.CurrentDossier.Id)));
        Assert.Equal(Canonical.Hash(source.Ledger),
            Canonical.Hash(await fixture.Store.Get<EudamedDeliveryLedger>("eudamedDelivery", source.Ledger.Id)));
    }

    [Fact]
    public async Task BatchedProcessProjectionMatchesTheWorkbenchIncludingHumanAndNonReportableHistory()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        await s.Respond();
        await s.Assess();
        await s.Prepare();
        s.Case = await s.Service.Approve(s.Case.Id, new(s.Case.Version, WorkspaceTestData.Reason, null),
            SqlFixture.Medical, CaseScenario.Key(), true);
        var reviewed = Assert.Single(await fixture.Store.ReadCockpitCases(["ortho"], s.Clock.Now.AddSeconds(-1), s.Clock.Now.AddSeconds(1)),
            row => row.Case.Id == s.Case.Id);
        var reviewedView = WorkspaceReadService.ProjectCockpit([reviewed], SqlFixture.Pms, "ortho", new(2026, 11, 16), new(2026, 11, 16),
            WorkspaceDates.Zone("UTC"), s.Clock.Now);
        Assert.Equal(1, reviewedView.Kpis.PendingRegulatory);
        Assert.Equal("MedicalReviewed", Assert.Single(reviewedView.Cases).ReviewState);
        Assert.False(reviewedView.RiskGroupsAvailable);
        var medicalNowEmpty = WorkspaceReadService.ProjectCockpit([reviewed], SqlFixture.Pms, "ortho",
            new(2026, 11, 16), new(2026, 11, 16), WorkspaceDates.Zone("UTC"), s.Clock.Now, "process:medical");
        Assert.Equal(1, medicalNowEmpty.TotalCases);
        Assert.Equal(0, medicalNowEmpty.MatchingCases);
        Assert.Empty(medicalNowEmpty.Cases);
        var content = s.Case.CurrentAssessment!.Content with
            { Uncertainties = [.. s.Case.CurrentAssessment.Content.Uncertainties, "Synthetischer menschlicher Review fuer die Leseprojektion."] };
        s.Case = await s.Service.Revise(s.Case.Id, new(s.Case.Version, content, WorkspaceTestData.Reason), SqlFixture.Medical, CaseScenario.Key());
        async Task Match()
        {
            var input = Assert.Single(await fixture.Store.ReadCockpitCases(["ortho"], s.Clock.Now.AddSeconds(-1), s.Clock.Now.AddSeconds(1)),
                c => c.Case.Id == s.Case.Id);
            var view = WorkspaceReadService.ProjectCockpit([input], SqlFixture.Pms, "ortho", new(2026, 11, 16), new(2026, 11, 16),
                WorkspaceDates.Zone("UTC"), s.Clock.Now);
            var batch = Assert.Single(view.Cases, c => c.Id == s.Case.Id);
            Assert.Equal((await s.Service.Process(s.Case.Id, SqlFixture.Pms)).CurrentStep, batch.CurrentStep);
        }
        await Match();
        s.Case = await s.Service.Decide(s.Case.Id, new(s.Case.Version, Disposition.NonReportable, WorkspaceTestData.Reason),
            SqlFixture.Medical, CaseScenario.Key());
        await Match();
        s.Case = await s.Service.ApproveDisposition(s.Case.Id, new(s.Case.Version, WorkspaceTestData.Reason), SqlFixture.Ra, CaseScenario.Key());
        await Match();
        Assert.Equal("followup", (await s.Service.Process(s.Case.Id, SqlFixture.Pms)).CurrentStep);
    }

    [Fact]
    public async Task SqlFiltersBeforeProjectionAndCalendarDoesNotMutateSourceVersions()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var other = await s.Service.Register(s.Input with { ProductId = "cardio", IncidentKey = CaseScenario.Key() }, SqlFixture.Pms, CaseScenario.Key());
        var actor = SqlFixture.Pms with { Products = ["ortho"] };
        var inputs = await fixture.Store.ReadCockpitCases(actor.Products, s.Clock.Now.AddSeconds(-1), s.Clock.Now.AddSeconds(1));
        Assert.Contains(inputs, r => r.Case.Id == s.Case.Id);
        Assert.DoesNotContain(inputs, r => r.Case.Id == other.Id);
        var reader = new WorkspaceReadService(fixture.Store, s.Clock);
        var audit = await fixture.Store.Audit(s.Case.Id);
        var c = Canonical.Serialize(s.Case);
        var calendar = await reader.Calendar(actor, null, new(2026, 11, 1), "Europe/Berlin", null, null, null, 0);
        Assert.Contains(calendar.Events, e => e.EntityId == s.Case.Id && e.Kind == "LegalDue");
        Assert.DoesNotContain(calendar.Events, e => e.EntityId == other.Id);
        Assert.Equal(c, Canonical.Serialize(await s.Service.Get(s.Case.Id, actor)));
        Assert.Equal(audit.Length, (await fixture.Store.Audit(s.Case.Id)).Length);
        Assert.Equal("product_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            reader.Calendar(actor, "cardio", new(2026, 11, 1), "UTC", null, null, null, 0))).Code);
    }

    [Fact]
    public async Task StoredPsurDateAndBsDeadlineAreProjectedWithoutReturningSnapshots()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-25T12:00:00Z"));
        var psur = WorkspaceTestData.Psur("calendar-psur-" + CaseScenario.Key(), new(2026, 11, 1));
        var review = WorkspaceTestData.Review(psur, DateTimeOffset.Parse("2026-10-31T23:30:00Z"));
        foreach (var input in new[] { ("psur", psur.Id, (object)psur), ("psurReview", review.Id, (object)review) })
            await fixture.Store.Execute<object>(new(input.Item2, input.Item1, "ortho", 0, CaseScenario.Key(),
                "SyntheticCalendarSourceRecorded", WorkspaceTestData.Reason, SqlFixture.Pms, input.Item3, clock.Now), _ => input.Item3);
        var reader = new WorkspaceReadService(fixture.Store, clock);
        var calendar = await reader.Calendar(SqlFixture.Pms, "ortho", new(2026, 11, 1), "Europe/Berlin", null, null, null, 0);
        var p = Assert.Single(calendar.Events, e => e.EntityId == psur.Id);
        Assert.Null(p.DueAt);
        Assert.Equal(new DateOnly(2026, 11, 1), p.DueDate);
        var q = Assert.Single(calendar.Events, e => e.EntityId == review.Id);
        Assert.Equal(new DateOnly(2026, 11, 1), q.DisplayDate);
        Assert.Equal(review.Questions[0].DueAt, q.DueAt);
        Assert.DoesNotContain("\"snapshot\":", Canonical.Serialize(calendar), StringComparison.Ordinal);
        Assert.DoesNotContain("\"draft\":", Canonical.Serialize(calendar), StringComparison.Ordinal);
    }
}

internal static class WorkspaceTestData
{
    internal const string Reason = "SYNTHETISCH: Der gebundene Kalender-/Prozessnachweis wird ohne klinische Risikoannahme oder Aenderung einer bestehenden Frist nachvollziehbar vorbereitet.";
    internal static SafetyCase Case(string id, DateTimeOffset now) => new(id, 1, "DEMO-" + id, "ortho", "Synthetic " + id,
        Reason, null, null, "DE", "EU-DE", Seriousness.OtherSerious, null, now, now, null, "workspace-test", true,
        CaseStage.Intake, Disposition.Undetermined, [], [], [], [], [], [], [], [], [], [], [], []);
    internal static Evidence Evidence(DateTimeOffset now)
    {
        var hash = Canonical.HashText(Reason);
        return new("test-evidence", 1, "ortho", "Synthetic source", "Complaint", Reason, hash, "cohors:synthetic",
            "Synthetic field", "Synthetic", "Product", now, null, now, true, true, hash);
    }
    internal static SafetyCase Assessed(SafetyCase c, string mode = "Replay") => c with
    {
        Assessments = [new(1, c.SystemReceivedAt, "synthetic-author", mode, "test", "replay", [], Canonical.Hash(c.Evidence),
            new([], [], [], ["Synthetic test only."], .8m, "Not a risk group"),
            new("qa", 1, c.SystemReceivedAt, "qa-run", [], "Synthetic structure test"))]
    };
    internal static Deadline Deadline(string id, DateTimeOffset due, DateTimeOffset safety) =>
        new(id, "EU-DE", Seriousness.OtherSerious, due.AddDays(-15), due, safety, "test", "test", "Europe/Berlin",
            ["Synthetic stored dates, not a fresh calculation."], "Open", false);
    internal static CalendarCaseSource CalendarCase(SafetyCase c) => new(c.Id, c.Version, c.ProductId, c.Reference, c.Title, c.Deadlines, c.FollowUps);
    internal static Psur Psur(string id, DateOnly due)
    {
        var profile = new ProductGovernance("ortho", 1, "ortho", new("Synthetic", "DEMO-SRN", "DE", null),
            new("DEMO-UDI", "DEMO-DI", "III", true, false, true, Reason, "DEMO-CERT", new(2025, 1, 1), "Synthetic NB", "0000", "synthetic@example.invalid"),
            new("Synthetic PRRC", "Synthetic deputy", "DEMO-SOP", "DEMO-ESCALATION", Reason), "DEMO-PMS", "DEMO-SERIES-" + id,
            new(2025, 1, 1), Reason, due, [], Reason, true);
        var series = new PsurSeries(PsurSeriesRules.Contract, profile.PsurReference, 1, "Initial", Reason,
            new(profile.Id, profile.Version, Canonical.Hash(profile)), profile, null, due, Reason, Reason);
        var snapshot = new PsurSnapshot("ortho", "ortho", new(2025, 1, 1), new(2025, 12, 31), "Worldwide",
            [], [], [], [], [], DateTimeOffset.Parse("2026-10-25T12:00:00Z"), [], [], [], "Synthetic date test", "No clinical assertion", Series: series);
        return new(id, 1, "ortho", snapshot, Canonical.Hash(snapshot), "", "", "Draft", [], null, true, [], AgentMode.Replay, null, null);
    }
    internal static PsurReview Review(Psur p, DateTimeOffset due) => new("review-" + p.Id, 1, p.ProductId, [p.ProductId],
        "DEMO-BS-" + p.Id, PsurReviewRules.Bind(p), "EU", "de", PsurReviewRules.Recipient,
        [new(1, Reason, due, false, Reason, due.AddDays(-1))], "QuestionOpen", null, "", [], [], [], [], true);

    internal static EudamedCorrespondenceSource Correspondence(DateTimeOffset now, DateTimeOffset? due, string kind = "InformationRequest")
    {
        var id = "calendar-dossier-" + Guid.NewGuid().ToString("N");
        var report = Psur("calendar-source-" + id, new(2026, 11, 1));
        report = report with { Snapshot = report.Snapshot with
            { RegulatoryHistory = new(PsurRegulatoryRules.Contract, ["ortho", "cardio"], now, [], Reason) } };
        report = report with { SnapshotHash = Canonical.Hash(report.Snapshot) };
        var governance = report.Snapshot.Series!.Governance;
        var actor = new VersionReference("calendar-actor", 1, Canonical.HashText("synthetic calendar actor"));
        var references = new EudamedReferences("calendar-references", 1, "ortho", actor,
            new("DEMO calendar manufacturer", "MF", "DE", "SYNTHETISCH: Teststrasse 0",
                new("Demo", "Contact", "calendar@example.invalid", "+49-0000"), true),
            "DEMO-EUDAMED-CALENDAR", new("ortho", "DEMO implant", "Implant", governance.Device.BasicUdi,
                Reason, "DEMO model", ["DE"]), report.Snapshot.Series.GovernanceSource, governance.Device,
            new("DEMO-CERT", "1", "DEMO-NB", "synthetic", new(2025, 1, 1), new(2027, 12, 31),
                Canonical.HashText("synthetic certificate"), Reason, "SyntheticMappingConfirmed", true),
            "DEMO-NB", Reason, SqlFixture.Ra.Id, now, true, "GS1");
        var profile = new EudamedProfile(EudamedMode.Simulator, EudamedEnvironment.Simulation,
            actor.Id, actor.Version, references.Id, references.Version, "DEMO-NB", EudamedContracts.Business, EudamedContracts.Gateway, null);
        var binding = new EudamedBinding("calendar-channel", 1, 1, Canonical.Hash(profile), profile, actor,
            EudamedContracts.Bind(references), references.ActorIdentity, EudamedContracts.WsdlSha256, true);
        var xml = "<SyntheticCalendarFixture />";
        EudamedSource Source(Psur value) => new("psur", new(value.Id, value.Version, Canonical.Hash(value)), Canonical.Serialize(value));
        var package = new EudamedPackage(EudamedContracts.Business, EudamedDossierKind.PSUR, id, 1, "ortho", binding, references,
            new("SYNTHETISCH: Kalendervertrag", Reason, Reason, "de", "EU-DE", ["DE"], "Submit", null, null,
                new(report.Snapshot.Start, report.Snapshot.End, Reason), null, null),
            new("EU-DE", "calendar-fixture", "Europe/Berlin", new(2025, 1, 1), new(2027, 12, 31), [], false),
            [Source(report)], xml, Canonical.HashText(xml), [],
            new(EudamedContracts.Business, "fixture", "fixture", EudamedContracts.Mapping,
                Canonical.HashText("fixture"), Canonical.HashText("fixture"), EudamedContracts.SchemaRuntime,
                Canonical.HashText("fixture"), [], ["Calendar fixture only; no XSD or human approval acceptance."], "Synthetic contract fixture"), true);
        var hash = Canonical.Hash(package);
        var approved = new EudamedDossier(id, 1, "ortho", EudamedDossierKind.PSUR, 1, package, hash, "Approved",
            [], hash, [], [], [], [], [], SqlFixture.Pms.Id, now) { AccessProducts = ["ortho", "cardio"] };
        var narrower = report with { Snapshot = report.Snapshot with { RegulatoryHistory = null } };
        narrower = narrower with { SnapshotHash = Canonical.Hash(narrower.Snapshot) };
        var revised = package with { Revision = 2, Sources = [Source(narrower)] };
        var current = approved with { Version = 2, Revision = 2, Package = revised, ScopeHash = Canonical.Hash(revised),
            ReviewStatus = "Draft", FrozenPackageHash = null, AccessProducts = ["ortho"] };
        var attempt = new EudamedAttempt("attempt-" + id, "correlation-" + id, id + "@cohors.invalid",
            1, hash, binding, kind, EudamedContracts.Simulator, "TransportDelivered", 1, now.AddDays(-1), now.AddDays(-1), now.AddDays(-1), null, null);
        var original = new EudamedInboxOriginal("question-" + id, attempt.CorrelationId, attempt.Id, id, 1, hash, 2,
            kind, "Open", "DEMO evaluating NB", Reason, "DEMO-DTX-CALENDAR", 1, [], null, due, true);
        var inbox = new EudamedInbox("inbox-" + id, 1, "ortho", binding, original, Canonical.Hash(original), null,
            original.MessageId, "Applied", "NotApplicable", null, now, null);
        var correspondence = new EudamedCorrespondence("correspondence-" + id, inbox.Id, inbox.OriginalHash,
            original.Issuer, kind, original.Content, 1, now, due, Roles.Regulatory, "Open", null, true);
        var entry = new EudamedDeliveryEntry(id, EudamedContracts.Bind(approved), attempt, "BusinessAccepted", 1)
            { Correspondence = [correspondence] };
        var ledger = new EudamedDeliveryLedger("ledger-" + id, 1, "ortho", id, [entry]);
        return new(ledger, entry, correspondence, current, approved, inbox);
    }
}
