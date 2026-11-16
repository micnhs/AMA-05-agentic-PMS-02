using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class CaseProcessTests
{
    [Fact]
    public void CurrentAnalysisOffersExplicitDraftPreparationWithoutPretendingHumanApprovals()
    {
        var progress = CaseProcess.Read(Case());
        Assert.Equal("completed", Step(progress, "analysis").State);
        Assert.Equal("report", progress.CurrentStep);
        Assert.Equal("prepare", progress.NextAction);
        Assert.NotEqual("completed", Step(progress, "medical").State);
        Assert.NotEqual("completed", Step(progress, "regulatory").State);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void ANewUnfinishedAnalysisNeverUsesOldAssessmentAndSignaturesAsNewSuccess(string status)
    {
        var c = Approved();
        c = c with { AgentRuns = [new("new-run", AgentRole.CausalityRisk, AgentMode.Replay, status, Now, Now,
            "replay", "assessment-1", Canonical.Hash(c.Evidence), null, [], [], status == "Failed" ? "test_failure" : null, 0, null)] };
        var progress = CaseProcess.Read(c);
        Assert.Equal("analysis", progress.CurrentStep);
        Assert.NotEqual("completed", Step(progress, "analysis").State);
        Assert.NotEqual("completed", Step(progress, "medical").State);
        Assert.NotEqual("completed", Step(progress, "regulatory").State);
    }

    [Fact]
    public void ChangedEvidenceAndSupersededReportRemainVisiblyNotCurrent()
    {
        var c = Approved();
        c = c with { Evidence = [c.Evidence[0] with { Version = 2 }], Reports = [c.CurrentReport! with { State = ReportState.Superseded }] };
        var progress = CaseProcess.Read(c);
        Assert.Equal("blocked", Step(progress, "analysis").State);
        Assert.Equal("blocked", Step(progress, "report").State);
        Assert.NotEqual("completed", Step(progress, "regulatory").State);
        Assert.Contains(progress.Blockers, text => text.Contains("Evidenzstand", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameIdentityOrMissingFrozenBytesCannotLookLikeReadyRegulatoryApproval(bool sameIdentity)
    {
        var c = Approved();
        c = sameIdentity
            ? c with { Approvals = [c.Approvals[0], c.Approvals[1] with { ActorId = c.Approvals[0].ActorId }] }
            : c with { Reports = [c.CurrentReport! with { FrozenJson = null }] };
        var progress = CaseProcess.Read(c);
        Assert.Equal("regulatory", progress.CurrentStep);
        Assert.NotEqual("completed", Step(progress, "regulatory").State);
        Assert.NotEmpty(progress.Blockers);
    }

    [Fact]
    public void UnknownOnHistoricalPackageCannotBeHiddenByAChangedCurrentTarget()
    {
        var c = Approved();
        var old = c.CurrentReport!;
        var next = old with { Version = 2, Jurisdiction = "EU-IE", State = ReportState.Draft, FrozenJson = null };
        next = next with { ScopeHash = ReportRules.Scope(c.CurrentAssessment!, next) };
        c = c with { Reports = [old with { State = ReportState.Superseded }, next],
            Submissions = [new("attempt", old.ScopeHash, old.Version, "original-correlation", "TransmissionUnknown",
                "Timeout", Now, null, null, true, 1, "Unknown")] };
        var progress = CaseProcess.Read(c);
        Assert.Equal("EU-IE", progress.Jurisdiction);
        Assert.Equal("delivery", progress.CurrentStep);
        Assert.Equal("reconcile", progress.NextAction);
        Assert.Equal("blocked", Step(progress, "delivery").State);
        Assert.NotEqual("completed", Step(progress, "medical").State);
    }

    [Fact]
    public void SimulatedReceiptIsNotCaseClosureAndKeepsAnActualFollowUpVisible()
    {
        var c = Approved();
        var report = c.CurrentReport!;
        c = c with { Reports = [report with { State = ReportState.SimulatedAccepted }],
            Deadlines = c.Deadlines.Select(d => d with { Status = "SimulatedReported" }).ToArray(),
            Submissions = [new("attempt", report.ScopeHash, report.Version, "correlation", "SimulatedAccepted",
                "Accepted", Now, Now, "DEMO-RECEIPT", true, 1, "SimulatedAccepted")],
            FollowUps = [new("follow-up", "GB", report.ScopeHash, report.Version, "DEMO-RECEIPT",
                new(new(2026, 11, 23), Now.AddDays(7), Now.AddDays(6), "UTC", "test-1", "human-promised-mir-date-1"),
                "Open", Now, null, null)] };
        var progress = CaseProcess.Read(c);
        Assert.Equal("completed", Step(progress, "delivery").State);
        Assert.Contains("nur", Step(progress, "delivery").Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("followup", progress.CurrentStep);
        Assert.Equal("current", Step(progress, "followup").State);
        Assert.Contains("1 Folgepflicht", Step(progress, "followup").Detail, StringComparison.Ordinal);
    }

    private static CaseProcessStep Step(CaseProcessView view, string id) => view.Steps.Single(s => s.Id == id);
    [Fact]
    public void CombinedUrgencyAndADeathWordInNarrativeDoNotProveADeath()
    {
        var c = Case() with { Seriousness = Seriousness.DeathOrSeriousDeterioration,
            Description = "SYNTHETISCH: Hypothese eines Todesfalls, nicht bestaetigt." };
        Assert.Equal("Unknown", CaseOutcomes.Read(c).Status);
        Assert.Equal("Unknown", CaseOutcomes.Read(c).Code);
    }

    [Theory]
    [InlineData(false, false, "Documented")]
    [InlineData(true, false, "Historical")]
    [InlineData(false, true, "Stale")]
    public void ExplicitHumanDeathCodingRetainsItsActualEvidenceAndHistoricalStatus(bool historical, bool changedSource, string status)
    {
        var c = WithCoding(Case(), "F02", historical, changedSource);
        var outcome = CaseOutcomes.Read(c);
        Assert.Equal("Death", outcome.Code);
        Assert.Equal(status, outcome.Status);
    }

    [Fact]
    public void OtherFCodingDoesNotHideACurrentMedicallyReviewedMirDeath()
    {
        var outcome = CaseOutcomes.Read(WithCoding(MedicalMir("Death"), "F01"));
        Assert.Equal("Death", outcome.Code);
        Assert.Equal("MedicalReviewed", outcome.Status);
        Assert.Contains(outcome.Sources, s => s.Code == "CodedOther" && s.Status == "Documented");
        Assert.Contains(outcome.Sources, s => s.Code == "Death" && s.Status == "MedicalReviewed");
        Assert.DoesNotContain(outcome.Sources, s => s.Status == "Conflict");
    }

    [Theory]
    [InlineData(true, false, "Death", "Historical")]
    [InlineData(false, true, "Unanticipated serious deterioration in state of health", "Stale")]
    public void HistoricalOrStaleDeathAndNewMedicalMirKeepSeparateReviewStates(bool historical, bool stale, string classification, string oldStatus)
    {
        var outcome = CaseOutcomes.Read(WithCoding(MedicalMir(classification), "F02", historical, stale));
        Assert.Equal("MedicalReviewed", outcome.Status);
        Assert.Contains(outcome.Sources, s => s.Code == "Death" && s.Status == oldStatus);
        Assert.Contains(outcome.Sources, s => s.Code == (classification == "Death" ? "Death" : "SeriousDeterioration") && s.Status == "MedicalReviewed");
    }

    [Fact]
    public void ANewDraftWithoutMirDoesNotEraseTheHistoricalExplicitDeath()
    {
        var c = MedicalMir("Death");
        var previous = c.CurrentReport!;
        var draft = previous with { Version = previous.Version + 1, State = ReportState.Draft, Mir = null, FrozenJson = null };
        draft = draft with { ScopeHash = ReportRules.Scope(c.CurrentAssessment!, draft) };
        c = c with { Reports = [previous with { State = ReportState.Superseded }, draft] };
        var outcome = CaseOutcomes.Read(c);
        Assert.Equal("Death", outcome.Code);
        Assert.Equal("Historical", outcome.Status);
        Assert.DoesNotContain(outcome.Sources, source => source.Status == "MedicalReviewed");
        Assert.Contains(outcome.Sources, source => source.Source.Contains($"Bericht v{previous.Version};", StringComparison.Ordinal));
        Assert.NotEqual("completed", Step(CaseProcess.Read(c), "medical").State);
    }

    private static SafetyCase WithCoding(SafetyCase c, string code, bool historical = false, bool stale = false)
    {
        var evidence = c.Evidence[0];
        var term = new TerminologyTerm(code, "F", code == "F02" ? "Death" : "Synthetic other health impact", 1, 1, [code],
            null, true, true, null, null, "F", 1, code);
        return c with { Coding = new(1, "synthetic-current-reference", Canonical.HashText("synthetic-reference"),
            [new(term, code, term.Term, "CurrentMapping")],
            [new(evidence.Id, stale ? Canonical.HashText("older source") : evidence.Hash, evidence.Locator, evidence.Content)],
            "SYNTHETISCH: explizite menschliche Testkodierung, keine echte klinische Aussage.", "medical-user", Now.AddDays(-1),
            historical ? new DateOnly(2020, 1, 1) : null) };
    }

    private static SafetyCase MedicalMir(string classification)
    {
        var c = Approved() with { Jurisdiction = "EU-IE", Country = "IE", Seriousness = Seriousness.DeathOrSeriousDeterioration };
        c = c with { Deadlines = c.Deadlines.Select(d => d with { Jurisdiction = c.Jurisdiction }).ToArray() };
        var report = c.CurrentReport! with { Jurisdiction = c.Jurisdiction, FormVersion = "MIR-7.3.1", SchemaVersion = "SB11252", FrozenJson = null };
        var facts = new MirFacts
        {
            EventDateFrom = new(2026, 11, 16), EventDateTo = new(2026, 11, 16), NextReportDate = new(2026, 11, 23),
            EventClassification = classification, DeviceProblemCode = "ZZZ", DeviceLocation = "Unknown",
            PreliminaryAnalysis = "SYNTHETISCH: strukturierte Meldungsangabe fuer einen isolierten Anzeige-Regressionsfall; Kausalitaet bleibt offen.",
            EvidenceIds = c.Evidence.Select(e => e.Id).ToArray(),
            Rationale = "SYNTHETISCHE ANZEIGEPRUEFUNG: ZZZ ist kein fachlich gepruefter IMDRF-Code und diese Daten sind keine reale klinische Beobachtung."
        };
        var mir = new MirComposer().Build(c, report, new("ortho", "Synthetic device", "Implant", "DEMO-BASIC",
                "Synthetic intended use", "DEMO-MODEL", ["IE"]),
            new("EU-IE", "synthetic-test-calendar", "UTC", new(2026, 1, 1), new(2027, 12, 31), [], true), facts, Now);
        Assert.True(mir.Valid, string.Join("; ", mir.Errors));
        report = report with { Mir = mir };
        report = report with { ScopeHash = ReportRules.Scope(c.CurrentAssessment!, report) };
        report = report with { FrozenJson = Canonical.Serialize(report) };
        return c with { Reports = [report], Approvals = c.Approvals.Select(a => a with { ScopeHash = report.ScopeHash }).ToArray() };
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");

    private static SafetyCase Case()
    {
        const string content = "SYNTHETISCH: Statusanzeige, keine klinische Schlussfolgerung.";
        var hash = Canonical.HashText(content);
        var evidence = new Evidence("text", 1, "ortho", "Synthetischer Text", "Complaint", content, hash,
            "cohors:synthetic", "Testfundstelle", "synthetic", "pilot", Now, null, Now, true, true, hash);
        var assessment = new Assessment(1, Now, "agent:CausalityRisk", "Replay", "test", "replay", [evidence.Id],
            Canonical.Hash(new[] { evidence }), new([], [], [], ["Keine klinischen Aussagen in diesem Statustest."], null, "Nicht bestimmt"),
            new("quality", 1, Now, "run-assurance", [], "Nur bestehende Zustandsbeziehungen pruefen."));
        return new("case", 1, "COH-STATE", "ortho", "Synthetic process state", content, null, null, "GB", "GB",
            Seriousness.OtherSerious, null, Now, Now, null, "state-test", true, CaseStage.AwaitingMedicalReview, Disposition.Undetermined,
            [new("source", "external", "Complaint", "Synthetic source", "cohors:synthetic", hash, Now, Now, "Test")], [],
            [new("obligation", "GB", Seriousness.OtherSerious, Now, Now.AddDays(15), Now.AddDays(14), "test", "test", "UTC", [], "Open", false)],
            [evidence], [assessment], [], [], [], [], [], [], []);
    }

    private static SafetyCase Approved()
    {
        var c = Case();
        var report = new ReportDraft(1, 1, "Initial", "GB", "Cohors GB simulator", "en", "GB-SIM-1", "narrative-only",
            "SYNTHETIC: Pilot narrative; not a validated GB MIR form.", true, "Clinical facts remain open.", "test",
            c.CurrentAssessment!.EvidenceHash, [c.Evidence[0].Hash], ReportState.Frozen, "", null, [c.Deadlines[0].Id]);
        report = report with { ScopeHash = ReportRules.Scope(c.CurrentAssessment, report) };
        report = report with { FrozenJson = Canonical.Serialize(report) };
        return c with { Reports = [report], Approvals =
            [new("medical", "medical-user", "Medical", Roles.Medical, report.ScopeHash, Now, "Medical review", "Synthetic review", null),
             new("ra", "ra-user", "RA", Roles.Regulatory, report.ScopeHash, Now, "RA review", "Synthetic review", null)] };
    }
}
