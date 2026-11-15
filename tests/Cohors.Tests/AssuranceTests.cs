using Cohors.Domain;

namespace Cohors.Tests;

public sealed class AssuranceTests
{
    private static readonly Evidence Source = new("clinical", 1, "ortho", "Synthetic finding", "ClinicalFollowUp",
        "Synthetic observation; causality is unresolved.", "hash", "cohors:synthetic", "section 1", "Synthetic", "Product",
        DateTimeOffset.MinValue, null, DateTimeOffset.MinValue, true, true, "hash");
    private static AssessmentContent Valid() => new(
        AssessmentRules.RequiredSections.Select(key => new AnalysisSection(key, key,
            "Synthetic reasoned section without a definitive clinical conclusion.", key is "context" or "review" ? [] : ["C1"])).ToArray(),
        [new("C1", "The supplied synthetic observation is recorded.", "Observed", [new(Source.Id, Source.Hash, Source.Locator, Source.Content)])],
        [new("Human review", "Unresolved causality requires a reasoned independent human review.", Roles.Medical, "Immediate",
            "No final causality or benefit-risk acceptance.", ["C1"])], ["Causality remains unresolved."], null, "No calibrated probability estimate.");

    [Fact]
    public void T04_EmptyClaimsAndRecommendationsCannotProduceAReviewableNarrative()
    {
        var content = Valid() with { Claims = [], Recommendations = [] };
        var findings = AssessmentRules.Check(content, [Source]);
        Assert.Contains(findings, f => f.Code == "claims_missing" && f.Severity == "Critical");
        Assert.Contains(findings, f => f.Code == "recommendations_missing" && f.Severity == "Critical");
        Assert.Contains(findings, f => f.Code == "section_claims" && f.Severity == "Critical");
        var assessment = new Assessment(1, DateTimeOffset.UtcNow, "human", "Human", "test", "none", [Source.Id], "hash",
            content, new("qa", 1, DateTimeOffset.UtcNow, "test", findings, "Structural review"));
        Assert.Throws<DomainException>(() => AssessmentRules.RequireReviewable(assessment, true,
            "Urgency must not remove evidence requirements."));
    }

    [Fact]
    public void T04_FactualSectionsNeedResolvableClaimsButMetadataDoesNotNeedArtificialCitations()
    {
        var valid = Valid();
        Assert.Empty(AssessmentRules.Check(valid, [Source]));
        var invalid = valid with { Sections = valid.Sections.Select(s => s.Key == "technical" ? s with { ClaimIds = ["not-present"] } : s).ToArray() };
        Assert.Contains(AssessmentRules.Check(invalid, [Source]), f => f.Code == "section_claims");
        invalid = valid with { Sections = valid.Sections.Select(s => s.Key == "medical" ? s with { ClaimIds = [] } : s).ToArray() };
        Assert.Contains(AssessmentRules.Check(invalid, [Source]), f => f.Code == "section_claims");
    }

    [Fact]
    public void C03_ExpectedSideEffectRequiresProductInformationQuantifiedTdAndTrendReportingTogether()
    {
        Evidence Document(string id, string kind) => Source with { Id = id, Kind = kind };
        Citation Cite(Evidence e) => new(e.Id, e.Hash, e.Locator, e.Content);
        var information = Document("ifu", "IFU");
        var technical = Document("risk", "RiskFile");
        var trend = Document("plan", "PMSPlan");
        Evidence[] documents = [information, technical, trend];
        var exception = new ExpectedSideEffectException("Synthetische Nebenwirkung", Cite(information), Cite(technical), Cite(trend),
            "Gemeinsame medizinische Ausnahmebegruendung aus allen drei kontrollierten Dokumenten, ohne automatische Risikoakzeptanz.",
            0.1m, "pro 1000 synthetische Implantate", "pms-v1", true);
        EvidenceRules.RequireExpectedSideEffect(exception, documents, "pms-v1");
        foreach (var missing in documents)
            Assert.Throws<DomainException>(() => EvidenceRules.RequireExpectedSideEffect(exception,
                documents.Where(e => e.Id != missing.Id).ToArray(), "pms-v1"));
        Assert.Throws<DomainException>(() => EvidenceRules.RequireExpectedSideEffect(exception with { ReferenceRate = null }, documents, "pms-v1"));
        Assert.Throws<DomainException>(() => EvidenceRules.RequireExpectedSideEffect(exception with { IncludedInTrendReporting = false }, documents, "pms-v1"));
        Assert.Throws<DomainException>(() => EvidenceRules.RequireExpectedSideEffect(exception, documents, "pms-v2"));
    }

    [Fact]
    public void T04_MalformedNarrativeStructureProducesACriticalFindingRatherThanAnUnhandledException()
    {
        var malformed = Canonical.Read<AssessmentContent>("""{"sections":[{"key":"medical","text":null,"claimIds":[]}],"claims":[],"recommendations":[],"uncertainties":["open"],"probability":null,"probabilityBasis":"Unknown"}""");
        Assert.Contains(AssessmentRules.Check(malformed, [Source]), f => f.Code == "assessment_schema" && f.Severity == "Critical");
    }
}
