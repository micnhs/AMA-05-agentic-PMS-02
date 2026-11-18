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
    public void FirstDeploymentCannotSilentlySwitchRegionOrUseTheHostedReservedEndpointRange()
    {
        using var run = new Harness();
        Assert.Contains("location", FabricC1Deployment.Plan(run.Input with { Location = "swedencentral" },
            run.Clock.GetUtcNow()).MissingInputs);
        var invalid = run.Input with
        {
            Network = run.Input.Network! with
            {
                VnetPrefix = "192.168.0.0/20", ClientPrefix = "192.168.1.0/24",
                AgentPrefix = "192.168.2.0/24", EndpointPrefix = "192.168.0.0/24"
            }
        };
        Assert.Contains("network.prefixes", FabricC1Deployment.Plan(invalid, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Fact]
    public void PrivateOperatorHasExplicitNatWithoutPublicNicOrImplicitOutbound()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "infra", "fabric", "c1.foundation.json")))!;
        var resources = template["resources"]!.AsArray().OfType<JsonObject>().ToArray();
        var nat = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/natGateways");
        var publicIp = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/publicIPAddresses");
        var network = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/virtualNetworks");
        var subnets = network["properties"]!["subnets"]!.AsArray().OfType<JsonObject>().ToArray();
        var client = Assert.Single(subnets, subnet => subnet["name"]!.GetValue<string>() == "client");
        Assert.False(client["properties"]!["defaultOutboundAccess"]!.GetValue<bool>());
        var natId = client["properties"]!["natGateway"]!["id"]!.GetValue<string>();
        Assert.Contains("Microsoft.Network/natGateways", natId, StringComparison.Ordinal);
        Assert.Contains(network["dependsOn"]!.AsArray(), dependency => dependency?.GetValue<string>() == natId);
        Assert.Equal("Static", publicIp["properties"]!["publicIPAllocationMethod"]!.GetValue<string>());
        Assert.Equal("Standard", publicIp["sku"]!["name"]!.GetValue<string>());
        Assert.Contains("Microsoft.Network/publicIPAddresses", nat["properties"]!["publicIpAddresses"]![0]!["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(Assert.Single(subnets, subnet => subnet["name"]!.GetValue<string>() == "agents")["properties"]!["natGateway"]);
        var nic = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/networkInterfaces");
        Assert.All(nic["properties"]!["ipConfigurations"]!.AsArray(), configuration => Assert.Null(configuration!["properties"]!["publicIPAddress"]));
        Assert.Equal("[parameters('tags')]", nat["tags"]!.GetValue<string>());
        Assert.Equal("[parameters('tags')]", publicIp["tags"]!.GetValue<string>());
        Assert.NotNull(template["outputs"]!["natGateway"]);
        Assert.NotNull(template["outputs"]!["egressPublicIp"]);
        var vault = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.KeyVault/vaults");
        Assert.True(vault["properties"]!["enablePurgeProtection"]!.GetValue<bool>());
        Assert.True(vault["properties"]!["enableSoftDelete"]!.GetValue<bool>());
        Assert.Equal(7, vault["properties"]!["softDeleteRetentionInDays"]!.GetValue<int>());
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

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ConfirmedAuthorizationDenialCanResumeWithoutPretendingItWasAnUnknownCreate(int status)
    {
        using var run = new Harness();
        run.Capacity();
        run.Api.List();
        run.Api.Reply(status, new());
        Assert.Equal("c1_deploy_http_" + status, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Equal(status, run.Store.Current.DeniedRequests["workspace-agent"].Status);
        run.Restart();
        run.Api.List();
        run.Api.Reply(201, run.Workspace("agent"));
        await run.Provisioner.EnsureWorkspace("agent", default);
        Assert.True(run.Store.Current!.Objects.ContainsKey("workspace-agent"));
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Post));
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
    public async Task DelegatedGraphUsesObservedUriAudienceRatherThanAssumingTheApplicationId()
    {
        using var run = new Harness(delegated: true);
        var input = run.Input with
        {
            ControlTokenBindings = run.Input.ControlTokenBindings! with { GraphAudience = "https://graph.microsoft.com/" }
        };
        var credential = new BoundCredential(Token(input, "https://graph.microsoft.com/", run.Clock.GetUtcNow(),
            "Application.ReadWrite.All", "Group.ReadWrite.All"));
        var api = new FabricC1DeploymentHttp(input, credential, new Dictionary<string, HttpClient>(), run.Clock);
        var rights = await api.GraphRights(default);
        Assert.True(rights.Delegated);
        rights.Require(FabricC1GraphOperation.OwnApplications);
        rights.Require(FabricC1GraphOperation.GroupMembers);
        Assert.Equal("https://graph.microsoft.com/.default", Assert.Single(credential.Scopes));

        var differentAudience = new FabricC1DeploymentHttp(input,
            new BoundCredential(Token(input, "00000003-0000-0000-c000-000000000000", run.Clock.GetUtcNow(), "Application.ReadWrite.All")),
            new Dictionary<string, HttpClient>(), run.Clock);
        Assert.Equal("c1_deploy_token_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            differentAudience.GraphRights(default))).Code);
        var differentIssuer = new FabricC1DeploymentHttp(input with
        {
            ControlTokenBindings = input.ControlTokenBindings! with
            { GraphIssuer = $"https://login.microsoftonline.com/{input.TenantId}/v2.0" }
        }, credential, new Dictionary<string, HttpClient>(), run.Clock);
        Assert.Equal("c1_deploy_token_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            differentIssuer.GraphRights(default))).Code);
    }

    [Fact]
    public async Task ArmRequestUsesItsOwnObservedAudienceAndCannotTreatDefaultScopeAsAudience()
    {
        using var run = new Harness(delegated: true);
        var input = run.Input with
        {
            ControlTokenBindings = run.Input.ControlTokenBindings! with { ArmAudience = "https://management.azure.com/" }
        };
        var credential = new BoundCredential(Token(input, "https://management.azure.com/", run.Clock.GetUtcNow(), "user_impersonation"));
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var api = new FabricC1DeploymentHttp(input, credential, new Dictionary<string, HttpClient>
        { ["management.azure.com"] = client }, run.Clock);
        var result = await api.Send(HttpMethod.Get,
            new($"https://management.azure.com/subscriptions/{input.SubscriptionId}?api-version=2022-12-01"));
        Assert.Equal(200, result.Status);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("https://management.azure.com/.default", Assert.Single(credential.Scopes));
        Assert.Contains("controlTokenBindings", FabricC1Deployment.Plan(input with
        {
            ControlTokenBindings = input.ControlTokenBindings! with { ArmAudience = "https://management.azure.com/.default" }
        }, run.Clock.GetUtcNow()).MissingInputs);

        var absent = new Credential();
        var unbound = new FabricC1DeploymentHttp(input with { ControlTokenBindings = null }, absent,
            new Dictionary<string, HttpClient>(), run.Clock);
        Assert.Equal("c1_deploy_control_token_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            unbound.GraphRights(default))).Code);
        Assert.Equal(0, absent.Requests);
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
        run.Api.List(InboundSetting(true));
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
        run.Api.List(InboundSetting(true));
        run.Api.Reply(200, new(), etag);
        if (etag is not null) run.Api.Reply(status, new());
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.DenyWorkspacePublicAccess("agent", default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("policy-agent"));
        Assert.Equal(etag is null ? 2 : 3, run.Api.Requests.Count);
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
        run.Api.List(InboundSetting(true));
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
    public void OwnApplicationsPermissionIsAcceptedWithoutGrantingGroupMembershipOrDelegatedRights()
    {
        using var run = new Harness();
        const string audience = "00000003-0000-0000-c000-000000000000";
        var token = Token(run.Input, audience, run.Clock.GetUtcNow(), "Application.ReadWrite.OwnedBy");
        FabricC1DeploymentHttp.ValidateBootstrapToken(token, run.Input.BootstrapIdentity!, audience, run.Clock.GetUtcNow(), graph: true);
        Assert.Equal("c1_deploy_graph_group_rights", Assert.Throws<DomainException>(() =>
            FabricC1DeploymentHttp.ValidateBootstrapToken(token, run.Input.BootstrapIdentity!, audience,
                run.Clock.GetUtcNow(), true, FabricC1GraphOperation.GroupMembers)).Code);
        Assert.False(new FabricC1GraphRights(true, ["Application.ReadWrite.OwnedBy"]).Allows(FabricC1GraphOperation.OwnApplications));
    }

    [Fact]
    public void GraphPermissionsAreCheckedForTheOperationRatherThanOneBroadBootstrapScope()
    {
        Assert.True(new FabricC1GraphRights(false, ["AppRegistration.Create"]).Allows(FabricC1GraphOperation.ApplicationCreate));
        Assert.False(new FabricC1GraphRights(false, ["AppRegistration.Create"]).Allows(FabricC1GraphOperation.OwnApplications));
        Assert.True(new FabricC1GraphRights(false, ["Group.Create"]).Allows(FabricC1GraphOperation.GroupCreate));
        Assert.False(new FabricC1GraphRights(false, ["Group.Create"]).Allows(FabricC1GraphOperation.GroupMembers));
        Assert.True(new FabricC1GraphRights(false, ["GroupMember.ReadWrite.All", "Application.ReadWrite.All"]).Allows(FabricC1GraphOperation.GroupMembers));
        Assert.True(new FabricC1GraphRights(false, ["Group.ReadWrite.All", "Application.ReadWrite.All"]).Allows(FabricC1GraphOperation.GroupMembers));
        Assert.False(new FabricC1GraphRights(false, ["GroupMember.ReadWrite.All", "Application.ReadWrite.OwnedBy"]).Allows(FabricC1GraphOperation.GroupMembers));
    }

    [Fact]
    public async Task OwnAppBootstrapCreatesFourSeparateIdentitiesAndPausesBeforeGroupOrPaidResources()
    {
        using var run = new Harness();
        var identities = QueueIdentities(run);
        var result = await run.Provisioner.EnsureIdentityPhase(new(false, ["Application.ReadWrite.OwnedBy"]), default);
        Assert.Equal("AwaitingGroupPermission", result.Status);
        Assert.Null(result.AllowlistGroupId);
        Assert.False(result.PaidResourcesCreated);
        Assert.Equal(4, result.PrincipalIds.Count);
        foreach (var role in FabricC1Provisioner.IdentityRoles)
        {
            Assert.Equal(identities[role].App["appId"]!.GetValue<string>(), result.ApplicationIds[role]);
            Assert.Equal(identities[role].Principal["id"]!.GetValue<string>(), result.PrincipalIds[role]);
        }
        Assert.All(run.Api.Requests, request => Assert.Equal("graph.microsoft.com", request.Uri.Host));
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.AbsolutePath.StartsWith("/v1.0/groups", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Api.Requests, request => request.Body?.ContainsKey("passwordCredentials") == true);
        Assert.DoesNotContain(run.Api.Requests, request => request.Body?.ContainsKey("requiredResourceAccess") == true);
    }

    [Fact]
    public async Task IdentityAndGroupResumeUsesActualIdsWithoutRecreatingOrRegranting()
    {
        using var run = new Harness();
        var identities = QueueIdentities(run);
        var group = Group(run);
        QueueGroup(run, group, resume: false);
        var memberIds = new[] { "runtime", "bootstrap", "negative" }.Select(role => identities[role].Principal["id"]!.GetValue<string>()).ToArray();
        for (var i = 0; i < memberIds.Length; i++)
        {
            run.Api.Reply(200, GroupMembers(group, memberIds.Take(i).ToArray()));
            run.Api.Reply(204, new());
            run.Api.Reply(200, GroupMembers(group, memberIds.Take(i + 1).ToArray()));
        }
        run.Api.Reply(200, GroupMembers(group, memberIds));
        var rights = new FabricC1GraphRights(false, ["Application.ReadWrite.All", "Group.ReadWrite.All"]);
        var initial = await run.Provisioner.EnsureIdentityPhase(rights, default);
        Assert.Equal("IdentitiesProvisioned", initial.Status);
        Assert.Equal(group["id"]!.GetValue<string>(), initial.AllowlistGroupId);
        var writes = run.Api.Requests.Count(request => request.Method == HttpMethod.Post);
        Assert.Equal(12, writes);

        run.Restart();
        foreach (var role in FabricC1Provisioner.IdentityRoles)
        {
            run.Api.Reply(200, identities[role].App);
            run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
            run.Api.Reply(200, identities[role].Principal);
        }
        QueueGroup(run, group, resume: true);
        foreach (var _ in memberIds) run.Api.Reply(200, GroupMembers(group, memberIds));
        run.Api.Reply(200, GroupMembers(group, memberIds));
        var resumed = await run.Provisioner.EnsureIdentityPhase(rights, default);
        Assert.Equal(initial.AllowlistGroupId, resumed.AllowlistGroupId);
        Assert.Equal(writes, run.Api.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.Contains(run.Api.Requests, request => request.Uri.AbsolutePath.EndsWith("/ownedObjects", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath.EndsWith("/members", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Api.Requests, request => request.Body?["@odata.id"]?.GetValue<string>()?.EndsWith(initial.PrincipalIds["hosted-control"], StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task MissingOwnApplicationRightsStopBeforeAnyEffect()
    {
        using var run = new Harness();
        Assert.Equal("c1_deploy_graph_rights", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureIdentityPhase(new(false, ["Group.ReadWrite.All"]), default))).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task ASecondIdentityCannotBeBoundToTheRuntimeApplicationByMistake()
    {
        using var run = new Harness();
        var appId = Guid.NewGuid().ToString("D");
        run.Seed("hosted-control-app", "Application", new() { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = appId });
        run.Api.List();
        run.Api.Reply(201, new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = Guid.NewGuid().ToString("D"),
            ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
        });
        Assert.Equal("c1_deploy_context", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsurePrincipalForRole("hosted-control", default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("hosted-control-sp"));
    }

    [Fact]
    public async Task ForeignGroupNameIsNotAdoptedEvenWhenItCopiesTheOwnershipMarker()
    {
        using var run = new Harness();
        run.Api.List(Group(run));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureAllowlistGroup(default))).Code);
        Assert.Single(run.Api.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedGroupCreationDistinguishesAutomaticNonAdminOwnership(bool administrator)
    {
        using var run = new Harness(delegated: true, delegatedAdministrator: administrator);
        var group = Group(run);
        run.Api.List();
        run.Api.Reply(201, group);
        run.Api.Reply(200, group);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        await run.Provisioner.EnsureAllowlistGroup(default);
        var request = Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal(administrator, request.Body!.ContainsKey("owners@odata.bind"));
    }

    [Fact]
    public async Task MissingDirectoryRoleClaimDoesNotSelectAutomaticNonAdministratorOwnership()
    {
        using var run = new Harness(delegated: true);
        var unbound = run.Input with { DelegatedGroupOwnerMode = null };
        var provisioner = new FabricC1Provisioner(unbound, run.Api, run.Store, run.Clock);
        Assert.Equal("c1_deploy_group_owner_review", (await Assert.ThrowsAsync<DomainException>(() =>
            provisioner.EnsureIdentityPhase(new(true, ["Application.ReadWrite.All", "Group.ReadWrite.All"]), default))).Code);
        Assert.Empty(run.Api.Requests);
        Assert.Contains("delegatedGroupOwnerMode", FabricC1Deployment.Plan(unbound, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Fact]
    public async Task GroupDeleteDoesNotInheritTheAdditionalServicePrincipalMembershipPermission()
    {
        using var run = new Harness();
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var credential = new BoundCredential(Token(run.Input, run.Input.ControlTokenBindings!.GraphAudience!,
            run.Clock.GetUtcNow(), "Group.ReadWrite.All"));
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>
        { ["graph.microsoft.com"] = client }, run.Clock);
        var group = Guid.NewGuid().ToString("D");
        await api.Send(HttpMethod.Delete, new($"https://graph.microsoft.com/v1.0/groups/{group}"));
        Assert.Equal(1, handler.Requests);
        Assert.Equal("c1_deploy_graph_group_rights", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Delete, new($"https://graph.microsoft.com/v1.0/groups/{group}/members/{Guid.NewGuid():D}/$ref")))).Code);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AdditionalGroupMembersStopBeforeGrantingAnythingElse()
    {
        using var run = new Harness();
        var group = Group(run);
        run.Seed("fabric-api-group", "SecurityGroup", group);
        foreach (var role in new[] { "runtime", "bootstrap", "negative" })
            run.Seed(role + "-sp", "ServicePrincipal", new() { ["id"] = Guid.NewGuid().ToString("D") });
        run.Api.Reply(200, GroupMembers(group, Guid.NewGuid().ToString("D")));
        Assert.Equal("c1_deploy_group_drift", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureAllowlistMember("runtime", default))).Code);
        Assert.Single(run.Api.Requests);
        Assert.Equal(HttpMethod.Get, run.Api.Requests[0].Method);
    }

    [Fact]
    public void ScopedTenantRequestPreservesExclusionsAndDelegationsInsteadOfEnablingEveryone()
    {
        var group = Guid.NewGuid().ToString("D");
        var existingGroup = Guid.NewGuid().ToString("D");
        var excludedGroup = Guid.NewGuid().ToString("D");
        var setting = TenantSetting("SyntheticFabricApi", true);
        setting["enabledSecurityGroups"] = new JsonArray(new JsonObject { ["graphId"] = existingGroup, ["name"] = "existing" });
        setting["excludedSecurityGroups"] = new JsonArray(new JsonObject { ["graphId"] = excludedGroup, ["name"] = "excluded" });
        setting["delegateToCapacity"] = true;
        var result = FabricC1Provisioner.PlanTenantSettings([setting], ["SyntheticFabricApi"], group, "own-generated-group");
        Assert.False(result.CanPrepareEmptyFoundation);
        Assert.Empty(result.Blockers);
        var change = Assert.Single(result.Changes);
        Assert.Equal(Canonical.Hash(setting), change.BeforeHash);
        Assert.Equal(2, change.Request["enabledSecurityGroups"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(setting["excludedSecurityGroups"], change.Request["excludedSecurityGroups"]));
        Assert.True(change.Request["delegateToCapacity"]!.GetValue<bool>());
    }

    [Fact]
    public void InactiveOtherGroupsAndUnscopablePoliciesAreNotSilentlyEnabled()
    {
        var group = Guid.NewGuid().ToString("D");
        var inactive = TenantSetting("Inactive", false);
        inactive["enabledSecurityGroups"] = new JsonArray(new JsonObject { ["graphId"] = Guid.NewGuid().ToString("D"), ["name"] = "unrelated" });
        var unscopable = TenantSetting("WholeTenantOnly", false);
        unscopable["canSpecifySecurityGroups"] = false;
        var result = FabricC1Provisioner.PlanTenantSettings([inactive, unscopable], ["Inactive", "WholeTenantOnly"], group, "own");
        Assert.Empty(result.Changes);
        Assert.Contains("enabling-would-also-reactivate-other-groups:Inactive", result.Blockers);
        Assert.Contains("setting-cannot-be-scoped:WholeTenantOnly", result.Blockers);
    }

    [Fact]
    public void AdminApiScopeNeverUsesTheRuntimeGroupEvenWhenItIsAnExistingSetting()
    {
        var group = Guid.NewGuid().ToString("D");
        var setting = TenantSetting("SyntheticAdminApi", true);
        setting["tenantSettingGroup"] = "AdminApiSettings";
        setting["enabledSecurityGroups"] = new JsonArray(new JsonObject { ["graphId"] = group, ["name"] = "own" });
        var result = FabricC1Provisioner.PlanTenantSettings([setting], ["SyntheticAdminApi"], group, "own");
        Assert.False(result.CanPrepareEmptyFoundation);
        Assert.Empty(result.Changes);
        Assert.Contains("runtime-group-has-admin-api-scope:SyntheticAdminApi", result.Blockers);
        Assert.Contains("runtime-group-cannot-receive-admin-api-scope:SyntheticAdminApi", result.Blockers);
    }

    [Fact]
    public void AlreadyEnabledTenantWideSettingIsReportedRatherThanNarrowedOrRewritten()
    {
        var result = FabricC1Provisioner.PlanTenantSettings([TenantSetting("ExistingWide", true)],
            ["ExistingWide"], Guid.NewGuid().ToString("D"), "own");
        Assert.True(result.SelectedRequirementsObserved);
        Assert.Empty(result.Changes);
        Assert.Equal(new[] { "ExistingWide" }, result.ExistingTenantWideSettings);
    }

    [Fact]
    public async Task ScopedTenantUpdateRequiresExactApprovedDiffBeforeAnyWrite()
    {
        using var run = new Harness(requiredTenantSettingNames: ["SyntheticFabricApi"]);
        var (group, members) = SeedTenantGroup(run);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(TenantSetting("SyntheticFabricApi", false));
        Assert.Equal("c1_deploy_admin_approval", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ApplyScopedTenantSettings(new string('e', 64), new string('f', 64), default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public void AdministrativeApprovalCannotOutliveOrChangeItsDeploymentBinding()
    {
        using var run = new Harness();
        var approval = new FabricC1TenantApproval("cohors-fabric-c1-scoped-admin-approval-1", Canonical.Hash(run.Input),
            new string('e', 64), new string('f', 64), run.Clock.GetUtcNow().AddMinutes(30));
        approval.Validate(run.Input, run.Clock.GetUtcNow());
        Assert.Equal("c1_deploy_admin_approval", Assert.Throws<DomainException>(() =>
            approval.Validate(run.Input with { Location = "swedencentral" }, run.Clock.GetUtcNow())).Code);
        Assert.Equal("c1_deploy_admin_approval", Assert.Throws<DomainException>(() =>
            approval.Validate(run.Input, approval.ExpiresAtUtc)).Code);
        Assert.Equal("c1_deploy_admin_approval", Assert.Throws<DomainException>(() =>
            (approval with { ExpiresAtUtc = run.Input.ExpiresAtUtc!.Value.AddSeconds(1) }).Validate(run.Input, run.Clock.GetUtcNow())).Code);
    }

    [Fact]
    public async Task ApprovedTenantPlanResumesACompletedFirstChangeAndALostSecondResponseWithoutRepeatingPost()
    {
        using var run = new Harness(requiredTenantSettingNames: ["SyntheticFirst", "SyntheticSecond"]);
        var (group, members) = SeedTenantGroup(run);
        var groupId = group["id"]!.GetValue<string>();
        var first = TenantSetting("SyntheticFirst", false);
        var second = TenantSetting("SyntheticSecond", false);
        var review = FabricC1Provisioner.PlanTenantSettings([first, second], run.Input.RequiredTenantSettingNames,
            groupId, group["displayName"]!.GetValue<string>());
        var appliedFirst = AppliedSetting(first, review.Changes[0]);
        var appliedSecond = AppliedSetting(second, review.Changes[1]);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(first, second);
        run.Api.List(first, second);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.Reply(200, new());
        run.Api.List(appliedFirst, second);
        run.Api.List(appliedFirst, second);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.Fail(new HttpRequestException("synthetic response lost after update"));
        var concurrencyReview = new string('f', 64);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            run.Provisioner.ApplyScopedTenantSettings(review.ChangeHash, concurrencyReview, default));
        Assert.Contains("tenant-change-SyntheticFirst", run.Store.Current!.Objects.Keys);
        Assert.Contains("tenant-change-SyntheticSecond", run.Store.Current.PendingCreates.Keys);

        run.Restart();
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(appliedFirst, appliedSecond);
        run.Api.List(appliedFirst, appliedSecond);
        run.Api.List(appliedFirst, appliedSecond);
        var resumed = await run.Provisioner.ApplyScopedTenantSettings(review.ChangeHash, concurrencyReview, default);
        Assert.True(resumed.SelectedRequirementsObserved);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task UnknownTenantWriteCannotBeReplayedWhenReadbackStillShowsOldState()
    {
        using var run = new Harness(requiredTenantSettingNames: ["SyntheticFabricApi"]);
        var (group, members) = SeedTenantGroup(run);
        var groupId = group["id"]!.GetValue<string>();
        var setting = TenantSetting("SyntheticFabricApi", false);
        var review = FabricC1Provisioner.PlanTenantSettings([setting], run.Input.RequiredTenantSettingNames,
            groupId, group["displayName"]!.GetValue<string>());
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(setting);
        run.Api.List(setting);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.Fail(new HttpRequestException("synthetic uncertain write"));
        var concurrencyReview = new string('f', 64);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            run.Provisioner.ApplyScopedTenantSettings(review.ChangeHash, concurrencyReview, default));
        run.Restart();
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(setting);
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ApplyScopedTenantSettings(review.ChangeHash, concurrencyReview, default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ForeignGroupMemberAfterApprovalStopsTenantMutation()
    {
        using var run = new Harness(requiredTenantSettingNames: ["SyntheticFabricApi"]);
        var (group, _) = SeedTenantGroup(run);
        run.Api.Reply(200, GroupMembers(group, Guid.NewGuid().ToString("D")));
        Assert.Equal("c1_deploy_group_drift", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ApplyScopedTenantSettings(new string('e', 64), new string('f', 64), default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public void EmptyFoundationAndSharedInboundActivationAreDifferentStages()
    {
        var original = InboundSetting(false);
        var review = FabricC1Provisioner.PlanTenantSettings([original], [FabricC1Provisioner.WorkspaceInboundSettingName],
            Guid.NewGuid().ToString("D"), "own");
        Assert.True(review.CanPrepareEmptyFoundation);
        Assert.False(review.SelectedRequirementsObserved);
        Assert.Empty(review.Changes);
        Assert.Equal("AwaitingSharedInboundApproval", review.Status);
        Assert.Equal(FabricC1Provisioner.SharedInboundApprovalBlocker, Assert.Single(review.Blockers));
        Assert.True(JsonNode.DeepEquals(original, review.SharedInbound!.Original));
        Assert.False(review.SharedInbound.AutomaticDisableAllowed);
        Assert.False(review.SharedInbound.PublicDenyProven);
        original["canSpecifySecurityGroups"] = true;
        var changed = FabricC1Provisioner.PlanTenantSettings([original], [FabricC1Provisioner.WorkspaceInboundSettingName],
            Guid.NewGuid().ToString("D"), "own");
        Assert.False(changed.CanPrepareEmptyFoundation);
        Assert.Contains("shared-inbound-shape-differs", changed.Blockers);
    }

    [Fact]
    public async Task ScopedApprovalCannotImplicitlyEnableTheSharedInboundSetting()
    {
        using var run = new Harness(requiredTenantSettingNames: ["SyntheticFabricApi", FabricC1Provisioner.WorkspaceInboundSettingName]);
        var (group, members) = SeedTenantGroup(run);
        var scoped = TenantSetting("SyntheticFabricApi", false);
        var shared = InboundSetting(false);
        var review = FabricC1Provisioner.PlanTenantSettings([scoped, shared], run.Input.RequiredTenantSettingNames,
            group["id"]!.GetValue<string>(), group["displayName"]!.GetValue<string>());
        var applied = AppliedSetting(scoped, Assert.Single(review.Changes));
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.List(scoped, shared);
        run.Api.List(scoped, shared);
        run.Api.Reply(200, GroupMembers(group, members));
        run.Api.Reply(200, new());
        run.Api.List(applied, shared);
        run.Api.List(applied, shared);
        var result = await run.Provisioner.ApplyScopedTenantSettings(review.ChangeHash, new string('f', 64), default);
        Assert.True(result.CanPrepareEmptyFoundation);
        Assert.False(result.SelectedRequirementsObserved);
        Assert.EndsWith("/SyntheticFabricApi/update", Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post).Uri.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledInboundSettingBlocksPolicyAndDataEvenWithAnOldPolicyReceipt(bool dataSetup)
    {
        using var run = new Harness();
        var purpose = dataSetup ? "released" : "agent";
        var workspace = run.Workspace(purpose);
        run.Seed("workspace-" + purpose, "Workspace", workspace);
        if (dataSetup) run.Seed("policy-released", "CommunicationPolicy", new() { ["id"] = workspace["id"]!.GetValue<string>() });
        run.Api.List(InboundSetting(false));
        Assert.Equal("c1_deploy_inbound_approval_required", (await Assert.ThrowsAsync<DomainException>(async () =>
        {
            if (dataSetup) await run.Provisioner.EnsureLakehouse("released", default);
            else await run.Provisioner.DenyWorkspacePublicAccess("agent", default);
        })).Code);
        Assert.Equal(HttpMethod.Get, Assert.Single(run.Api.Requests).Method);
    }

    [Fact]
    public async Task SharedInboundApprovalRequiresExplicitConsequencesAndCurrentBindingBeforeAnyCall()
    {
        using var run = new Harness();
        var approval = InboundApproval(run, InboundSetting(false));
        approval.Validate(run.Input, run.Clock.GetUtcNow());
        foreach (var invalid in new[]
        {
            approval with { DirectUserApprovalSha256 = "" },
            approval with { AcceptsTenantWideWorkspaceAdminAuthority = false },
            approval with { AcceptsNoAutomaticDisable = false },
            approval with { DeploymentBindingHash = new string('0', 64) },
            approval with { ExpiresAtUtc = run.Input.ExpiresAtUtc!.Value.AddSeconds(1) },
            approval with { ExpiresAtUtc = run.Clock.GetUtcNow() }
        })
            Assert.Equal("c1_deploy_inbound_approval", (await Assert.ThrowsAsync<DomainException>(() =>
                run.Provisioner.ApplyWorkspaceInboundEnable(invalid, default))).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task SharedEnableRetainsItsOriginalDiffAndCannotSilentlyReenableAfterLaterDrift()
    {
        using var run = new Harness();
        SeedPrivateLinkPreparation(run);
        var before = InboundSetting(false);
        before["delegateToCapacity"] = false;
        var after = (JsonObject)before.DeepClone();
        after["enabled"] = true;
        var unrelated = TenantSetting("SyntheticUntouchedFirewall", true);
        var approval = InboundApproval(run, before);
        run.Api.List(before, unrelated);
        run.Api.Reply(200, new());
        run.Api.List(after, unrelated);
        var result = await run.Provisioner.ApplyWorkspaceInboundEnable(approval, default);
        Assert.True(result.Enabled);
        Assert.False(result.PublicDenyProven);
        var post = Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
        Assert.EndsWith("/WorkspaceBlockInboundAccess/update", post.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.True(post.Body!["enabled"]!.GetValue<bool>());
        Assert.False(post.Body["delegateToCapacity"]!.GetValue<bool>());
        Assert.Empty(post.Body["enabledSecurityGroups"]!.AsArray());
        var effect = run.Store.Current!.Objects["tenant-shared-inbound-enable"];
        Assert.Equal("SharedTenantSettingEffect", effect.Kind);
        Assert.True(JsonNode.DeepEquals(before, effect.Response["original"]));
        Assert.False(effect.Response["automaticDisableAllowed"]!.GetValue<bool>());
        Assert.Equal("RetainUntilSeparateCurrentAdministrativeDecision", effect.Response["cleanupDisposition"]!.GetValue<string>());
        run.Restart();
        run.Api.List(after, unrelated);
        await run.Provisioner.ApplyWorkspaceInboundEnable(approval, default);
        run.Api.List(before, unrelated);
        Assert.Equal("c1_deploy_inbound_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ApplyWorkspaceInboundEnable(approval, default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostSharedEnableResponseIsReconciledWithoutRepeatingPost(bool observedEnabled)
    {
        using var run = new Harness();
        SeedPrivateLinkPreparation(run);
        var before = InboundSetting(false);
        var approval = InboundApproval(run, before);
        run.Api.List(before);
        run.Api.Fail(new HttpRequestException("synthetic response loss"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.ApplyWorkspaceInboundEnable(approval, default));
        var started = run.Store.Current!.StartedAtUtc;
        Assert.Contains("tenant-shared-inbound-enable", run.Store.Current.PendingCreates.Keys);
        run.Restart();
        run.Api.List(InboundSetting(observedEnabled));
        if (observedEnabled)
            Assert.True((await run.Provisioner.ApplyWorkspaceInboundEnable(approval, default)).Enabled);
        else
            Assert.Equal("c1_deploy_inbound_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
                run.Provisioner.ApplyWorkspaceInboundEnable(approval, default))).Code);
        Assert.Equal(started, run.Store.Current!.StartedAtUtc);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task SharedEnableServiceDenialIsNeitherSuccessNorUnknownMutation(int status)
    {
        using var run = new Harness();
        SeedPrivateLinkPreparation(run);
        var before = InboundSetting(false);
        run.Api.List(before);
        run.Api.Reply(status, new());
        Assert.Equal("c1_deploy_http_" + status, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ApplyWorkspaceInboundEnable(InboundApproval(run, before), default))).Code);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Equal(status, run.Store.Current.DeniedRequests["tenant-shared-inbound-enable"].Status);
        Assert.DoesNotContain("tenant-shared-inbound-enable", run.Store.Current.Objects.Keys);
        Assert.Contains("tenant-shared-inbound-original", run.Store.Current.Objects.Keys);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SharedSettingTransportRejectsDisableAndPretendedGroupScopeBeforeCredentials(bool enabled, bool groupScoped)
    {
        using var run = new Harness();
        var credential = new Credential();
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>(), run.Clock);
        var body = new JsonObject { ["enabled"] = enabled, ["enabledSecurityGroups"] = new JsonArray() };
        if (groupScoped) body["enabledSecurityGroups"]!.AsArray().Add(new JsonObject { ["graphId"] = Guid.NewGuid().ToString("D") });
        Assert.Equal("c1_deploy_shared_setting_scope", (await Assert.ThrowsAsync<DomainException>(() => api.Send(HttpMethod.Post,
            new("https://api.fabric.microsoft.com/v1/admin/tenantsettings/WorkspaceBlockInboundAccess/update"), body))).Code);
        Assert.Equal(0, credential.Requests);
    }

    private static JsonObject InboundSetting(bool enabled)
    {
        var setting = TenantSetting(FabricC1Provisioner.WorkspaceInboundSettingName, enabled);
        setting["canSpecifySecurityGroups"] = false;
        return setting;
    }

    private static FabricC1SharedInboundApproval InboundApproval(Harness run, JsonObject before)
    {
        var review = FabricC1Provisioner.PlanTenantSettings([before], [FabricC1Provisioner.WorkspaceInboundSettingName],
            Guid.NewGuid().ToString("D"), "own").SharedInbound!;
        return new("cohors-fabric-c1-shared-inbound-approval-1", Canonical.Hash(run.Input), review.ChangeHash,
            new string('e', 64), new string('f', 64), run.Clock.GetUtcNow().AddMinutes(30), true, true);
    }

    private static void SeedPrivateLinkPreparation(Harness run) =>
        run.Seed("arm-private-endpoints", "ArmDeployment", new()
        { ["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Resources/deployments/c1-private-endpoints" });

    private static (JsonObject Group, string[] Members) SeedTenantGroup(Harness run)
    {
        var group = Group(run);
        var groupId = group["id"]!.GetValue<string>();
        run.Seed("fabric-api-group", "SecurityGroup", group);
        run.Seed("fabric-api-members", "VerifiedGroupMembers", new() { ["id"] = groupId });
        var members = new List<string>();
        foreach (var role in new[] { "runtime", "bootstrap", "negative" })
        {
            var id = Guid.NewGuid().ToString("D");
            run.Seed(role + "-sp", "ServicePrincipal", new() { ["id"] = id });
            members.Add(id);
        }
        return (group, members.ToArray());
    }

    private static JsonObject AppliedSetting(JsonObject original, FabricC1TenantChange change)
    {
        var applied = (JsonObject)original.DeepClone();
        foreach (var pair in change.Request) applied[pair.Key] = pair.Value?.DeepClone();
        return applied;
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

    [Theory]
    [InlineData("servicePrincipals", "appRoleAssignments")]
    [InlineData("applications", "owners/$ref")]
    [InlineData("groups", "owners/$ref")]
    public async Task GraphAuthorityCannotBeUsedForUnboundConsentOrOwnerMutation(string collection, string suffix)
    {
        using var run = new Harness();
        var token = new Credential();
        var api = new FabricC1DeploymentHttp(run.Input, token, new Dictionary<string, HttpClient>(), run.Clock);
        Assert.Equal("c1_deploy_graph_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Post, new($"https://graph.microsoft.com/v1.0/{collection}/{Guid.NewGuid():D}/{suffix}")))).Code);
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

    [Fact]
    public void PrivateInputRejectsSharedParentPermissionsAndLinksOnTheAllocatedLinuxRunner()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("cohors-c1-private-").FullName;
        var path = Path.Combine(directory, "synthetic.txt");
        var alias = Path.Combine(directory, "alias.txt");
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(path, "synthetic-nonsecret");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            FabricC1PrivateFiles.RequireFile(path);
            File.CreateSymbolicLink(alias, path);
            Assert.Equal("c1_private_file_link", Assert.Throws<DomainException>(() => FabricC1PrivateFiles.RequireFile(alias)).Code);
            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.GroupWrite);
            Assert.Equal("c1_private_file_access", Assert.Throws<DomainException>(() => FabricC1PrivateFiles.RequireFile(path)).Code);
        }
        finally
        {
            File.Delete(alias);
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static JsonObject TenantSetting(string name, bool enabled) => new()
    {
        ["settingName"] = name, ["enabled"] = enabled, ["canSpecifySecurityGroups"] = true,
        ["tenantSettingGroup"] = "SyntheticNonAdminGroup", ["enabledSecurityGroups"] = new JsonArray()
    };

    private static Dictionary<string, (JsonObject App, JsonObject Principal)> QueueIdentities(Harness run)
    {
        var result = new Dictionary<string, (JsonObject, JsonObject)>(StringComparer.Ordinal);
        foreach (var role in FabricC1Provisioner.IdentityRoles)
        {
            var appId = Guid.NewGuid().ToString("D");
            var app = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = appId, ["signInAudience"] = "AzureADMyOrg",
                ["displayName"] = FabricC1Deployment.Name(run.Input) + "-" + role, ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
            };
            var principal = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = appId, ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
            };
            run.Api.List();
            run.Api.Reply(201, app);
            run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
            run.Api.List();
            run.Api.Reply(201, principal);
            result.Add(role, (app, principal));
        }
        return result;
    }

    private static JsonObject Group(Harness run) => new()
    {
        ["id"] = Guid.NewGuid().ToString("D"), ["displayName"] = FabricC1Deployment.Name(run.Input) + "-fabric-api",
        ["description"] = FabricC1Deployment.Marker(run.Input), ["securityEnabled"] = true,
        ["mailEnabled"] = false, ["isAssignableToRole"] = false, ["groupTypes"] = new JsonArray()
    };

    private static void QueueGroup(Harness run, JsonObject group, bool resume)
    {
        if (!resume) run.Api.List();
        run.Api.Reply(resume ? 200 : 201, group);
        run.Api.Reply(200, group);
        run.Api.List(new JsonObject { ["id"] = group["id"]!.GetValue<string>() });
    }

    private static JsonObject GroupMembers(JsonObject group, params string[] members) => new()
    {
        ["id"] = group["id"]!.GetValue<string>(),
        ["members"] = new JsonArray(members.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray())
    };

    private static AccessToken Token(FabricC1DeploymentInputs input, string audience, DateTimeOffset now, params string[] roles)
    {
        static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject
        {
            ["aud"] = audience, ["iss"] = $"https://sts.windows.net/{input.TenantId}/", ["tid"] = input.TenantId,
            ["oid"] = input.BootstrapIdentity!.PrincipalObjectId, ["appid"] = input.BootstrapIdentity.ClientId,
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(), ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds()
        };
        if (input.BootstrapIdentity.CredentialKind == "DelegatedAzureCli") payload["scp"] = string.Join(' ', roles);
        else payload["roles"] = new JsonArray(roles.Select(role => JsonValue.Create(role)).ToArray());
        return new(Encode(new { alg = "none", typ = "JWT" }) + "." + Encode(payload) + ".", now.AddHours(1));
    }

    private sealed class BoundCredential(AccessToken token) : TokenCredential
    {
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return token;
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class JsonHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("{}") });
        }
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
        public Harness(int maxRequests = 100, string[]? requiredTenantSettingNames = null, bool delegated = false, bool delegatedAdministrator = true)
        {
            var tenant = Guid.NewGuid().ToString("D");
            var hash = new string('a', 64);
            Input = new(FabricC1Deployment.Contract, Guid.NewGuid().ToString("D"), new string('b', 40), tenant,
                Guid.NewGuid().ToString("D"), "francecentral", "c1test", Clock.GetUtcNow().AddHours(4),
                new(tenant, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                    delegated ? "DelegatedAzureCli" : "ServicePrincipalCertificate",
                    delegated ? null : Path.Combine(Path.GetTempPath(), "synthetic-not-read.pem"),
                    $"https://sts.windows.net/{tenant}/", "https://api.fabric.microsoft.com"),
                ["synthetic-admin@example.invalid"],
                new("10.241.0.0/20", "10.241.1.0/24", "10.241.2.0/24", "10.241.3.0/24", hash, hash),
                new("Standard_B2s", "synthetic", "synthetic", "synthetic", "1.0.0", "ssh-ed25519 synthetic"),
                new(hash, hash, hash, hash, hash, hash), maxRequests, 600)
            {
                RequiredTenantSettingNames = requiredTenantSettingNames ?? [],
                DelegatedGroupOwnerMode = delegated ? delegatedAdministrator ? "ExplicitReviewedAdministrator" : "AutomaticReviewedNonAdministrator" : null,
                ControlTokenBindings = new("https://management.core.windows.net/", $"https://sts.windows.net/{tenant}/",
                    "00000003-0000-0000-c000-000000000000", $"https://sts.windows.net/{tenant}/")
            };
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
