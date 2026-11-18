using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class FabricC1DeploymentTests
{
    [Fact]
    public void CompleteStartValuesDoNotHideUnimplementedPrivateBootstrapOrRequireManualTargets()
    {
        using var run = new Harness();
        var plan = FabricC1Deployment.Plan(run.Input, run.Clock.GetUtcNow());
        Assert.Empty(plan.MissingInputs);
        Assert.False(plan.CanApply);
        Assert.Equal("Blocked", plan.Status);
        Assert.NotEmpty(plan.ImplementationBlockers);
        Assert.Equal("c1_deploy_incomplete", Assert.Throws<DomainException>(() => FabricC1Deployment.RequireComplete(plan)).Code);
        var parameters = FabricC1Deployment.ArmParameters(run.Input).ToJsonString();
        Assert.DoesNotContain("workspaceId", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("clientId", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialReference", parameters, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.241.1.0/24", "10.241.1.0/24", "10.241.3.0/24")]
    [InlineData("10.241.1.0/24", "10.241.2.0/28", "10.241.3.0/24")]
    [InlineData("10.241.1.0/24", "10.242.2.0/24", "10.241.3.0/24")]
    public void OverlappingSmallOrOutOfRangeSubnetsCannotPassPlanning(string client, string agent, string endpoint)
    {
        using var run = new Harness();
        var invalid = run.Input with { Network = run.Input.Network! with { ClientPrefix = client, AgentPrefix = agent, EndpointPrefix = endpoint } };
        Assert.Contains("network.prefixes", FabricC1Deployment.Plan(invalid, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Fact]
    public async Task WorkspaceCreateUsesGeneratedCapacityAndReturnedIdentity()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List();
        var created = run.Workspace("agent");
        run.Api.Reply(201, created);
        var result = await run.Provisioner.EnsureWorkspace("agent", default);
        Assert.Equal(created["id"]!.GetValue<string>(), result["id"]!.GetValue<string>());
        var post = Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(run.CapacityId, post.Body!["capacityId"]!.GetValue<string>());
        Assert.Equal(created["id"]!.GetValue<string>(), run.Store.Current!.Objects["workspace-agent"].Id);
        Assert.Empty(run.Store.Current.PendingCreates);
    }

    [Fact]
    public async Task ResumeReadsBoundWorkspaceWithoutAnotherCreate()
    {
        using var run = new Harness();
        run.Capacity();
        var workspace = run.Workspace("agent");
        run.Api.List();
        run.Api.Reply(201, workspace);
        await run.Provisioner.EnsureWorkspace("agent", default);
        run.Restart();
        run.Api.Reply(200, workspace);
        await run.Provisioner.EnsureWorkspace("agent", default);
        Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/" + workspace["id"]!.GetValue<string>(), run.Api.Requests[^1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NameAndEvenCopiedMarkerDoNotAuthorizeAdoptingUnjournaledWorkspace()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List(run.Workspace("agent"));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.All(run.Api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task LostCreateResponseRemainsUnknownWithoutAnotherPost()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List();
        run.Api.Fail(new HttpRequestException("synthetic transport loss"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureWorkspace("agent", default));
        Assert.Contains("workspace-agent", run.Store.Current!.PendingCreates.Keys);
        run.Restart();
        run.Api.List();
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task LostCreateCanReconcileExactOwnedInventoryEntry()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List();
        run.Api.Fail(new HttpRequestException("synthetic transport loss"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureWorkspace("agent", default));
        run.Restart();
        var returned = run.Workspace("agent");
        run.Api.List(returned);
        Assert.Equal(returned["id"]!.GetValue<string>(), (await run.Provisioner.EnsureWorkspace("agent", default))["id"]!.GetValue<string>());
        Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task AsynchronousCreateResumesRecordedOperationInsteadOfRepeatingPost()
    {
        using var run = new Harness();
        run.Capacity();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation);
        run.Api.Fail(new HttpRequestException("synthetic polling interruption"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureWorkspace("agent", default));
        Assert.Equal(operation, run.Store.Current!.PendingOperations["workspace-agent"]);
        run.Restart();
        var item = run.Workspace("agent");
        run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(200, item);
        Assert.Equal(item["id"]!.GetValue<string>(), (await run.Provisioner.EnsureWorkspace("agent", default))["id"]!.GetValue<string>());
        Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Empty(run.Store.Current!.PendingOperations);
    }

    [Fact]
    public async Task AsyncLocationCannotSendTheCallerToAnotherHost()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: "https://unrelated.example.invalid/v1/operations/" + Guid.NewGuid().ToString("D"));
        Assert.Equal("c1_deploy_operation_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
    }

    [Fact]
    public async Task IncompletePaginatedInventoryNeverAuthorizesCreate()
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.Reply(200, new() { ["value"] = new JsonArray(), ["continuationToken"] = "synthetic-unresolved" });
        Assert.Equal("c1_deploy_paging", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Single(run.Api.Requests);
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task BudgetSurvivesProcessResumeAndRefusesRequestBeforeEffects()
    {
        using var run = new Harness(maxRequests: 2);
        run.Capacity();
        var item = run.Workspace("agent");
        run.Api.List();
        run.Api.Reply(201, item);
        await run.Provisioner.EnsureWorkspace("agent", default);
        run.Restart();
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
    }

    [Fact]
    public async Task PersistFailurePreventsCreatingResource()
    {
        using var run = new Harness();
        run.Capacity();
        run.Store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.EnsureWorkspace("agent", default));
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public void ChangedManifestCannotResumeAnotherDeploymentJournal()
    {
        using var run = new Harness();
        run.Capacity();
        Assert.Equal("c1_deploy_journal_binding", Assert.Throws<DomainException>(() =>
            new FabricC1Provisioner(run.Input with { Location = "swedencentral" }, run.Api, run.Store, run.Clock)).Code);
    }

    [Fact]
    public async Task ReadOnlyArmPermissionsAreNotCreatePermissions()
    {
        using var run = new Harness();
        run.Api.Reply(200, run.Subscription());
        run.Api.Reply(200, run.Principal());
        run.Api.List(new JsonObject { ["actions"] = new JsonArray("*/read"), ["notActions"] = new JsonArray() });
        Assert.Equal("c1_deploy_rights", (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.Preflight(default))).Code);
        Assert.All(run.Api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public void ExcludedArmActionIsNotRestoredBySameWildcardGrant()
    {
        var permissions = new JsonArray(new JsonObject { ["actions"] = new JsonArray("*"), ["notActions"] = new JsonArray("Microsoft.Fabric/*") });
        Assert.False(FabricC1Deployment.AllowsAction(permissions, "Microsoft.Fabric/capacities/write"));
        Assert.True(FabricC1Deployment.AllowsAction(permissions, "Microsoft.Network/virtualNetworks/write"));
    }

    [Fact]
    public async Task WrongTenantStopsBeforeGraphOrCreate()
    {
        using var run = new Harness();
        var subscription = run.Subscription();
        subscription["tenantId"] = Guid.NewGuid().ToString("D");
        run.Api.Reply(200, subscription);
        Assert.Equal("c1_deploy_context", (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.Preflight(default))).Code);
        Assert.Single(run.Api.Requests);
    }

    [Fact]
    public async Task PolicyPutPreservesUnknownFieldsSetsBothDeniesAndUsesReadEtag()
    {
        using var run = new Harness();
        run.Seed("workspace-agent", "Workspace", run.Workspace("agent"));
        var before = JsonNode.Parse("""{"inbound":{"publicAccessRules":{"defaultAction":"Allow"}},"outbound":{"publicAccessRules":{"defaultAction":"Allow"}},"future":{"keep":true}}""")!.AsObject();
        var locked = FabricC1Deployment.LockCommunicationPolicy(before);
        run.Api.Reply(200, before, "\"synthetic-etag\"");
        run.Api.Reply(200, new());
        run.Api.Reply(200, locked);
        await run.Provisioner.DenyWorkspacePublicAccess("agent", default);
        var put = Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal("\"synthetic-etag\"", put.ETag);
        Assert.Equal("Deny", put.Body!["inbound"]!["publicAccessRules"]!["defaultAction"]!.GetValue<string>());
        Assert.Equal("Deny", put.Body["outbound"]!["publicAccessRules"]!["defaultAction"]!.GetValue<string>());
        Assert.True(put.Body["future"]!["keep"]!.GetValue<bool>());
        Assert.True(run.Store.Current!.Objects.ContainsKey("policy-agent"));
    }

    [Theory]
    [InlineData(null, 200, "c1_deploy_etag")]
    [InlineData("\"old\"", 412, "c1_deploy_http_412")]
    public async Task MissingOrStalePolicyEtagCannotOverwriteConcurrentPolicy(string? etag, int status, string code)
    {
        using var run = new Harness();
        run.Seed("workspace-agent", "Workspace", run.Workspace("agent"));
        run.Api.Reply(200, new(), etag);
        if (etag is not null) run.Api.Reply(status, new());
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.DenyWorkspacePublicAccess("agent", default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("policy-agent"));
        Assert.Equal(etag is null ? 1 : 2, run.Api.Requests.Count);
    }

    [Fact]
    public async Task ContributorWorkspaceCannotReceiveSourceLakehouse()
    {
        using var run = new Harness();
        Assert.Equal("c1_deploy_source_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureLakehouse("agent", default))).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task RuntimeGrantRecordsReturnedAssignmentIdForCleanupNotPrincipalId()
    {
        using var run = new Harness();
        run.Seed("workspace-agent", "Workspace", run.Workspace("agent"));
        var principal = Guid.NewGuid().ToString("D");
        run.Seed("runtime-sp", "ServicePrincipal", new() { ["id"] = principal });
        var assignment = Guid.NewGuid().ToString("D");
        var receipt = new JsonObject
        {
            ["id"] = assignment, ["principal"] = new JsonObject { ["id"] = principal, ["type"] = "ServicePrincipal" },
            ["role"] = "Contributor"
        };
        run.Api.List();
        run.Api.Reply(201, receipt);
        run.Api.List(receipt);
        await run.Provisioner.AssignRuntimeToAgentWorkspace(default);
        Assert.Equal(assignment, run.Store.Current!.Objects["runtime-agent-grant"].Id);
        Assert.Empty(run.Store.Current.PendingCreates);
        var post = Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("Contributor", post.Body!["role"]!.GetValue<string>());
        Assert.DoesNotContain("released", post.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataAgentCreate201AloneDoesNotMakePublishedMcpBinding()
    {
        using var run = new Harness();
        var workspace = run.Workspace("agent");
        run.Seed("workspace-agent", "Workspace", workspace);
        run.Seed("policy-agent", "CommunicationPolicy", new() { ["id"] = workspace["id"]!.GetValue<string>() });
        var definition = new JsonObject { ["parts"] = new JsonArray(
            new JsonObject { ["path"] = "Files/Config/publish_info.json", ["payload"] = "e30=", ["payloadType"] = "InlineBase64" },
            new JsonObject { ["path"] = "Files/Config/published/stage_config.json", ["payload"] = "e30=", ["payloadType"] = "InlineBase64" }) };
        run.Api.List();
        run.Api.Reply(201, new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["displayName"] = "C1_Agent", ["description"] = FabricC1Deployment.Marker(run.Input)
        });
        run.Api.Reply(200, new() { ["definition"] = new JsonObject { ["parts"] = new JsonArray() } });
        Assert.Equal("c1_deploy_publication_readback", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureDataAgent(definition, default))).Code);
        var draft = run.Provisioner.PreparationDraft();
        Assert.NotNull(draft.Preparation.Target.DataAgentId);
        Assert.Null(draft.Preparation.Target.PublishedMcpEndpoint);
        Assert.False(draft.CloudExecutionAllowed);
    }

    [Fact]
    public void ExportUsesObservedIdsButNeverInventsCredentialOntologyOrReviewBindings()
    {
        using var run = new Harness();
        var workspace = run.Workspace("agent");
        var agentId = Guid.NewGuid().ToString("D");
        var sourceId = Guid.NewGuid().ToString("D");
        var clientId = Guid.NewGuid().ToString("D");
        run.Seed("workspace-agent", "Workspace", workspace);
        run.Seed("data-agent", "DataAgent", new() { ["id"] = agentId });
        run.Seed("published-definition", "DataAgentDefinition", new() { ["id"] = agentId });
        run.Seed("lakehouse-released", "Lakehouse", new() { ["id"] = sourceId });
        run.Seed("runtime-app", "Application", new() { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = clientId });
        run.Seed("runtime-sp", "ServicePrincipal", new() { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = clientId });
        var draft = run.Provisioner.PreparationDraft();
        Assert.Equal(workspace["id"]!.GetValue<string>(), draft.Preparation.Target.WorkspaceId);
        Assert.Equal(agentId, draft.Preparation.Target.DataAgentId);
        Assert.Equal(sourceId, draft.Preparation.Target.SourceItemId);
        Assert.Equal(clientId, draft.Preparation.Identity.ClientId);
        Assert.Contains(agentId, draft.Preparation.Target.PublishedMcpEndpoint!, StringComparison.Ordinal);
        Assert.Null(draft.Preparation.Identity.CredentialReference);
        Assert.Null(draft.Preparation.Target.OntologyId);
        Assert.Null(draft.Preparation.Source.SourceGrantReviewSha256);
        Assert.Contains("identity.credentialReference", draft.MissingOrInvalid);
        Assert.Contains("target.ontologyId", draft.MissingOrInvalid);
        Assert.Equal("c1_deploy_binding_incomplete", Assert.Throws<DomainException>(run.Provisioner.RequireFullBindings).Code);
    }

    [Fact]
    public async Task GraphCreateNeedsOwnerReadbackNotJustAnOwnershipTag()
    {
        using var run = new Harness();
        run.Api.List();
        run.Api.Reply(201, new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = Guid.NewGuid().ToString("D"), ["signInAudience"] = "AzureADMyOrg",
            ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
        });
        run.Api.List(new JsonObject { ["id"] = Guid.NewGuid().ToString("D") });
        Assert.Equal("c1_deploy_app_owner", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureApplication("synthetic-public-certificate", run.Clock.GetUtcNow(),
                run.Clock.GetUtcNow().AddHours(1), default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("runtime-app"));
        Assert.Contains("runtime-app", run.Store.Current.PendingCreates.Keys);
    }

    [Fact]
    public async Task ForeignResourceGroupIsNotModifiedEvenWithMatchingName()
    {
        using var run = new Harness();
        run.Api.Reply(200, new() { ["id"] = FabricC1Deployment.GroupId(run.Input), ["tags"] = FabricC1Deployment.Tags(run.Input) });
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureResourceGroup(default))).Code);
        Assert.Single(run.Api.Requests);
    }

    [Fact]
    public async Task CreateAndResumeResourceGroupBindExactReturnedArmIdentity()
    {
        using var run = new Harness();
        var response = new JsonObject { ["id"] = FabricC1Deployment.GroupId(run.Input), ["tags"] = FabricC1Deployment.Tags(run.Input) };
        run.Api.Reply(404, new());
        run.Api.Reply(201, response);
        await run.Provisioner.EnsureResourceGroup(default);
        run.Restart();
        run.Api.Reply(200, response);
        await run.Provisioner.EnsureResourceGroup(default);
        Assert.Single(run.Api.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void GraphAppCreateOnlyPermissionDoesNotAuthorizeWholeBootstrap()
    {
        using var run = new Harness();
        var token = Token(run.Input, "00000003-0000-0000-c000-000000000000", run.Clock.GetUtcNow(), "AppRegistration.Create");
        Assert.Equal("c1_deploy_graph_rights", Assert.Throws<DomainException>(() =>
            FabricC1DeploymentHttp.ValidateBootstrapToken(token, run.Input.BootstrapIdentity!,
                "00000003-0000-0000-c000-000000000000", run.Clock.GetUtcNow(), graph: true)).Code);
    }

    [Fact]
    public async Task PublicFabricDataPlaneIsRejectedBeforeGettingAToken()
    {
        using var run = new Harness();
        var token = new Credential();
        var api = new FabricC1DeploymentHttp(run.Input, token, new Dictionary<string, HttpClient>(), run.Clock);
        Assert.Equal("c1_deploy_public_data_path", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Get, new($"https://api.fabric.microsoft.com/v1/workspaces/{Guid.NewGuid():D}/lakehouses")))).Code);
        Assert.Equal(0, token.Requests);
    }

    [Fact]
    public async Task MissingPrivateRouteDoesNotFallBackToDefaultHttpOrCaller()
    {
        using var run = new Harness();
        var token = new Credential();
        var api = new FabricC1DeploymentHttp(run.Input, token, new Dictionary<string, HttpClient>(), run.Clock);
        var workspace = Guid.NewGuid().ToString("D");
        Assert.Equal("c1_deploy_private_route", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Get, new($"https://{FabricC1Provisioner.WorkspaceHost(workspace)}/v1/workspaces/{workspace}/lakehouses")))).Code);
        Assert.Equal(0, token.Requests);
    }

    [Fact]
    public void FileJournalRoundTripAndExclusiveLeaseDoNotResetPendingCreates()
    {
        using var run = new Harness();
        var directory = Path.Combine(Path.GetTempPath(), "cohors-c1-deploy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        try
        {
            using (var file = new FabricC1DeploymentFileJournal(path))
            {
                var state = new FabricC1DeploymentJournal(Canonical.Hash(run.Input), new(), new() { ["runtime-app"] = new string('a', 64) }) { Requests = 3 };
                file.Save(state);
                Assert.Throws<IOException>(() => new FabricC1DeploymentFileJournal(path));
            }
            using (var resumed = new FabricC1DeploymentFileJournal(path))
            {
                var loaded = Assert.IsType<FabricC1DeploymentJournal>(resumed.Load());
                Assert.Equal(3, loaded.Requests);
                Assert.Contains("runtime-app", loaded.PendingCreates.Keys);
                Assert.Equal(Canonical.Hash(run.Input), loaded.BindingHash);
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
            Directory.Delete(directory);
        }
    }

    private static AccessToken Token(FabricC1DeploymentInputs input, string audience, DateTimeOffset now, string role)
    {
        static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(Encode(new { alg = "none", typ = "JWT" }) + "." + Encode(new
        {
            aud = audience, iss = $"https://sts.windows.net/{input.TenantId}/", tid = input.TenantId,
            oid = input.BootstrapIdentity!.PrincipalObjectId, appid = input.BootstrapIdentity.ClientId,
            exp = now.AddHours(1).ToUnixTimeSeconds(), nbf = now.AddMinutes(-1).ToUnixTimeSeconds(), roles = new[] { role }
        }) + ".", now.AddHours(1));
    }

    private sealed class Credential : TokenCredential
    {
        public int Requests { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("No token acquisition expected.");
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed record Request(HttpMethod Method, Uri Uri, JsonObject? Body, string? ETag);
    private sealed class FakeApi : IFabricC1DeploymentApi
    {
        private readonly Queue<Func<FabricC1DeploymentReply>> replies = new();
        public List<Request> Requests { get; } = [];
        public void Reply(int status, JsonObject body, string? etag = null, string? operationUrl = null) =>
            replies.Enqueue(() => new(status, (JsonObject)body.DeepClone(), etag, operationUrl));
        public void List(params JsonObject[] items) =>
            Reply(200, new JsonObject { ["value"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()) });
        public void Fail(Exception error) => replies.Enqueue(() => throw error);
        public Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null, string? ifMatch = null, CancellationToken ct = default)
        {
            Requests.Add(new(method, uri, (JsonObject?)body?.DeepClone(), ifMatch));
            return Task.FromResult(replies.Dequeue()());
        }
    }
    private sealed class MemoryJournal : IFabricC1DeploymentJournal
    {
        public FabricC1DeploymentJournal? Current;
        public bool FailSave;
        public FabricC1DeploymentJournal? Load() => Current is null ? null :
            JsonSerializer.Deserialize<FabricC1DeploymentJournal>(JsonSerializer.Serialize(Current, Canonical.Json), Canonical.Json);
        public void Save(FabricC1DeploymentJournal state)
        {
            if (FailSave) throw new IOException("synthetic journal failure");
            Current = JsonSerializer.Deserialize<FabricC1DeploymentJournal>(JsonSerializer.Serialize(state, Canonical.Json), Canonical.Json);
        }
    }
    private sealed class Harness : IDisposable
    {
        public Clock Clock { get; } = new(DateTimeOffset.Parse("2026-11-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        public FakeApi Api { get; } = new();
        public MemoryJournal Store { get; } = new();
        public FabricC1DeploymentInputs Input { get; }
        public FabricC1Provisioner Provisioner { get; private set; }
        public string CapacityId { get; } = Guid.NewGuid().ToString("D");
        public Harness(int maxRequests = 100)
        {
            var tenant = Guid.NewGuid().ToString("D");
            var hash = new string('a', 64);
            Input = new(FabricC1Deployment.Contract, Guid.NewGuid().ToString("D"), new string('b', 40), tenant,
                Guid.NewGuid().ToString("D"), "francecentral", "c1test", Clock.GetUtcNow().AddHours(4),
                new(tenant, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "ServicePrincipalCertificate",
                    Path.Combine(Path.GetTempPath(), "synthetic-not-read.pem"), $"https://sts.windows.net/{tenant}/", "https://api.fabric.microsoft.com"),
                ["synthetic-admin@example.invalid"],
                new("10.241.0.0/20", "10.241.1.0/24", "10.241.2.0/24", "10.241.3.0/24", hash, hash),
                new("Standard_B2s", "synthetic", "synthetic", "synthetic", "1.0.0", "ssh-ed25519 synthetic"),
                new(hash, hash, hash, hash, hash, hash), maxRequests, 600);
            Provisioner = new(Input, Api, Store, Clock);
        }
        public void Capacity() => Seed("fabric-capacity", "FabricCapacity", new() { ["id"] = CapacityId });
        public void Seed(string key, string kind, JsonObject body)
        {
            var state = Store.Load() ?? new(Canonical.Hash(Input), new(), new());
            state.Objects[key] = new(kind, body["id"]!.GetValue<string>(), "synthetic-fixture-not-a-request", body);
            Store.Save(state);
            Restart();
        }
        public JsonObject Workspace(string key) => new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["displayName"] = FabricC1Deployment.Name(Input) + "-" + key,
            ["description"] = FabricC1Deployment.Marker(Input), ["capacityId"] = CapacityId
        };
        public JsonObject Subscription() => new() { ["subscriptionId"] = Input.SubscriptionId, ["tenantId"] = Input.TenantId, ["state"] = "Enabled" };
        public JsonObject Principal() => new()
        {
            ["id"] = Input.BootstrapIdentity!.PrincipalObjectId, ["appId"] = Input.BootstrapIdentity.ClientId,
            ["appOwnerOrganizationId"] = Input.TenantId, ["accountEnabled"] = true
        };
        public void Restart() => Provisioner = new(Input, Api, Store, Clock);
        public void Dispose() { }
    }
}
