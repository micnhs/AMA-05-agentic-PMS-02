using System.Text.Json;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class FabricC1PreparationTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "c1-synthetic-source.json"));
    private static FabricC1Inputs Template => Canonical.Read<FabricC1Inputs>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "c1-inputs.template.json")));
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { question = new { type = "string" } },
        required = new[] { "question" },
        additionalProperties = false
    });

    [Fact]
    public void PublicPreparationRemainsBlockedAfterGeneralCostApprovalAndBindsOnlySyntheticBytes()
    {
        var input = Template;
        var check = FabricC1ProbeClient.Preflight(input);
        Assert.True(input.GeneralCostApprovalReceived);
        Assert.Equal("Blocked", check.Status);
        Assert.False(check.CloudExecutionAllowed);
        Assert.Contains("identity.clientId", check.MissingOrInvalid);
        Assert.Contains("identity.expectedAudience", check.MissingOrInvalid);
        Assert.Contains("target.publishedMcpEndpoint", check.MissingOrInvalid);
        Assert.Contains("controls.budgetPlanSha256", check.MissingOrInvalid);
        Assert.Contains("controls.runApprovalReference", check.MissingOrInvalid);
        Assert.Contains("limits.maxDurationSeconds", check.MissingOrInvalid);
        Assert.Equal(Canonical.HashText(Fixture), input.Source.FixtureSha256);
        Assert.Equal("c1_inputs_missing", Assert.Throws<DomainException>(() => FabricC1ProbeClient.Discovery(input)).Code);
    }

    [Theory]
    [InlineData("DefaultAzureCredential")]
    [InlineData("AzureCliCredential")]
    [InlineData("ManagedIdentity")]
    [InlineData("OnBehalfOf")]
    public void PreparationRejectsCredentialChainsAndDelegatedOrManagedIdentitySubstitutes(string kind)
    {
        var input = Bound();
        var check = FabricC1ProbeClient.Preflight(input with { Identity = input.Identity with { CredentialKind = kind } });
        Assert.Contains("identity.credentialKind", check.MissingOrInvalid);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("audience")]
    [InlineData("endpoint")]
    [InlineData("mode")]
    public void IncompatibleAudienceItemOrLiveModeCannotProduceAQueryPlan(string changed)
    {
        var input = Bound();
        input = changed switch
        {
            "scope" => input with { TokenScope = "https://analysis.windows.net/powerbi/api/.default" },
            "audience" => input with { Identity = input.Identity with { ExpectedAudience = FabricC1ProbeClient.TokenScope } },
            "endpoint" => input with { Target = input.Target with { PublishedMcpEndpoint = "https://synthetic.invalid/another-item" } },
            _ => input with { Mode = "Live" }
        };
        Assert.Equal("c1_inputs_missing", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.Call(input, Fixture, "allowed", Tools())).Code);
    }

    [Fact]
    public void DiscoveryNeedsNoGuessedToolAndQueryUsesOnlyReviewedToolSchemaAndSyntheticQuestion()
    {
        var input = Bound();
        var discoveryInput = input with { Target = input.Target with
            { ToolName = null, QuestionArgument = null, InputSchemaSha256 = null, TaskProtocolReview = null } };
        var discovery = FabricC1ProbeClient.Discovery(discoveryInput);
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list" },
            discovery.Messages.Select(m => m.GetProperty("method").GetString()));
        Assert.Empty(discovery.Messages[0].GetProperty("params").GetProperty("capabilities").EnumerateObject());
        Assert.Equal("c1_inputs_missing", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.Call(discoveryInput, Fixture, "allowed", Tools())).Code);

        var plan = FabricC1ProbeClient.Call(input, Fixture, "allowed", Tools());
        var request = Assert.Single(plan.Messages);
        Assert.Equal("tools/call", request.GetProperty("method").GetString());
        Assert.Equal(input.Target.ToolName, request.GetProperty("params").GetProperty("name").GetString());
        using var fixture = JsonDocument.Parse(Fixture);
        Assert.Equal(fixture.RootElement.GetProperty("probes")[0].GetProperty("question").GetString(),
            request.GetProperty("params").GetProperty("arguments").GetProperty("question").GetString());
        Assert.Equal(Canonical.Hash(input), plan.BindingHash);
        Assert.Equal("offline-request-preparation", plan.EvidenceLevel);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("schema")]
    [InlineData("additional-tool")]
    [InlineData("pagination")]
    public void DiscoveryDriftCannotSilentlyPickAnotherToolOrArgument(string problem)
    {
        var input = Bound();
        var actual = problem switch
        {
            "name" => Tools(name: "different-tool"),
            "schema" => Tools(schema: JsonSerializer.SerializeToElement(new { type = "object", properties = new { instruction = new { type = "string" } } })),
            "additional-tool" => JsonSerializer.SerializeToElement(new { tools = new[]
                { new { name = input.Target.ToolName, inputSchema = Schema }, new { name = "other", inputSchema = Schema } } }),
            _ => JsonSerializer.SerializeToElement(new { tools = new[] { new { name = input.Target.ToolName, inputSchema = Schema } }, nextCursor = "next" })
        };
        var error = Assert.Throws<DomainException>(() => FabricC1ProbeClient.Call(input, Fixture, "allowed", actual));
        Assert.Contains(error.Code, new[] { "c1_tools", "c1_tool_binding" });
    }

    [Fact]
    public void ChangedSourceBytesOrUnapprovedQuestionCannotPrepareAnotherCall()
    {
        var input = Bound();
        Assert.Equal("c1_fixture", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.Call(input, Fixture + " ", "allowed", Tools())).Code);
        Assert.Equal("c1_probe", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.Call(input, Fixture, "arbitrary-question", Tools())).Code);
        Assert.Equal("c1_fixture", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.Call(input with { Source = input.Source with { ReleaseId = "other-release" } }, Fixture, "allowed", Tools())).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumeAndCancelKeepTheOriginalTaskIdentityInsteadOfStartingAnotherQuery(bool cancel)
    {
        var input = Bound();
        var call = FabricC1ProbeClient.Call(input, Fixture, "allowed", Tools());
        var checkpoint = new FabricC1TaskCheckpoint(call.BindingHash, Canonical.Hash(call.Messages[0]), "synthetic-task", input.Target.TaskProtocolReview!);
        var action = FabricC1ProbeClient.TaskAction(input, call, checkpoint, cancel);
        Assert.Equal(cancel ? "tasks/cancel" : "tasks/get", action.Method);
        Assert.Equal(checkpoint.TaskId, action.TaskId);
        Assert.Equal(checkpoint.CallHash, action.CallHash);
        Assert.Equal("c1_task_binding", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.TaskAction(input, call, checkpoint with { TaskId = "" }, cancel)).Code);
        Assert.Equal("c1_task_binding", Assert.Throws<DomainException>(() =>
            FabricC1ProbeClient.TaskAction(input with { Identity = input.Identity with
                { ClientId = "99999999-9999-9999-9999-999999999999" } }, call, checkpoint, cancel)).Code);
    }

    private static JsonElement Tools(string name = "synthetic_data_agent", JsonElement? schema = null) =>
        JsonSerializer.SerializeToElement(new { tools = new[] { new { name, inputSchema = schema ?? Schema } } });

    private static FabricC1Inputs Bound()
    {
        var input = Template;
        var hash = Canonical.HashText("SYNTHETIC IN-PROCESS REVIEW; NOT A REAL RESOURCE OR APPROVAL");
        const string workspace = "44444444-4444-4444-4444-444444444444";
        const string agent = "55555555-5555-5555-5555-555555555555";
        return input with
        {
            Identity = new("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
                "33333333-3333-3333-3333-333333333333", "ServicePrincipalCertificate", "synthetic-credential-reference-only",
                "https://issuer.invalid/synthetic", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Target = new("synthetic-capacity-reference", "synthetic-region", workspace, agent,
                "66666666-6666-6666-6666-666666666666", "77777777-7777-7777-7777-777777777777",
                $"https://synthetic.invalid/v1/mcp/workspaces/{workspace}/dataagents/{agent}/agent",
                FabricC1ProbeClient.ProtocolVersion, "synthetic_data_agent", "question", Canonical.Hash(Schema), "synthetic-task-dialect-review"),
            Source = input.Source with { ReleaseId = "C1-RELEASE-1", MaterializationSha256 = hash,
                OntologyBindingsSha256 = hash, SourceGrantReviewSha256 = hash },
            Runtime = new("synthetic-private-runner", "https://hosted.invalid/api/projects/synthetic",
                "synthetic-version", hash, "synthetic-model", "synthetic-model-version"),
            Controls = new(hash, hash, hash, hash, hash, hash, hash, "synthetic-run-review"),
            Limits = new(10, 60, 8192, 30)
        };
    }
}
