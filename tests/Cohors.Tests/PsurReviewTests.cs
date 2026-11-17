using Cohors.Agents;
using Cohors.Api;
using Cohors.Application;
using Cohors.Domain;
using System.IO.Compression;

namespace Cohors.Tests;

[Collection("SQL pilot")]
[Trait("Category", "Sql")]
public sealed class PsurReviewTests : IAsyncLifetime
{
    private readonly SqlFixture fixture = new() { SeedCases = false };
    private readonly TestClock clock = new(DateTimeOffset.Parse("2026-11-16T15:00:00Z"));
    private const string Reason = "Der synthetische Rueckfragevorgang wird anhand der konkret gebundenen Quellen und Massnahmen nachvollziehbar geprueft; keine klinische Risikoakzeptanz.";
    private const string Response = "SYNTHETISCHE ANTWORT. Die getrennten Produktfamilien, Nenner und Quellversionen des eingefrorenen Berichts bleiben unveraendert. Die ausgewaehlten Quellen begruenden diese Praezisierung; eine gepoolte Inzidenz oder reale klinische Eignung wird nicht behauptet.";
    private AnalyticsService Analytics() => new(fixture.Store, new AgentRunner(new(null, null, null, null)), fixture.Policies, clock);
    private PsurReviewService Service() => new(fixture.Store, Analytics(), clock);
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private async Task<Psur> Report(string lead = "ortho")
    {
        var service = Analytics();
        var group = await service.RecordGroup(new(0, "DEMO-PSUR-GROUP-" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            lead, ["ortho", "cardio"], new(2025, 11, 15),
            "Ausschliesslich synthetische Gruppierung fuer den getrennten Nachweis zweier Implantatfamilien. Keine Aussage zur medizinischen Eignung realer Produktkombinationen.",
            "Expliziter Leitprodukt-Jahresplan mit gesonderter synthetischer Einreichungsvereinbarung."),
            SqlFixture.Pms, CaseScenario.Key());
        var report = await service.Freeze(new(lead, lead, group.FirstPeriodStart, group.FirstPeriodStart.AddYears(1).AddDays(-1),
            "Worldwide", GroupId: group.Id, GroupVersion: group.Version), SqlFixture.Pms, CaseScenario.Key());
        report = await service.Approve(report.Id, new(report.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        report = await service.Approve(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        return await service.Simulate(report.Id, new(report.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
    }

    private OpenPsurReviewCommand Question(Psur report, bool action = false) => new(report.Id, report.Version,
        "DEMO-BS-QUESTION-" + Guid.NewGuid().ToString("N"),
        "Die synthetische BS bittet um eine begruendete Praezisierung der getrennten Basic-UDI-Auswertung und des vorgesehenen Massnahmenbezugs.",
        clock.Now.AddDays(2), action, "Explizit fuer diesen synthetischen Prueffall vereinbarter Antworttermin, keine allgemeine Rechtsfrist.",
        "de", Reason);

    private static PsurReviewSelection[] Sources(Psur report) => PsurGroupRules.Snapshots(report.Snapshot)
        .Select(s => s.Directory!.Entries.First(e => e.Kind == "evidence"))
        .Select(e => new PsurReviewSelection(e.Kind, e.Source.Id, e.Source.Version)).ToArray();
    private static DraftPsurReplyCommand Draft(PsurReview review, Psur report, params PsurReviewSelection[] actions) =>
        new(review.Version, report.Id, report.Version, Response, Sources(report), actions,
            "Die konkrete Klarstellung veraendert keine Falldaten oder Bewertung; ohne verlangte Massnahme wird keine solche erfunden.", Reason);

    private async Task<PsurReview> Submit(PsurReview review)
    {
        var service = Service();
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        return await service.Simulate(review.Id, new(review.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
    }

    private async Task<Capa> Capa()
    {
        var scenario = new CaseScenario(fixture);
        var c = await fixture.Service(clock).Register(scenario.Input with { ProductId = "cardio" }, SqlFixture.Pms, CaseScenario.Key());
        return await new QualityService(fixture.Store, fixture.Objects, clock).OpenCapa(new(c.Id, "cardio",
            "Synthetische Dokumentationspruefung fuer BS-Rueckfrage", "Drei synthetische Dokumentationsproben einzeln gegen das definierte Soll pruefen.",
            Reason, SqlFixture.Pms.Id, "Alle drei Soll/Ist-Vergleiche sind nachpruefbar und ohne die beanstandete Abweichung dokumentiert.",
            clock.Now.AddDays(10)), SqlFixture.Pms, CaseScenario.Key());
    }

    [Fact]
    public async Task T16_BsCycleSeparatesReceiptQuestionsRevisionsAndAssessmentWithoutChangingPsurBytes()
    {
        var report = await Report();
        var originalBytes = Canonical.Serialize(report);
        var snapshotBytes = Canonical.Serialize(report.Snapshot);
        var service = Service();
        Assert.Empty(await service.List(report.Id, SqlFixture.Pms));
        var review = await service.Open(Question(report), SqlFixture.Ra, CaseScenario.Key());
        var originalDue = review.Questions[0].DueAt;
        review = await service.Draft(review.Id, Draft(review, report), SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(2, review.Draft!.Sources.Length);
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        var oldScope = review.ScopeHash;
        review = await service.Draft(review.Id, Draft(review, report) with { Response = Response + " Eine medizinische Klarstellung bleibt ausschliesslich auf die benannten Quellen begrenzt." },
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Empty(review.Approvals);
        Assert.NotEqual(oldScope, review.ScopeHash);
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        Assert.Equal("four_eyes", (await Assert.ThrowsAsync<DomainException>(() => service.Approve(review.Id,
            new(review.Version, Reason), SqlFixture.Medical with { Roles = [Roles.Regulatory] }, CaseScenario.Key()))).Code);
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Ra, CaseScenario.Key());
        var send = new VersionCommand(review.Version, Reason);
        var key = CaseScenario.Key();
        review = await service.Simulate(review.Id, send, SqlFixture.Ra, key);
        Assert.Equal("ReplySubmitted", review.Status);
        Assert.Empty(review.Assessments);
        Assert.Equal(Canonical.Hash(review), Canonical.Hash(await service.Simulate(review.Id, send, SqlFixture.Ra, key)));
        var firstSubmission = Canonical.Serialize(Assert.Single(review.Submissions));
        var nextDue = clock.Now.AddDays(4);
        review = await service.Assess(review.Id, new(review.Version, "FurtherResponseRequired", Reason,
            "Die synthetische BS fragt getrennt nach der Dokumentation begruendeter Ausschluesse; keine erneute Initialfrist.", nextDue,
            NextScheduleBasis: "Neue ausdrueckliche Vereinbarung ausschliesslich fuer die zweite Rueckfragerunde."),
            SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("QuestionOpen", review.Status);
        Assert.Null(review.Draft);
        Assert.Empty(review.Approvals);
        Assert.Equal(originalDue, review.Questions[0].DueAt);
        Assert.Equal(nextDue, review.Questions[1].DueAt);
        Assert.Equal(firstSubmission, Canonical.Serialize(review.Submissions[0]));
        Assert.Equal("ReplySubmitted", (await service.Simulate(review.Id, send, SqlFixture.Ra, key)).Status);
        review = await service.Draft(review.Id, Draft(review, report), SqlFixture.Pms, CaseScenario.Key());
        review = await Submit(review);
        var close = new AssessPsurReplyCommand(review.Version, "NoFurtherQuestion", Reason);
        var closeKey = CaseScenario.Key();
        review = await service.Assess(review.Id, close, SqlFixture.Ra, closeKey);
        Assert.Equal("QuestionClosed", review.Status);
        Assert.Equal(Canonical.Hash(review), Canonical.Hash(await service.Assess(review.Id, close, SqlFixture.Ra, closeKey)));
        Assert.Equal(2, review.Submissions.Length);
        Assert.Equal(2, review.Assessments.Length);
        Assert.Equal(originalBytes, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", report.Id)));
        Assert.Equal(snapshotBytes, Canonical.Serialize((await fixture.Store.Get<Psur>("psur", report.Id))!.Snapshot));
        Assert.Equal(2, (await fixture.Store.Get<Psur>("psur", report.Id))!.Approvals.Length);
        await fixture.Store.Manifest(review.Id, kind: "psurReview");
    }

    [Fact]
    public async Task T16_BsClosureRequiresEffectiveActionsFromEveryEarlierRound()
    {
        var report = await Report();
        var capa = await Capa();
        var service = Service();
        var quality = new QualityService(fixture.Store, fixture.Objects, clock);
        var review = await service.Open(Question(report, true), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("psur_review_action", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Draft(review.Id, Draft(review, report), SqlFixture.Pms, CaseScenario.Key()))).Code);
        review = await service.Draft(review.Id, Draft(review, report, new PsurReviewSelection("capa", capa.Id, capa.Version)), SqlFixture.Pms, CaseScenario.Key());
        review = await Submit(review);
        var submitted = Canonical.Serialize(review.Submissions[0]);
        capa = await quality.Verify(capa.Id, new(capa.Version,
            "Synthetischer Pruefbeleg: mindestens ein Soll/Ist-Vergleich zeigt weiterhin die urspruengliche Abweichung.", false, Reason),
            SqlFixture.Quality, CaseScenario.Key());
        Assert.Equal("psur_review_open_actions", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Assess(review.Id, new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        review = await service.Assess(review.Id, new(review.Version, "FurtherResponseRequired", Reason,
            "Zusaetzliche erlaeuternde Rueckfrage; die frueher verknuepfte CAPA und ihre Wirksamkeitspflicht bleiben bestehen.",
            clock.Now.AddDays(5), false, "Gesondert vereinbarter Termin fuer diese weitere synthetische Erlaeuterung."),
            SqlFixture.Ra, CaseScenario.Key());
        review = await service.Draft(review.Id, Draft(review, report), SqlFixture.Pms, CaseScenario.Key());
        review = await Submit(review);
        Assert.Empty(review.Submissions[^1].Reply.Actions);
        Assert.Equal("psur_review_open_actions", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Assess(review.Id, new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        capa = await quality.Amend(capa.Id, new(capa.Version, capa.Action + " Dokumentierte Abweichung nacharbeiten.",
            capa.EffectivenessCriterion, clock.Now.AddDays(12), Reason), SqlFixture.Pms, CaseScenario.Key());
        capa = await quality.Verify(capa.Id, new(capa.Version,
            "Synthetischer Pruefbericht: drei unabhaengig nachvollzogene Soll/Ist-Vergleiche erfuellen das vorher festgelegte Kriterium.", true, Reason),
            SqlFixture.Quality, CaseScenario.Key());
        review = await service.Assess(review.Id, new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("QuestionClosed", review.Status);
        var proof = Assert.Single(review.Assessments[^1].ActionEvidence);
        Assert.Equal(new(capa.Id, capa.Version, Canonical.Hash(capa)), proof.Source);
        Assert.Equal("Verified", proof.Status);
        Assert.Equal(submitted, Canonical.Serialize(review.Submissions[0]));
    }

    [Fact]
    public async Task T16_BsMemberPermissionsCoverListingDetailsMutationAndReceiptReplay()
    {
        var report = await Report();
        var service = Service();
        var review = await service.Open(Question(report), SqlFixture.Ra, CaseScenario.Key());
        var draft = Draft(review, report);
        var key = CaseScenario.Key();
        review = await service.Draft(review.Id, draft, SqlFixture.Pms, key);
        var limited = SqlFixture.Pms with { Products = ["ortho"] };
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.List(report.Id, limited))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Get(review.Id, limited))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Draft(review.Id, draft, limited, key))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Approve(review.Id,
            new(review.Version, Reason), SqlFixture.Medical with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        review = await Submit(review);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Simulate(review.Id,
            new(review.Version, Reason), SqlFixture.Ra with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.Assess(review.Id,
            new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra with { Products = ["ortho"] }, CaseScenario.Key()))).Status);
        var pending = (await fixture.Store.PendingEvents(1000)).Single(e => e.AggregateId == review.Id && e.Type == "PsurReplySimulated");
        var owner = Guid.NewGuid().ToString("N");
        await fixture.Store.ClaimEvent(pending.Id, owner);
        await fixture.Store.CompleteEvent(pending.Id, "test-psur-scope", owner, "synthetic-test-error");
        Assert.DoesNotContain(await fixture.Store.OutboxIssues(["ortho"]), e => e.Id == pending.Id);
        Assert.Contains(await fixture.Store.OutboxIssues(["ortho", "cardio"]), e => e.Id == pending.Id);
    }

    [Fact]
    public async Task T16_BsReplyRejectsUnboundSourcesDifferentSeriesAndStaleActionVersions()
    {
        var report = await Report();
        var otherSeries = await Report("cardio");
        var service = Service();
        var review = await service.Open(Question(report), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal("psur_reply_report", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Draft(review.Id, Draft(review, otherSeries), SqlFixture.Pms, CaseScenario.Key()))).Code);
        Assert.Equal("psur_reply_sources", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Draft(review.Id, Draft(review, report) with { Sources = [new("evidence", "not-in-this-snapshot", 1)] },
                SqlFixture.Pms, CaseScenario.Key()))).Code);
        var capa = await Capa();
        review = await service.Draft(review.Id, Draft(review, report, new PsurReviewSelection("capa", capa.Id, capa.Version)), SqlFixture.Pms, CaseScenario.Key());
        review = await service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Medical, CaseScenario.Key());
        await new QualityService(fixture.Store, fixture.Objects, clock).Amend(capa.Id,
            new(capa.Version, capa.Action + " Neu definierte Pruefbedingung.", capa.EffectivenessCriterion, clock.Now.AddDays(14), Reason),
            SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Approve(review.Id, new(review.Version, Reason), SqlFixture.Ra, CaseScenario.Key()))).Code);
        Assert.Single((await service.Get(review.Id, SqlFixture.Pms)).Approvals);
    }

    [Fact]
    public async Task T16_BsCurrentActionDependencyIsCheckedInTheSqlTransactionWithoutPartialWrites()
    {
        var report = await Report();
        var capa = await Capa();
        var review = await Service().Open(Question(report), SqlFixture.Ra, CaseScenario.Key());
        var source = new VersionReference(capa.Id, capa.Version, Canonical.Hash(capa));
        var command = new CommandContext(review.Id, "psurReview", review.ProductId, review.Version, CaseScenario.Key(),
            "DependencyGuardProbe", Reason, SqlFixture.Ra, new { source }, clock.Now)
            { Dependencies = [new("capa", source)] };
        await new QualityService(fixture.Store, fixture.Objects, clock).Amend(capa.Id,
            new(capa.Version, capa.Action + " Geaenderte Ausfuehrungsbedingungen.", capa.EffectivenessCriterion, clock.Now.AddDays(14), Reason),
            SqlFixture.Pms, CaseScenario.Key());
        var beforeAudit = (await fixture.Store.Audit(review.Id)).Length;
        var beforeOutbox = (await fixture.Store.PendingEvents(1000)).Length;
        Assert.Equal("dependency_version", (await Assert.ThrowsAsync<DomainException>(() => fixture.Store.Execute<PsurReview>(
            command, current => current! with { Version = current.Version + 1, Status = "QuestionClosed" }))).Code);
        Assert.Equal(Canonical.Hash(review), Canonical.Hash(await fixture.Store.Get<PsurReview>("psurReview", review.Id)));
        Assert.Equal(beforeAudit, (await fixture.Store.Audit(review.Id)).Length);
        Assert.Equal(beforeOutbox, (await fixture.Store.PendingEvents(1000)).Length);
        Assert.Null(await fixture.Store.Receipt<PsurReview>(command));
    }

    [Fact]
    public async Task T16_BsWatchdogPreservesAlreadyOverdueAgreementAndDoesNotRepeatOrReschedule()
    {
        var report = await Report();
        var service = Service();
        var due = clock.Now.AddDays(-2);
        var review = await service.Open(Question(report) with { DueAt = due }, SqlFixture.Ra, CaseScenario.Key());
        var operations = new OperationsService(fixture.Store, fixture.Store, clock);
        await operations.Tick();
        review = await service.Get(review.Id, SqlFixture.Pms);
        Assert.Single(review.Alerts);
        Assert.Equal(due, review.Questions[0].DueAt);
        var version = review.Version;
        await operations.Tick();
        review = await service.Get(review.Id, SqlFixture.Pms);
        Assert.Equal(version, review.Version);
        Assert.Equal(due, review.Questions[0].DueAt);
        Assert.Equal("QuestionOpen", review.Status);
        Assert.Equal(Canonical.Hash(report), Canonical.Hash(await fixture.Store.Get<Psur>("psur", report.Id)));
    }

    [Fact]
    public async Task T16_NewGroupSeriesRetainsEarlierBsObligationsAndSourcesWithoutRewritingEitherReport()
    {
        var first = await Report();
        var original = Canonical.Serialize(first);
        var service = Service();
        var review = await service.Open(Question(first), SqlFixture.Ra, CaseScenario.Key());
        var second = await Report("cardio");
        Assert.NotEqual(first.Snapshot.Series!.Reference, second.Snapshot.Series!.Reference);
        var history = second.Snapshot.Group!.RegulatoryHistory!;
        var source = Assert.Single(history.Reviews);
        Assert.Equal("PostCutoffAddendum", source.Selection);
        Assert.Equal("NotRecordedAtCutoff", source.StatusAtCutoff);
        Assert.Null(source.AtCutoff);
        Assert.Equal("QuestionOpen", source.CurrentAtFreeze.Status);
        Assert.Equal(first.Snapshot.Series.Reference, source.CurrentAtFreeze.OriginalReport.SeriesReference);
        Assert.Contains($"[psurReview:{review.Id}@{review.Version}]", second.Draft);
        Assert.All(second.Snapshot.Group.Members, m => Assert.Empty(m.Snapshot.RegulatoryHistory!.Reviews));
        Assert.Equal(Canonical.Hash(second.Snapshot with { Group = null }),
            Canonical.Hash(second.Snapshot.Group.Members.Single(m => m.Product.ProductId == "cardio").Snapshot));
        Assert.DoesNotContain(second.Findings, f => f.Severity is "Critical" or "Major");
        var frozenBytes = Canonical.Serialize(second);
        review = await service.Draft(review.Id, Draft(review, first), SqlFixture.Pms, CaseScenario.Key());
        review = await Submit(review);
        await service.Assess(review.Id, new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra, CaseScenario.Key());
        Assert.Equal(original, Canonical.Serialize(await fixture.Store.Get<Psur>("psur", first.Id)));
        Assert.Equal(frozenBytes, Canonical.Serialize(await Analytics().GetVerifiedPsur(second.Id, SqlFixture.Pms)));

        var nextQuestion = await service.Open(Question(second), SqlFixture.Ra, CaseScenario.Key());
        nextQuestion = await service.Draft(nextQuestion.Id, Draft(nextQuestion, second) with {
            Sources = [new("psurReview", source.CurrentSource.Id, source.CurrentSource.Version)]
        }, SqlFixture.Pms, CaseScenario.Key());
        Assert.Equal(source.CurrentSource, Assert.Single(nextQuestion.Draft!.Sources).Source);
        var exported = await new PsurExportService(fixture.Store, clock).Export(second.Id, second.Version, "zip", SqlFixture.Ra);
        using var archive = new ZipArchive(new MemoryStream(exported.Bytes));
        using var reader = new StreamReader(archive.GetEntry("snapshot.json")!.Open());
        var snapshot = Canonical.Read<PsurSnapshot>(await reader.ReadToEndAsync());
        Assert.Equal(Canonical.Hash(second.Snapshot), Canonical.Hash(snapshot));
        Assert.Equal("QuestionOpen", Assert.Single(snapshot.Group!.RegulatoryHistory!.Reviews).CurrentAtFreeze.Status);
        Assert.Contains(archive.Entries, e => e.FullName == "report.pdf" && e.Length > 1000);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => new PsurExportService(fixture.Store, clock)
            .Export(second.Id, second.Version, "json", SqlFixture.Ra with { Products = ["cardio"] }))).Status);
    }

    [Fact]
    public async Task T16_BsAndCapaCutoffStatesStaySeparateFromLaterClosureAndRequireRealHistoricalVersions()
    {
        var first = await Report();
        clock.Now = DateTimeOffset.Parse("2027-11-13T15:00:00Z");
        var capa = await Capa();
        var service = Service();
        var review = await service.Open(Question(first, true), SqlFixture.Ra, CaseScenario.Key());
        review = await service.Draft(review.Id, Draft(review, first, new PsurReviewSelection("capa", capa.Id, capa.Version)),
            SqlFixture.Pms, CaseScenario.Key());
        review = await Submit(review);
        var atCutoff = review;
        clock.Now = DateTimeOffset.Parse("2027-11-16T15:00:00Z");
        capa = await new QualityService(fixture.Store, fixture.Objects, clock).Verify(capa.Id, new(capa.Version,
            "Synthetischer Pruefbericht: Drei einzeln belegte Soll/Ist-Vergleiche erfuellen das vorab dokumentierte Kriterium.", true, Reason),
            SqlFixture.Quality, CaseScenario.Key());
        review = await service.Assess(review.Id, new(review.Version, "NoFurtherQuestion", Reason), SqlFixture.Ra, CaseScenario.Key());
        var start = first.Snapshot.End.AddDays(1);
        var end = first.Snapshot.End.AddYears(1);
        foreach (var product in new[] { "ortho", "cardio" })
        {
            var exposure = new Exposure("bs-next-period-" + product, 1, product, "Worldwide", start, end,
                "Implants", 1200, "Explicit synthetic annual implant population", "DEMO-BS-NEXT-PERIOD-EXPOSURE", true);
            await fixture.Store.Execute<Exposure>(new(exposure.Id, "exposure", product, 0, CaseScenario.Key(), "SyntheticExposureRecorded",
                Reason, SqlFixture.Quality, exposure, clock.Now), _ => exposure);
        }
        var group = first.Snapshot.Group!.Definition;
        var next = await Analytics().Freeze(new(first.ProductId, first.ProductId, start, end, "Worldwide",
            PreviousReportId: first.Id, PreviousReportVersion: first.Version, SubmissionDate: new(2028, 1, 10),
            ScheduleAgreement: "DEMO-BS-EXPLICIT-NEXT-PERIOD-AGREEMENT", RevisionReason: Reason, GroupId: group.Id, GroupVersion: group.Version),
            SqlFixture.Pms, CaseScenario.Key());
        var history = next.Snapshot.Group!.RegulatoryHistory!;
        var state = Assert.Single(history.Reviews);
        Assert.Equal("OpenInPeriod", state.Selection);
        Assert.Equal("ReplySubmitted", state.StatusAtCutoff);
        Assert.Equal(new(atCutoff.Id, atCutoff.Version, Canonical.Hash(atCutoff)), state.CutoffSource);
        Assert.Empty(state.AtCutoff!.Assessments);
        Assert.Equal("QuestionClosed", state.CurrentAtFreeze.Status);
        var action = Assert.Single(next.Snapshot.Group.Members.Single(m => m.Product.ProductId == "cardio").Snapshot.Actions!.Capas);
        Assert.Equal("Open", action.StatusAtCutoff);
        Assert.Equal("Verified", action.CurrentAtFreeze.Status);
        Assert.Contains("Stichtagsstatus ReplySubmitted", next.Draft);
        Assert.Contains("Freeze-Status QuestionClosed", next.Draft);
        Assert.DoesNotContain(next.Findings, f => f.Severity is "Critical" or "Major");
        await Analytics().GetVerifiedPsur(next.Id, SqlFixture.Medical);
        Assert.Contains(PortfolioRules.CheckPsur(next.Snapshot, next.Draft.Replace(
            $"[psurReview:{review.Id}@{review.Version}]", "BS-Beleg ausgelassen", StringComparison.Ordinal)),
            f => f.Code == "psur_bs_source_missing" && f.Severity == "Critical");
        var cutoffForgery = history with { Reviews = [state with { StatusAtCutoff = "QuestionClosed" }] };
        Assert.Contains(PsurRegulatoryRules.Check(cutoffForgery, ["ortho", "cardio"], end), f => f.Severity == "Critical");
        var fabricated = review with { Version = review.Version + 100 };
        var forgedHistory = history with { Reviews = [state with {
            CurrentAtFreeze = fabricated, CurrentSource = new(fabricated.Id, fabricated.Version, Canonical.Hash(fabricated))
        }] };
        var snapshot = next.Snapshot with { Group = next.Snapshot.Group with { RegulatoryHistory = forgedHistory } };
        var forged = next with { Version = next.Version + 1, Snapshot = snapshot, SnapshotHash = Canonical.Hash(snapshot),
            Draft = next.Draft.Replace($"[psurReview:{review.Id}@{review.Version}]",
                $"[psurReview:{fabricated.Id}@{fabricated.Version}]", StringComparison.Ordinal) };
        forged = forged with { Findings = PortfolioRules.CheckPsur(snapshot, forged.Draft) };
        Assert.DoesNotContain(forged.Findings, f => f.Severity is "Critical" or "Major");
        forged = forged with { ScopeHash = PortfolioRules.PsurScope(forged) };
        await fixture.Store.Execute<Psur>(new(next.Id, "psur", next.ProductId, next.Version, CaseScenario.Key(),
            "MissingBsVersionNegativeFixture", Reason, SqlFixture.Quality, forged, clock.Now), _ => forged);
        Assert.Equal("psur_bs_source", (await Assert.ThrowsAsync<DomainException>(() => Analytics().GetVerifiedPsur(
            forged.Id, SqlFixture.Medical))).Code);
        Assert.Equal("psur_bs_source", (await Assert.ThrowsAsync<DomainException>(() => new PsurExportService(fixture.Store, clock)
            .Export(forged.Id, forged.Version, "zip", SqlFixture.Ra))).Code);
    }

    [Fact]
    public async Task T16_NarrowerReportDoesNotCopyWiderBsDataOrClaimItsObligationsClosed()
    {
        var grouped = await Report();
        var review = await Service().Open(Question(grouped), SqlFixture.Ra, CaseScenario.Key());
        var limited = SqlFixture.Pms with { Products = ["cardio"] };
        var single = await Analytics().Freeze(new("cardio", "cardio", grouped.Snapshot.Start, grouped.Snapshot.End, "Worldwide"),
            limited, CaseScenario.Key());
        var history = single.Snapshot.RegulatoryHistory!;
        Assert.Null(single.Snapshot.Group);
        Assert.Empty(history.Reviews);
        Assert.Contains("not declared closed or absent", history.Coverage);
        Assert.DoesNotContain(review.Reference, Canonical.Serialize(single));
        await Analytics().GetVerifiedPsur(single.Id, limited);
        Assert.Equal("QuestionOpen", (await Service().Get(review.Id, SqlFixture.Pms)).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => Service().Get(review.Id, limited))).Status);
        var scopedCopy = single.Snapshot with { RegulatoryHistory = history with {
            Products = ["ortho", "cardio"],
            Reviews = [new(new(review.Id, review.Version, Canonical.Hash(review)), review, null, null, null,
                "NotRecordedAtCutoff", "PostCutoffAddendum", "Negativer Test einer unzulaessigen breiteren Korrespondenzkopie.")]
        } };
        Assert.Equal(403, Assert.Throws<DomainException>(() => PsurGroupRules.RequireScope(scopedCopy, limited)).Status);
        Assert.Contains(PortfolioRules.CheckPsur(scopedCopy, single.Draft), f => f.Code == "psur_bs_history_integrity");
    }
}
