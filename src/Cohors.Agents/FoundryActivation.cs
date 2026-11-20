using System.Reflection;
using System.Security.Cryptography;
using Azure.AI.OpenAI;
using Azure.Identity;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Cohors.Agents;

public sealed record FoundryRoleProfile(FoundryTaskBinding? Binding, FoundryQualityReview? QualityReview);

public sealed class FoundryProfiles
{
    public Dictionary<string, FoundryRoleProfile> Roles { get; init; } = new(StringComparer.Ordinal);

    public FoundryRoleProfile Require(AgentRole role)
    {
        if (Roles is null || !Roles.TryGetValue(role.ToString(), out var profile) || profile?.Binding is null)
            throw new DomainException("model_profile_missing", "Das eigene Rollenprofil fehlt; kein gemeinsamer Modell-Fallback.", 409);
        Guard.Against(profile.Binding.Role != role, "model_profile_role",
            "Das Modellprofil gehoert einer anderen Rolle.", 409);
        Guard.Against(profile.QualityReview is null, "model_quality_review_missing",
            "Ein gebundener fachlicher Aktivierungsreview ist erforderlich.", 409);
        return Canonical.Read<FoundryRoleProfile>(Canonical.Serialize(profile));
    }
}

public sealed record FoundryRuntimeHashes(string PromptSha256, string PipelineConfigurationSha256);

public sealed record FoundryActivation(FoundryTaskBinding Binding, Uri Endpoint, string Instructions,
    AgentModelProvenance QualifiedBinding)
{
    public static FoundryRuntimeHashes RuntimeHashes(AgentRole role, string? taskInstruction = null, string language = "de")
    {
        Guard.Against(!Enum.IsDefined(role), "agent_role", "Nicht freigegebene Agentenrolle.");
        var prompt = Canonical.Hash(new { AgentRunner.PromptVersion, instructions = AgentRunner.Instructions(role),
            taskInstruction = taskInstruction ?? AgentInstructions.For(role), outputLanguage = language });
        var pipeline = Canonical.Hash(new
        {
            contract = "cohors-foundry-live-pipeline-1",
            AgentRunner.RequestBudgetSeconds,
            AgentRunner.MaximumRetries,
            AgentBoundary.MaximumNarrativeLength,
            tools = Array.Empty<string>(),
            responseIdentitySource = AgentModelIdentity.MetadataSource,
            runtime = Environment.Version.ToString(),
            binaries = new[] { typeof(AgentRunner).Assembly, typeof(AgentBoundary).Assembly, typeof(Canonical).Assembly,
                typeof(AzureOpenAIClient).Assembly, typeof(WorkloadIdentityCredential).Assembly,
                typeof(AIAgent).Assembly, typeof(ChatClient).Assembly, typeof(ChatClientAgent).Assembly,
                typeof(ChatResponse).Assembly, typeof(OpenAIClientExtensions).Assembly,
                typeof(OpenAIChatClientExtensions).Assembly }
                .Distinct().OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(BinaryHash).ToArray()
        });
        return new(prompt, pipeline);
    }

    public static FoundryActivation Require(FoundryProfiles profiles, AgentInput input)
    {
        var selected = profiles.Require(input.Role);
        var binding = selected.Binding!;
        var review = selected.QualityReview!;
        var actual = RuntimeHashes(input.Role, input.Instruction, input.Language);
        FoundryRoleProfile? analysis = null;
        FoundryRuntimeHashes? analysisHashes = null;
        if (input.Role == AgentRole.EvidenceAssurance)
        {
            analysis = profiles.Require(AgentRole.CausalityRisk);
            analysisHashes = RuntimeHashes(AgentRole.CausalityRisk);
            Validate(analysis, analysisHashes);
        }
        var endpoint = Validate(selected, actual, analysis?.Binding);
        var qualified = CreateQualifiedBinding(binding, review, actual, input.Evidence);
        if (analysis is not null)
        {
            var expected = CreateQualifiedBinding(analysis.Binding!, analysis.QualityReview!,
                analysisHashes!, input.Evidence);
            Guard.Against(input.AnalysisProvenance is null ||
                !AgentModelIdentity.SameQualifiedBinding(input.AnalysisProvenance, expected),
                "model_quality_assurance_input",
                "Assurance braucht den gebundenen Live-Analyselauf und denselben unverkuerzten Originalevidenz-Snapshot.", 409);
            AgentModelIdentity.RequireObservedModel(input.AnalysisProvenance!);
        }
        Guard.Against(input.ExpectedActivation is not null &&
            !AgentModelIdentity.SameQualifiedBinding(qualified, input.ExpectedActivation),
            "model_activation_mismatch", "API und AgentHost verwenden unterschiedliche Profil-/Reviewstaende.", 409);
        return new(binding, endpoint, AgentRunner.Instructions(input.Role), qualified);
    }

    public AgentModelProvenance BindResponse(FoundryDraftResponse response)
    {
        var provenance = QualifiedBinding with { ObservedModel = response.Observation };
        AgentModelIdentity.RequireObservedModel(provenance);
        return provenance;
    }

    private static Uri Validate(FoundryRoleProfile profile, FoundryRuntimeHashes actual, FoundryTaskBinding? analysis = null)
    {
        var binding = profile.Binding!;
        Guard.Against(binding.Model is null, "model_quality_profile", "Das eigene Rollenmodell fehlt.", 409);
        Guard.Against(binding.PromptVersion != AgentRunner.PromptVersion, "model_quality_pipeline",
            "Promptversion und ausfuehrender Stand unterscheiden sich.", 409);
        var endpoint = FoundryBoundary.RequireApproved(binding.Model!);
        FoundryQuality.ValidateActivationReview(binding, profile.QualityReview!, actual.PromptSha256,
            actual.PipelineConfigurationSha256, analysis);
        return endpoint;
    }

    private static AgentModelProvenance CreateQualifiedBinding(FoundryTaskBinding binding, FoundryQualityReview review,
        FoundryRuntimeHashes actual, Evidence[] evidence) =>
        new(binding.Role, binding.Model.Deployment!, binding.Model.ModelVersion!, FoundryQuality.BindingHash(binding),
            Canonical.Hash(review), review.QualifiedReviewSha256, actual.PromptSha256, actual.PipelineConfigurationSha256,
            binding.WorkloadTenantId, binding.WorkloadClientId, binding.WorkloadPrincipalId, Canonical.Hash(evidence),
            binding.ResponseIdentity!.ModelId);

    private static object BinaryHash(Assembly assembly)
    {
        try
        {
            Guard.Against(string.IsNullOrEmpty(assembly.Location), "model_quality_pipeline_runtime",
                "Der tatsaechliche Pipeline-Binaerstand ist nicht lesbar; Live bleibt gesperrt.", 409);
            using var stream = File.OpenRead(assembly.Location);
            return new { name = assembly.GetName().Name, sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
                module = assembly.ManifestModule.ModuleVersionId };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DomainException("model_quality_pipeline_runtime",
                "Der tatsaechliche Pipeline-Binaerstand konnte nicht gebunden werden; kein Ersatzhash.", 409);
        }
    }
}

public sealed class FoundryRemoteActivationRunner(FoundryProfiles profiles, Func<IAgentRunner> createTransport) : IAgentRunner
{
    private readonly Lazy<IAgentRunner> transport = new(createTransport);

    public async Task<AgentOutput> Run(AgentInput input, CancellationToken ct = default)
    {
        AgentBoundary.Validate(input);
        if (input.Mode != AgentMode.Live) return await transport.Value.Run(input, ct);
        ct.ThrowIfCancellationRequested();
        var activation = FoundryActivation.Require(profiles, input);
        var bound = input with { ExpectedActivation = activation.QualifiedBinding };
        var output = await transport.Value.Run(bound, ct);
        AgentBoundary.ValidateResult(bound, output);
        return output;
    }
}
