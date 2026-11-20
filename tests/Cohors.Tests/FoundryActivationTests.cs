using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Cohors.Agents;
using Cohors.Application;
using Cohors.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Cohors.Tests;

public sealed partial class FoundryBoundaryTests
{
    [Theory]
    [InlineData("missing-profile", "model_profile_missing")]
    [InlineData("foreign-profile", "model_profile_role")]
    [InlineData("missing-review", "model_quality_review_missing")]
    [InlineData("missing-model", "model_quality_profile")]
    [InlineData("stale-prompt", "model_quality_pipeline")]
    [InlineData("stale-pipeline", "model_quality_pipeline")]
    [InlineData("stale-prompt-version", "model_quality_pipeline")]
    [InlineData("changed-instruction", "model_quality_pipeline")]
    [InlineData("changed-language", "model_quality_pipeline")]
    [InlineData("changed-model-version", "model_quality_regression")]
    [InlineData("missing-boundary", "model_boundary")]
    [InlineData("stale-boundary", "model_boundary_stale")]
    [InlineData("non-eu-boundary", "model_boundary_countries")]
    [InlineData("global-sku", "model_boundary_sku")]
    [InlineData("ai-judge", "model_quality_reference")]
    [InlineData("missing-coverage", "model_quality_coverage")]
    [InlineData("missing-private-path", "model_quality_runtime")]
    [InlineData("missing-original-preservation", "model_quality_runtime")]
    [InlineData("missing-authentication", "model_workload_authentication")]
    [InlineData("ambient-authentication", "model_workload_authentication")]
    [InlineData("missing-federated-token-file", "model_workload_authentication")]
    [InlineData("missing-response-binding", "model_response_identity_binding")]
    [InlineData("response-binding-alias", "model_response_identity_binding")]
    [InlineData("response-binding-wrong-version", "model_response_identity_binding")]
    [InlineData("missing-version-evidence", "model_response_identity_binding")]
    [InlineData("changed-response-binding-without-review", "model_quality_regression")]
    [InlineData("assurance-same-client", "model_quality_assurance")]
    [InlineData("assurance-same-principal", "model_quality_assurance")]
    [InlineData("assurance-foreign-tenant", "model_quality_assurance")]
    [InlineData("assurance-missing-analysis", "model_profile_missing")]
    [InlineData("assurance-foreign-analysis", "model_profile_role")]
    [InlineData("assurance-unreviewed-analysis", "model_quality_review_missing")]
    [InlineData("assurance-stale-analysis", "model_quality_pipeline")]
    [InlineData("assurance-stale-boundary", "model_boundary_stale")]
    [InlineData("assurance-ai-judge-analysis", "model_quality_reference")]
    [InlineData("assurance-missing-separation", "model_quality_assurance")]
    [InlineData("assurance-missing-analysis-output", "model_quality_assurance_input")]
    [InlineData("assurance-changed-originals", "model_quality_assurance_input")]
    [InlineData("assurance-no-observation", "model_response_identity_missing")]
    [InlineData("assurance-wrong-observation", "model_response_identity_mismatch")]
    [InlineData("expected-activation-mismatch", "model_activation_mismatch")]
    [InlineData("expected-observation-present", "model_activation_mismatch")]
    public async Task LiveRefusalPrecedesEveryCredentialClientAndTransportFactory(string fault, string code)
    {
        var profiles = SyntheticLiveProfiles();
        var role = fault.StartsWith("assurance-", StringComparison.Ordinal) ? AgentRole.EvidenceAssurance : AgentRole.CausalityRisk;
        var input = SyntheticLiveInput(role, profiles);
        var analysis = profiles.Roles[nameof(AgentRole.CausalityRisk)].Binding!;
        switch (fault)
        {
            case "missing-profile": profiles.Roles.Remove(role.ToString()); break;
            case "foreign-profile": profiles.Roles[role.ToString()] = profiles.Roles[nameof(AgentRole.Ingestion)]; break;
            case "missing-review": profiles.Roles[role.ToString()] = profiles.Roles[role.ToString()] with { QualityReview = null }; break;
            case "missing-model": ChangeBinding(profiles, role, b => b with { Model = null! }); break;
            case "stale-prompt": ChangeBinding(profiles, role, b => b with { PromptSha256 = Hash }); break;
            case "stale-pipeline": ChangeBinding(profiles, role, b => b with { PipelineConfigurationSha256 = Hash }); break;
            case "stale-prompt-version": ChangeBinding(profiles, role, b => b with { PromptVersion = "synthetic-old-prompt" }); break;
            case "changed-instruction": input = input with { Instruction = "SYNTHETIC changed task; not the reviewed prompt." }; break;
            case "changed-language": input = input with { Language = "en" }; break;
            case "changed-model-version":
                ChangeBinding(profiles, role, b => b with
                {
                    Model = RebindBoundary(b.Model with { ModelVersion = "2026-11-17" }),
                    ResponseIdentity = b.ResponseIdentity! with { ModelId = "synthetic-unselected-family-2026-11-17" }
                },
                    rebindReview: false);
                break;
            case "missing-boundary": ChangeBinding(profiles, role, b => b with { Model = b.Model with { BoundaryApproval = null } }); break;
            case "stale-boundary": ChangeBinding(profiles, role, b => b with { Model = b.Model with { ModelVersion = "synthetic-next-version" } }); break;
            case "non-eu-boundary": ChangeBinding(profiles, role, b => b with { Model = Settings(countries: ["NO"]) }); break;
            case "global-sku": ChangeBinding(profiles, role, b => b with { Model = Settings(sku: "GlobalStandard") }); break;
            case "ai-judge": ChangeReview(profiles, role, r => r with { ReferenceBasis = "AIJudgeOnly" }); break;
            case "missing-coverage": ChangeReview(profiles, role, r => r with { Coverage = new(r.Coverage) { ["rare-critical"] = 0 } }); break;
            case "missing-private-path": ChangeBinding(profiles, role, b => b with { PrivatePathReviewSha256 = "" }); break;
            case "missing-original-preservation": ChangeBinding(profiles, role, b => b with { EvidencePreservationReviewSha256 = "" }); break;
            case "missing-authentication": ChangeBinding(profiles, role, b => b with { Authentication = null }); break;
            case "ambient-authentication": ChangeBinding(profiles, role, b => b with { Authentication = new("DefaultAzureCredential") }); break;
            case "missing-federated-token-file": ChangeBinding(profiles, role, b => b with { Authentication = new("WorkloadIdentity") }); break;
            case "missing-response-binding": ChangeBinding(profiles, role, b => b with { ResponseIdentity = null }); break;
            case "response-binding-alias":
                ChangeBinding(profiles, role, b => b with { ResponseIdentity = b.ResponseIdentity! with { ModelId = "synthetic-alias" } });
                break;
            case "response-binding-wrong-version":
                ChangeBinding(profiles, role, b => b with
                { ResponseIdentity = b.ResponseIdentity! with { ModelId = "synthetic-unselected-family-2026-11-17" } });
                break;
            case "missing-version-evidence":
                ChangeBinding(profiles, role, b => b with { ResponseIdentity = b.ResponseIdentity! with { VersionEvidenceReviewSha256 = "" } });
                break;
            case "changed-response-binding-without-review":
                ChangeBinding(profiles, role, b => b with
                { ResponseIdentity = b.ResponseIdentity! with { ModelId = "synthetic-other-family-2026-11-16" } }, rebindReview: false);
                break;
            case "assurance-same-client": ChangeBinding(profiles, role, b => b with { WorkloadClientId = analysis.WorkloadClientId }); break;
            case "assurance-same-principal": ChangeBinding(profiles, role, b => b with { WorkloadPrincipalId = analysis.WorkloadPrincipalId }); break;
            case "assurance-foreign-tenant": ChangeBinding(profiles, role, b => b with { WorkloadTenantId = Guid.Parse("00000000-0000-4000-8000-000000000099") }); break;
            case "assurance-missing-analysis": profiles.Roles.Remove(nameof(AgentRole.CausalityRisk)); break;
            case "assurance-foreign-analysis": profiles.Roles[nameof(AgentRole.CausalityRisk)] = profiles.Roles[nameof(AgentRole.Ingestion)]; break;
            case "assurance-unreviewed-analysis":
                profiles.Roles[nameof(AgentRole.CausalityRisk)] = profiles.Roles[nameof(AgentRole.CausalityRisk)] with { QualityReview = null };
                break;
            case "assurance-stale-analysis": ChangeBinding(profiles, AgentRole.CausalityRisk, b => b with { PromptSha256 = Hash }); break;
            case "assurance-stale-boundary":
                ChangeBinding(profiles, AgentRole.CausalityRisk, b => b with { Model = b.Model with { ModelVersion = "synthetic-next-version" } });
                break;
            case "assurance-ai-judge-analysis": ChangeReview(profiles, AgentRole.CausalityRisk, r => r with { ReferenceBasis = "AIJudgeOnly" }); break;
            case "assurance-missing-separation": ChangeBinding(profiles, role, b => b with { Assurance = null }); break;
            case "assurance-missing-analysis-output": input = input with { AnalysisProvenance = null }; break;
            case "assurance-changed-originals": input = input with { Evidence = input.Evidence[..^1] }; break;
            case "assurance-no-observation":
                input = input with { AnalysisProvenance = input.AnalysisProvenance! with { ObservedModel = null } };
                break;
            case "assurance-wrong-observation":
                input = input with { AnalysisProvenance = input.AnalysisProvenance! with
                { ObservedModel = SyntheticObservation() with { ModelId = "synthetic-other-family-2026-11-16" } } };
                break;
            case "expected-activation-mismatch":
                input = input with { ExpectedActivation = FoundryActivation.Require(profiles, input).QualifiedBinding with { BindingSha256 = Hash } };
                break;
            case "expected-observation-present":
                input = input with { ExpectedActivation = FoundryActivation.Require(profiles, input).QualifiedBinding with
                { ObservedModel = SyntheticObservation() } };
                break;
            default: throw new InvalidOperationException("Unknown synthetic refusal fixture.");
        }
        await AssertNoLiveFactories(profiles, input, code);
    }

    [Theory]
    [InlineData(AgentRole.CausalityRisk, "TaskProvenCostOptimized", "Strong")]
    [InlineData(AgentRole.TrendSignal, "TaskProvenCostOptimized", "Strong")]
    [InlineData(AgentRole.EvidenceAssurance, "TaskProvenCostOptimized", "Strong")]
    [InlineData(AgentRole.VigilanceAuthoring, "TaskProvenCostOptimized", "Strong")]
    [InlineData(AgentRole.PsurAuthoring, "TaskProvenCostOptimized", "Strong")]
    [InlineData(AgentRole.CausalityRisk, "QualityFirst", "TaskQualified")]
    [InlineData(AgentRole.TrendSignal, "QualityFirst", "TaskQualified")]
    [InlineData(AgentRole.EvidenceAssurance, "QualityFirst", "TaskQualified")]
    [InlineData(AgentRole.VigilanceAuthoring, "QualityFirst", "TaskQualified")]
    [InlineData(AgentRole.PsurAuthoring, "QualityFirst", "TaskQualified")]
    public async Task EveryCriticalRoleRejectsCostOptimizationAndTaskQualifiedBeforeFactories(AgentRole role, string policy, string capability)
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(role, profiles);
        ChangeBinding(profiles, role, b => b with { SelectionPolicy = policy, CapabilityClass = capability });
        await AssertNoLiveFactories(profiles, input, "model_quality_priority");
    }

    [Theory]
    [InlineData(AgentRole.Ingestion)]
    [InlineData(AgentRole.CausalityRisk)]
    [InlineData(AgentRole.TrendSignal)]
    [InlineData(AgentRole.EvidenceAssurance)]
    [InlineData(AgentRole.VigilanceAuthoring)]
    [InlineData(AgentRole.PsurAuthoring)]
    public async Task SyntheticReviewedRoleUsesOnlyItsBoundIdentityModelAndAuditProvenance(AgentRole role)
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(role, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var output = await new AgentRunner(profiles, factory).Run(input);
        var selected = profiles.Roles[role.ToString()];
        var binding = selected.Binding!;
        Assert.Equal(Canonical.Hash(binding), Canonical.Hash(Assert.Single(factory.Bindings)));
        Assert.Equal(1, factory.Credentials);
        Assert.Equal(1, factory.Clients);
        Assert.Equal(1, factory.Requests);
        Assert.Equal(binding.Model.Deployment + "@" + binding.Model.ModelVersion, output.Model);
        Assert.Equal(AgentRunner.PromptVersion, output.PromptVersion);
        Assert.Empty(output.Tools);
        Assert.NotNull(output.ModelProvenance);
        Assert.Equal(role, output.ModelProvenance.Role);
        Assert.Equal(FoundryQuality.BindingHash(binding), output.ModelProvenance.BindingSha256);
        Assert.Equal(Canonical.Hash(selected.QualityReview), output.ModelProvenance.ReviewSha256);
        Assert.Equal(selected.QualityReview!.QualifiedReviewSha256, output.ModelProvenance.QualifiedReviewSha256);
        Assert.Equal(binding.WorkloadClientId, output.ModelProvenance.WorkloadClientId);
        Assert.Equal(binding.WorkloadPrincipalId, output.ModelProvenance.WorkloadPrincipalId);
        Assert.Equal(Canonical.Hash(input.Evidence), output.ModelProvenance.EvidenceSha256);
        Assert.Equal(binding.ResponseIdentity!.ModelId, output.ModelProvenance.ExpectedResponseModelId);
        Assert.Equal(SyntheticObservation(), output.ModelProvenance.ObservedModel);
        Assert.NotEqual(binding.Model.Deployment, output.ModelProvenance.ObservedModel!.ModelId);
        Assert.Equal(binding.PromptSha256, Canonical.Hash(new { AgentRunner.PromptVersion, instructions = factory.Instructions,
            taskInstruction = input.Instruction, outputLanguage = input.Language }));
        using var request = JsonDocument.Parse(factory.Prompt!);
        Assert.Equal(input.Role.ToString(), request.RootElement.GetProperty("role").GetString());
        Assert.Equal(Canonical.Serialize(input.Evidence),
            Canonical.Serialize(request.RootElement.GetProperty("evidence")));
        AgentBoundary.ValidateResult(input, output);
    }

    [Fact]
    public async Task OnlySyntheticReviewedIngestionCanUseTaskProvenCostOptimization()
    {
        var profiles = SyntheticLiveProfiles();
        ChangeBinding(profiles, AgentRole.Ingestion, b => b with { SelectionPolicy = "TaskProvenCostOptimized", CapabilityClass = "TaskQualified" });
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var output = await new AgentRunner(profiles, factory).Run(input);
        Assert.Equal("TaskProvenCostOptimized", Assert.Single(factory.Bindings).SelectionPolicy);
        Assert.Equal(1, factory.Requests);
        Assert.NotNull(output.ModelProvenance);
    }

    [Fact]
    public async Task ASecondLiveCallRechecksItsReviewEvenAfterTheTransportExists()
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var creations = 0;
        var runner = new FoundryRemoteActivationRunner(profiles, () =>
        {
            creations++;
            return new AgentRunner(profiles, factory);
        });
        await runner.Run(input);
        ChangeReview(profiles, input.Role, r => r with { BindingSha256 = Hash });
        Assert.Equal("model_quality_regression", (await Assert.ThrowsAsync<DomainException>(() => runner.Run(input))).Code);
        Assert.Equal(1, creations);
        Assert.Equal(1, factory.Credentials);
        Assert.Equal(1, factory.Clients);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task ApiAndHostProfileDriftCannotReachTheHostCredentialOrModelClient()
    {
        var api = SyntheticLiveProfiles();
        var host = SyntheticLiveProfiles();
        ChangeBinding(host, AgentRole.Ingestion, b => b with
        {
            Model = RebindBoundary(b.Model with { ModelVersion = "2026-11-17" }),
            ResponseIdentity = b.ResponseIdentity! with { ModelId = "synthetic-unselected-family-2026-11-17" }
        });
        var input = SyntheticLiveInput(AgentRole.Ingestion, api);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var runner = new FoundryRemoteActivationRunner(api, () => new AgentRunner(host, factory));
        Assert.Equal("model_activation_mismatch", (await Assert.ThrowsAsync<DomainException>(() => runner.Run(input))).Code);
        Assert.Equal(0, factory.Credentials);
        Assert.Equal(0, factory.Clients);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task ProviderFailureDoesNotSwitchModelRoleOrReplay()
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input)) { Error = new Azure.RequestFailedException(503, "SYNTHETIC outage") };
        var error = await Assert.ThrowsAsync<AgentExecutionException>(() => new AgentRunner(profiles, factory).Run(input));
        Assert.Equal("foundry_request_failed", error.Code);
        Assert.Single(factory.Bindings);
        Assert.Equal(1, factory.Credentials);
        Assert.Equal(1, factory.Clients);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task ReplayAndFailureNeedNoProfileAndKeepTheirHistoricalCanonicalShapes()
    {
        var input = SyntheticInput(AgentRole.CausalityRisk) with { Mode = AgentMode.Replay };
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var runner = new AgentRunner(new(), factory);
        var output = await runner.Run(input);
        Assert.Equal("replay-2026.1", output.Model);
        Assert.Null(output.ModelProvenance);
        Assert.Equal(Canonical.Serialize(new { output.Model, output.PromptVersion, output.Assessment, output.Narrative, output.Findings, output.Tools }),
            Canonical.Serialize(output));
        Assert.Equal(Canonical.Serialize(new { input.Role, input.Mode, input.Case, input.Snapshot, input.Trend, input.Evidence,
            input.Instruction, input.Revision, input.Language }), Canonical.Serialize(input));
        Assert.Equal("simulated_model_outage", (await Assert.ThrowsAsync<DomainException>(() =>
            runner.Run(input with { Mode = AgentMode.Failure }))).Code);
        Assert.Equal(0, factory.Credentials);
        Assert.Equal(0, factory.Clients);
        Assert.Equal(0, factory.Requests);
    }

    [Fact]
    public async Task CompletedInvocationAndItsAuditCommandRetainTheExactLiveProfileBinding()
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        var store = new InvocationRecordingStore();
        var service = new AgentExecutionService(store, new AgentRunner(profiles, factory), TimeProvider.System);
        var output = await service.Run("synthetic-profile-audit", "ortho", input);
        Assert.Equal("Completed", store.Value!.Status);
        Assert.Equal(Canonical.Serialize(output), store.Value.OutputJson);
        var command = Assert.Single(store.Commands, c => c.Action == "AgentInvocationCompleted");
        using var audited = JsonDocument.Parse(Canonical.Serialize(command.Request));
        Assert.Equal(output.ModelProvenance!.BindingSha256,
            audited.RootElement.GetProperty("output").GetProperty("modelProvenance").GetProperty("bindingSha256").GetString());
        Assert.Equal(Canonical.Serialize(output.ModelProvenance.ObservedModel),
            Canonical.Serialize(audited.RootElement.GetProperty("output").GetProperty("modelProvenance").GetProperty("observedModel")));
        Assert.Equal(Canonical.Hash(output), Canonical.Hash(await service.Run("synthetic-profile-audit", "ortho", input)));
        Assert.Equal(1, factory.Requests);
    }

    [Theory]
    [InlineData("different-model", "model_response_identity_mismatch")]
    [InlineData("different-version", "model_response_identity_mismatch")]
    [InlineData("alias", "model_response_identity_insufficient")]
    [InlineData("unsupported-version", "model_response_identity_insufficient")]
    [InlineData("invalid-date", "model_response_identity_insufficient")]
    [InlineData("missing-agent-original", "model_response_identity_missing")]
    [InlineData("missing-provider-original", "model_response_identity_missing")]
    [InlineData("missing-provider-model", "model_response_identity_missing")]
    [InlineData("missing-chat-model", "model_response_identity_missing")]
    [InlineData("missing-response-id", "model_response_identity_missing")]
    [InlineData("conflicting-model", "model_response_identity_ambiguous")]
    [InlineData("conflicting-response-id", "model_response_identity_ambiguous")]
    [InlineData("multiple-messages", "model_response_identity_ambiguous")]
    [InlineData("different-text", "model_response_identity_ambiguous")]
    public async Task RejectedResponseIdentityNeverCompletesAnInvocationOrReplaysTheRequest(string fault, string code)
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var body = SyntheticModelReply(input) with
        {
            Model = profiles.Roles[nameof(AgentRole.Ingestion)].Binding!.Model.Deployment + "@" + SyntheticResponseVersion,
            ModelProvenance = FoundryActivation.Require(profiles, input).QualifiedBinding with { ObservedModel = SyntheticObservation() }
        };
        var response = SyntheticSdkResponse(body, fault switch
        {
            "different-model" => "synthetic-other-family-2026-11-16",
            "different-version" => "synthetic-unselected-family-2026-11-17",
            "alias" => "synthetic-request-alias",
            "unsupported-version" => "synthetic-unselected-family-0125",
            "invalid-date" => "synthetic-unselected-family-2026-02-31",
            "missing-provider-model" => null,
            _ => SyntheticResponseModelId
        });
        var chat = Assert.IsType<ChatResponse>(response.RawRepresentation);
        switch (fault)
        {
            case "missing-agent-original": response.RawRepresentation = null; break;
            case "missing-provider-original": chat.RawRepresentation = null; break;
            case "missing-chat-model": chat.ModelId = null; break;
            case "missing-response-id": response.ResponseId = null; break;
            case "conflicting-model": chat.ModelId = "synthetic-other-family-2026-11-16"; break;
            case "conflicting-response-id": chat.ResponseId = "synthetic-different-response"; break;
            case "multiple-messages": response.Messages.Add(new(ChatRole.Assistant, "SYNTHETIC ambiguous second response.")); break;
            case "different-text": response.Messages = [new(ChatRole.Assistant, "SYNTHETIC replacement text.")]; break;
        }
        var factory = new SyntheticClientFactory(body) { Response = response };
        var store = new InvocationRecordingStore();
        var service = new AgentExecutionService(store, new AgentRunner(profiles, factory), TimeProvider.System);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.Run("synthetic-observation-refusal", "ortho", input));
        Assert.Equal(code, error.Code);
        Assert.Equal(409, error.Status);
        Assert.Equal("Failed", store.Value!.Status);
        Assert.Equal(code, store.Value.ErrorCode);
        Assert.Null(store.Value.OutputJson);
        Assert.DoesNotContain(store.Commands, command => command.Action == "AgentInvocationCompleted");
        Assert.Single(store.Commands, command => command.Action == "AgentInvocationFailed");
        Assert.Equal("invocation_unresolved", (await Assert.ThrowsAsync<DomainException>(() =>
            service.Run("synthetic-observation-refusal", "ortho", input))).Code);
        Assert.Equal(1, factory.Credentials);
        Assert.Equal(1, factory.Clients);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task OriginalSdkMetadataNotGeneratedJsonDeterminesTheSuccessfulObservation()
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var qualified = FoundryActivation.Require(profiles, input).QualifiedBinding;
        Assert.Null(qualified.ObservedModel);
        var body = SyntheticModelReply(input) with
        {
            ModelProvenance = qualified with
            { ObservedModel = new("LLM-generated-not-provider-metadata", "invented-id", "invented-model", "invented-version") }
        };
        var response = SyntheticSdkResponse(body);
        var draft = FoundryDraftResponse.FromAgentResponse(response);
        Assert.Equal(SyntheticObservation(), draft.Observation);
        Assert.Equal(Canonical.Serialize(body), draft.Text);
        var output = await new AgentRunner(profiles, new SyntheticClientFactory(body) { Response = response }).Run(input);
        Assert.Equal(SyntheticObservation(), output.ModelProvenance!.ObservedModel);
        Assert.True(AgentModelIdentity.SameQualifiedBinding(output.ModelProvenance, qualified));
        AgentBoundary.ValidateResult(input with { ExpectedActivation = qualified }, output);
        Assert.Equal("model_response_identity_missing", Assert.Throws<DomainException>(() =>
            AgentBoundary.ValidateResult(input, output with { ModelProvenance = qualified })).Code);
        Assert.Equal("model_response_identity_mismatch", Assert.Throws<DomainException>(() =>
            AgentBoundary.ValidateResult(input, output with { ModelProvenance = output.ModelProvenance with
            { ObservedModel = SyntheticObservation() with { ModelId = "synthetic-other-family-2026-11-16" } } })).Code);
    }

    [Theory]
    [InlineData(SyntheticResponseModelId, null)]
    [InlineData("synthetic-unselected-family-2026-11-17", "model_response_identity_mismatch")]
    [InlineData("synthetic-request-alias", "model_response_identity_insufficient")]
    [InlineData(null, "model_response_identity_missing")]
    public async Task InstalledAzureSdkAndAgentAdapterKeepProviderIdentityInsteadOfTheRequestAlias(string? model, string? code)
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var activation = FoundryActivation.Require(profiles, input);
        using var handler = new SyntheticResponseHandler(SyntheticSdkJson(SyntheticModelReply(input), model));
        using var http = new HttpClient(handler);
        var fakeCredential = new SyntheticTokenCredential("synthetic-sdk-fixture-not-a-real-credential");
        var agent = new AzureOpenAIClient(activation.Endpoint, fakeCredential, new AzureOpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetChatClient("synthetic-request-alias").AsAIAgent(instructions: activation.Instructions, name: "SyntheticOfflineAdapter");
        var response = await agent.RunAsync("SYNTHETIC OFFLINE SDK CONTRACT; intercepted HTTP, no service call.");
        var chat = Assert.IsType<ChatResponse>(response.RawRepresentation);
        var completion = Assert.IsType<ChatCompletion>(chat.RawRepresentation);
        Assert.Equal(model, completion.Model);
        Assert.Equal(model, chat.ModelId);
        Assert.Equal(1, handler.Requests);
        Assert.Contains("/deployments/synthetic-request-alias/chat/completions", handler.RequestUri!.AbsolutePath);
        if (code is null)
        {
            var observed = activation.BindResponse(FoundryDraftResponse.FromAgentResponse(response));
            Assert.Equal(SyntheticObservation(), observed.ObservedModel);
            Assert.True(AgentModelIdentity.SameQualifiedBinding(observed, activation.QualifiedBinding));
        }
        else
            Assert.Equal(code, Assert.Throws<DomainException>(() =>
                activation.BindResponse(FoundryDraftResponse.FromAgentResponse(response))).Code);
    }

    [Fact]
    public void AsChatResponseCannotTurnAMissingOriginalIntoProviderEvidence()
    {
        var response = new AgentResponse(new AIChatMessage(ChatRole.Assistant, "SYNTHETIC generated JSON"))
        { ResponseId = SyntheticResponseId };
        var inventedWrapper = response.AsChatResponse();
        inventedWrapper.ModelId = SyntheticResponseModelId;
        response.RawRepresentation = inventedWrapper;
        Assert.Equal("model_response_identity_missing", Assert.Throws<DomainException>(() =>
            FoundryDraftResponse.FromAgentResponse(response)).Code);
    }

    [Fact]
    public async Task AssuranceRequiresTheObservedAnalysisVersionAndKeepsItsOwnIdentity()
    {
        var profiles = SyntheticLiveProfiles();
        var analysisInput = SyntheticLiveInput(AgentRole.CausalityRisk, profiles);
        var analysisFactory = new SyntheticClientFactory(SyntheticModelReply(analysisInput));
        var analysis = await new AgentRunner(profiles, analysisFactory).Run(analysisInput);
        var input = SyntheticLiveInput(AgentRole.EvidenceAssurance, profiles) with { AnalysisProvenance = analysis.ModelProvenance };
        var assuranceFactory = new SyntheticClientFactory(SyntheticModelReply(input));
        var assurance = await new AgentRunner(profiles, assuranceFactory).Run(input);
        Assert.NotEqual(analysis.ModelProvenance!.WorkloadPrincipalId, assurance.ModelProvenance!.WorkloadPrincipalId);
        Assert.NotEqual(analysis.ModelProvenance.WorkloadClientId, assurance.ModelProvenance.WorkloadClientId);
        Assert.Equal(analysis.ModelProvenance.EvidenceSha256, assurance.ModelProvenance.EvidenceSha256);
        Assert.Equal(SyntheticObservation(), assurance.ModelProvenance.ObservedModel);
        await AssertNoLiveFactories(profiles, input with
        { AnalysisProvenance = analysis.ModelProvenance with { ObservedModel = null } }, "model_response_identity_missing");
    }

    [Fact]
    public void NewObservationFieldsDoNotChangeHistoricNullOmittedProvenanceBytes()
    {
        var profiles = SyntheticLiveProfiles();
        var old = FoundryActivation.Require(profiles, SyntheticInput(AgentRole.Ingestion)).QualifiedBinding with
        { ExpectedResponseModelId = null, ObservedModel = null };
        Assert.Equal(Canonical.Serialize(new { old.Role, old.Deployment, old.ModelVersion, old.BindingSha256,
            old.ReviewSha256, old.QualifiedReviewSha256, old.PromptSha256, old.PipelineConfigurationSha256,
            old.WorkloadTenantId, old.WorkloadClientId, old.WorkloadPrincipalId, old.EvidenceSha256 }), Canonical.Serialize(old));
    }

    [Fact]
    public async Task BothEntryPointsCanBindTheSameRoleConfigurationWithoutLegacyFallback()
    {
        var expected = SyntheticLiveProfiles();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Canonical.Serialize(new { Foundry = expected })));
        var config = new ConfigurationBuilder().AddJsonStream(source).Build();
        var loaded = config.GetSection("Foundry").Get<FoundryProfiles>()!;
        Assert.Equal(Canonical.Hash(expected), Canonical.Hash(loaded));
        using var oldSource = new MemoryStream(Encoding.UTF8.GetBytes(Canonical.Serialize(new { Foundry = Settings() })));
        var legacy = new ConfigurationBuilder().AddJsonStream(oldSource).Build().GetSection("Foundry").Get<FoundryProfiles>()!;
        await AssertNoLiveFactories(legacy, SyntheticInput(AgentRole.Ingestion), "model_profile_missing");
    }

    [Theory]
    [InlineData(AgentRole.Ingestion)]
    [InlineData(AgentRole.CausalityRisk)]
    [InlineData(AgentRole.TrendSignal)]
    [InlineData(AgentRole.EvidenceAssurance)]
    [InlineData(AgentRole.VigilanceAuthoring)]
    [InlineData(AgentRole.PsurAuthoring)]
    public async Task UnboundDeploymentTemplateCannotActivateAnyRole(AgentRole role)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cohors.slnx"))) root = root.Parent;
        using var source = File.OpenRead(Path.Combine(root!.FullName, "fabric", "foundry", "profiles.template.json"));
        var profiles = new ConfigurationBuilder().AddJsonStream(source).Build().GetSection("Foundry").Get<FoundryProfiles>()!;
        await AssertNoLiveFactories(profiles, SyntheticInput(role), "model_profile_missing");
    }

    [Fact]
    public async Task CallerCancellationDoesNotCreateCredentialsOrClients()
    {
        var profiles = SyntheticLiveProfiles();
        var input = SyntheticLiveInput(AgentRole.Ingestion, profiles);
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentRunner(profiles, factory).Run(input, cancelled.Token));
        var transports = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FoundryRemoteActivationRunner(profiles, () =>
        {
            transports++;
            return new AgentRunner(profiles, factory);
        }).Run(input, cancelled.Token));
        Assert.Equal(0, transports);
        Assert.Equal(0, factory.Credentials);
        Assert.Equal(0, factory.Clients);
        Assert.Equal(0, factory.Requests);
    }

    [Theory]
    [InlineData("tenant", "model_workload_identity")]
    [InlineData("client", "model_workload_identity")]
    [InlineData("principal", "model_workload_identity")]
    [InlineData("delegated", "model_workload_identity")]
    [InlineData("audience", "model_workload_identity")]
    [InlineData("conflicting-client", "model_workload_identity")]
    [InlineData("user", "model_workload_identity")]
    [InlineData("malformed", "model_workload_token")]
    public async Task ASelectedWorkloadCredentialCannotSupplyAnotherIdentityOrDelegatedToken(string fault, string code)
    {
        var binding = SyntheticLiveProfiles().Roles[nameof(AgentRole.Ingestion)].Binding!;
        var claims = SyntheticTokenClaims(binding);
        switch (fault)
        {
            case "tenant": claims["tid"] = Guid.Empty.ToString(); break;
            case "client": claims["appid"] = Guid.Empty.ToString(); break;
            case "principal": claims["oid"] = Guid.Empty.ToString(); break;
            case "delegated": claims["scp"] = "synthetic.delegated"; break;
            case "audience": claims["aud"] = "https://example.invalid/"; break;
            case "conflicting-client": claims["azp"] = Guid.Empty.ToString(); break;
            case "user": claims["idtyp"] = "user"; break;
            case "malformed": break;
            default: throw new InvalidOperationException("Unknown synthetic token fixture.");
        }
        var token = new SyntheticTokenCredential(fault == "malformed" ? "not-a-token" : SyntheticToken(claims));
        var credential = new FoundryBoundCredential(token, binding);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(async () =>
            await credential.GetTokenAsync(new(["https://cognitiveservices.azure.com/.default"]), default))).Code);
        Assert.Equal(1, token.Calls);
    }

    [Fact]
    public async Task BoundCredentialChecksScopeBeforeTokenAcquisitionAndAllowsOnlyMatchingSyntheticClaims()
    {
        var binding = SyntheticLiveProfiles().Roles[nameof(AgentRole.Ingestion)].Binding!;
        var token = new SyntheticTokenCredential(SyntheticToken(SyntheticTokenClaims(binding)));
        var credential = new FoundryBoundCredential(token, binding);
        Assert.Equal("model_workload_scope", (await Assert.ThrowsAsync<DomainException>(async () =>
            await credential.GetTokenAsync(new(["https://example.invalid/.default"]), default))).Code);
        Assert.Equal(0, token.Calls);
        var value = await credential.GetTokenAsync(new(["https://cognitiveservices.azure.com/.default"]), default);
        Assert.Equal(token.Token, value.Token);
        Assert.Equal(1, token.Calls);
    }

    private static async Task AssertNoLiveFactories(FoundryProfiles profiles, AgentInput input, string code)
    {
        var factory = new SyntheticClientFactory(SyntheticModelReply(input));
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => new AgentRunner(profiles, factory).Run(input))).Code);
        var transports = 0;
        var remote = new FoundryRemoteActivationRunner(profiles, () =>
        {
            transports++;
            return new AgentRunner(profiles, factory);
        });
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => remote.Run(input))).Code);
        Assert.Equal(0, transports);
        Assert.Equal(0, factory.Credentials);
        Assert.Equal(0, factory.Clients);
        Assert.Equal(0, factory.Requests);
    }

    // Invented evidence/metrics exercise contracts only: no clinical dataset, adjudication or deployed model.
    private static FoundryProfiles SyntheticLiveProfiles()
    {
        var profiles = new FoundryProfiles();
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var actual = FoundryActivation.RuntimeHashes(role);
            var binding = Binding(role) with
            {
                Model = RebindBoundary(Settings() with { Deployment = "synthetic-unprovisioned-" + role }),
                PromptSha256 = actual.PromptSha256,
                PipelineConfigurationSha256 = actual.PipelineConfigurationSha256,
                WorkloadTenantId = Guid.Parse("00000000-0000-4000-8000-000000000001"),
                WorkloadClientId = Guid.Parse($"00000000-0000-4000-8000-{10 + (int)role:D12}"),
                WorkloadPrincipalId = Guid.Parse($"00000000-0000-4000-8000-{20 + (int)role:D12}")
            };
            profiles.Roles.Add(role.ToString(), new(binding, Review(binding)));
        }
        var analysis = profiles.Roles[nameof(AgentRole.CausalityRisk)].Binding!;
        ChangeBinding(profiles, AgentRole.EvidenceAssurance, b => b with
        { Assurance = new(FoundryQuality.BindingHash(analysis), Hash, Pipeline) });
        return profiles;
    }

    private static void ChangeBinding(FoundryProfiles profiles, AgentRole role, Func<FoundryTaskBinding, FoundryTaskBinding> change,
        bool rebindReview = true)
    {
        var profile = profiles.Roles[role.ToString()];
        var binding = change(profile.Binding!);
        profiles.Roles[role.ToString()] = profile with { Binding = binding, QualityReview = rebindReview ? Review(binding) : profile.QualityReview };
    }

    private static void ChangeReview(FoundryProfiles profiles, AgentRole role, Func<FoundryQualityReview, FoundryQualityReview> change)
    {
        var profile = profiles.Roles[role.ToString()];
        profiles.Roles[role.ToString()] = profile with { QualityReview = change(profile.QualityReview!) };
    }

    private static FoundryOptions RebindBoundary(FoundryOptions model) => model with
    {
        BoundaryApproval = model.BoundaryApproval! with
        { ConfigurationHash = FoundryBoundary.ConfigurationHash(model, model.BoundaryApproval!) }
    };

    private static AgentInput SyntheticLiveInput(AgentRole role, FoundryProfiles profiles)
    {
        var input = SyntheticInput(role);
        return role == AgentRole.EvidenceAssurance
            ? input with { AnalysisProvenance = FoundryActivation.Require(profiles, SyntheticInput(AgentRole.CausalityRisk))
                .QualifiedBinding with { ObservedModel = SyntheticObservation() } }
            : input;
    }

    private static AgentInput SyntheticInput(AgentRole role)
    {
        var now = DateTimeOffset.Parse("2026-11-16T15:00:00Z");
        var evidence = new[] { "Complaint", "Specification", "BenchTest", "RiskFile" }.Select(kind =>
        {
            var content = "SYNTHETIC OFFLINE CONTRACT FIXTURE, NO CLINICAL EVIDENCE: " + kind;
            var hash = Canonical.HashText(content);
            return new Evidence(kind, 1, "ortho", kind, kind, content, hash, "cohors:synthetic",
                "synthetic-contract-locator", "synthetic", "pilot", now, null, now, true, true, hash);
        }).ToArray();
        var c = new SafetyCase("synthetic-profile-case", 1, "SYNTHETIC-PROFILE", "ortho", "Synthetic contract fixture",
            evidence[0].Content, null, null, "DE", "EU-DE", Seriousness.OtherSerious, null, now, now, null, "synthetic-profile-test",
            true, CaseStage.AwaitingMedicalReview, Disposition.Undetermined,
            [new("source", "synthetic-source", "Complaint", "Synthetic source", "cohors:synthetic", evidence[0].Hash, now, now, "Synthetic")],
            [], [], evidence, [], [], [], [], [], [], [], []);
        c = c with { Assessments = [new(1, now, "synthetic-analysis", "Live", AgentRunner.PromptVersion, "synthetic",
            evidence.Select(e => e.Id).ToArray(), Canonical.Hash(evidence), ReplayAssessment.Create(c, evidence), null)] };
        if (role == AgentRole.TrendSignal)
        {
            var exposure = new Exposure("synthetic-exposure", 1, "ortho", "Worldwide", new(2026, 1, 1), new(2026, 12, 31),
                "synthetic-unit", 100, "synthetic", "OFFLINE CONTRACT ONLY", true);
            var trend = new TrendRun("synthetic-trend", 1, "ortho",
                new("ortho", "Worldwide", exposure.Start, exposure.End, [], exposure, 0.1m, "synthetic"),
                new(1, 100, "synthetic-unit", 10m, 1m, 0.5, false, ["Synthetic contract only"]), now, "Draft", "", null, null);
            return new(role, AgentMode.Live, null, null, trend, [], AgentInstructions.For(role), 0);
        }
        if (role == AgentRole.PsurAuthoring)
        {
            var snapshot = new PsurSnapshot("ortho", "ortho", new(2026, 1, 1), new(2026, 12, 31), "Worldwide",
                [], [], [], [], evidence, now, [], [], [], "OccurredAt.UtcDate", "SYNTHETIC CONTRACT ONLY");
            return new(role, AgentMode.Live, null, snapshot, null, evidence, AgentInstructions.For(role), 0);
        }
        return new(role, AgentMode.Live, c, null, null, evidence, AgentInstructions.For(role), 0);
    }

    private static AgentOutput SyntheticModelReply(AgentInput input) =>
        new("untrusted-model-label", "untrusted-prompt-label", input.Case?.CurrentAssessment?.Content,
            "SYNTHETIC MODEL DOUBLE: this is not a measured model response or clinical quality finding.", [], ["untrusted-tool"],
            input.Role == AgentRole.VigilanceAuthoring ? VigilanceNarrative.Fields(input.Case!, input.Language) : null);

    private const string SyntheticResponseVersion = "2026-11-16";
    private const string SyntheticResponseModelId = "synthetic-unselected-family-2026-11-16";
    private const string SyntheticResponseId = "chatcmpl-synthetic-offline-contract";

    private static AgentModelObservation SyntheticObservation() =>
        new(AgentModelIdentity.MetadataSource, SyntheticResponseId, SyntheticResponseModelId, SyntheticResponseVersion);

    private static string SyntheticSdkJson(AgentOutput output, string? model = SyntheticResponseModelId) => Canonical.Serialize(new
    {
        id = SyntheticResponseId, @object = "chat.completion", created = 1794841200, model,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = Canonical.Serialize(output) }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 2, completion_tokens = 3, total_tokens = 5 }
    });

    private static AgentResponse SyntheticSdkResponse(AgentOutput output, string? model = SyntheticResponseModelId)
    {
        var completion = ModelReaderWriter.Read<ChatCompletion>(BinaryData.FromString(SyntheticSdkJson(output, model))) ??
            throw new InvalidOperationException("Synthetic SDK fixture failed to deserialize.");
        var chat = new ChatResponse(new AIChatMessage(ChatRole.Assistant, string.Concat(completion.Content.Select(part => part.Text))))
        { RawRepresentation = completion, ModelId = completion.Model, ResponseId = completion.Id };
        return new AgentResponse(chat);
    }

    private sealed class SyntheticResponseHandler(string response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Requests++;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SyntheticClientFactory(AgentOutput output) : IFoundryClientFactory
    {
        public int Credentials { get; private set; }
        public int Clients { get; private set; }
        public int Requests { get; private set; }
        public List<FoundryTaskBinding> Bindings { get; } = [];
        public string? Instructions { get; private set; }
        public string? Prompt { get; private set; }
        public Exception? Error { get; init; }
        public AgentResponse? Response { get; init; }

        public TokenCredential CreateCredential(FoundryTaskBinding binding)
        {
            Credentials++;
            Bindings.Add(binding);
            return new SyntheticTokenCredential("never-requested-by-synthetic-client");
        }

        public IFoundryDraftClient CreateClient(Uri endpoint, FoundryTaskBinding binding, TokenCredential credential, string instructions)
        {
            Clients++;
            Assert.Equal(binding.Model.Endpoint, endpoint.ToString());
            Instructions = instructions;
            return new SyntheticClient(this, output);
        }

        private sealed class SyntheticClient(SyntheticClientFactory owner, AgentOutput output) : IFoundryDraftClient
        {
            public Task<FoundryDraftResponse> Run(string prompt, CancellationToken ct)
            {
                owner.Requests++;
                owner.Prompt = prompt;
                ct.ThrowIfCancellationRequested();
                return owner.Error is { } error ? Task.FromException<FoundryDraftResponse>(error) :
                    Task.FromResult(FoundryDraftResponse.FromAgentResponse(owner.Response ?? SyntheticSdkResponse(output)));
            }
        }
    }

    private static Dictionary<string, string> SyntheticTokenClaims(FoundryTaskBinding binding) => new()
    {
        ["tid"] = binding.WorkloadTenantId.ToString(), ["oid"] = binding.WorkloadPrincipalId.ToString(),
        ["appid"] = binding.WorkloadClientId.ToString(), ["idtyp"] = "app", ["aud"] = "https://cognitiveservices.azure.com"
    };

    private static string SyntheticToken(Dictionary<string, string> claims) => "synthetic-header." +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') +
        ".synthetic-signature-not-a-real-credential";

    private sealed class SyntheticTokenCredential(string token) : TokenCredential
    {
        public string Token => token;
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new(token, DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class InvocationRecordingStore : IStateStore
    {
        public AgentInvocation? Value { get; private set; }
        public List<CommandContext> Commands { get; } = [];
        public Task<T?> Get<T>(string kind, string id, CancellationToken ct = default) =>
            Task.FromResult(Value is null ? default : Canonical.Read<T>(Canonical.Serialize(Value)));
        public Task<T> Execute<T>(CommandContext command, Func<T?, T> change, CancellationToken ct = default)
        {
            Assert.Equal("agentInvocation", command.Kind);
            var result = change(Value is null ? default : Canonical.Read<T>(Canonical.Serialize(Value)));
            Value = Canonical.Read<AgentInvocation>(Canonical.Serialize(result));
            Commands.Add(command);
            return Task.FromResult(result);
        }
        public Task<T?> GetVersion<T>(string kind, string id, int version, string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<T[]> List<T>(string kind, string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<T[]> History<T>(string kind, string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PortfolioState> Capture(string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<T?> Receipt<T>(CommandContext command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Psur> FreezePsur(CommandContext command, PsurCaptureScope scope, Func<PortfolioState, Psur> create, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditEvent[]> Audit(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OutboxEvent[]> PendingEvents(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OutboxEvent?> ClaimEvent(string eventId, string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteEvent(string eventId, string consumer, string owner, string? error, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OutboxIssue[]> OutboxIssues(string[] products, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasReceipt(string eventId, string consumer, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
