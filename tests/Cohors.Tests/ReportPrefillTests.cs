using System.Text.Json.Nodes;
using System.Xml.Linq;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using ReportContactFixture = Cohors.Tests.ReportPrefillTests.ReportContactFixture;

namespace Cohors.Tests;

public sealed class ReportPrefillRuleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
    private static readonly CalendarProfile Calendar = new("EU-IE", "synthetic-test-calendar", "UTC",
        new(2026, 1, 1), new(2027, 12, 31), [], true);
    private static readonly Device Device = new("ortho", "SYNTHETIC device", "Implant", "DEMO-BASIC",
        "Synthetic intended use", "DEMO-MODEL", ["IE"]);

    [Fact]
    public void PrefillPreservesUnknownsAndGroundedTextWithoutInventingClinicalFacts()
    {
        var c = Case() with { OccurredAt = null };
        var report = Report(c);
        var texts = VigilanceNarrative.Fields(c, "de");
        var (preparation, facts) = ReportPrefill.Create(c, report, Device, null, Calendar, texts, "synthetic-author-run", Now);
        Assert.Null(facts.EventDateFrom);
        Assert.Null(facts.EventDateTo);
        Assert.Null(facts.EventClassification);
        Assert.Null(facts.NextReportDate);
        Assert.Null(facts.DeviceProblemCode);
        Assert.Null(facts.DeviceLocation);
        Assert.Null(facts.InitialCorrectiveAction);
        Assert.Null(facts.RootCauseConfirmed);
        Assert.Null(facts.RiskAssessmentReviewed);
        Assert.Null(facts.SubmitterIsManufacturerContact);
        Assert.Equal(texts.PreliminaryAnalysis.Text, facts.PreliminaryAnalysis);
        Assert.Equal(texts.FurtherInvestigation.Text, facts.FurtherInvestigation);
        Assert.Equal(new VersionReference(c.Id, c.Version, Canonical.Hash(c)), preparation.CaseSource);
        Assert.All(preparation.Fields.Where(f => f.Origin == "AgentDraft"), field =>
        {
            Assert.NotEmpty(field.ClaimIds);
            Assert.NotEmpty(field.Citations);
            Assert.All(field.Citations, citation => Assert.True(EvidenceRules.CitationMatches(citation, c.Evidence)));
        });
        var tampered = report with { Preparation = preparation with { Target = preparation.Target with { Authority = "Different authority" } } };
        Assert.Equal("report_target", Assert.Throws<DomainException>(() => MirSchemas.RequireValid(tampered)).Code);
    }

    [Theory]
    [InlineData("F02", false, "Death")]
    [InlineData("F02", true, null)]
    [InlineData("F01", false, null)]
    public void OnlyExplicitCurrentHumanDeathCodingCanFillTheAmbiguousClassification(string code, bool historical, string? expected)
    {
        var c = Case();
        var citation = c.CurrentAssessment!.Content.Claims[0].Citations[0];
        var term = new TerminologyTerm(code, "F", "SYNTHETIC terminology fixture", 1, 1, [code], null, true, true, null, null, "F", 1, code);
        c = c with { Coding = new(1, "synthetic-release", Canonical.HashText("synthetic-reference"),
            [new(term, code, term.Term)], [citation], "SYNTHETIC human coding fixture.", "medical", Now,
            historical ? new DateOnly(2020, 1, 1) : null) };
        var result = ReportPrefill.Create(c, Report(c), Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        Assert.Equal(expected, result.Facts.EventClassification);
        if (expected is not null) Assert.Equal("HumanCoding", Assert.Single(result.Preparation.Fields, f => f.Field == "eventClassification").Origin);
    }

    [Theory]
    [InlineData("Initial", "A", true)]
    [InlineData("Initial", "B", false)]
    [InlineData("Final", "B", true)]
    public void MultipleCurrentCodesAreAnExplicitMappingLimitOnlyForTheActualRequiredField(string kind, string annex, bool required)
    {
        var c = Case();
        var citation = c.CurrentAssessment!.Content.Claims[0].Citations[0];
        var terms = new[] { annex + "01", annex + "02" }.Select(code =>
            new TerminologyTerm(code, annex, "SYNTHETIC terminology fixture", 1, 1, [code], null, true, true, null, null, annex, 1, code));
        c = c with { Coding = new(1, "synthetic-release", Canonical.HashText("synthetic-reference"),
            terms.Select(term => new AssignedCode(term, term.Code, term.Term)).ToArray(), [citation],
            "SYNTHETIC human selection of two separate codes; no automatic reduction to one.", "medical", Now, null) };
        var originalCoding = Canonical.Hash(c.Coding);
        var report = Report(c) with { Kind = kind };
        var result = ReportPrefill.Create(c, report, Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        Assert.Null(annex == "A" ? result.Facts.DeviceProblemCode : result.Facts.InvestigationTypeCode);
        var field = annex == "A" ? "deviceProblemCode" : "investigationTypeCode";
        var mir = new MirComposer().Build(c, report with { Preparation = result.Preparation }, Device, Calendar, result.Facts, Now);
        if (required) Assert.Contains(mir.Checks!.CompletionIssues!, p => p.Field == field && p.Reason == "Unsupported");
        else Assert.DoesNotContain(mir.Checks!.CompletionIssues!, p => p.Field == field);
        Assert.DoesNotContain(mir.Checks!.CompletionIssues!, p => p.Field == field && p.Reason == "MissingSource");
        Assert.Equal(originalCoding, Canonical.Hash(c.Coding));
    }

    [Fact]
    public void ApprovedContactIsCopiedAsOneVersionedIdentityWhileDatesAndUnknownClinicalFactsRemainOpen()
    {
        var profile = Governance();
        var source = ReportContactFixture.Source(profile, Now);
        var c = CaseWith(source);
        var report = Report(c);
        var result = ReportPrefill.Create(c, report, Device, profile, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        Assert.Equal("Synthetic", result.Facts.ContactFirstName);
        Assert.Equal("synthetic@example.invalid", result.Facts.ContactEmail);
        Assert.True(result.Facts.SubmitterIsManufacturerContact);
        Assert.Null(result.Facts.NextReportDate);
        Assert.Null(result.Facts.EventClassification);
        Assert.Null(result.Facts.RootCauseConfirmed);
        Assert.Null(result.Facts.InitialCorrectiveAction);
        var field = Assert.Single(result.Preparation.Fields, f => f.Field == "contactFirstName");
        Assert.Equal("ControlledContactEvidence", field.Origin);
        Assert.Contains($"{source.Id}@{source.Version}", field.Source);
        Assert.Contains(source.Hash, field.Source);
        Assert.All(field.Citations, citation => Assert.True(EvidenceRules.CitationMatches(citation, c.Evidence)));
        var mir = new MirComposer().Build(c, report with { Preparation = result.Preparation }, Device, Calendar, result.Facts, Now);
        Assert.False(mir.Valid);
        Assert.Contains(mir.Checks!.CompletionIssues!, i => i.Field == "nextReportDate" && i.Reason == "HumanDecision");
        Assert.DoesNotContain(mir.Checks.CompletionIssues!, i => i.Field.StartsWith("contact", StringComparison.Ordinal));
        Assert.Empty(c.Approvals);
        Assert.Empty(c.Submissions);
    }

    [Theory]
    [InlineData("unapproved", "MissingSource")]
    [InlineData("expired", "MissingSource")]
    [InlineData("other-manufacturer", "Unsupported")]
    [InlineData("other-target", "Unsupported")]
    [InlineData("prrc", "Unsupported")]
    [InlineData("malformed", "Unsupported")]
    [InlineData("duplicate-property", "Unsupported")]
    public void IneligibleContactNeverSuppliesInventedOrMisassignedValues(string variant, string reason)
    {
        var profile = Governance();
        var source = ReportContactFixture.Source(profile, Now);
        var document = Canonical.Read<ReportingContactEvidence>(source.Content);
        var content = variant switch
        {
            "other-manufacturer" => Canonical.Serialize(document with { ManufacturerSrn = "DEMO-OTHER" }),
            "other-target" => Canonical.Serialize(document with { Jurisdictions = ["EU-FR"] }),
            "prrc" => Canonical.Serialize(document with { Role = "PRRC" }),
            "malformed" => "SYNTHETIC unreadable contact source, never parsed as a name.",
            "duplicate-property" => source.Content.Insert(1, "\"productId\":\"ortho\","),
            _ => source.Content
        };
        source = source with { Content = content, Hash = Canonical.HashText(content), ObjectKey = Canonical.HashText(content),
            Approved = variant != "unapproved", ValidTo = variant == "expired" ? Now.AddDays(-1) : null };
        var c = CaseWith(source);
        var result = ReportPrefill.Create(c, Report(c), Device, profile, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        Assert.Null(result.Facts.ContactFirstName);
        Assert.Null(result.Facts.ContactEmail);
        Assert.Null(result.Facts.SubmitterIsManufacturerContact);
        Assert.Contains(result.Preparation.Problems!, p => p.Field == "$contact" && p.Reason == reason && p.Sources.Any(s => s.Contains(source.Hash)));
    }

    [Fact]
    public void ConflictingContactDocumentsBlockCompletionWithoutMixingPeople()
    {
        var profile = Governance();
        var c = CaseWith(ReportContactFixture.Source(profile, Now), ReportContactFixture.Source(profile, Now, "second-contact", "Alternative"));
        var result = ReportPrefill.Create(c, Report(c), Device, profile, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        Assert.Null(result.Facts.ContactFirstName);
        Assert.Null(result.Facts.ContactLastName);
        Assert.Null(result.Facts.ContactEmail);
        var conflict = Assert.Single(result.Preparation.Problems!, p => p.Field == "$contact" && p.Reason == "ConflictingSources");
        Assert.Equal(2, conflict.Sources.Length);
        var human = Facts() with { ManufacturerName = profile.Manufacturer.Name, ManufacturerSrn = profile.Manufacturer.Srn,
            ManufacturerCountry = profile.Manufacturer.Country, EvidenceIds = c.Evidence.Select(e => e.Id).ToArray() };
        var unacknowledged = ReportPrefill.Review(result.Preparation, human, SqlFixture.Medical,
            "SYNTHETIC manual entry without an explicit resolution of the known competing contact sources.", c.Evidence);
        var report = Report(c) with { Preparation = unacknowledged };
        var mir = new MirComposer().Build(c, report, Device, Calendar, human, Now);
        Assert.False(mir.Valid);
        Assert.True(mir.Checks!.SchemaValid);
        Assert.Contains(mir.Checks.CompletionIssues!, i => i.Field == "$contact" && i.Reason == "ConflictingSources");
        var resolved = ReportPrefill.Review(unacknowledged, human, SqlFixture.Medical,
            "SYNTHETIC explicit source resolution: the chosen contact is supported by the selected original case evidence; new package review remains necessary.",
            c.Evidence, resolveSourceProblems: true);
        Assert.Empty(resolved.Problems!);
        Assert.Equal("HumanEntry", Assert.Single(resolved.Fields, f => f.Field == "contactFirstName").Origin);
        Assert.True(new MirComposer().Build(c, report with { Preparation = resolved }, Device, Calendar, human, Now).Valid);
    }

    [Fact]
    public void FreshDraftPreservesUnapprovedHumanInputsButOnlySameIntentCompletionKeepsTheHumanPromise()
    {
        var c = Case();
        var initial = Report(c);
        var generated = ReportPrefill.Create(c, initial, Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now);
        var human = Facts();
        var prior = initial with { Preparation = ReportPrefill.Review(generated.Preparation, human, SqlFixture.Medical,
            "SYNTHETIC source-backed human correction, not an approval of the report.", c.Evidence) };
        prior = prior with { Mir = new MirComposer().Build(c, prior, Device, Calendar, human, Now) };
        prior = prior with { ScopeHash = ReportRules.Scope(c.CurrentAssessment!, prior) };
        c = c with { Reports = [prior] };
        var frozenInput = Canonical.Serialize(c);
        var next = Report(c) with { Version = 2 };
        var fresh = ReportPrefill.Create(c, next, Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "new-report", Now);
        Assert.Equal(human.ContactFirstName, fresh.Facts.ContactFirstName);
        Assert.Equal(human.PreliminaryAnalysis, fresh.Facts.PreliminaryAnalysis);
        Assert.Null(fresh.Facts.NextReportDate);
        var completion = ReportPrefill.Create(c, next, Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "same-report", Now, prior);
        Assert.Equal(Canonical.Serialize(human), Canonical.Serialize(completion.Facts));
        Assert.Equal("HumanEntry", Assert.Single(completion.Preparation.Fields, f => f.Field == "nextReportDate").Origin);
        Assert.Equal(frozenInput, Canonical.Serialize(c));
        Assert.Empty(c.Approvals);
    }

    [Fact]
    public void NewNullableCompletionMetadataDoesNotChangeHistoricalBoundReportBytes()
    {
        var c = Case();
        var report = Report(c);
        var preparation = ReportPrefill.Create(c, report, Device, null, Calendar, VigilanceNarrative.Fields(c, "de"), "run", Now).Preparation;
        var artifact = new MirComposer().Build(c, report, Device, Calendar, Facts(), Now);
        report = report with { Preparation = preparation with { Contract = "cohors-report-prefill-1", Problems = null },
            Mir = artifact with { MappingVersion = "cohors-mir-map-2026.3", Checks = artifact.Checks! with { CompletionIssues = null } } };
        var historical = JsonNode.Parse(Canonical.Serialize(report))!.AsObject();
        historical["preparation"]!.AsObject().Remove("problems");
        historical["mir"]!["checks"]!.AsObject().Remove("completionIssues");
        var bytes = Canonical.Serialize(historical);
        Assert.Equal(bytes, Canonical.Serialize(Canonical.Read<ReportDraft>(bytes)));
    }

    private static ProductGovernance Governance() => new("ortho", 1, "ortho", new("SYNTHETIC manufacturer", "DEMO-SRN", "IE", null),
        new(Device.BasicUdi, "DEMO-PROFILE-UDI", "III", true, false, true, "SYNTHETIC scope rationale", "DEMO-CERT",
            new(2025, 1, 1), "SYNTHETIC NB", "0000", "Not the manufacturer reporting contact"),
        new("PRRC primary, not reporting contact", "PRRC deputy", "DEMO-RESPONSIBILITY", "DEMO-ESCALATION", "Synthetic only"),
        "pms", "DEMO-PSUR", new(2025, 1, 1), "", new(2026, 1, 2), [], "SYNTHETIC lifecycle", true);

    private static SafetyCase CaseWith(params Evidence[] contacts)
    {
        var c = Case();
        var evidence = c.Evidence.Concat(contacts).ToArray();
        return c with { Evidence = evidence, Assessments = [c.CurrentAssessment! with { EvidenceHash = Canonical.Hash(evidence),
            EvidenceIds = evidence.Select(e => e.Id).ToArray() }] };
    }

    [Theory]
    [InlineData(4000, true)]
    [InlineData(4001, false)]
    public void MirFieldLengthIsMeasuredWithoutTruncatingTheAgentText(int length, bool valid)
    {
        var c = Case();
        var text = new string('a', length);
        var artifact = new MirComposer().Build(c, Report(c), Device, Calendar, Facts() with { PreliminaryAnalysis = text }, Now);
        Assert.Equal(valid, artifact.Valid);
        Assert.Equal(text, XDocument.Parse(artifact.Xml).Descendants("manufacturersPrelimAnalysis").Single().Value);
        Assert.Equal(valid, artifact.Checks!.SchemaValid);
        if (!valid) Assert.Contains(artifact.Checks.ContentErrors, e => e.Contains("4001", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("No")]
    public void FinalRiskReviewHasConditionalContentGatesIndependentOfXsd(string reviewed)
    {
        var c = WithReceipt(Case());
        var facts = Facts() with { RiskAssessmentReviewed = reviewed, RationaleNoRiskReview = null,
            RiskAssessmentAdequate = null, RiskAssessmentResults = null };
        var blocked = new MirComposer().Build(c, Report(c, "Final"), Device, Calendar, facts, Now);
        Assert.True(blocked.Checks!.SchemaValid, string.Join("\n", blocked.Errors));
        Assert.False(blocked.Valid);
        Assert.NotEmpty(blocked.Checks.ContentErrors);
        var complete = new MirComposer().Build(c, Report(c, "Final"), Device, Calendar, facts with
        {
            RationaleNoRiskReview = reviewed == "No" ? "SYNTHETISCH: konkrete Testbegruendung fuer den noch nicht durchgefuehrten Review." : null,
            RiskAssessmentAdequate = reviewed == "Yes" ? "No" : null,
            RiskAssessmentResults = reviewed == "Yes" ? "SYNTHETISCH: Testreview zeigt weitere Evidenzluecken, keine pauschale Risikoakzeptanz." : null
        }, Now);
        Assert.True(complete.Valid, string.Join("\n", complete.Errors));
        Assert.False(complete.Checks!.NationalSubmissionReady);
        Assert.Empty(XDocument.Parse(complete.Xml).Descendants("worldSimInc"));
        Assert.Contains("keine Nullwerte", complete.Xml);
    }

    [Fact]
    public void SimulatorReceiptCannotBecomeAnAuthorityReferenceOrImplicitUnknown()
    {
        var c = WithReceipt(Case());
        var report = Report(c, "FollowUp");
        var composer = new MirComposer();
        var missing = composer.Build(c, report, Device, Calendar, Facts() with { AuthorityReference = null }, Now);
        Assert.False(missing.Valid);
        Assert.Empty(XDocument.Parse(missing.Xml).Descendants("ncaReportNo"));
        var receipt = composer.Build(c, report, Device, Calendar,
            Facts() with { AuthorityReference = c.Submissions[0].ReceiptReference }, Now);
        Assert.Contains(receipt.Checks!.ContentErrors, e => e.Contains("Simulatorquittung", StringComparison.Ordinal));
        var unknown = composer.Build(c, report, Device, Calendar, Facts() with { AuthorityReference = "Unknown", AuthorityReferenceRationale = null }, Now);
        Assert.False(unknown.Valid);
        var justified = composer.Build(c, report, Device, Calendar, Facts() with { AuthorityReference = "Unknown",
            AuthorityReferenceRationale = "SYNTHETISCH: Es gab keine reale NCA-Meldung und daher kein externes Aktenzeichen; nur eine interne Simulatorquittung." }, Now);
        Assert.True(justified.Valid, string.Join("\n", justified.Errors));
    }

    [Fact]
    public void FollowUpMissingActionAndInvestigationRemainExplicitGaps()
    {
        var c = WithReceipt(Case());
        var artifact = new MirComposer().Build(c, Report(c, "FollowUp"), Device, Calendar,
            Facts() with { InitialCorrectiveAction = null, FurtherInvestigation = null }, Now);
        Assert.False(artifact.Valid);
        Assert.Contains(artifact.Checks!.ContentErrors, e => e.StartsWith("InitialCorrectiveAction:", StringComparison.Ordinal));
        Assert.Contains(artifact.Checks.ContentErrors, e => e.StartsWith("FurtherInvestigation:", StringComparison.Ordinal));
        Assert.Empty(XDocument.Parse(artifact.Xml).Descendants("manufacturersInitialCorrecAction"));
    }

    [Theory]
    [InlineData("EU-IE", "HPRA")]
    [InlineData("EU-DE", "BfArM")]
    [InlineData("EU-FR", "ANSM")]
    [InlineData("EU-IT", "Ministero della Salute")]
    [InlineData("NI", "Northern Ireland")]
    public void SupportedDocumentedTargetsUseRealAuthorityNamesAndNeverClaimNationalReadiness(string jurisdiction, string authority)
    {
        var c = Case() with { Jurisdiction = jurisdiction };
        c = c with { Deadlines = c.Deadlines.Select(d => d with { Jurisdiction = jurisdiction }).ToArray() };
        var report = Report(c) with { Jurisdiction = jurisdiction, Recipient = PolicyCatalog.Recipient(jurisdiction) };
        var calendar = Calendar with { Id = jurisdiction };
        var preparation = ReportPrefill.Create(c, report, Device, null, calendar, VigilanceNarrative.Fields(c, "de"), "run", Now).Preparation;
        var artifact = new MirComposer().Build(c, report with { Preparation = preparation }, Device, calendar, Facts(), Now);
        Assert.True(artifact.Valid, string.Join("\n", artifact.Errors));
        var nca = XDocument.Parse(artifact.Xml).Descendants("ncaName").Single().Value;
        Assert.Contains(authority, nca);
        Assert.DoesNotContain("SIMULATOR", nca, StringComparison.Ordinal);
        Assert.False(artifact.Checks!.NationalSubmissionReady);
        Assert.NotEmpty(artifact.Checks.ExternalRequirements);
    }

    [Fact]
    public void SimilarIncidentDefaultDatesCannotMasqueradeAsKnownPeriods()
    {
        var c = WithReceipt(Case());
        var similar = Facts().SimilarIncidents!;
        var artifact = new MirComposer().Build(c, Report(c, "Final"), Device, Calendar,
            Facts() with { SimilarIncidents = similar with { Periods = [similar.Periods[0] with { Start = default }] } }, Now);
        Assert.False(artifact.Valid);
        Assert.Contains(artifact.Checks!.ContentErrors, e => e.StartsWith("SimilarIncidents:", StringComparison.Ordinal));
        Assert.Empty(XDocument.Parse(artifact.Xml).Descendants("worldSimInc"));
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void SimilarMethodLimitIncludesGeographiesAndDataGapReasons(int length, bool valid)
    {
        var c = WithReceipt(Case());
        var composer = new MirComposer();
        var similar = Facts().SimilarIncidents!;
        var baseline = composer.Build(c, Report(c, "Final"), Device, Calendar, Facts(), Now);
        var overhead = XDocument.Parse(baseline.Xml).Descendants("howWereDetermined").Single().Value.Length - similar.Method.Length;
        var artifact = composer.Build(c, Report(c, "Final"), Device, Calendar,
            Facts() with { SimilarIncidents = similar with { Method = new string('m', length - overhead) } }, Now);
        Assert.Equal(valid, artifact.Valid);
        Assert.Equal(length, XDocument.Parse(artifact.Xml).Descendants("howWereDetermined").Single().Value.Length);
        Assert.Equal(valid, artifact.Checks!.SchemaValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalPrefillFieldsPreserveTheOriginalReportHashContract(bool withMir)
    {
        var c = Case();
        var report = Report(c) with { FollowUpObligationIds = null };
        if (withMir)
        {
            var artifact = new MirComposer().Build(c, report, Device, Calendar, Facts(), Now);
            Assert.True(artifact.Valid, string.Join("\n", artifact.Errors));
            report = report with { Mir = artifact with { Checks = null, MappingVersion = "cohors-mir-map-2026.2",
                Facts = new MirFacts { EventDateFrom = new(2026, 11, 14), EventDateTo = new(2026, 11, 14),
                    NextReportDate = new(2026, 11, 23), EventClassification = "Death", EvidenceIds = ["proof"],
                    Rationale = "SYNTHETIC historical serialization fixture with only the original fact properties." } } };
        }
        var assessment = c.CurrentAssessment!;
        var content = new
        {
            assessment = new { assessment.Version, assessment.Content, assessment.EvidenceHash, assessment.Quality },
            report.Version, report.AssessmentVersion, report.Kind, report.Jurisdiction, report.Recipient,
            report.Language, report.FormVersion, report.SchemaVersion, report.Narrative, report.Preliminary,
            report.OpenQuestions, report.RuleVersion, report.EvidenceHash, report.AttachmentHashes, report.ObligationIds
        };
        var originalScope = report.Mir is null ? Canonical.Hash(content) : Canonical.Hash(new { content, report.Mir });
        Assert.Equal(originalScope, ReportRules.Scope(assessment, report));
        var json = Canonical.Serialize(report);
        Assert.DoesNotContain("preparation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewReturn", json, StringComparison.Ordinal);
        Assert.Equal(json, Canonical.Serialize(Canonical.Read<ReportDraft>(json)));
        Assert.DoesNotContain("manufacturerName", Canonical.Serialize(new MirFacts()), StringComparison.Ordinal);
        Assert.Equal("GB-SIM-1; GB-MIR 7.2.1GB nicht implementiert", ReportTargets.For("GB").FormFamily);
        Assert.Contains(ReportTargets.For("EU-FR").ExternalRequirements, r => r.Contains("PDF UND XML", StringComparison.Ordinal));
    }

    private static SafetyCase Case()
    {
        const string text = "SYNTHETISCH: Tod steht hier nur als Freitext-Testwort; ein strukturierter Ausgang ist damit nicht bestaetigt.";
        var hash = Canonical.HashText(text);
        var evidence = new Evidence("proof", 1, "ortho", "Synthetic source", "Complaint", text, hash, "cohors:synthetic",
            "Originaltesttext", "synthetic", "Product", Now.AddDays(-2), null, Now, true, true, hash);
        var claim = new Claim("claim-1", text, "Observed", [new(evidence.Id, hash, evidence.Locator, text)]);
        var content = new AssessmentContent([new("causality", "Ursachen", "SYNTHETISCH: Die Quelle erlaubt keine bestaetigte Kausalitaetsfeststellung; Gegenhypothesen bleiben offen.", [claim.Id]),
            new("information", "Untersuchungsbedarf", "SYNTHETISCH: Gezielte Untersuchungen werden vorgeschlagen, sind aber weder durchgefuehrt noch menschlich zugesagt.", [claim.Id])],
            [claim], [], ["Ursache und genauer Ausgang offen."], null, "Nicht bestimmt.");
        var assessment = new Assessment(1, Now, "synthetic-agent", "Replay", "test", "replay", [evidence.Id],
            Canonical.Hash(new[] { evidence }), content, new("qa", 1, Now, "run", [], "Synthetic fixture."));
        return new("prefill-case", 1, "DEMO-PREFILL", "ortho", "Synthetic prefill", text, null, "DEMO-LOT", "IE", "EU-IE",
            Seriousness.DeathOrSeriousDeterioration, Now.AddDays(-2), Now.AddDays(-1), Now, null, "prefill", true,
            CaseStage.AwaitingMedicalReview, Disposition.Undetermined, [], [],
            [DeadlineCalculator.Calculate("EU-IE", Seriousness.DeathOrSeriousDeterioration, Now.AddDays(-1), Calendar)],
            [evidence], [assessment], [], [], [], [], [], [], []);
    }

    private static ReportDraft Report(SafetyCase c, string kind = "Initial") => new(1, 1, kind, "EU-IE", "HPRA-SIMULATOR",
        "de", "MIR-7.3.1-SB11154", "MIR-7.3.1-SB11252", "SYNTHETISCHER BERICHT", kind != "Final", "Datenluecken",
        DeadlineCalculator.RuleVersion, Canonical.Hash(c.Evidence), [], ReportState.Draft, "", null, c.Deadlines.Select(d => d.Id).ToArray());

    private static SafetyCase WithReceipt(SafetyCase c)
    {
        var previous = Report(c) with { ScopeHash = "previous-scope", State = ReportState.SimulatedAccepted };
        return c with { Reports = [previous], Submissions = [new("submission", previous.ScopeHash, 1, "correlation",
            "SimulatedAccepted", "Accept", Now.AddDays(-1), null, "DEMO-RECEIPT-NOT-NCA", true, 1, "BusinessAccepted")] };
    }

    private static MirFacts Facts() => new()
    {
        EventDateFrom = new(2026, 11, 14), EventDateTo = new(2026, 11, 14), NextReportDate = new(2026, 11, 23),
        EventClassification = "Death", DeviceProblemCode = "ZZZ", DeviceLocation = "Unknown",
        PreliminaryAnalysis = "SYNTHETISCH: ungeklaerte Kausalitaet im gezielten Strukturtest, keine echte medizinische Behauptung.",
        InitialCorrectiveAction = "SYNTHETISCH: Der Testbefund dokumentiert noch keine abgeschlossene Massnahme, nicht deren erfundene Durchfuehrung.",
        FurtherInvestigation = "SYNTHETISCH: Eine gezielte weitere Ursachenuntersuchung ist vorgeschlagen; kein menschlicher Termin wird hier erfunden.",
        EvidenceIds = ["proof"], Rationale = "SYNTHETISCHE TESTDATEN: ZZZ und alle Kontaktdaten sind explizite Testangaben, keine validierte Kodierung oder reale Identitaet.",
        ManufacturerName = "SYNTHETIC manufacturer", ManufacturerSrn = "DEMO-SRN", ManufacturerCountry = "IE",
        ContactFirstName = "Synthetic", ContactLastName = "Testcontact", ContactStreet = "Demo street", ContactCity = "Demo city",
        ContactPostcode = "DEMO", ContactPhone = "000000", ContactEmail = "synthetic@example.invalid", SubmitterIsManufacturerContact = true,
        AuthorityReference = "DEMO-NCA-REF", AuthorityReferenceRationale = "SYNTHETISCH: Eine gesonderte Testreferenz ist dokumentiert; nicht aus dem Simulator abgeleitet.",
        UdiDi = "DEMO-UDI", UdiPi = "DEMO-LOT", UdiEntity = "N/A", BasicUdiEntity = "N/A",
        Distribution = "SYNTHETISCH: IE als ausdruecklicher Testmarkt.", RootCauses = "SYNTHETISCH: Ursache trotz Untersuchung ungeklaert.",
        RootCauseConfirmed = "No", RiskAssessmentReviewed = "Yes", RiskAssessmentAdequate = "No",
        RiskAssessmentResults = "SYNTHETISCH: Weitere Belege sind erforderlich.", InvestigationTypeCode = "ZZZ",
        InvestigationFindingCode = "ZZZ", InvestigationConclusionCode = "ZZZ", ComponentCode = "ZZZ",
        CorrectiveAction = "SYNTHETISCH: offene Massnahmen bleiben in der Nachverfolgung.", FinalComments = "SYNTHETISCH: keine echte Einreichung.",
        SimilarIncidents = new("Model", "SYNTHETISCH: gleiches Testmodell.", "Number of devices implanted", null,
            "SYNTHETISCH: explizite Auswahl anhand gleicher Problembeschreibung; keine Inzidenz aus oeffentlichen Meldedaten.",
            "IE; EWR/CH/TR; weltweit, nicht addiert.",
            [new(new(2025, 11, 16), new(2026, 11, 15), null, null, null, null, null, null,
                "SYNTHETISCHE DATENLUECKE: keine belastbaren Testzaehler oder Nenner verfuegbar, daher keine Nullwerte.")])
    };
}

[Collection("SQL pilot")]
public sealed class ReportPrefillTests(SqlFixture fixture)
{
    [Fact]
    public async Task MedicalCompletionPreservesInputsHistoricalSignaturesAndDeadlinesWithVersionAndIntentGuards()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        var before = s.Case;
        var prior = before.CurrentReport!;
        var command = new PrepareReportCommand(before.Version, prior.Kind, prior.Jurisdiction, prior.Language,
            prior.Preliminary, prior.OpenQuestions, "Medical requests completion from the existing sources without retyping known values or granting approval.", CompleteCurrent: true);
        Assert.Equal("completion_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.Prepare(before.Id, command with { Language = "en" }, SqlFixture.Medical, CaseScenario.Key()))).Code);
        var key = CaseScenario.Key();
        s.Case = await s.Service.Prepare(before.Id, command, SqlFixture.Medical, key);
        var next = s.Case.CurrentReport!;
        Assert.Equal(prior.Version + 1, next.Version);
        Assert.Equal(ReportState.Draft, next.State);
        Assert.Null(next.FrozenJson);
        Assert.True(next.Mir!.Valid, string.Join("\n", next.Mir.Errors));
        Assert.Equal(Canonical.Serialize(prior.Mir!.Facts), Canonical.Serialize(next.Mir.Facts));
        Assert.Equal(Canonical.Serialize(before.Approvals), Canonical.Serialize(s.Case.Approvals));
        Assert.DoesNotContain(s.Case.Approvals, a => a.ScopeHash == next.ScopeHash);
        Assert.Equal(prior.FrozenJson, s.Case.Reports.Single(r => r.Version == prior.Version).FrozenJson);
        Assert.Equal(Canonical.Serialize(before.Deadlines), Canonical.Serialize(s.Case.Deadlines));
        Assert.Equal(Canonical.Serialize(before.Awareness), Canonical.Serialize(s.Case.Awareness));
        Assert.Equal(Canonical.Serialize(before.FollowUps), Canonical.Serialize(s.Case.FollowUps));
        Assert.Empty(s.Case.Submissions);
        Assert.Equal(Canonical.Hash(s.Case), Canonical.Hash(await s.Service.Prepare(before.Id, command, SqlFixture.Medical, key)));
        Assert.Equal("version_conflict", (await Assert.ThrowsAsync<DomainException>(() =>
            s.Service.Prepare(before.Id, command, SqlFixture.Medical, CaseScenario.Key()))).Code);
        Assert.Equal("medical_required", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Approve(s.Case.Id,
            new(s.Case.Version, "RA cannot reuse the historical Medical signature for this new draft.", null),
            SqlFixture.Ra, CaseScenario.Key(), false))).Code);
        var historical = (await fixture.Store.History<SafetyCase>("case", [before.ProductId]))
            .Single(c => c.Id == before.Id && c.Version == before.Version);
        Assert.Equal(Canonical.Serialize(before), Canonical.Serialize(historical));
        Assert.Contains(await fixture.Store.Audit(before.Id), a => a.Action == "ReportFieldsCompleted");
        await fixture.Store.Manifest(before.Id);
    }

    [Fact]
    public async Task SqlBoundContactIsAutomaticallyUsedAndLaterConflictingEvidenceRemainsUnconfirmed()
    {
        var s = new CaseScenario(fixture);
        await s.Create();
        var profile = (await fixture.Store.Get<ProductGovernance>("productGovernance", s.Case.ProductId))!;
        async Task Bind(Evidence evidence)
        {
            Assert.Equal(evidence.Hash, await fixture.Objects.Put(evidence.Content));
            s.Case = await fixture.Store.Execute<SafetyCase>(new(s.Case.Id, "case", s.Case.ProductId, s.Case.Version,
                CaseScenario.Key(), "SyntheticContactBound", "Bind an explicit synthetic source only to this isolated test case.",
                SqlFixture.Pms, new { evidence.Id, evidence.Hash }, s.Clock.Now),
                current => current! with { Version = current.Version + 1, Evidence = [.. current.Evidence, evidence] });
            await s.Assess();
            s.Case = await s.Service.Prepare(s.Case.Id, new(s.Case.Version, "Initial", s.Case.Jurisdiction, "de", true,
                "Clinical investigation remains open.", "Complete known mandatory facts only from the actual bound evidence."), SqlFixture.Medical, CaseScenario.Key());
        }
        await Bind(ReportContactFixture.Source(profile, s.Clock.Now, s.Case.Id + "-contact"));
        var first = s.Case.CurrentReport!;
        Assert.Equal("Synthetic", first.Mir!.Facts.ContactFirstName);
        Assert.Equal("ControlledContactEvidence", Assert.Single(first.Preparation!.Fields, f => f.Field == "contactFirstName").Origin);
        Assert.Null(first.Mir.Facts.NextReportDate);
        var deadlines = Canonical.Serialize(s.Case.Deadlines);
        await Bind(ReportContactFixture.Source(profile, s.Clock.Now, s.Case.Id + "-other-contact", "Alternative"));
        var current = s.Case.CurrentReport!;
        Assert.Null(current.Mir!.Facts.ContactFirstName);
        Assert.Null(current.Mir.Facts.ContactEmail);
        Assert.Contains(current.Mir.Checks!.CompletionIssues!, i => i.Field == "$contact" && i.Reason == "ConflictingSources");
        Assert.False(current.Mir.Valid);
        Assert.Equal("Synthetic", s.Case.Reports.Single(r => r.Version == first.Version).Mir!.Facts.ContactFirstName);
        Assert.Empty(s.Case.Approvals);
        Assert.Empty(s.Case.Submissions);
        Assert.Equal(deadlines, Canonical.Serialize(s.Case.Deadlines));
        await fixture.Store.Manifest(s.Case.Id);
    }

    [Fact]
    public async Task GbAuthoringIsExplicitlyNarrativeOnlyAndNeverExportsAnEuSchemaAsGb()
    {
        var s = new CaseScenario(fixture);
        s.Case = await s.Service.Register(s.Input with { Country = "GB", Jurisdiction = "GB" }, SqlFixture.Pms, CaseScenario.Key());
        await s.Assess(); await s.Prepare();
        var report = s.Case.CurrentReport!;
        Assert.Equal("GB-SIM-1", report.FormVersion);
        Assert.Null(report.Mir);
        Assert.False(report.Preparation!.Target.MirSupported);
        Assert.False(report.Preparation.Target.NationalSubmissionReady);
        Assert.Equal("MHRA-GB-SIMULATOR", report.Recipient);
        Assert.Contains("7.2.1GB", report.Preparation.Target.FormFamily);
        Assert.Contains(report.Preparation.Fields, f => f.Origin == "AgentDraft");
        Assert.Empty(s.Case.Approvals);
        Assert.Empty(s.Case.Submissions);
    }

    internal static class ReportContactFixture
    {
        public static Evidence Source(ProductGovernance profile, DateTimeOffset now, string id = "manufacturer-contact", string firstName = "Synthetic")
        {
            var document = new ReportingContactEvidence("cohors-reporting-contact-1", profile.ProductId, profile.Manufacturer.Srn,
                ["EU-IE"], "ManufacturerReportingContact",
                new("DEMO-REPORTING-CONTACT", firstName, "Testcontact", "Demo street", "Demo city", "DEMO", "000000", "synthetic@example.invalid", true));
            var content = Canonical.Serialize(document);
            var hash = Canonical.HashText(content);
            return new(id, 1, profile.ProductId, "SYNTHETIC approved reporting contact", "ManufacturerContact", content, hash,
                "cohors:synthetic-contact", "Explicit structured synthetic contact", "synthetic", "Product",
                now.AddDays(-2), null, now, true, true, hash);
        }
    }

    [Fact]
    public async Task AuthoringCreatesRealPartialMirAndFieldSourcesWithoutSendingOrApproving()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess();
        var deadlines = Canonical.Hash(s.Case.Deadlines);
        var version = s.Case.Version;
        s.Case = await s.Service.Prepare(s.Case.Id, new(version, "Initial", "EU-IE", "de", true,
            "Offene Belege gezielt nachfordern.", "Autorenagent erstellt den pruefbaren Entwurf ohne erfundene Fakten."), SqlFixture.Pms, CaseScenario.Key());
        var report = s.Case.CurrentReport!;
        Assert.NotNull(report.Mir);
        Assert.False(report.Mir.Valid);
        Assert.NotNull(report.Preparation);
        Assert.Equal(version, report.Preparation.CaseSource.Version);
        Assert.Equal("Replay", report.Preparation.Mode);
        Assert.Equal("Health Products Regulatory Authority (HPRA)", report.Preparation.Target.Authority);
        Assert.Equal("HPRA-SIMULATOR", report.Recipient);
        Assert.Contains(report.Preparation.Target.Authority, report.Mir.Xml);
        Assert.Null(report.Mir.Facts.NextReportDate);
        Assert.Null(report.Mir.Facts.ContactFirstName);
        Assert.Null(report.Mir.Facts.InitialCorrectiveAction);
        Assert.Equal(new DateOnly(2026, 11, 11), report.Mir.Facts.EventDateFrom);
        Assert.Equal(report.Preparation.Governance!.Manufacturer.Name, report.Mir.Facts.ManufacturerName);
        Assert.All(report.Preparation.Fields.Where(f => f.Origin == "AgentDraft"), f => Assert.NotEmpty(f.Citations));
        Assert.Empty(s.Case.Approvals);
        Assert.Empty(s.Case.Submissions);
        Assert.Equal(deadlines, Canonical.Hash(s.Case.Deadlines));
        Assert.Equal(Roles.Pms, CaseProcess.Read(s.Case).NextRole);
        Assert.Equal("jurisdiction_scope", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Prepare(s.Case.Id,
            new(s.Case.Version, "Initial", "EU-FR", "de", true, "Nicht dokumentiertes Ziel.", "Es darf keine neue Jurisdiktion erfunden werden."),
            SqlFixture.Pms, CaseScenario.Key()))).Code);
    }

    [Fact]
    public async Task ReviewerReturnsExactPackageAndPmsCorrectionNeedsBothNewSignatures()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Prepare(); await s.Approve();
        var deadlines = Canonical.Hash(s.Case.Deadlines);
        var scope = s.Case.CurrentReport!.ScopeHash;
        var input = new VersionCommand(s.Case.Version, "Konkrete Angaben zur belegten Datenherkunft korrigieren; den unveraenderten Datums-/Fristscope erhalten.");
        var key = CaseScenario.Key();
        s.Case = await s.Service.ReturnReport(s.Case.Id, input, SqlFixture.Ra, key);
        Assert.Equal(Roles.Regulatory, s.Case.CurrentReport!.ReviewReturn!.Role);
        Assert.Equal(scope, s.Case.CurrentReport.ReviewReturn.PreviousScopeHash);
        Assert.NotEqual(scope, s.Case.CurrentReport.ScopeHash);
        Assert.Equal(Canonical.Hash(s.Case), Canonical.Hash(await s.Service.ReturnReport(s.Case.Id, input, SqlFixture.Ra, key)));
        Assert.Equal("report_correction_required", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Approve(s.Case.Id,
            new(s.Case.Version, "Unzulaessige Freigabe der unkorrigierten Fassung.", "Offene Befunde werden untersucht."),
            SqlFixture.Medical, CaseScenario.Key(), true))).Code);
        s.Case = await s.Regulatory.AttachMir(s.Case.Id, new(s.Case.Version, s.MirFacts(),
            "PMS ergaenzt die konkreten synthetischen Quellen; dies ist noch keine medizinische oder RA-Freigabe."), SqlFixture.Pms, CaseScenario.Key());
        Assert.Null(s.Case.CurrentReport!.ReviewReturn);
        Assert.Equal("medical_required", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Approve(s.Case.Id,
            new(s.Case.Version, "RA darf den erneuten medizinischen Review nicht umgehen.", null), SqlFixture.Ra, CaseScenario.Key(), false))).Code);
        await s.Approve();
        Assert.Equal(deadlines, Canonical.Hash(s.Case.Deadlines));
        Assert.Empty(s.Case.Submissions);
        await fixture.Store.Manifest(s.Case.Id);
    }

    [Fact]
    public async Task ProfileChangeBlocksNewApprovalButDoesNotInvalidateAnIdempotentHistoricalReceipt()
    {
        var s = new CaseScenario(fixture);
        await s.Create(); await s.Assess(); await s.Prepare();
        var input = new ApprovalCommand(s.Case.Version, "Belegte Pflichtdaten und medizinische Unsicherheit geprueft.",
            "Offene Befunde bleiben begruendet vorlaeufig und werden gezielt weiter untersucht.");
        var key = CaseScenario.Key();
        s.Case = await s.Service.Approve(s.Case.Id, input, SqlFixture.Medical, key, true);
        var saved = Canonical.Hash(s.Case);
        var profile = s.Case.CurrentReport!.Preparation!.Governance!;
        await fixture.Store.Execute<ProductGovernance>(new(profile.Id, "productGovernance", profile.ProductId, profile.Version,
            CaseScenario.Key(), "SyntheticProfileRevised", "Isolierter Test einer neuen Herstellerprofilfassung.",
            SqlFixture.Quality, new { profile.Version }, s.Clock.Now), p => p! with
            { Version = p!.Version + 1, Manufacturer = p.Manufacturer with { Name = p.Manufacturer.Name + " / revised synthetic fixture" } });
        Assert.Equal(saved, Canonical.Hash(await s.Service.Approve(s.Case.Id, input, SqlFixture.Medical, key, true)));
        Assert.Equal("report_profile_stale", (await Assert.ThrowsAsync<DomainException>(() => s.Service.Approve(s.Case.Id,
            new(s.Case.Version, "Altes Herstellerprofil ist keine aktuelle Freigabegrundlage.", null), SqlFixture.Ra, CaseScenario.Key(), false))).Code);
    }
}
