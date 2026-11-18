using Cohors.Agents;
using Cohors.Domain;

namespace Cohors.Tests;

public sealed class FoundryBoundaryTests
{
    private static FoundryOptions Settings(string sku = "Standard", string boundary = "EU-Member-States",
        string[]? countries = null)
    {
        var review = new FoundryBoundaryApproval("synthetic-review-2026-001", "", sku, "germanywestcentral", "Germany", countries ?? ["DE"]);
        var settings = new FoundryOptions("https://synthetic-not-provisioned.openai.azure.com/", "unprovisioned", "test-version", boundary, review);
        return settings with { BoundaryApproval = review with { ConfigurationHash = FoundryBoundary.ConfigurationHash(settings, review) } };
    }

    [Theory]
    [InlineData("DataZoneStandard")]
    [InlineData("DataZoneProvisionedManaged")]
    [InlineData("GlobalStandard")]
    [InlineData("GlobalProvisionedManaged")]
    [InlineData("DeveloperTier")]
    public void T08_NonRegionalSkusAreRejectedEvenWithAnEuLabel(string sku)
    {
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(sku)));
    }

    [Fact]
    public void T08_ProviderBoundaryNeedsAConfigurationBoundReviewNotABareLabel()
    {
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(boundary: "EUDataZone")));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(boundary: "EU")));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(countries: ["DE", "NO"])));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings(countries: ["CH"])));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings() with { BoundaryApproval = null }));
        Assert.Throws<DomainException>(() => FoundryBoundary.RequireApproved(Settings() with { ModelVersion = "changed" }));
        Assert.Equal("synthetic-not-provisioned.openai.azure.com", FoundryBoundary.RequireApproved(Settings()).Host);
    }

    [Theory]
    [InlineData(AgentRole.CausalityRisk)]
    [InlineData(AgentRole.TrendSignal)]
    [InlineData(AgentRole.EvidenceAssurance)]
    public void CriticalRolesCannotTradeQualityForACostLabel(AgentRole role)
    {
        var binding = Binding(role) with { SelectionPolicy = "TaskProvenCostOptimized" };
        Assert.Equal("model_quality_priority", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, Review(binding), Hash, Pipeline)).Code);
    }

    [Fact]
    public void ConfusionMatrixKeepsFalsePositivesAndFalseNegativesSeparate()
    {
        var binding = Binding(AgentRole.Ingestion) with { SelectionPolicy = "TaskProvenCostOptimized", CapabilityClass = "TaskQualified" };
        var result = FoundryQuality.ValidateActivationReview(binding, Review(binding), Hash, Pipeline);
        Assert.Equal(20, result.Measurements.DecisionExamples);
        Assert.Equal(0.8, result.Measurements.Sensitivity!.Value, 8);
        Assert.Equal(8d / 11, result.Measurements.Precision!.Value, 8);
        Assert.Equal(0.3, result.Measurements.FalsePositiveRate!.Value, 8);
        Assert.Equal(0.2, result.Measurements.FalseNegativeRate!.Value, 8);
        Assert.False(result.ActivationPerformed);
        Assert.True(result.HumanMedicalAndRaApprovalStillRequired);
        Assert.Null(new FoundryQualityMeasurements(0, 0, 0, 0, 0, 0, 0, 0).Sensitivity);
    }

    [Fact]
    public void ActualPromptOrPipelineChangeRequiresNewRegression()
    {
        var binding = Binding(AgentRole.CausalityRisk);
        var review = Review(binding);
        Assert.Equal("model_quality_pipeline", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review, Pipeline, Pipeline)).Code);
        Assert.Equal("model_quality_pipeline", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review, Hash, Hash)).Code);
        var changed = binding with { WorkloadClientId = Guid.NewGuid() };
        Assert.Equal("model_quality_regression", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(changed, review, Hash, Pipeline)).Code);
        var model = Settings() with { Deployment = "another-unprovisioned-model" };
        var boundary = model.BoundaryApproval!;
        model = model with { BoundaryApproval = boundary with { ConfigurationHash = FoundryBoundary.ConfigurationHash(model, boundary) } };
        changed = binding with { Model = model };
        Assert.Equal("model_quality_regression", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(changed, review, Hash, Pipeline)).Code);
    }

    [Fact]
    public void JudgeOnlyReferencesAndIncompleteCoverageCannotApproveQuality()
    {
        var binding = Binding(AgentRole.CausalityRisk);
        var review = Review(binding);
        Assert.Equal("model_quality_reference", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review with { ReferenceBasis = "AIJudgeOnly" }, Hash, Pipeline)).Code);
        Assert.Equal("model_quality_review", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review with { QualifiedReviewSha256 = "" }, Hash, Pipeline)).Code);
        var coverage = new Dictionary<string, int>(review.Coverage) { ["rare-critical"] = 0 };
        Assert.Equal("model_quality_coverage", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review with { Coverage = coverage }, Hash, Pipeline)).Code);
        Assert.Equal("model_quality_measurements", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding, review with
            { Measurements = review.Measurements with { FalseNegative = -1 } }, Hash, Pipeline)).Code);
        Assert.Equal("model_quality_runtime", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(binding with { EvidencePreservationReviewSha256 = "" }, review, Hash, Pipeline)).Code);
    }

    [Fact]
    public void AssuranceNeedsItsOwnOriginalReadAndExecutionEvidenceNotAnotherModelName()
    {
        var analysis = Binding(AgentRole.CausalityRisk);
        var assurance = Binding(AgentRole.EvidenceAssurance) with { ModelFamily = "different-synthetic-family" };
        Assert.Equal("model_quality_assurance", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(assurance, Review(assurance), Hash, Pipeline, analysis)).Code);
        assurance = assurance with
        {
            ModelFamily = analysis.ModelFamily,
            WorkloadTenantId = analysis.WorkloadTenantId,
            Assurance = new(FoundryQuality.BindingHash(analysis), Hash, Pipeline)
        };
        Assert.False(FoundryQuality.ValidateActivationReview(assurance, Review(assurance), Hash, Pipeline, analysis).ActivationPerformed);
        var aliased = assurance with { WorkloadClientId = analysis.WorkloadClientId };
        Assert.Equal("model_quality_assurance", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(aliased, Review(aliased), Hash, Pipeline, analysis)).Code);
        var changedAnalysis = analysis with { PromptVersion = "another-version" };
        Assert.Equal("model_quality_assurance", Assert.Throws<DomainException>(() =>
            FoundryQuality.ValidateActivationReview(assurance, Review(assurance), Hash, Pipeline, changedAnalysis)).Code);
    }

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Pipeline = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static FoundryTaskBinding Binding(AgentRole role) => new(role, Settings(), "synthetic-unselected-family",
        "Strong", "QualityFirst", AgentRunner.PromptVersion, Hash, Pipeline, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Hash, Hash);
    private static FoundryQualityReview Review(FoundryTaskBinding binding) => new(FoundryQuality.Contract,
        FoundryQuality.BindingHash(binding), Hash, Hash, "ProfessionallyAdjudicated", Hash,
        "IntakeRetrievalAnalysisAssurance", Hash, 20, FoundryQuality.RequiredCoverage.ToDictionary(name => name, _ => 1),
        new(8, 3, 2, 7, 40, 2, 30, 1), Hash, Hash, "ApprovedForHumanReviewedDrafting");
}
