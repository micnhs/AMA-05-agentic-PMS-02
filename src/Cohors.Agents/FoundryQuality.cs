using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Agents;

public sealed record FoundryAssuranceSeparation(string AnalysisBindingSha256,
    string OriginalReadPathReviewSha256, string IndependenceReviewSha256);

public sealed record FoundryWorkloadAuthentication(string Kind, string? FederatedTokenFile = null);

public sealed record FoundryResponseIdentityBinding(string ModelId, string VersionEvidenceReviewSha256);

public sealed record FoundryTaskBinding(AgentRole Role, FoundryOptions Model, string ModelFamily,
    string CapabilityClass, string SelectionPolicy, string PromptVersion, string PromptSha256,
    string PipelineConfigurationSha256, Guid WorkloadTenantId, Guid WorkloadClientId, Guid WorkloadPrincipalId,
    string PrivatePathReviewSha256, string EvidencePreservationReviewSha256,
    FoundryAssuranceSeparation? Assurance = null, FoundryWorkloadAuthentication? Authentication = null,
    FoundryResponseIdentityBinding? ResponseIdentity = null);

public sealed record FoundryQualityMeasurements(int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative,
    int ClaimsReviewed, int UnsupportedClaims, int CitationsReviewed, int FabricatedCitations)
{
    public long DecisionExamples => (long)TruePositive + FalsePositive + FalseNegative + TrueNegative;
    public double? Sensitivity => Ratio(TruePositive, (long)TruePositive + FalseNegative);
    public double? Precision => Ratio(TruePositive, (long)TruePositive + FalsePositive);
    public double? FalsePositiveRate => Ratio(FalsePositive, (long)FalsePositive + TrueNegative);
    public double? FalseNegativeRate => Ratio(FalseNegative, (long)FalseNegative + TruePositive);
    private static double? Ratio(long numerator, long denominator) => denominator == 0 ? null : (double)numerator / denominator;
}

public sealed record FoundryQualityReview(string Contract, string BindingSha256, string DatasetSha256,
    string ReferenceStandardSha256, string ReferenceBasis, string EvaluationProtocolSha256, string EvaluationScope,
    string ResultsSha256, int DatasetCases, Dictionary<string, int> Coverage, FoundryQualityMeasurements Measurements,
    string UncertaintyReportSha256, string QualifiedReviewSha256, string Decision);

public sealed record FoundryQualityContractResult(string BindingSha256, int DatasetCases, FoundryQualityMeasurements Measurements)
{
    public string Status => "QualityReviewContractValidated";
    public string EvidenceLevel => "configuration-validation-not-model-quality-proof";
    public bool ActivationPerformed => false;
    public bool HumanMedicalAndRaApprovalStillRequired => true;
}

public static class FoundryQuality
{
    public const string Contract = "cohors-fabric-task-model-quality-1";
    public static string[] RequiredCoverage =>
        ["rare-critical", "negation", "conflict", "missing-evidence", "unsupported-claims", "fabricated-citations"];

    public static string BindingHash(FoundryTaskBinding binding) => Canonical.Hash(binding);

    // This validates the activation evidence contract. It neither evaluates a model,
    // verifies external report contents nor provisions/activates an endpoint.
    public static FoundryQualityContractResult ValidateActivationReview(FoundryTaskBinding binding, FoundryQualityReview review,
        string actualPromptSha256, string actualPipelineConfigurationSha256, FoundryTaskBinding? analysisBinding = null)
    {
        Guard.Against(!Enum.IsDefined(binding.Role) || binding.Model is null,
            "model_quality_profile", "Ein eigenes aufgabenspezifisches Modellprofil ist erforderlich.");
        Guard.Text(binding.ModelFamily, "Modellfamilie", 2, 200);
        Guard.Text(binding.PromptVersion, "Promptversion", 2, 200);
        Guard.Against(!Hash(binding.PromptSha256) || !Hash(binding.PipelineConfigurationSha256) ||
            binding.PromptSha256 != actualPromptSha256 || binding.PipelineConfigurationSha256 != actualPipelineConfigurationSha256,
            "model_quality_pipeline", "Review und tatsaechlicher Prompt-/Aufnahme-/Retrieval-/Analyse-/Assurance-Stand unterscheiden sich.");
        Guard.Against(binding.WorkloadTenantId == Guid.Empty || binding.WorkloadClientId == Guid.Empty ||
            binding.WorkloadPrincipalId == Guid.Empty || !Hash(binding.PrivatePathReviewSha256) ||
            !Hash(binding.EvidencePreservationReviewSha256),
            "model_quality_runtime", "Eigene Workloadidentitaet, Privatpfad und verlustfreie Evidenzbehandlung muessen gebunden sein.");
        Guard.Against(binding.Authentication is null ||
            (binding.Authentication.Kind == "ManagedIdentity" ? binding.Authentication.FederatedTokenFile is not null :
             binding.Authentication.Kind != "WorkloadIdentity" ||
             string.IsNullOrWhiteSpace(binding.Authentication.FederatedTokenFile) ||
             !Path.IsPathFullyQualified(binding.Authentication.FederatedTokenFile)),
            "model_workload_authentication", "Nur die explizit gebundene Workloadidentitaet ist erlaubt; keine ambiente Credentialkette.");
        Guard.Against(binding.SelectionPolicy is not ("QualityFirst" or "TaskProvenCostOptimized") ||
            binding.CapabilityClass is not ("Strong" or "Frontier" or "TaskQualified"),
            "model_quality_selection", "Nicht gebundene Modellauswahl; kein automatischer Billigmodell-Fallback.");
        var critical = binding.Role is AgentRole.CausalityRisk or AgentRole.TrendSignal or AgentRole.EvidenceAssurance
            or AgentRole.VigilanceAuthoring or AgentRole.PsurAuthoring;
        Guard.Against(critical && (binding.SelectionPolicy != "QualityFirst" || binding.CapabilityClass == "TaskQualified"),
            "model_quality_priority", "Analyse, Risiko, Empfehlungen und Assurance werden nach Fachqualitaet ausgewaehlt.");
        FoundryBoundary.RequireApproved(binding.Model!);
        Guard.Against(binding.ResponseIdentity is null || !Hash(binding.ResponseIdentity.VersionEvidenceReviewSha256),
            "model_response_identity_binding", "Eine qualifizierte Provider-Snapshotkennung und ihr Versionssemantiknachweis fehlen.", 409);
        var responseVersion = AgentModelIdentity.RequireDatedSnapshotVersion(binding.ResponseIdentity!.ModelId,
            "model_response_identity_binding");
        Guard.Against(responseVersion != binding.Model!.ModelVersion, "model_response_identity_binding",
            "Provider-Snapshotkennung und qualifizierte Modellversion widersprechen sich.", 409);

        if (binding.Role == AgentRole.EvidenceAssurance)
        {
            var separation = binding.Assurance;
            Guard.Against(separation is null || analysisBinding is null || analysisBinding.Role != AgentRole.CausalityRisk,
                "model_quality_assurance", "Assurance braucht eine explizite Bindung an den unabhaengig zu pruefenden Analysepfad.");
            Guard.Against(separation!.AnalysisBindingSha256 != BindingHash(analysisBinding!) ||
                !Hash(separation.OriginalReadPathReviewSha256) || !Hash(separation.IndependenceReviewSha256) ||
                binding.WorkloadClientId == analysisBinding!.WorkloadClientId ||
                binding.WorkloadPrincipalId == analysisBinding.WorkloadPrincipalId ||
                binding.WorkloadTenantId != analysisBinding.WorkloadTenantId ||
                binding.PipelineConfigurationSha256 != analysisBinding.PipelineConfigurationSha256,
                "model_quality_assurance", "Separater Originalzugriff und unabhaengige Ausfuehrung fehlen oder sind veraltet.");
        }
        else Guard.Against(binding.Assurance is not null, "model_quality_assurance", "Assurance-Nachweise sind nicht auf andere Rollen uebertragbar.");

        Guard.Against(review.Contract != Contract || review.BindingSha256 != BindingHash(binding),
            "model_quality_regression", "Modell-, Versions-, Prompt-, Retrieval- oder Laufzeitwechsel braucht eine neu gebundene Regression.");
        Guard.Against(review.ReferenceBasis != "ProfessionallyAdjudicated" ||
            review.EvaluationScope != "IntakeRetrievalAnalysisAssurance" ||
            review.Decision != "ApprovedForHumanReviewedDrafting",
            "model_quality_reference", "KI-Judge, Modellkonfidenz oder Einzelaufruf ersetzen keine fachlich referenzierte Gesamtkettenfreigabe.");
        foreach (var value in new[] { review.DatasetSha256, review.ReferenceStandardSha256, review.EvaluationProtocolSha256,
            review.ResultsSha256, review.UncertaintyReportSha256, review.QualifiedReviewSha256 })
            Guard.Against(!Hash(value), "model_quality_review", "Datensatz, Referenz, Protokoll, Ergebnisse, Unsicherheit und fachlicher Review muessen gebunden sein.");
        Guard.Against(review.DatasetCases < 1 || review.Coverage is null ||
            RequiredCoverage.Any(name => !review.Coverage.TryGetValue(name, out var count) || count < 1 || count > review.DatasetCases),
            "model_quality_coverage", "Kritische seltene Faelle, Negation, Konflikte, Evidenzluecken und unbelegte Behauptungen/Zitate fehlen im Nachweis.");
        var metrics = review.Measurements;
        Guard.Against(metrics is null || metrics.TruePositive < 0 || metrics.FalsePositive < 0 || metrics.FalseNegative < 0 ||
            metrics.TrueNegative < 0 || metrics.DecisionExamples < review.DatasetCases ||
            metrics.ClaimsReviewed < 1 || metrics.UnsupportedClaims < 0 || metrics.UnsupportedClaims > metrics.ClaimsReviewed ||
            metrics.CitationsReviewed < 1 || metrics.FabricatedCitations < 0 || metrics.FabricatedCitations > metrics.CitationsReviewed ||
            (long)metrics.TruePositive + metrics.FalseNegative == 0 || (long)metrics.FalsePositive + metrics.TrueNegative == 0,
            "model_quality_measurements", "FP/FN und ihre Bezugszahlen muessen getrennt messbar sein; unbekannt ist nicht null Fehler.");
        return new(review.BindingSha256, review.DatasetCases, metrics!);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
