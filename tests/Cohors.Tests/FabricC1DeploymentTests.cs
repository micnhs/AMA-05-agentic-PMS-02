using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using Azure.Core;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class FabricC1DeploymentTests
{
    [Fact]
    public void StandaloneStartValuesCannotActivateTheSupersededTopologyOrHideBootstrapBlockers()
    {
        using var run = new Harness(operatorNetwork: true);
        var plan = FabricC1Deployment.Plan(run.Input, run.Clock.GetUtcNow());
        Assert.Equal(["network.sharedHub"], plan.MissingInputs);
        Assert.False(plan.CanApply);
        Assert.Equal("Blocked", plan.Status);
        Assert.NotEmpty(plan.ImplementationBlockers);
        Assert.Equal("c1_deploy_inputs", Assert.Throws<DomainException>(() => FabricC1Deployment.RequireComplete(plan)).Code);
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
    public void SelectedNetworkSeparatesOperatorEndpointsAndRoutesOnlyWorkloadsThroughTheFirewall()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "infra", "fabric", "c1.foundation.json")))!;
        var resources = template["resources"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.DoesNotContain(resources, resource => resource["type"]!.GetValue<string>() is
            "Microsoft.Network/natGateways" or "Microsoft.Network/publicIPAddresses");
        var network = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/virtualNetworks");
        var subnets = network["properties"]!["subnets"]!.AsArray().OfType<JsonObject>().ToArray();
        var client = Assert.Single(subnets, subnet => subnet["name"]!.GetValue<string>() == "client");
        Assert.False(client["properties"]!["defaultOutboundAccess"]!.GetValue<bool>());
        Assert.Equal("[parameters('firewallPrivateIp')]", network["properties"]!["dhcpOptions"]!["dnsServers"]![0]!.GetValue<string>());
        Assert.All(subnets, subnet => Assert.Null(subnet["properties"]!["natGateway"]));
        Assert.Equal(["agents", "client"], subnets.Where(subnet => subnet["properties"]!["routeTable"] is not null)
            .Select(subnet => subnet["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        foreach (var name in new[] { "GatewaySubnet", "AzureFirewallSubnet" })
        {
            var subnet = Assert.Single(subnets, item => item["name"]!.GetValue<string>() == name);
            Assert.Null(subnet["properties"]!["routeTable"]);
            Assert.Null(subnet["properties"]!["networkSecurityGroup"]);
        }
        foreach (var name in new[] { "endpoints", "operator-endpoints" })
        {
            var subnet = Assert.Single(subnets, item => item["name"]!.GetValue<string>() == name);
            Assert.Equal("NetworkSecurityGroupEnabled", subnet["properties"]!["privateEndpointNetworkPolicies"]!.GetValue<string>());
        }
        var routes = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/routeTables");
        Assert.Equal("0.0.0.0/0", routes["properties"]!["routes"]![0]!["properties"]!["addressPrefix"]!.GetValue<string>());
        Assert.Equal("[parameters('firewallPrivateIp')]", routes["properties"]!["routes"]![0]!["properties"]!["nextHopIpAddress"]!.GetValue<string>());
        var operatorNsg = Assert.Single(resources, resource => resource["name"]!.GetValue<string>() == "[concat(parameters('name'), '-operator-endpoint-nsg')]");
        var operatorRule = Assert.Single(operatorNsg["properties"]!["securityRules"]!.AsArray(),
            rule => rule!["name"]!.GetValue<string>() == "DelegatedBootstrapHttps")!;
        Assert.Equal("443", operatorRule["properties"]!["destinationPortRange"]!.GetValue<string>());
        Assert.Equal("[parameters('network').operatorAccess.operatorEndpointPrefix]", operatorRule["properties"]!["destinationAddressPrefix"]!.GetValue<string>());
        var nic = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/networkInterfaces");
        Assert.All(nic["properties"]!["ipConfigurations"]!.AsArray(), configuration => Assert.Null(configuration!["properties"]!["publicIPAddress"]));
        var dnsLink = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/privateDnsZones/virtualNetworkLinks");
        Assert.Equal("[parameters('tags')]", dnsLink["tags"]!.GetValue<string>());
        Assert.NotNull(template["outputs"]!["vnet"]);
        Assert.NotNull(template["outputs"]!["egressRoutes"]);
        var vault = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.KeyVault/vaults");
        Assert.True(vault["properties"]!["enablePurgeProtection"]!.GetValue<bool>());
        Assert.True(vault["properties"]!["enableSoftDelete"]!.GetValue<bool>());
        Assert.Equal(7, vault["properties"]!["softDeleteRetentionInDays"]!.GetValue<int>());

        var edge = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "infra", "fabric", "c1.network-access.json")))!;
        var edgeResources = edge["resources"]!.AsArray().OfType<JsonObject>().ToArray();
        var ips = edgeResources.Where(resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/publicIPAddresses").ToArray();
        Assert.Equal(2, ips.Length);
        Assert.All(ips, ip => Assert.Equal("Static", ip["properties"]!["publicIPAllocationMethod"]!.GetValue<string>()));
        var gateway = Assert.Single(edgeResources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/virtualNetworkGateways");
        Assert.False(gateway["properties"]!["activeActive"]!.GetValue<bool>());
        Assert.Equal("[parameters('vpnAudience')]", gateway["properties"]!["vpnClientConfiguration"]!["aadAudience"]!.GetValue<string>());
        Assert.Equal("OpenVPN", Assert.Single(gateway["properties"]!["vpnClientConfiguration"]!["vpnClientProtocols"]!.AsArray())!.GetValue<string>());
        var policy = Assert.Single(edgeResources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/firewallPolicies");
        Assert.True(policy["properties"]!["dnsSettings"]!["enableProxy"]!.GetValue<bool>());
        var rules = Assert.Single(edgeResources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/firewallPolicies/ruleCollectionGroups");
        var rule = rules["properties"]!["ruleCollections"]![0]!["rules"]![0]!;
        Assert.False(rule["terminateTLS"]!.GetValue<bool>());
        Assert.Equal("[parameters('network').operatorAccess.httpsEgressFqdns]", rule["targetFqdns"]!.GetValue<string>());
        Assert.DoesNotContain("vpnClientPrefix", rule["sourceAddresses"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalNetworkBindingIsUnchangedButCannotOmitTheNewlySelectedConnection()
    {
        using var run = new Harness();
        var json = JsonSerializer.Serialize(run.Input.Network, Canonical.Json);
        Assert.DoesNotContain("operatorAccess", json, StringComparison.Ordinal);
        var original = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(6, original.Count);
        Assert.Equal(Canonical.Hash(run.Input.Network), Canonical.Hash(original));
        Assert.Contains("network.operatorAccess", FabricC1Deployment.Plan(run.Input, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Theory]
    [InlineData("gateway", "10.241.1.0/27")]
    [InlineData("firewall", "10.241.5.0/27")]
    [InlineData("operator", "10.242.6.0/27")]
    [InlineData("pool", "10.241.7.0/24")]
    [InlineData("pool", "192.168.10.0/24")]
    [InlineData("pool", "192.168.0.0/24")]
    [InlineData("pool", "172.30.240.0/28")]
    [InlineData("pool", "8.8.8.0/24")]
    public void OperatorPrefixesRejectOverlapUndersizingExternalRangesAndKnownLocalRoutes(string field, string value)
    {
        using var run = new Harness(operatorNetwork: true);
        var access = run.Input.Network!.OperatorAccess!;
        access = field switch
        {
            "gateway" => access with { GatewayPrefix = value },
            "firewall" => access with { FirewallPrefix = value },
            "operator" => access with { OperatorEndpointPrefix = value },
            _ => access with { VpnClientPrefix = value }
        };
        var input = run.Input with { Network = run.Input.Network with { OperatorAccess = access } };
        Assert.Contains("network.operatorAccess.prefixes", FabricC1Deployment.Plan(input, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Theory]
    [InlineData("*.blob.core.windows.net")]
    [InlineData("https://management.azure.com")]
    [InlineData("Management.azure.com")]
    [InlineData("10.241.1.4")]
    [InlineData("example.invalid:443")]
    [InlineData("example.invalid\n")]
    public void EgressReviewCannotTurnIntoWildcardUrlIpOrNoncanonicalHost(string host)
    {
        using var run = new Harness(operatorNetwork: true);
        var input = run.Input with { Network = run.Input.Network! with
        {
            OperatorAccess = run.Input.Network!.OperatorAccess! with
            { HttpsEgressFqdns = ["login.microsoftonline.com", "management.azure.com", host] }
        }};
        Assert.Contains("network.operatorAccess.httpsEgressFqdns",
            FabricC1Deployment.Plan(input, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Fact]
    public void NetworkParametersExcludeOperatorIdentityAndLocalRouteEvidence()
    {
        using var run = new Harness(operatorNetwork: true, vpnUserCount: 4);
        var parameters = FabricC1Deployment.ArmParameters(run.Input).ToJsonString();
        Assert.DoesNotContain(run.Input.Network!.OperatorAccess!.OperatorObjectId!, parameters, StringComparison.Ordinal);
        Assert.All(run.Input.Network.OperatorAccess.VpnUsers!.ObjectIds!, id =>
            Assert.DoesNotContain(id, parameters, StringComparison.Ordinal));
        Assert.DoesNotContain("vpnUsers", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("excludedPrefixes", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("clientPrerequisitesReviewSha256", parameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FoundationUsesOnlyTheAddressOfItsRecordedFirewall()
    {
        using var run = new Harness(operatorNetwork: true);
        run.Seed("arm-network-access", "ArmDeployment", NetworkDeploymentFixture(run));
        var parameters = run.Provisioner.NetworkedFoundationArmParameters();
        Assert.Equal("10.241.5.4", parameters["firewallPrivateIp"]!["value"]!.GetValue<string>());
        parameters["firewallPrivateIp"]!["value"] = "10.241.5.5";
        Assert.Equal("c1_deploy_operator_network", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm("foundation", new(), parameters, default))).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("subnet")]
    [InlineData("public-ip")]
    [InlineData("capacity")]
    [InlineData("address")]
    [InlineData("stage")]
    public void FirewallReceiptCannotAuthorizeAnotherEgressOrUnfinishedNetwork(string field)
    {
        using var run = new Harness(operatorNetwork: true);
        var deployment = NetworkDeploymentFixture(run);
        var properties = deployment["properties"]!["outputs"]!["firewall"]!["value"]!["properties"]!;
        switch (field)
        {
            case "policy": properties["firewallPolicy"]!["id"] = "/foreign/policy"; break;
            case "subnet": properties["ipConfigurations"]![0]!["properties"]!["subnet"]!["id"] = "/foreign/subnet"; break;
            case "public-ip": properties["ipConfigurations"]![0]!["properties"]!["publicIPAddress"]!["id"] = "/foreign/ip"; break;
            case "capacity": properties["autoscaleConfiguration"]!["minCapacity"] = 3; break;
            case "address": properties["ipConfigurations"]![0]!["properties"]!["privateIPAddress"] = "10.241.1.4"; break;
            case "stage": deployment["properties"]!["provisioningState"] = "Running"; break;
        }
        run.Seed("arm-network-access", "ArmDeployment", deployment);
        Assert.Equal("c1_deploy_firewall_output",
            Assert.Throws<DomainException>(() => run.Provisioner.NetworkedFoundationArmParameters()).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task OperatorAudienceUsesItsOwnAssignmentAndResumesWithoutAnotherWrite()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        var fixture = QueueOperatorVpn(run);
        var originalActor = run.Input.BootstrapIdentity;
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        var parameters = run.Provisioner.OperatorNetworkArmParameters();
        Assert.Equal(fixture.App["appId"]!.GetValue<string>(), parameters["vpnAudience"]!["value"]!.GetValue<string>());
        Assert.NotEqual(originalActor!.ClientId, parameters["vpnAudience"]!["value"]!.GetValue<string>());
        Assert.Equal(originalActor, run.Input.BootstrapIdentity);
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.AbsolutePath.StartsWith("/v1.0/groups", StringComparison.Ordinal));
        var creation = Assert.Single(run.Api.Requests, request => request.Uri.AbsolutePath == "/v1.0/applications" && request.Method == HttpMethod.Post);
        Assert.Empty(creation.Body!["keyCredentials"]!.AsArray());
        Assert.Empty(creation.Body!["passwordCredentials"]!.AsArray());
        Assert.Empty(creation.Body!["requiredResourceAccess"]!.AsArray());
        Assert.Equal(FabricC1Deployment.VpnClientApplicationId,
            creation.Body!["api"]!["preAuthorizedApplications"]![0]!["appId"]!.GetValue<string>());
        var writes = run.Api.Requests.Count(request => request.Method != HttpMethod.Get);
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List(fixture.Assignment);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        Assert.Equal(writes, run.Api.Requests.Count(request => request.Method != HttpMethod.Get));
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task OperatorAssignmentPermissionMustBePresentBeforeAnyNewDirectoryObject()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        Assert.Equal("c1_deploy_graph_rights", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureOperatorNetworkIdentity(new(true, ["Application.ReadWrite.All"]), default))).Code);
        Assert.Empty(run.Api.Requests);
        Assert.False(new FabricC1GraphRights(true, ["AppRoleAssignment.ReadWrite.All"]).Allows(FabricC1GraphOperation.AppRoleAssignmentGrant));
    }

    [Fact]
    public async Task FourSelectedVpnUsersAreIndividuallyJournaledWithoutChangingTheOperatorOrCohorsRights()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: 4);
        var fixture = QueueOperatorVpn(run);
        var assignments = QueueAdditionalVpnUsers(run, fixture);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        var grants = run.Api.Requests.Where(request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, grants.Length);
        Assert.Equal(run.Input.Network!.OperatorAccess!.VpnUsers!.ObjectIds!.Order(StringComparer.Ordinal),
            grants.Select(request => request.Body!["principalId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(grants, request =>
        {
            Assert.Equal(3, request.Body!.Count);
            Assert.Equal(Guid.Empty.ToString("D"), request.Body["appRoleId"]!.GetValue<string>());
            Assert.Equal(fixture.Principal["id"]!.GetValue<string>(), request.Body["resourceId"]!.GetValue<string>());
        });
        Assert.True(fixture.Principal["appRoleAssignmentRequired"]!.GetValue<bool>());
        Assert.Empty(fixture.App["appRoles"]!.AsArray());
        Assert.Empty(fixture.App["requiredResourceAccess"]!.AsArray());
        Assert.Equal(4, run.Store.Current!.Objects.Values.Count(item => item.Kind == "OperatorVpnAssignment"));
        Assert.Equal(run.Input.BootstrapIdentity!.PrincipalObjectId, run.Input.Network.OperatorAccess.OperatorObjectId);
        Assert.Empty(run.Store.Current.PendingCreates);
        var writes = run.Api.Requests.Count(request => request.Method != HttpMethod.Get);
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List(assignments);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        Assert.Equal(writes, run.Api.Requests.Count(request => request.Method != HttpMethod.Get));
        var parameters = run.Provisioner.OperatorNetworkArmParameters().ToJsonString();
        Assert.All(run.Input.Network.OperatorAccess.VpnUsers.ObjectIds!, id => Assert.DoesNotContain(id, parameters, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("operator-removed")]
    [InlineData("missing-authorization")]
    [InlineData("missing-evidence")]
    public async Task InvalidVpnUserSelectionCannotSilentlyFallBackToTheOperator(string fault)
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: 4);
        var access = run.Input.Network!.OperatorAccess!;
        var selected = access.VpnUsers!;
        selected = fault switch
        {
            "duplicate" => selected with { ObjectIds = [selected.ObjectIds![0], selected.ObjectIds[0]] },
            "operator-removed" => selected with { ObjectIds = selected.ObjectIds![1..] },
            "missing-authorization" => selected with { AuthorizationSha256 = null },
            "missing-evidence" => selected with { DirectoryEvidenceSha256 = null },
            _ => selected
        };
        var input = run.Input with { Network = run.Input.Network with
        { OperatorAccess = access with { VpnUsers = fault == "missing" ? null : selected } } };
        var provisioner = new FabricC1Provisioner(input, run.Api, run.Store, run.Clock);
        Assert.Equal("c1_deploy_operator_network", (await Assert.ThrowsAsync<DomainException>(() =>
            provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default))).Code);
        Assert.Empty(run.Api.Requests);
        if (fault == "missing")
        {
            Assert.DoesNotContain("vpnUsers", JsonSerializer.Serialize(input.Network.OperatorAccess, Canonical.Json), StringComparison.Ordinal);
            Assert.Empty(FabricC1Deployment.VpnUserObjectIds(input));
            Assert.Equal([access.OperatorObjectId!], FabricC1Deployment.VpnUserObjectIds(input, allowHistoricalCleanup: true));
        }
    }

    [Fact]
    public async Task UnjournaledSelectedVpnUserBlocksFurtherGrantsAsWellAsCleanup()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: 4);
        var fixture = QueueOperatorVpn(run, lostAssignment: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default));
        var other = VpnAssignment(fixture.Principal, run.Input.Network!.OperatorAccess!.VpnUsers!.ObjectIds![1], 1);
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List(other);
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal));
        run.Api.Reply(200, fixture.Principal);
        run.Api.List(other);
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UncertainFourthUserGrantCannotBeReplayedAndParentAbsenceSettlesItsOriginalIntent()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: 4);
        var fixture = QueueOperatorVpn(run);
        var assignments = QueueAdditionalVpnUsers(run, fixture, loseLast: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default));
        var key = "operator-vpn-assignment-" + run.Input.Network!.OperatorAccess!.VpnUsers!.ObjectIds![3];
        Assert.Contains(key, run.Store.Current!.PendingCreates.Keys);
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List(assignments[..3]);
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default))).Code);
        Assert.Equal(4, run.Api.Requests.Count(request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal)));
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.True(result.ActiveIdentityInventoryAbsent);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task VpnGrantTransportUsesAssignmentPermissionsRatherThanJustApplicationOwnership()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var credential = new BoundCredential(Token(run.Input, run.Input.ControlTokenBindings!.GraphAudience!,
            run.Clock.GetUtcNow(), "Application.ReadWrite.All"));
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>
        { ["graph.microsoft.com"] = client }, run.Clock);
        var target = Guid.NewGuid().ToString("D");
        var uri = new Uri($"https://graph.microsoft.com/v1.0/servicePrincipals/{target}/appRoleAssignedTo");
        var body = new JsonObject
        {
            ["resourceId"] = target, ["principalId"] = run.Input.Network!.OperatorAccess!.OperatorObjectId,
            ["appRoleId"] = Guid.Empty.ToString("D")
        };
        await api.Send(HttpMethod.Get, uri);
        Assert.Equal("c1_deploy_graph_rights", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Post, uri, body))).Code);
        Assert.Equal(1, handler.Requests);
        body["appRoleId"] = Guid.NewGuid().ToString("D");
        Assert.Equal("c1_deploy_vpn_assignment_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Post, uri, body))).Code);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task VpnGrantTransportAllowsASelectedNonOperatorButRejectsAnUnselectedUser()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: 4);
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var credential = new BoundCredential(Token(run.Input, run.Input.ControlTokenBindings!.GraphAudience!,
            run.Clock.GetUtcNow(), "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All"));
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>
        { ["graph.microsoft.com"] = client }, run.Clock);
        var target = Guid.NewGuid().ToString("D");
        var uri = new Uri($"https://graph.microsoft.com/v1.0/servicePrincipals/{target}/appRoleAssignedTo");
        var body = new JsonObject
        {
            ["resourceId"] = target, ["principalId"] = run.Input.Network!.OperatorAccess!.VpnUsers!.ObjectIds![1],
            ["appRoleId"] = Guid.Empty.ToString("D")
        };
        await api.Send(HttpMethod.Post, uri, body);
        Assert.Equal(1, handler.Requests);
        body["principalId"] = Guid.NewGuid().ToString("D");
        Assert.Equal("c1_deploy_vpn_assignment_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Post, uri, body))).Code);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task LostVpnAssignmentResponseCannotRegrantAfterAnEmptyResumeRead()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        var fixture = QueueOperatorVpn(run, lostAssignment: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default));
        Assert.Contains("operator-vpn-assignment", run.Store.Current!.PendingCreates.Keys);
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List();
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal));
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List(fixture.Assignment);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        Assert.Equal(fixture.Assignment["id"]!.GetValue<string>(), run.Store.Current!.Objects["operator-vpn-assignment"].Id);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post &&
            request.Uri.AbsolutePath.EndsWith("/appRoleAssignedTo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostVpnIdentifierUpdateReconcilesWithoutAnotherPatch()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        var fixture = QueueOperatorVpn(run, lostIdentifier: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default));
        Assert.Contains("operator-vpn-configuration", run.Store.Current!.PendingCreates.Keys);
        fixture.App["identifierUris"] = new JsonArray("api://" + fixture.App["appId"]!.GetValue<string>());
        run.Restart();
        QueueOperatorVpnResume(run, fixture);
        run.Api.List();
        run.Api.Reply(201, fixture.Assignment);
        run.Api.List(fixture.Assignment);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task ForeignVpnAssignmentPreventsCleanupInsteadOfBroadeningOrDeletingAccess()
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        var fixture = QueueOperatorVpn(run);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        var foreign = (JsonObject)fixture.Assignment.DeepClone();
        foreign["principalId"] = Guid.NewGuid().ToString("D");
        run.Api.Reply(200, fixture.Principal);
        run.Api.List(fixture.Assignment, foreign);
        Assert.Equal("c1_deploy_vpn_assignment", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task VpnIdentityCleanupDeletesOnlyItsOwnObjectsAndReadsTheirAbsence(int userCount)
    {
        using var run = new Harness(delegated: true, operatorNetwork: true, vpnUserCount: userCount);
        var fixture = QueueOperatorVpn(run);
        var assignments = QueueAdditionalVpnUsers(run, fixture);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        run.Api.Reply(200, fixture.Principal);
        run.Api.List(assignments);
        run.Api.Reply(200, fixture.App);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        run.Api.Reply(200, fixture.App);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.True(result.ActiveIdentityInventoryAbsent);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Equal(2, result.AbsentIdentityKeys.Length);
        Assert.All(run.Api.Requests.Where(request => request.Method == HttpMethod.Delete), request =>
            Assert.True(request.Uri.AbsolutePath.EndsWith("/" + fixture.App["id"]!.GetValue<string>(), StringComparison.Ordinal) ||
                request.Uri.AbsolutePath.EndsWith("/" + fixture.Principal["id"]!.GetValue<string>(), StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedParentAbsenceSettlesOnlyItsDependentUncertainVpnWrite(bool lostIdentifier)
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        QueueOperatorVpn(run, lostAssignment: !lostIdentifier, lostIdentifier: lostIdentifier);
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default));
        var pending = lostIdentifier ? "operator-vpn-configuration" : "operator-vpn-assignment";
        Assert.Contains(pending, run.Store.Current!.PendingCreates.Keys);
        run.Api.Reply(404, new());
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.True(result.ActiveIdentityInventoryAbsent);
        Assert.False(result.CompleteC1Cleanup);
        Assert.DoesNotContain(pending, run.Store.Current!.PendingCreates.Keys);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("credential")]
    [InlineData("owner")]
    public async Task VpnAudienceDriftIsRejectedBeforeAnotherWrite(string field)
    {
        using var run = new Harness(delegated: true, operatorNetwork: true);
        var fixture = QueueOperatorVpn(run);
        await run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default);
        var writes = run.Api.Requests.Count(request => request.Method != HttpMethod.Get);
        if (field == "scope") fixture.App["api"]!["preAuthorizedApplications"]![0]!["appId"] = Guid.NewGuid().ToString("D");
        if (field == "credential") fixture.App["passwordCredentials"] = new JsonArray(new JsonObject { ["keyId"] = Guid.NewGuid().ToString("D") });
        run.Restart();
        run.Api.Reply(200, fixture.App);
        if (field == "owner")
            run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId }, new JsonObject { ["id"] = Guid.NewGuid().ToString("D") });
        else run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        Assert.Equal(field == "owner" ? "c1_deploy_app_owner" : "c1_deploy_vpn_application",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(VpnRights(), default))).Code);
        Assert.Equal(writes, run.Api.Requests.Count(request => request.Method != HttpMethod.Get));
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

    [Theory]
    [InlineData("initial", 60)]
    [InlineData("running", 75)]
    [InlineData("result", 90)]
    public async Task ProviderPollMinimumSurvivesCancellationAndResumeWithoutNewBudget(string stage, int seconds)
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        Assert.DoesNotContain("pendingOperationNotBeforeUtc", Canonical.Serialize(run.Store.Current));
        var started = run.Clock.GetUtcNow();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: stage == "initial" ? seconds : 0);
        if (stage != "initial")
            run.Api.Reply(200, new() { ["status"] = stage == "running" ? "Running" : "Succeeded" }, retryAfterSeconds: seconds);
        using var cancel = new CancellationTokenSource();
        var first = run.Provisioner.EnsureWorkspace("agent", cancel.Token);
        Assert.False(first.IsCompleted);
        var requests = run.Api.Requests.Count;
        var notBefore = started.AddSeconds(seconds);
        Assert.Equal(notBefore, run.Store.Current!.PendingOperationNotBeforeUtc![operation]);
        Assert.Equal(notBefore, Assert.Single(run.Clock.PendingTimers));
        run.Clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Equal(requests, run.Api.Requests.Count);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        run.Restart();
        var item = run.Workspace("agent");
        run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(200, item);
        var resumed = run.Provisioner.EnsureWorkspace("agent", default);
        Assert.False(resumed.IsCompleted);
        Assert.Equal(requests, run.Api.Requests.Count);
        Assert.Equal(notBefore, Assert.Single(run.Clock.PendingTimers));
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(item["id"]!.GetValue<string>(), result["id"]!.GetValue<string>());
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        Assert.Equal(requests + 2, run.Store.Current.Requests);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
        Assert.Null(run.Store.Current.PendingOperationNotBeforeUtc);
        Assert.DoesNotContain("pendingOperationNotBeforeUtc", Canonical.Serialize(run.Store.Current));
    }

    [Theory]
    [InlineData(false, 600)]
    [InlineData(true, 900)]
    public async Task ProviderDelayBeyondOriginalDeadlineIsPersistedButNeverShortened(bool running, int seconds)
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        var started = run.Clock.GetUtcNow();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: running ? 0 : seconds);
        if (running) run.Api.Reply(200, new() { ["status"] = "Running" }, retryAfterSeconds: seconds);
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        var requests = run.Api.Requests.Count;
        Assert.Equal(running ? 3 : 2, requests);
        Assert.Equal(started.AddSeconds(seconds), run.Store.Current!.PendingOperationNotBeforeUtc![operation]);
        Assert.Empty(run.Clock.PendingTimers);
        run.Restart();
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(requests, run.Api.Requests.Count);
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        Assert.Equal(requests, run.Store.Current.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-operation")]
    [InlineData("before-start")]
    public async Task ResumeDoesNotInventMissingOrInvalidOriginalPollSchedule(string drift)
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: 60);
        using var cancel = new CancellationTokenSource();
        var first = run.Provisioner.EnsureWorkspace("agent", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var original = run.Store.Current!;
        run.Store.Save(original with
        {
            PendingOperationNotBeforeUtc = drift == "missing" ? null : new()
            {
                [drift == "other-operation" ? operation + "/result" : operation] =
                    drift == "before-start" ? original.StartedAtUtc!.Value.AddSeconds(-1) : run.Clock.GetUtcNow().AddSeconds(60)
            }
        });
        run.Restart();
        Assert.Equal("c1_deploy_operation_delay", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
        Assert.Empty(run.Clock.PendingTimers);
    }

    [Fact]
    public async Task UnknownOperationScheduleSaveFencesFurtherPollingUntilOriginalJournalIsReopened()
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        var notBefore = run.Clock.GetUtcNow().AddSeconds(60);
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: 60);
        run.Store.AfterSave = state =>
        {
            if (state.PendingOperationNotBeforeUtc is not null) throw new IOException("synthetic uncertain schedule save");
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.EnsureWorkspace("agent", default));
        Assert.Equal(operation, run.Store.Current!.PendingOperations["workspace-agent"]);
        Assert.Equal(notBefore, run.Store.Current.PendingOperationNotBeforeUtc![operation]);
        run.Store.AfterSave = null;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
        run.Restart();
        run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(200, run.Workspace("agent"));
        var resumed = run.Provisioner.EnsureWorkspace("agent", default);
        Assert.Equal(2, run.Api.Requests.Count);
        Assert.Equal(notBefore, Assert.Single(run.Clock.PendingTimers));
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DeadlineElapsedDuringScheduleSavePreventsWaitAndProviderPoll()
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: 60);
        run.Store.AfterSave = state =>
        {
            if (state.PendingOperationNotBeforeUtc is not null) run.Clock.Advance(TimeSpan.FromSeconds(601));
        };
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
        Assert.Empty(run.Clock.PendingTimers);
        Assert.Equal(2, run.Store.Current!.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollOrResultThrottlingIsNotRetriedAndKeepsItsMinimumForExplicitResume(bool result)
    {
        using var run = new Harness(manualTimers: true);
        run.Capacity();
        var operation = "https://api.fabric.microsoft.com/v1/operations/" + Guid.NewGuid().ToString("D");
        run.Api.List();
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: 0);
        if (result) run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(429, new(), retryAfterSeconds: 60);
        Assert.Equal("c1_deploy_http_429", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        var requests = run.Api.Requests.Count;
        Assert.Equal(result ? 4 : 3, requests);
        run.Restart();
        run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(200, run.Workspace("agent"));
        var resumed = run.Provisioner.EnsureWorkspace("agent", default);
        Assert.False(resumed.IsCompleted);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requests + 2, run.Api.Requests.Count);
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
        using var run = new Harness(maxRequests: 3);
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
    public void CheckpointAllocationKeepsItsOriginalSeedAndPrepaidBudgetAfterRestart()
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        var original = run.Provisioner.PrepareBlobCheckpoint(execution, binding);
        Assert.Equal(0, run.Store.Current!.Requests);
        var persisted = Canonical.Hash(run.Store.Current);
        var resumed = new FabricC1Provisioner(run.Input, run.Api, run.Store, new Clock(run.Clock.GetUtcNow().AddSeconds(5)));
        Assert.Equal(Canonical.Hash(original), Canonical.Hash(resumed.PrepareBlobCheckpoint(execution, binding)));
        Assert.Equal(persisted, Canonical.Hash(run.Store.Current));
        Assert.Equal("c1_deploy_checkpoint_drift", Assert.Throws<DomainException>(() =>
            resumed.PrepareBlobCheckpoint(execution, binding with { AllocatedRequests = 41 })).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public void LaterRuntimeAllocationCannotStartAnotherExecutionOrAnotherClock()
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        var first = run.Provisioner.PrepareBlobCheckpoint(execution, binding);
        var later = new FabricC1Provisioner(run.Input, run.Api, run.Store, new Clock(run.Clock.GetUtcNow().AddSeconds(5)));
        var changed = execution with { RunId = Guid.NewGuid().ToString("D") };
        Assert.Equal("c1_deploy_checkpoint_drift", Assert.Throws<DomainException>(() =>
            later.PrepareBlobCheckpoint(changed, binding with { Runtime = "hosted", ExecutionBindingHash = Canonical.Hash(changed) })).Code);
        var second = later.PrepareBlobCheckpoint(execution, binding with { Runtime = "hosted" });
        Assert.Equal(first.Seed.Checkpoint.StartedAtUtc, second.Seed.Checkpoint.StartedAtUtc);
        Assert.Equal(first.Seed.Checkpoint.DeadlineUtc, second.Seed.Checkpoint.DeadlineUtc);
        Assert.Equal(first.IoDeadlineUtc, second.IoDeadlineUtc);
        Assert.Empty(run.Api.Requests);
    }

    [Theory]
    [InlineData("budget", "c1_deploy_budget")]
    [InlineData("source", "c1_deploy_checkpoint_binding")]
    [InlineData("account", "c1_deploy_checkpoint_storage")]
    [InlineData("caller", "c1_deploy_context")]
    public void CheckpointAllocationRequiresOriginalBudgetSourceAndOwnedResources(string invalid, string code)
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        if (invalid == "budget") binding = binding with { AllocatedRequests = 81 };
        if (invalid == "account") binding = binding with { StorageAccountResourceId = binding.StorageAccountResourceId + "other" };
        if (invalid == "source") execution = execution with { SourceCommit = new string('c', 40) };
        if (invalid == "caller") execution = execution with
        {
            Preparation = execution.Preparation with
            {
                Identity = execution.Preparation.Identity with { PrincipalObjectId = Guid.NewGuid().ToString("D") }
            }
        };
        binding = binding with
        {
            ExecutionBindingHash = Canonical.Hash(execution),
            Identity = execution.Preparation.Identity with { ExpectedAudience = "https://storage.azure.com/" }
        };
        var original = Canonical.Hash(run.Store.Current!);
        Assert.Equal(code, Assert.Throws<DomainException>(() => run.Provisioner.PrepareBlobCheckpoint(execution, binding)).Code);
        Assert.Equal(original, Canonical.Hash(run.Store.Current!));
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public void CheckpointReservationsAreDurableOneUseClaimsAndAreNeverRefundedOnRestart()
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        var allocation = run.Provisioner.PrepareBlobCheckpoint(execution, binding);
        var id = Guid.NewGuid().ToString("D");
        var first = run.Provisioner.ReserveBlobCheckpoint(execution, binding, id, 24, allocation.IoDeadlineUtc);
        run.Restart();
        Assert.Equal(first, run.Provisioner.ReserveBlobCheckpoint(execution, binding, id, 24, allocation.IoDeadlineUtc));
        Assert.Equal("c1_deploy_checkpoint_drift", Assert.Throws<DomainException>(() =>
            run.Provisioner.ReserveBlobCheckpoint(execution, binding, id, 20, allocation.IoDeadlineUtc)).Code);
        Assert.Equal("c1_deploy_checkpoint_reservation", Assert.Throws<DomainException>(() =>
            run.Provisioner.ReserveBlobCheckpoint(execution, binding, Guid.NewGuid().ToString("D"), 20, allocation.IoDeadlineUtc)).Code);
        var second = run.Provisioner.ReserveBlobCheckpoint(execution, binding, Guid.NewGuid().ToString("D"), 16, allocation.IoDeadlineUtc);
        Assert.NotEqual(first.JournalReceiptSha256, second.JournalReceiptSha256);
        Assert.Equal(0, run.Store.Current!.Requests);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task OrdinaryControlCallsCannotBorrowPrepaidCheckpointIoOrCleanupReserve()
    {
        using var run = new Harness();
        run.Capacity();
        var (execution, binding) = CheckpointFixture(run);
        run.Provisioner.PrepareBlobCheckpoint(execution, binding);
        run.Store.Save(run.Store.Current! with { Requests = 40 });
        run.Restart();
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureWorkspace("agent", default))).Code);
        Assert.Empty(run.Api.Requests);
        Assert.Equal(0, run.Store.Current!.CleanupRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointDebitCannotResetOriginalTimeOrProceedAfterCleanupFencing(bool cleanup)
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        run.Store.Save(run.Store.Current! with
        {
            StartedAtUtc = run.Clock.GetUtcNow().AddMinutes(-11),
            CleanupStartedAtUtc = cleanup ? run.Clock.GetUtcNow() : null
        });
        run.Restart();
        Assert.Equal(cleanup ? "c1_deploy_cleanup_started" : "c1_deploy_budget",
            Assert.Throws<DomainException>(() => run.Provisioner.PrepareBlobCheckpoint(execution, binding)).Code);
        Assert.DoesNotContain("checkpoint-allocation-direct", run.Store.Current!.Objects.Keys);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public void LostJournalCommitResponseFencesTheWriterAndResumeRecoversTheSameDebit()
    {
        using var run = new Harness();
        var (execution, binding) = CheckpointFixture(run);
        run.Store.FailAfterSave = true;
        Assert.Throws<IOException>(() => run.Provisioner.PrepareBlobCheckpoint(execution, binding));
        var original = Canonical.Hash(run.Store.Current!);
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", Assert.Throws<DomainException>(() =>
            run.Provisioner.PrepareBlobCheckpoint(execution, binding)).Code);
        run.Restart();
        var allocation = run.Provisioner.PrepareBlobCheckpoint(execution, binding);
        Assert.Equal(original, Canonical.Hash(run.Store.Current!));
        Assert.Equal(run.Clock.GetUtcNow(), allocation.Seed.Checkpoint.StartedAtUtc);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task BlobSeedUsesConditionalCreateAndKeepsItsOriginalReceiptAfterTheRuntimeStarts()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        var original = await seed.Materialize();
        Assert.Equal(["GET", "PUT", "GET"], seed.Handler.Methods.ToArray());
        Assert.Equal(3, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.Empty(run.Store.Current.PendingCreates);
        Assert.All(seed.Credential.Scopes, scope => Assert.Equal(FabricC1BlobCheckpointStore.Scope, scope));
        var stored = JsonSerializer.Deserialize<FabricC1BlobCheckpointDocument>(seed.Handler.Content!, Canonical.Json)!;
        Assert.Equal(Canonical.Hash(seed.Binding), stored.StorageBindingHash);
        Assert.Equal(run.Clock.GetUtcNow(), stored.Checkpoint.StartedAtUtc);
        seed.Handler.Content = JsonSerializer.SerializeToUtf8Bytes(stored with
        { Checkpoint = stored.Checkpoint with { Requests = 1 } }, Canonical.Json);
        run.Restart();
        Assert.Equal(original, await seed.Materialize());
        Assert.Equal(3, seed.Handler.Methods.Count);
        Assert.Equal(1, JsonSerializer.Deserialize<FabricC1BlobCheckpointDocument>(seed.Handler.Content, Canonical.Json)!.Checkpoint.Requests);
    }

    [Fact]
    public async Task RuntimeProfileAndInvocationExportRequireActualSeedAndOriginalOneUseDebitReceipts()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        run.Provisioner.PrepareBlobCheckpoint(seed.Execution, seed.Binding);
        Assert.Equal("c1_deploy_checkpoint_profile", Assert.Throws<DomainException>(() =>
            run.Provisioner.ExportBlobCheckpointProfile(seed.Execution, seed.Binding)).Code);
        await seed.Materialize();
        var profile = run.Provisioner.ExportBlobCheckpointProfile(seed.Execution, seed.Binding);
        var id = Guid.NewGuid().ToString("D");
        var reservation = run.Provisioner.ReserveBlobCheckpoint(seed.Execution, seed.Binding, id, 8, run.Clock.GetUtcNow().AddSeconds(80));
        var command = new FabricC1Invocation(Canonical.Hash(seed.Execution), "status", InvocationId: id);
        var allowance = run.Provisioner.ExportBlobCheckpointAllowance(seed.Execution, seed.Binding, command, reservation);
        Assert.Equal(command, FabricC1CheckpointProfiles.ValidateAllowance(seed.Execution, profile, allowance,
            "direct", "status", null, run.Clock.GetUtcNow()));
        run.Restart();
        Assert.Equal(Canonical.Hash(allowance), Canonical.Hash(run.Provisioner.ExportBlobCheckpointAllowance(
            seed.Execution, seed.Binding, command, reservation)));
        Assert.Equal("c1_deploy_checkpoint_drift", Assert.Throws<DomainException>(() =>
            run.Provisioner.ExportBlobCheckpointAllowance(seed.Execution, seed.Binding,
                command with { Operation = "discover" }, reservation)).Code);
        Assert.Equal("c1_deploy_checkpoint_reservation", Assert.Throws<DomainException>(() =>
            run.Provisioner.ExportBlobCheckpointAllowance(seed.Execution, seed.Binding, command,
                reservation with { JournalReceiptSha256 = new string('f', 64) })).Code);
        Assert.Equal(3, seed.Handler.Methods.Count);
    }

    [Fact]
    public async Task HostedAllowanceBindsSeparateOriginalControlAndServerDebitsToOneInvocation()
    {
        using var run = new Harness(maxRequests: 300);
        var (execution, _, hosted, control) = CheckpointRuntimes(run);
        foreach (var binding in new[] { hosted, control })
        {
            using var handler = new ConditionalBlobHandler();
            using var client = new HttpClient(handler);
            var credential = new BoundCredential(Token(run.Input with { BootstrapIdentity = binding.Identity },
                binding.Identity.ExpectedAudience!, run.Clock.GetUtcNow()));
            await run.Provisioner.MaterializeBlobCheckpointWithTransport(execution, binding, client, credential, default);
        }
        var id = Guid.NewGuid().ToString("D");
        var expiry = run.Clock.GetUtcNow().AddSeconds(80);
        var local = run.Provisioner.ReserveBlobCheckpoint(execution, control, id, 8, expiry);
        var remote = run.Provisioner.ReserveBlobCheckpoint(execution, hosted, id, 8, expiry);
        var command = new FabricC1Invocation(Canonical.Hash(execution), "status", InvocationId: id)
            { HostedCheckpointReservation = remote };
        var allowance = run.Provisioner.ExportBlobCheckpointAllowance(execution, control, command, local, hosted);
        Assert.Equal(local, allowance.Reservation);
        Assert.Equal(remote, allowance.Command.HostedCheckpointReservation);
        Assert.NotEqual(local.JournalReceiptSha256, remote.JournalReceiptSha256);
        var profile = run.Provisioner.ExportBlobCheckpointProfile(execution, hosted);
        Assert.Equal(remote, FabricC1CheckpointProfiles.HostedReservation(execution, profile, allowance.Command, run.Clock.GetUtcNow()));
        Assert.Equal("c1_deploy_checkpoint_reservation", Assert.Throws<DomainException>(() =>
            run.Provisioner.ExportBlobCheckpointAllowance(execution, control,
                command with { HostedCheckpointReservation = local }, local, hosted)).Code);
        Assert.Equal(6, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
    }

    [Fact]
    public async Task CheckpointStorageAccessSeparatesExactPathsWithoutManagementDeleteOrHumanGrants()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        Assert.Empty(run.Api.Requests);
        var permissions = plan[0].Request["properties"]!["permissions"]![0]!;
        Assert.Empty(permissions["actions"]!.AsArray());
        Assert.Equal(new[]
        {
            "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read",
            "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write"
        }, permissions["dataActions"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(FabricC1Deployment.GroupId(run.Input), plan[0].Request["properties"]!["assignableScopes"]![0]!.GetValue<string>());
        var runtimeCondition = plan[1].Request["properties"]!["condition"]!.GetValue<string>();
        var controlCondition = plan[2].Request["properties"]!["condition"]!.GetValue<string>();
        Assert.Contains("/direct.json", runtimeCondition);
        Assert.Contains("/hosted.json", runtimeCondition);
        Assert.DoesNotContain("/hosted-control.json", runtimeCondition);
        Assert.Contains("/hosted-control.json", controlCondition);
        Assert.DoesNotContain("/direct.json", controlCondition);
        Assert.DoesNotContain("/hosted.json", controlCondition);
        Assert.All(plan.Skip(1), resource =>
        {
            var properties = resource.Request["properties"]!;
            Assert.Equal("ServicePrincipal", properties["principalType"]!.GetValue<string>());
            Assert.Contains("AND NOT SubOperationMatches{'Blob.List'}", properties["condition"]!.GetValue<string>());
            Assert.DoesNotContain("*", properties["condition"]!.GetValue<string>());
            Assert.Contains("/containers/c1-checkpoints/providers/Microsoft.Authorization/roleAssignments/", resource.Id);
            Assert.NotEqual(run.Input.BootstrapIdentity!.PrincipalObjectId, properties["principalId"]!.GetValue<string>());
        });
        foreach (var resource in plan)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        Assert.Equal(3, (await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default)).Length);
        Assert.Equal(9, run.Store.Current!.Requests);
        Assert.All(plan, resource => Assert.Equal(Canonical.Hash(resource),
            Canonical.Hash(run.Store.Current.Objects[resource.Key + "-intent"].Response)));
        Assert.DoesNotContain(run.Store.Current.Objects.Keys, key => key.StartsWith("checkpoint-allocation-", StringComparison.Ordinal));
        run.Restart();
        foreach (var resource in plan) run.Api.Reply(200, CheckpointAccessResponse(resource));
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        Assert.Equal(3, run.Api.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.Equal(12, run.Store.Current.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
    }

    [Theory]
    [InlineData("management")]
    [InlineData("data-delete")]
    [InlineData("scope")]
    [InlineData("condition")]
    [InlineData("human")]
    public async Task CheckpointAccessReadbackRejectsBroaderRolesScopesConditionsAndHumanAssignments(string changed)
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        var index = changed is "management" or "data-delete" ? 0 : 1;
        if (index == 1)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(plan[0]));
            run.Api.Reply(200, CheckpointAccessResponse(plan[0]));
        }
        var response = CheckpointAccessResponse(plan[index]);
        var properties = response["properties"]!;
        if (changed == "management") properties["permissions"]![0]!["actions"]!.AsArray().Add("Microsoft.Storage/storageAccounts/listKeys/action");
        if (changed == "data-delete") properties["permissions"]![0]!["dataActions"]!.AsArray().Add("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete");
        if (changed == "scope") properties["scope"] = direct.StorageAccountResourceId;
        if (changed == "condition") properties["condition"] = null;
        if (changed == "human") properties["principalType"] = "User";
        run.Api.Reply(404, new());
        run.Api.Reply(201, CheckpointAccessResponse(plan[index]));
        run.Api.Reply(200, response);
        Assert.Equal("c1_checkpoint_access_readback", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey(plan[index].Key));
        Assert.True(run.Store.Current.PendingCreates.ContainsKey(plan[index].Key));
    }

    [Fact]
    public async Task ExistingUnjournaledCheckpointRoleIsNotAdoptedOrUpdated()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var role = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control)[0];
        run.Api.Reply(200, CheckpointAccessResponse(role));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        Assert.Single(run.Api.Requests);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UnknownCheckpointRoleWriteOnlyReconcilesItsOriginalIntentWithoutPutReplay()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic lost role create"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        run.Restart();
        run.Api.Reply(404, new());
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        run.Restart();
        run.Api.Reply(200, CheckpointAccessResponse(plan[0]));
        foreach (var resource in plan.Skip(1))
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == plan[0].Id);
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task ExplicitCheckpointRoleDenialRetainsTheOriginalRequestForALaterAuthorizedInvocation()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        run.Api.Reply(404, new());
        run.Api.Reply(403, new());
        Assert.Equal("c1_deploy_http_403", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Equal(403, run.Store.Current.DeniedRequests[plan[0].Key].Status);
        run.Restart();
        foreach (var resource in plan)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        var attempts = run.Api.Requests.Where(request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == plan[0].Id).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(Canonical.Hash(attempts[0].Body), Canonical.Hash(attempts[1].Body));
        Assert.Equal(11, run.Store.Current.Requests);
    }

    [Fact]
    public void CheckpointAccessRejectsAliasedControlIdentityBeforeAnyRoleRequest()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        execution = execution with { Hosted = execution.Hosted! with
            { Caller = execution.Preparation.Identity with { ExpectedAudience = "https://ai.azure.com" } } };
        var hash = Canonical.Hash(execution);
        direct = direct with { ExecutionBindingHash = hash };
        hosted = hosted with { ExecutionBindingHash = hash };
        control = control with { ExecutionBindingHash = hash, Identity = direct.Identity };
        run.Seed("hosted-control-app", "Application", (JsonObject)run.Store.Current!.Objects["runtime-app"].Response.DeepClone());
        run.Seed("hosted-control-sp", "ServicePrincipal", (JsonObject)run.Store.Current.Objects["runtime-sp"].Response.DeepClone());
        Assert.Equal("c1_checkpoint_access_binding", Assert.Throws<DomainException>(() =>
            run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control)).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task UnknownCheckpointAccessReadbackSaveFencesFurtherWorkUntilOriginalJournalRecovery()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        run.Api.Reply(404, new());
        run.Api.Reply(201, CheckpointAccessResponse(plan[0]));
        run.Api.ReplyComputed(200, () =>
        {
            run.Store.FailAfterSave = true;
            return CheckpointAccessResponse(plan[0]);
        });
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        Assert.True(run.Store.Current!.Objects.ContainsKey(plan[0].Key));
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        Assert.Equal(3, run.Api.Requests.Count);
        run.Restart();
        run.Api.Reply(200, CheckpointAccessResponse(plan[0]));
        foreach (var resource in plan.Skip(1))
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == plan[0].Id);
    }

    [Fact]
    public async Task DeniedCheckpointGrantCannotMoveToANewExecutionPathOnResume()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        run.Api.Reply(404, new());
        run.Api.Reply(201, CheckpointAccessResponse(plan[0]));
        run.Api.Reply(200, CheckpointAccessResponse(plan[0]));
        run.Api.Reply(404, new());
        run.Api.Reply(403, new());
        await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        run.Restart();
        execution = execution with { RunId = Guid.NewGuid().ToString("D") };
        var changed = Canonical.Hash(execution);
        Assert.Equal("c1_deploy_drift", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct with { ExecutionBindingHash = changed },
                hosted with { ExecutionBindingHash = changed }, control with { ExecutionBindingHash = changed }, default))).Code);
        Assert.Equal(5, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == plan[1].Id);
    }

    [Fact]
    public async Task CheckpointAccessCleanupUsesOriginalReserveAfterExpiryAndDeletesAssignmentsBeforeRole()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        var started = run.Store.Current!.StartedAtUtc;
        run.Clock.Advance(TimeSpan.FromHours(1));
        QueueCheckpointAccessRemoval(run, resources);
        var result = await run.Provisioner.CleanupBlobCheckpointAccess(default);
        Assert.Equal("CheckpointAccessInventoryAbsent", result.Status);
        Assert.Equal(resources.Select(resource => resource.Key), result.AbsentKeys);
        Assert.Empty(result.UnresolvedKeys);
        Assert.Empty(result.NotManagedKeys);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Equal(resources.Select(resource => resource.Id),
            run.Api.Requests.Where(request => request.Method == HttpMethod.Delete).Select(request => request.Uri.AbsolutePath));
        Assert.Equal(18, run.Store.Current.Requests);
        Assert.Equal(9, run.Store.Current.CleanupRequests);
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        var cleanupStart = run.Store.Current.CleanupStartedAtUtc;
        Assert.All(result.AbsentKeys, key =>
        {
            var receipt = run.Store.Current.CleanupReceipts[key].Response;
            Assert.False(receipt["effectiveAccessRevoked"]!.GetValue<bool>());
            Assert.False(receipt["checkpointDataDeleted"]!.GetValue<bool>());
            Assert.False(receipt["workersStopped"]!.GetValue<bool>());
        });
        Assert.Equal("c1_deploy_cleanup_started", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.Preflight(default))).Code);
        Assert.Equal("c1_deploy_cleanup_started", Assert.Throws<DomainException>(run.Provisioner.RequireFullBindings).Code);
        run.Restart();
        foreach (var _ in resources) run.Api.Reply(404, new());
        Assert.Empty((await run.Provisioner.CleanupBlobCheckpointAccess(default)).UnresolvedKeys);
        Assert.Equal(3, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
        Assert.Equal(12, run.Store.Current.CleanupRequests);
        Assert.Equal(cleanupStart, run.Store.Current.CleanupStartedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupNeverTreatsUnknownAccessCreateAsAbsenceOrReplaysItsPut(bool grant)
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        if (grant)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(plan[0]));
            run.Api.Reply(200, CheckpointAccessResponse(plan[0]));
        }
        var target = plan[grant ? 1 : 0];
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic lost access create"));
        await Assert.ThrowsAsync<IOException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        run.Restart();
        run.Api.Reply(404, new());
        var unresolved = await run.Provisioner.CleanupBlobCheckpointAccess(default);
        Assert.Contains(target.Key, unresolved.UnresolvedKeys);
        Assert.Empty(unresolved.AbsentKeys);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        Assert.True(run.Store.Current!.PendingCreates.ContainsKey(target.Key));
        run.Restart();
        QueueCheckpointAccessRemoval(run, grant ? [target, plan[0]] : [target]);
        var completed = await run.Provisioner.CleanupBlobCheckpointAccess(default);
        Assert.Empty(completed.UnresolvedKeys);
        Assert.Equal(grant ? 2 : 1, completed.AbsentKeys.Length);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put && request.Uri.AbsolutePath == target.Id);
    }

    [Fact]
    public async Task AccessPlanWithoutACreateAttemptNeverAuthorizesCleanupOfAnExistingRole()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var role = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control)[0];
        run.Api.Reply(200, CheckpointAccessResponse(role));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        var result = await run.Provisioner.CleanupBlobCheckpointAccess(default);
        Assert.Equal("CheckpointAccessNotManaged", result.Status);
        Assert.Empty(result.AbsentKeys);
        Assert.Equal(3, result.NotManagedKeys.Length);
        Assert.Single(run.Api.Requests);
    }

    [Fact]
    public async Task UnknownCheckpointAccessDeleteOnlyReconcilesAndKeepsTheRoleUntilBothGrantsAreAbsent()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        run.Api.Fail(new IOException("synthetic lost access delete"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupBlobCheckpointAccess(default));
        run.Restart();
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        Assert.Equal(3, (await run.Provisioner.CleanupBlobCheckpointAccess(default)).UnresolvedKeys.Length);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        run.Restart();
        run.Api.Reply(404, new());
        QueueCheckpointAccessRemoval(run, resources.Skip(1));
        Assert.Empty((await run.Provisioner.CleanupBlobCheckpointAccess(default)).UnresolvedKeys);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == resources[0].Id);
        Assert.Equal(3, run.Api.Requests.Count(request => request.Method == HttpMethod.Put));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("principal")]
    [InlineData("missing-intent")]
    public async Task CleanupRejectsChangedOrMissingOriginalAccessTargetsBeforeIo(string changed)
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        var key = resources[0].Key + "-intent";
        var original = run.Store.Current!.Objects[key];
        var body = (JsonObject)original.Response.DeepClone();
        if (changed == "missing-intent") run.Store.Current.Objects.Remove(key);
        else
        {
            var id = original.Id;
            if (changed == "scope")
            {
                id = id.Replace(run.Input.SubscriptionId!, Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
                body["id"] = id;
            }
            else body["request"]!["properties"]!["principalId"] = Guid.NewGuid().ToString("D");
            run.Store.Current.Objects[key] = original with
                { Id = id, RequestHash = Canonical.Hash(body["request"]), Response = body };
        }
        run.Restart();
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.CleanupBlobCheckpointAccess(default));
        Assert.Equal(9, run.Api.Requests.Count);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("condition")]
    [InlineData("description")]
    [InlineData("createdOn")]
    public async Task CleanupRefusesCurrentGrantDriftOrReplacementRatherThanDeletingIt(string changed)
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        var current = CheckpointAccessResponse(resources[0]);
        current["properties"]![changed] = changed == "createdOn" ? "2026-11-18T12:01:00Z" : "not-the-original-value";
        run.Api.Reply(200, current);
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.CleanupBlobCheckpointAccess(default));
        Assert.Equal(10, run.Api.Requests.Count);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RejectedAccessDeleteCanResumeTheOriginalRequestWithoutRestartingCleanup()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        run.Api.Reply(403, new());
        Assert.Equal("c1_deploy_http_403", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        var started = run.Store.Current!.CleanupStartedAtUtc;
        Assert.Empty(run.Store.Current.PendingDeletes);
        Assert.Equal(403, run.Store.Current.DeniedRequests["cleanup-" + resources[0].Key].Status);
        run.Restart();
        QueueCheckpointAccessRemoval(run, resources);
        Assert.Empty((await run.Provisioner.CleanupBlobCheckpointAccess(default)).UnresolvedKeys);
        Assert.Equal(started, run.Store.Current.CleanupStartedAtUtc);
        Assert.Equal(11, run.Store.Current.CleanupRequests);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == resources[0].Id));
    }

    [Fact]
    public async Task ReappearingAccessAfterConfirmedAbsenceIsNotDeletedAgain()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        QueueCheckpointAccessRemoval(run, resources);
        await run.Provisioner.CleanupBlobCheckpointAccess(default);
        run.Restart();
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        Assert.Equal("c1_deploy_cleanup_restored", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Equal(3, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task UnknownAccessAbsenceSaveFencesFurtherCallsUntilOriginalJournalRecovery()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        run.Api.ReplyComputed(404, () => { run.Store.FailAfterSave = true; return new(); });
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupBlobCheckpointAccess(default));
        Assert.True(run.Store.Current!.CleanupReceipts.ContainsKey(resources[0].Key));
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Equal(12, run.Api.Requests.Count);
        run.Restart();
        run.Api.Reply(404, new());
        QueueCheckpointAccessRemoval(run, resources.Skip(1));
        Assert.Empty((await run.Provisioner.CleanupBlobCheckpointAccess(default)).UnresolvedKeys);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == resources[0].Id);
    }

    [Fact]
    public async Task UnknownControlDebitSaveAlsoFencesNormalAndCleanupIoWithoutRefundingTheDebit()
    {
        using var run = new Harness();
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var resources = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        run.Api.Fail(new IOException("synthetic initial GET loss"));
        await Assert.ThrowsAsync<IOException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        run.Store.FailAfterSave = true;
        await Assert.ThrowsAsync<IOException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default));
        Assert.Equal(2, run.Store.Current!.Requests);
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default))).Code);
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Single(run.Api.Requests);
        run.Restart();
        foreach (var resource in resources)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        Assert.Equal(11, run.Store.Current.Requests);
        Assert.Equal(10, run.Api.Requests.Count);
    }

    [Fact]
    public async Task CheckpointAccessCleanupCannotBorrowNormalBudgetAfterItsOwnReserveIsSpent()
    {
        using var run = new Harness(maxRequests: 21);
        var resources = await CreateCheckpointAccessForCleanup(run);
        QueueCheckpointAccessRemoval(run, resources);
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Equal(7, run.Store.Current!.CleanupRequests);
        Assert.Equal(16, run.Store.Current.Requests);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Equal(16, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData(601)]
    [InlineData(-1)]
    public async Task OriginalAccessCleanupDeadlineAndClockFenceSurviveRestart(int elapsedSeconds)
    {
        using var run = new Harness();
        await CreateCheckpointAccessForCleanup(run);
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic next access GET loss"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupBlobCheckpointAccess(default));
        var started = run.Store.Current!.CleanupStartedAtUtc;
        run.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Equal(started, run.Store.Current.CleanupStartedAtUtc);
        Assert.Equal(11, run.Api.Requests.Count);
    }

    [Fact]
    public async Task AccessAbsenceReturnedAfterCleanupDeadlineCannotBeRecordedAsSuccess()
    {
        using var run = new Harness();
        var resources = await CreateCheckpointAccessForCleanup(run);
        run.Api.Reply(200, CheckpointAccessResponse(resources[0]));
        run.Api.Reply(204, new());
        run.Api.ReplyComputed(404, () => { run.Clock.Advance(TimeSpan.FromSeconds(601)); return new(); });
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupBlobCheckpointAccess(default))).Code);
        Assert.Empty(run.Store.Current!.CleanupReceipts);
        Assert.True(run.Store.Current.PendingDeletes.ContainsKey(resources[0].Key));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task PrivateStageBindsOriginalToolsVmAndTwoRequestDebitWithoutReallocatingOnResume()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        var before = run.Store.Current!.Requests;
        var calls = run.Api.Requests.Count;
        artifact.QueueStageIdentity();
        var first = await artifact.PrepareStage();
        Assert.Equal(before + 4, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.Equal(ArtifactHarness.ReceiverTool, first.ReceiverSource);
        Assert.Equal(ArtifactHarness.VerifierTool, first.VerifierSource);
        Assert.Equal(artifact.ManagedIdentityClientId, first.Profile["managedIdentityClientId"]!.GetValue<string>());
        Assert.Equal(artifact.Probe["identity"]!["principalId"]!.GetValue<string>(),
            first.Profile["managedIdentityObjectId"]!.GetValue<string>());
        Assert.Equal(2, first.Allowance["requests"]!.GetValue<int>());
        Assert.Equal(Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-plan"]),
            first.Allowance["journalReceiptSha256"]!.GetValue<string>());
        Assert.Equal(2, run.Api.Requests.Count - calls);
        Assert.All(run.Api.Requests.Skip(calls), request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("/servicePrincipals/" + artifact.Probe["identity"]!["principalId"]!.GetValue<string>() + "?$select=",
            run.Api.Requests[^1].Uri.AbsoluteUri, StringComparison.Ordinal);
        var original = Canonical.Hash(first);
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        artifact.QueueTarget();
        await artifact.Publish();
        run.Restart();
        before = run.Store.Current.Requests;
        calls = run.Api.Requests.Count;
        Assert.Equal(original, Canonical.Hash(await artifact.PrepareStage()));
        Assert.Equal(before, run.Store.Current.Requests);
        Assert.Equal(calls, run.Api.Requests.Count);
        first.Profile["privateAddresses"]![0] = "8.8.8.8";
        Assert.Equal(original, Canonical.Hash(await artifact.PrepareStage()));
    }

    [Fact]
    public async Task ActualPythonValidatorAcceptsTheDotnetStageWireShapeAndNewlineHash()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        artifact.QueueStageIdentity();
        var bundle = await artifact.PrepareStage();
        await AssertPythonContract(
            "import json,sys,c1_private_stage as s; d=json.load(sys.stdin); s.validate_allowance(d['profile'],d['allowance'],s.utc(d['now'])); assert s.stage_directory(d['profile']).as_posix()==d['workerDirectory']",
            new
            {
                bundle.Profile, bundle.Allowance, bundle.WorkerDirectory,
                Now = run.Clock.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    [Fact]
    public async Task ManagedDeliveryRequiresExecutionResultRatherThanProvisioningSuccessAndDoesNotRerunOnResume()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.ReplyComputed(201, () => artifact.Command("Pending", defaultStart: true));
        run.Api.ReplyComputed(200, () => artifact.Command("Running"));
        var first = await artifact.Deliver();
        Assert.False(first.ArtifactVerified);
        Assert.Equal("Pending", first.Status);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-receipt"));
        var stage = Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-plan"]);
        var intent = Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-command-intent"]);
        var completed = artifact.Command("Succeeded");
        run.Restart();
        artifact.QueueGuestAgent();
        run.Api.Reply(200, completed);
        var result = await artifact.Deliver();
        Assert.True(result.ArtifactVerified);
        Assert.False(result.Receipt!["result"]!["payloadExecuted"]!.GetValue<bool>());
        Assert.Equal(stage, result.AllocationReceiptSha256);
        Assert.Equal(stage, Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-plan"]));
        Assert.Equal(intent, Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-command-intent"]));
        artifact.QueueGuestAgent();
        run.Api.Reply(200, completed);
        Assert.Equal(Canonical.Hash(result), Canonical.Hash(await artifact.Deliver()));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.Contains("/runCommands/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostManagedCreateAllowsOnlyOriginalReadbackAndNeverReplaysThePut()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic lost command create response"));
        await Assert.ThrowsAsync<IOException>(() => artifact.Deliver());
        run.Restart();
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        Assert.Equal("Pending", (await artifact.Deliver()).Status);
        Assert.True(run.Store.Current!.PendingCreates.ContainsKey("private-artifact-stage-command"));
        artifact.QueueGuestAgent();
        run.Api.ReplyComputed(200, () => artifact.Command("Succeeded"));
        Assert.True((await artifact.Deliver()).ArtifactVerified);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.Contains("/runCommands/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchingButUnjournaledRunCommandIsNotAdoptedOrDeleted()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.ReplyComputed(200, () => artifact.Command("Succeeded"));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        var calls = run.Api.Requests.Count;
        Assert.Equal("ArtifactCommandNotManaged", (await run.Provisioner.CleanupPrivateArtifactDelivery(default)).Status);
        Assert.Equal(calls, run.Api.Requests.Count);
    }

    [Fact]
    public async Task UncertainCommandResultSaveFencesIoUntilOriginalJournalRecovery()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () =>
        {
            run.Store.FailAfterSave = true;
            return artifact.Command("Succeeded");
        });
        await Assert.ThrowsAsync<IOException>(() => artifact.Deliver());
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-receipt"));
        var calls = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.Equal(calls, run.Api.Requests.Count);
        run.Store.FailAfterSave = false;
        run.Restart();
        artifact.QueueGuestAgent();
        run.Api.ReplyComputed(200, () => artifact.Command("Succeeded"));
        Assert.True((await artifact.Deliver()).ArtifactVerified);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put &&
            request.Uri.AbsolutePath.Contains("/runCommands/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("TimedOut")]
    [InlineData("Canceled")]
    public async Task FailedCommandRetainsCleanupInventoryButNeverAnArtifactReceipt(string execution)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () => artifact.Command(execution));
        Assert.Equal("c1_artifact_command_failed", (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.True(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-command"));
        Assert.False(run.Store.Current.Objects.ContainsKey("private-artifact-stage-receipt"));
    }

    [Theory]
    [InlineData("old-agent")]
    [InlineData("not-ready")]
    [InlineData("not-running")]
    [InlineData("extensions-disabled")]
    [InlineData("not-linux")]
    public async Task UnsupportedGuestAgentCannotStartDelivery(string change)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        var vm = (JsonObject)artifact.Probe.DeepClone();
        var instance = ArtifactHarness.GuestAgent();
        switch (change)
        {
            case "old-agent": instance["vmAgent"]!["vmAgentVersion"] = "2.4.0.1"; break;
            case "not-ready": instance["vmAgent"]!["statuses"]![0]!["code"] = "ProvisioningState/failed"; break;
            case "not-running": instance["statuses"]![0]!["code"] = "PowerState/deallocated"; break;
            case "extensions-disabled": vm["properties"]!["osProfile"]!["allowExtensionOperations"] = false; break;
            case "not-linux": vm["properties"]!["storageProfile"]!["osDisk"]!["osType"] = "Windows"; break;
        }
        run.Api.Reply(200, vm);
        run.Api.Reply(200, instance);
        Assert.Equal("c1_artifact_guest_agent", (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.AbsolutePath.Contains("/runCommands/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("nonzero-exit")]
    [InlineData("error-output")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    [InlineData("profile")]
    [InlineData("allowance")]
    [InlineData("archive")]
    [InlineData("invocation")]
    [InlineData("public-address")]
    [InlineData("unobserved-connection")]
    [InlineData("executed-payload")]
    public async Task ManagedDeliveryRejectsIncompleteOrUnboundResults(string change)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () =>
        {
            var command = artifact.Command("Succeeded");
            var view = command["properties"]!["instanceView"]!;
            var output = JsonNode.Parse(view["output"]!.GetValue<string>())!.AsObject();
            switch (change)
            {
                case "nonzero-exit": view["exitCode"] = 1; break;
                case "error-output": view["error"] = "synthetic receiver error"; break;
                case "profile": output["profileSha256"] = new string('0', 64); break;
                case "allowance": output["allowanceSha256"] = new string('0', 64); break;
                case "archive": output["archiveSha256"] = new string('0', 64); break;
                case "invocation": output["invocationId"] = Guid.NewGuid().ToString("D"); break;
                case "public-address": output["privateTransport"]!["resolvedAddresses"]![0] = "8.8.8.8"; break;
                case "unobserved-connection": output["privateTransport"]!["connectedAddress"] = "10.0.0.99"; break;
                case "executed-payload": output["payloadExecuted"] = true; break;
            }
            var json = Canonical.Serialize(output);
            view["output"] = change switch
            {
                "truncated" => json[..^1],
                "oversized" => json + new string(' ', 4096),
                "duplicate" => "{\"status\":\"VerifiedNotExecuted\"," + json[1..],
                _ => json
            };
            return command;
        });
        Assert.Equal("c1_artifact_stage_result", (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-receipt"));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("parameters")]
    [InlineData("external-output")]
    [InlineData("resource-id")]
    [InlineData("restart")]
    public async Task ChangedCommandCannotReplaceAnOriginalSuccessfulReceipt(string change)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await CompleteArtifactDelivery(run, artifact);
        var original = Canonical.Hash(run.Store.Current!.Objects["private-artifact-stage-receipt"]);
        var observed = Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-command"]);
        var command = artifact.Command("Succeeded");
        switch (change)
        {
            case "script": command["properties"]!["source"]!["script"] = "exit 0"; break;
            case "parameters": command["properties"]!["parameters"] = new JsonArray(new JsonObject { ["name"] = "foreign", ["value"] = "value" }); break;
            case "external-output": command["properties"]!["outputBlobUri"] = "https://example.test/foreign-output"; break;
            case "resource-id": command["id"] = command["id"]!.GetValue<string>() + "-replacement"; break;
            case "restart": command["properties"]!["instanceView"]!["startTime"] = "2026-11-18T12:00:01Z"; break;
        }
        artifact.QueueGuestAgent();
        run.Api.Reply(200, command);
        Assert.Equal(change == "restart" ? "c1_deploy_drift" : "c1_artifact_command_binding",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.Equal(original, Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-receipt"]));
        Assert.Equal(observed, Canonical.Hash(run.Store.Current.Objects["private-artifact-stage-command"]));
    }

    [Fact]
    public async Task ReceiverSubdeadlineIsRecheckedAfterControlIntentPersistenceBeforeProviderIo()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        var requests = run.Store.Current!.Requests;
        var calls = run.Api.Requests.Count;
        run.Store.AfterSave = state =>
        {
            if (state.Requests == requests + 1) run.Clock.Advance(TimeSpan.FromSeconds(60));
        };
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() => artifact.Deliver())).Code);
        Assert.Equal(calls, run.Api.Requests.Count);
        Assert.Equal(requests + 1, run.Store.Current.Requests);
        Assert.False(run.Store.Current.PendingCreates.ContainsKey("private-artifact-stage-command"));
    }

    [Fact]
    public async Task ExpiredReceiverCanCleanUpOnlyItsExactCommandWithoutClaimingWorkerStop()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await CompleteArtifactDelivery(run, artifact);
        run.Clock.Advance(TimeSpan.FromSeconds(61));
        var requests = run.Store.Current!.Requests;
        run.Api.Reply(200, artifact.Probe);
        run.Api.Reply(200, artifact.Command("Succeeded"));
        run.Api.Reply(202, new(), operationUrl: "https://example.test/untrusted-operation");
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupPrivateArtifactDelivery(default);
        Assert.Equal("ArtifactCommandInventoryAbsent", result.Status);
        Assert.False(result.WorkersStopped);
        Assert.False(result.ArtifactDataDeleted);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Equal(requests + 4, run.Store.Current.Requests);
        Assert.Equal(4, run.Store.Current.CleanupRequests);
        var deletion = Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal("https://management.azure.com" + result.CommandResourceId + "?api-version=2024-07-01", deletion.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task LostCommandDeletionIsReconciledWithoutRepeatedDelete()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await CompleteArtifactDelivery(run, artifact);
        run.Api.Reply(200, artifact.Probe);
        run.Api.Reply(200, artifact.Command("Succeeded"));
        run.Api.Fail(new IOException("synthetic unknown command deletion"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupPrivateArtifactDelivery(default));
        var started = run.Store.Current!.CleanupStartedAtUtc;
        run.Restart();
        run.Api.Reply(200, artifact.Probe);
        run.Api.Reply(200, artifact.Command("Succeeded"));
        Assert.Equal("ArtifactCommandCleanupPendingReconciliation", (await run.Provisioner.CleanupPrivateArtifactDelivery(default)).Status);
        run.Api.Reply(200, artifact.Probe);
        run.Api.Reply(404, new());
        Assert.Equal("ArtifactCommandInventoryAbsent", (await run.Provisioner.CleanupPrivateArtifactDelivery(default)).Status);
        Assert.Equal(started, run.Store.Current.CleanupStartedAtUtc);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UnknownCommandCreationPlusNotFoundIsNotAnAbsenceReceipt()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic unknown command create"));
        await Assert.ThrowsAsync<IOException>(() => artifact.Deliver());
        run.Api.Reply(200, artifact.Probe);
        run.Api.Reply(404, new());
        Assert.Equal("ArtifactCommandCleanupPendingReconciliation", (await run.Provisioner.CleanupPrivateArtifactDelivery(default)).Status);
        Assert.False(run.Store.Current!.CleanupReceipts.ContainsKey("private-artifact-stage-command"));
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CommandCleanupRefusesAReplacedVmBeforeReadingOrDeletingItsCommand()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await CompleteArtifactDelivery(run, artifact);
        var vm = (JsonObject)artifact.Probe.DeepClone();
        vm["properties"]!["vmId"] = Guid.NewGuid().ToString("D");
        var calls = run.Api.Requests.Count;
        run.Api.Reply(200, vm);
        Assert.Equal("c1_artifact_access_receiver",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.CleanupPrivateArtifactDelivery(default))).Code);
        Assert.Equal(calls + 1, run.Api.Requests.Count);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ActualGeneratedDeliveryPythonMaterializesOnlyBoundPrivateFilesAndRefusesNativeCiExecution()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () => artifact.Command("Running"));
        await artifact.Deliver();
        var intent = run.Store.Current!.Objects["private-artifact-stage-command-intent"];
        await AssertPythonContract("""
            import base64, contextlib, copy, io, json, os, pathlib, sys, tempfile
            data = json.load(sys.stdin)
            script = data["script"]
            prefix = "set -eu\numask 077\nexec python3 -I - <<'COHORS_C1_STAGE_DELIVERY'\n"
            suffix = "\nCOHORS_C1_STAGE_DELIVERY\n"
            assert script.startswith(prefix) and script.endswith(suffix)
            module = {"__name__": "delivery_contract"}
            exec(compile(script[len(prefix):-len(suffix)], "<actual-delivery>", "exec"), module)
            payload = json.loads(base64.b64decode(module["PAYLOAD_BASE64"], validate=True))
            def fails(callback):
                try:
                    callback()
                except (ValueError, OSError):
                    return
                raise AssertionError("unsafe materialization succeeded")
            with tempfile.TemporaryDirectory() as temporary:
                base = pathlib.Path(temporary) / "worker"
                module["BASE_DIRECTORY"] = base
                root = module["materialize"](payload)
                assert root == base / payload["profile"]["deploymentBindingHash"] / "artifact-stage"
                assert root.stat().st_mode & 0o777 == 0o700
                expected = {
                    "profile.json": module["encoded"](payload["profile"]),
                    "allowance.json": module["encoded"](payload["allowance"]),
                    "c1_private_stage.py": base64.b64decode(payload["receiver"]),
                    "c1_package.py": base64.b64decode(payload["verifier"]),
                }
                assert {path.name for path in root.iterdir()} == set(expected)
                for name, content in expected.items():
                    assert (root / name).read_bytes() == content
                    assert (root / name).stat().st_mode & 0o777 == 0o600
                assert module["materialize"](payload) == root
                profile = root / "profile.json"
                profile.chmod(0o644)
                fails(lambda: module["materialize"](payload))
                profile.chmod(0o600)
                profile.write_bytes(b"changed")
                fails(lambda: module["materialize"](payload))
                assert profile.read_bytes() == b"changed"
                profile.write_bytes(expected["profile.json"])
                link = root / "extra-link"
                os.link(profile, link)
                fails(lambda: module["materialize"](payload))
                link.unlink()
                profile.unlink()
                profile.symlink_to(root / "allowance.json")
                fails(lambda: module["materialize"](payload))
                profile.unlink()
                module["materialize"](payload)
                fsync = os.fsync
                def unavailable_fsync(_):
                    raise OSError("synthetic uncertain persistence")
                os.fsync = unavailable_fsync
                try:
                    fails(lambda: module["materialize"](payload))
                finally:
                    os.fsync = fsync
                bad = copy.deepcopy(payload)
                bad["receiver"] = base64.b64encode(b"unbound tool").decode()
                module["BASE_DIRECTORY"] = pathlib.Path(temporary) / "never-created"
                fails(lambda: module["materialize"](bad))
                assert not module["BASE_DIRECTORY"].exists()
                previous = os.environ.get("GITHUB_ACTIONS")
                os.environ["GITHUB_ACTIONS"] = "true"
                try:
                    with contextlib.redirect_stderr(io.StringIO()) as error:
                        assert module["main"]() == 2
                    assert json.loads(error.getvalue())["code"] == "c1_stage_delivery_incomplete"
                    assert not module["BASE_DIRECTORY"].exists()
                finally:
                    if previous is None:
                        del os.environ["GITHUB_ACTIONS"]
                    else:
                        os.environ["GITHUB_ACTIONS"] = previous
            """, new { Script = intent.Response["request"]!["properties"]!["source"]!["script"]!.GetValue<string>() });
    }

    private static async Task AssertPythonContract(string script, object data)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "python" : "python3",
            WorkingDirectory = Path.Combine(root, "scripts", "fabric"),
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            var input = JsonSerializer.Serialize(data, Canonical.Json);
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.Equal("", await error);
        Assert.Equal("", await output);
        Assert.Equal(0, process.ExitCode);
    }

    [Theory]
    [InlineData("vm-generation")]
    [InlineData("principal-id")]
    [InlineData("application")]
    [InlineData("disabled")]
    [InlineData("foreign-vm")]
    [InlineData("operator-client")]
    public async Task StagePreparationRefusesChangedVmOrForeignManagedIdentityWithoutIssuingAllowance(string change)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        var calls = run.Api.Requests.Count;
        var probe = (JsonObject)artifact.Probe.DeepClone();
        var principal = artifact.ManagedIdentity();
        if (change == "vm-generation") probe["properties"]!["vmId"] = Guid.NewGuid().ToString("D");
        if (change == "principal-id") principal["id"] = Guid.NewGuid().ToString("D");
        if (change == "application") principal["servicePrincipalType"] = "Application";
        if (change == "disabled") principal["accountEnabled"] = false;
        if (change == "foreign-vm") principal["alternativeNames"] = new JsonArray(principal["alternativeNames"]![0]!.GetValue<string>() + "-foreign");
        if (change == "operator-client") principal["appId"] = run.Input.BootstrapIdentity!.ClientId;
        run.Api.Reply(200, probe);
        if (change != "vm-generation") run.Api.Reply(200, principal);
        Assert.Equal(change == "vm-generation" ? "c1_artifact_access_receiver" : "c1_artifact_stage_identity",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.PrepareStage())).Code);
        Assert.Equal(calls + (change == "vm-generation" ? 1 : 2), run.Api.Requests.Count);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-plan"));
    }

    [Theory]
    [InlineData("missing-tool", "c1_artifact_stage_tools")]
    [InlineData("oversized-tool", "c1_artifact_stage_tools")]
    [InlineData("inner-digest", "c1_artifact_stage_tools")]
    [InlineData("outer-digest", "c1_artifact_digest")]
    public async Task StageToolsMustComeFromTheOriginalArchiveBeforeAnyIdentityLookup(string change, string code)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive(change == "missing-tool" ? null :
            change == "oversized-tool" ? new string('x', 256 * 1024 + 1) : ArtifactHarness.ReceiverTool);
        if (change == "inner-digest")
        {
            artifact.Provenance["innerArchiveSha256"] = new string('f', 64);
            artifact.WriteProvenance();
        }
        await PublishArtifactForStage(run, artifact);
        if (change == "outer-digest")
        {
            var bytes = artifact.Bytes.ToArray();
            bytes[^1] ^= 1;
            File.WriteAllBytes(Path.Combine(artifact.Directory, "transport.zip"), bytes);
        }
        var calls = run.Api.Requests.Count;
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => artifact.PrepareStage())).Code);
        Assert.Equal(calls, run.Api.Requests.Count);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-stage-plan"));
    }

    [Fact]
    public async Task StageAllowanceCannotBorrowCleanupReserveAfterItsTwoIdentityReads()
    {
        using var run = new Harness(maxRequests: 33, delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        artifact.QueueStageIdentity();
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() => artifact.PrepareStage())).Code);
        Assert.Equal(22, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.False(run.Store.Current.Objects.ContainsKey("private-artifact-stage-plan"));
    }

    [Fact]
    public async Task UncertainStageAllocationSaveRequiresOriginalJournalRecoveryWithoutAnotherDebit()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        run.Api.Reply(200, artifact.Probe);
        run.Api.ReplyComputed(200, () => { run.Store.FailAfterSave = true; return artifact.ManagedIdentity(); });
        await Assert.ThrowsAsync<IOException>(() => artifact.PrepareStage());
        var original = Canonical.Hash(run.Store.Current!.Objects["private-artifact-stage-plan"]);
        var calls = run.Api.Requests.Count;
        var requests = run.Store.Current.Requests;
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.PrepareStage())).Code);
        run.Restart();
        var recovered = await artifact.PrepareStage();
        Assert.Equal(original, recovered.Allowance["journalReceiptSha256"]!.GetValue<string>());
        Assert.Equal(requests, run.Store.Current.Requests);
        Assert.Equal(calls, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("duration", "c1_artifact_stage_binding")]
    [InlineData("expiry", "c1_artifact_stage_binding")]
    [InlineData("cleanup", "c1_deploy_cleanup_started")]
    [InlineData("generation", "c1_artifact_stage_binding")]
    [InlineData("profile", "c1_artifact_stage_binding")]
    public async Task RetainedStageCannotResetTimeIntentGenerationOrCleanupFence(string change, string code)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        artifact.QueueStageIdentity();
        await artifact.PrepareStage();
        if (change == "expiry") run.Clock.Advance(TimeSpan.FromSeconds(60));
        if (change == "cleanup") run.Store.Current = run.Store.Current! with { CleanupStartedAtUtc = run.Clock.GetUtcNow() };
        if (change == "generation") run.Store.Current!.Objects["private-artifact-published"].Response["eTag"] = "\"another-generation\"";
        if (change == "profile") run.Store.Current!.Objects["private-artifact-stage-plan"].Response["profile"]!["privateAddresses"]![0] = "8.8.8.8";
        run.Restart();
        var calls = run.Api.Requests.Count;
        var requests = run.Store.Current!.Requests;
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.PreparePrivateArtifactStage(artifact.Binding, artifact.Directory, change == "duration" ? 61 : 60, default))).Code);
        Assert.Equal(calls, run.Api.Requests.Count);
        Assert.Equal(requests, run.Store.Current.Requests);
    }

    [Fact]
    public async Task PrivateArtifactPublisherConditionallyCreatesAndReadsBackTheBoundArchiveWithoutExecutingIt()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.QueueTarget();
        var first = await artifact.Publish();
        Assert.Equal(artifact.Bytes, artifact.Handler.Content);
        Assert.Equal(["GET", "PUT", "GET"], artifact.Handler.Methods.ToArray());
        Assert.Equal(5, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.Equal(artifact.Binding.DeploymentBindingHash,
            artifact.Handler.Metadata["x-ms-meta-cohors_deployment"]);
        Assert.Equal(artifact.Binding.DownloadProvenanceSha256,
            artifact.Handler.Metadata["x-ms-meta-cohors_provenance"]);
        Assert.Contains("/c1-artifacts/" + artifact.Binding.DeploymentBindingHash + "/" + run.Input.SourceCommit + "/", first.Endpoint);
        Assert.Equal(artifact.Bytes.Length, first.OuterArchiveLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifact.Bytes)), first.OuterArchiveSha256);
        var originalPlan = Canonical.Hash(run.Store.Current.Objects["private-artifact-publication-plan"]);
        run.Restart();
        artifact.QueueTarget();
        var second = await artifact.Publish();
        Assert.Equal(first.CreationId, second.CreationId);
        Assert.Equal(first.ETag, second.ETag);
        Assert.Equal(originalPlan, Canonical.Hash(run.Store.Current.Objects["private-artifact-publication-plan"]));
        Assert.Single(artifact.Handler.Methods, method => method == "PUT");
        Assert.Equal(8, run.Store.Current.Requests);
        Assert.All(artifact.Credential.Scopes, scope => Assert.Equal("https://storage.azure.com/.default", scope));
        Assert.DoesNotContain(run.Store.Current.Objects.Keys, key => key.StartsWith("checkpoint-allocation-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LostArtifactPublicationOnlyReconcilesAndNeverReplaysTheConditionalPut()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Handler.LoseAfterPut = true;
        artifact.QueueTarget();
        await Assert.ThrowsAsync<IOException>(() => artifact.Publish());
        var uploaded = artifact.Handler.Content;
        artifact.Handler.Content = null;
        run.Restart();
        artifact.QueueTarget();
        Assert.Equal("c1_artifact_unknown", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        artifact.Handler.Content = uploaded;
        run.Restart();
        artifact.QueueTarget();
        await artifact.Publish();
        Assert.Single(artifact.Handler.Methods, method => method == "PUT");
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Equal(10, run.Store.Current.Requests);
    }

    [Fact]
    public async Task MatchingButUnjournaledArchiveIsNotAdoptedOrOverwritten()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Handler.Content = artifact.Bytes;
        artifact.QueueTarget();
        Assert.Equal("c1_artifact_foreign", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        Assert.Equal(["GET"], artifact.Handler.Methods.ToArray());
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-published"));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("source")]
    [InlineData("provenance")]
    public async Task ArtifactLocalTamperingStopsBeforeTokensOrProviderIo(string changed)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        if (changed == "archive")
        {
            var changedBytes = artifact.Bytes.ToArray();
            changedBytes[0] ^= 1;
            File.WriteAllBytes(Path.Combine(artifact.Directory, "transport.zip"), changedBytes);
        }
        else if (changed == "source")
        {
            artifact.Provenance["sourceCommit"] = new string('d', 40);
            artifact.WriteProvenance();
        }
        else artifact.Binding = artifact.Binding with { DownloadProvenanceSha256 = new string('f', 64) };
        await Assert.ThrowsAsync<DomainException>(() => artifact.Publish());
        Assert.Empty(artifact.Credential.Scopes);
        Assert.Empty(run.Api.Requests);
        Assert.Empty(artifact.Handler.Methods);
    }

    [Fact]
    public async Task PublisherChecksCurrentStoragePrivacyBeforeAnyBlobCall()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var publicStorage = (JsonObject)artifact.Storage.DeepClone();
        publicStorage["properties"]!["publicNetworkAccess"] = "Enabled";
        run.Api.Reply(200, publicStorage);
        Assert.Equal("c1_artifact_target", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        Assert.Single(run.Api.Requests);
        Assert.Empty(artifact.Handler.Methods);
        Assert.Empty(artifact.Credential.Scopes);
    }

    [Fact]
    public async Task PublicationRefusesAnotherCallerInsteadOfUsingAWorkerIdentity()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Credential = new(Token(run.Input with
        {
            BootstrapIdentity = run.Input.BootstrapIdentity! with { PrincipalObjectId = Guid.NewGuid().ToString("D") }
        }, artifact.Binding.StorageAudience, run.Clock.GetUtcNow(), "user_impersonation"));
        artifact.QueueTarget();
        Assert.Equal("c1_deploy_token_binding", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        Assert.Empty(artifact.Handler.Methods);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-published"));
    }

    [Fact]
    public async Task RejectedArtifactPutRetainsItsOriginalPlanForALaterExplicitInvocation()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Handler.RejectPut = true;
        artifact.QueueTarget();
        Assert.Equal("c1_artifact_http_403", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        var originalPlan = Canonical.Hash(run.Store.Current!.Objects["private-artifact-publication-plan"]);
        Assert.Empty(run.Store.Current.PendingCreates);
        run.Restart();
        artifact.QueueTarget();
        await artifact.Publish();
        Assert.Equal(originalPlan, Canonical.Hash(run.Store.Current.Objects["private-artifact-publication-plan"]));
        Assert.Equal(2, artifact.Handler.Methods.Count(method => method == "PUT"));
        Assert.Equal(9, run.Store.Current.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtifactPublicationCannotResetOrExtendItsOriginalWindow(bool changeBinding)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.QueueTarget();
        await artifact.Publish();
        var originalPlan = Canonical.Hash(run.Store.Current!.Objects["private-artifact-publication-plan"]);
        if (changeBinding) artifact.Binding = artifact.Binding with { MaxDurationSeconds = 300 };
        else run.Clock.Advance(TimeSpan.FromSeconds(121));
        run.Restart();
        Assert.Equal(changeBinding ? "c1_artifact_binding" : "c1_artifact_deadline",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        Assert.Equal(originalPlan, Canonical.Hash(run.Store.Current.Objects["private-artifact-publication-plan"]));
        Assert.Equal(2, run.Api.Requests.Count);
        Assert.Equal(3, artifact.Handler.Methods.Count);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("metadata")]
    [InlineData("etag")]
    public async Task ArtifactResumeRejectsChangedContentOwnershipOrStorageGeneration(string changed)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.QueueTarget();
        await artifact.Publish();
        if (changed == "body") artifact.Handler.Content![0] ^= 1;
        if (changed == "metadata") artifact.Handler.Metadata["x-ms-meta-cohors_creation"] = Guid.NewGuid().ToString("D");
        if (changed == "etag") artifact.Handler.ETag = "\"artifact-next-generation\"";
        run.Restart();
        artifact.QueueTarget();
        await Assert.ThrowsAsync<DomainException>(() => artifact.Publish());
        Assert.Single(artifact.Handler.Methods, method => method == "PUT");
    }

    [Fact]
    public async Task ArtifactPublicationDoesNotBorrowCleanupReserveForItsReadback()
    {
        using var run = new Harness(maxRequests: 6, delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.QueueTarget();
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        Assert.Equal(4, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.Equal(["GET", "PUT"], artifact.Handler.Methods.ToArray());
        Assert.True(run.Store.Current.PendingCreates.ContainsKey("private-artifact-published"));
        Assert.False(run.Store.Current.Objects.ContainsKey("private-artifact-published"));
    }

    [Fact]
    public async Task UncertainArtifactReceiptSaveFencesFurtherIoUntilOriginalJournalRecovery()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Handler.AfterRead = () => run.Store.FailAfterSave = true;
        artifact.QueueTarget();
        await Assert.ThrowsAsync<IOException>(() => artifact.Publish());
        Assert.True(run.Store.Current!.Objects.ContainsKey("private-artifact-published"));
        run.Store.FailAfterSave = false;
        artifact.Handler.AfterRead = null;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.Publish())).Code);
        run.Restart();
        artifact.QueueTarget();
        await artifact.Publish();
        Assert.Single(artifact.Handler.Methods, method => method == "PUT");
        Assert.Equal(8, run.Store.Current.Requests);
    }

    [Fact]
    public async Task ArtifactStreamReadbackMustFinishWithinTheOriginalPublicationDeadline()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        artifact.Binding = artifact.Binding with { MaxDurationSeconds = 2 };
        artifact.Handler.BlockBody = true;
        artifact.QueueTarget();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => artifact.Publish());
        Assert.True(artifact.Handler.BodyStarted.Task.IsCompleted);
        Assert.False(run.Store.Current!.Objects.ContainsKey("private-artifact-published"));
        Assert.True(run.Store.Current.PendingCreates.ContainsKey("private-artifact-published"));
        Assert.Single(artifact.Handler.Methods, method => method == "PUT");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArtifactAccessBindsOnlyThePublisherAndReadOnlyOwnVmToOneVerifiedArchive(bool delegated)
    {
        using var run = new Harness(delegated: delegated);
        using var artifact = new ArtifactHarness(run);
        var resources = artifact.PlanAccess();
        artifact.QueueAccessTarget();
        foreach (var resource in resources)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, StorageAccessResponse(resource));
            run.Api.Reply(200, StorageAccessResponse(resource));
        }
        Assert.Equal(4, (await artifact.EnsureAccess()).Length);
        Assert.Equal(["read", "write"], resources[0].Request["properties"]!["permissions"]![0]!["dataActions"]!
            .AsArray().Select(action => action!.GetValue<string>().Split('/')[^1]).ToArray());
        Assert.Equal(["read"], resources[1].Request["properties"]!["permissions"]![0]!["dataActions"]!
            .AsArray().Select(action => action!.GetValue<string>().Split('/')[^1]).ToArray());
        Assert.Equal(delegated ? "User" : "ServicePrincipal", resources[2].Request["properties"]!["principalType"]!.GetValue<string>());
        Assert.Equal(run.Input.BootstrapIdentity!.PrincipalObjectId, resources[2].Request["properties"]!["principalId"]!.GetValue<string>());
        Assert.Equal(artifact.Probe["identity"]!["principalId"]!.GetValue<string>(),
            resources[3].Request["properties"]!["principalId"]!.GetValue<string>());
        var path = Canonical.Hash(run.Input) + "/" + run.Input.SourceCommit + "/" +
            Convert.ToHexStringLower(SHA256.HashData(artifact.Bytes)) + ".zip";
        foreach (var resource in resources.Skip(2))
        {
            Assert.Contains("/containers/c1-artifacts/providers/Microsoft.Authorization/roleAssignments/", resource.Id);
            Assert.Contains("StringEquals '" + path + "'", resource.Request["properties"]!["condition"]!.GetValue<string>());
            Assert.Contains("NOT SubOperationMatches{'Blob.List'}", resource.Request["properties"]!["condition"]!.GetValue<string>());
        }
        Assert.DoesNotContain("/blobs/write", resources[3].Request["properties"]!["condition"]!.GetValue<string>());
        Assert.Equal(15, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
        Assert.Empty(artifact.Credential.Scopes);
        Assert.Empty(artifact.Handler.Methods);
        Assert.False(run.Store.Current.Objects.ContainsKey("private-artifact-publication-plan"));
        var target = Canonical.Hash(run.Store.Current.Objects["artifact-storage-target"]);
        run.Restart();
        artifact.QueueAccessTarget();
        foreach (var resource in resources) run.Api.Reply(200, StorageAccessResponse(resource));
        await artifact.EnsureAccess();
        Assert.Equal(4, run.Api.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.Equal(22, run.Store.Current.Requests);
        Assert.Equal(target, Canonical.Hash(run.Store.Current.Objects["artifact-storage-target"]));
        Assert.All(resources, resource => Assert.Equal(Canonical.Hash(resource),
            Canonical.Hash(run.Store.Current.Objects[resource.Key + "-intent"].Response)));
    }

    [Theory]
    [InlineData("vmInstance")]
    [InlineData("vmPrincipal")]
    [InlineData("publicStorage")]
    public async Task ArtifactAccessChecksCurrentVmIdentityAndStoragePrivacyBeforeAnyGrant(string changed)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var storage = (JsonObject)artifact.Storage.DeepClone();
        var probe = (JsonObject)artifact.Probe.DeepClone();
        if (changed == "publicStorage") storage["properties"]!["publicNetworkAccess"] = "Enabled";
        if (changed == "vmInstance") probe["properties"]!["vmId"] = Guid.NewGuid().ToString("D");
        if (changed == "vmPrincipal") probe["identity"]!["principalId"] = Guid.NewGuid().ToString("D");
        run.Api.Reply(200, storage);
        run.Api.Reply(200, artifact.Container);
        run.Api.Reply(200, probe);
        await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess());
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.DoesNotContain(run.Store.Current.Objects.Values, value => value.Kind is "ArtifactStorageRole" or "ArtifactStorageGrant");
    }

    [Theory]
    [InlineData("readerWrite")]
    [InlineData("receiverPrincipal")]
    [InlineData("receiverPath")]
    public async Task ArtifactAccessRefusesBroaderOrRedirectedFreshReadbacks(string changed)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = artifact.PlanAccess();
        var index = changed == "readerWrite" ? 1 : 3;
        artifact.QueueAccessTarget();
        foreach (var resource in resources.Take(index))
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, StorageAccessResponse(resource));
            run.Api.Reply(200, StorageAccessResponse(resource));
        }
        var current = StorageAccessResponse(resources[index]);
        if (changed == "readerWrite") current["properties"]!["permissions"]![0]!["dataActions"]!.AsArray()
            .Add("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write");
        if (changed == "receiverPrincipal") current["properties"]!["principalId"] = run.Input.BootstrapIdentity!.PrincipalObjectId;
        if (changed == "receiverPath") current["properties"]!["condition"] = "true";
        run.Api.Reply(404, new());
        run.Api.Reply(201, StorageAccessResponse(resources[index]));
        run.Api.Reply(200, current);
        Assert.Equal("c1_artifact_access_readback", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        Assert.True(run.Store.Current!.PendingCreates.ContainsKey(resources[index].Key));
        Assert.False(run.Store.Current.Objects.ContainsKey(resources[index].Key));
    }

    [Fact]
    public async Task LostArtifactRoleCreateCannotBeReplayedAndOnlyReconcilesTheOriginalReadback()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = artifact.PlanAccess();
        artifact.QueueAccessTarget();
        run.Api.Reply(404, new());
        run.Api.Fail(new IOException("synthetic lost role create"));
        await Assert.ThrowsAsync<IOException>(() => artifact.EnsureAccess());
        run.Restart();
        artifact.QueueAccessTarget();
        run.Api.Reply(404, new());
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
        run.Restart();
        artifact.QueueAccessTarget();
        run.Api.Reply(200, StorageAccessResponse(resources[0]));
        foreach (var resource in resources.Skip(1))
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, StorageAccessResponse(resource));
            run.Api.Reply(200, StorageAccessResponse(resource));
        }
        await artifact.EnsureAccess();
        Assert.Equal(4, run.Api.Requests.Count(request => request.Method == HttpMethod.Put));
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task UnjournaledArtifactRoleIsNotAdoptedOrDeletedByCleanup()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = artifact.PlanAccess();
        artifact.QueueAccessTarget();
        run.Api.Reply(200, StorageAccessResponse(resources[0]));
        Assert.Equal("c1_deploy_foreign", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        var before = run.Api.Requests.Count;
        var result = await run.Provisioner.CleanupPrivateArtifactAccess(default);
        Assert.Equal("ArtifactAccessNotManaged", result.Status);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ArtifactAccessCleanupUsesOriginalReserveAndRemovesGrantsBeforeBothSubscriptionRoles()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = await CreateArtifactAccessForCleanup(run, artifact);
        var started = run.Store.Current!.StartedAtUtc;
        run.Clock.Advance(TimeSpan.FromHours(4).Add(TimeSpan.FromSeconds(1)));
        QueueStorageAccessRemoval(run, resources);
        var result = await run.Provisioner.CleanupPrivateArtifactAccess(default);
        Assert.Equal("ArtifactAccessInventoryAbsent", result.Status);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Equal(resources.Select(resource => resource.Key), result.AbsentKeys);
        Assert.Equal(resources.Select(resource => resource.Id),
            run.Api.Requests.Where(request => request.Method == HttpMethod.Delete).Select(request => request.Uri.AbsolutePath));
        Assert.Equal(12, run.Store.Current!.CleanupRequests);
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        Assert.Empty(result.UnresolvedKeys);
        Assert.All(run.Store.Current.CleanupReceipts.Values, receipt =>
        {
            Assert.False(receipt.Response["effectiveAccessRevoked"]!.GetValue<bool>());
            Assert.False(receipt.Response["artifactDataDeleted"]!.GetValue<bool>());
            Assert.False(receipt.Response["workersStopped"]!.GetValue<bool>());
        });
        Assert.Equal("c1_deploy_cleanup_started", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
    }

    [Fact]
    public async Task UnknownArtifactGrantDeleteDoesNotReplayOrDeleteTheStillReferencedRole()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = await CreateArtifactAccessForCleanup(run, artifact);
        run.Api.Reply(200, StorageAccessResponse(resources[0]));
        run.Api.Fail(new IOException("synthetic lost delete"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupPrivateArtifactAccess(default));
        run.Restart();
        run.Api.Reply(200, StorageAccessResponse(resources[0]));
        Assert.Equal(4, (await run.Provisioner.CleanupPrivateArtifactAccess(default)).UnresolvedKeys.Length);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        run.Restart();
        run.Api.Reply(404, new());
        QueueStorageAccessRemoval(run, resources.Skip(1));
        Assert.Empty((await run.Provisioner.CleanupPrivateArtifactAccess(default)).UnresolvedKeys);
        Assert.Equal(resources.Select(resource => resource.Id),
            run.Api.Requests.Where(request => request.Method == HttpMethod.Delete).Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ArtifactAccessCannotRebindItsPublicationProfileAfterGrantCreation()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        await CreateArtifactAccessForCleanup(run, artifact);
        var before = run.Api.Requests.Count;
        artifact.Binding = artifact.Binding with { MaxDurationSeconds = 300 };
        run.Restart();
        Assert.Equal("c1_artifact_access_binding", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Fact]
    public async Task CorruptArchiveCannotAcquireArtifactGrants()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var bytes = artifact.Bytes.ToArray();
        bytes[0] ^= 1;
        File.WriteAllBytes(Path.Combine(artifact.Directory, "transport.zip"), bytes);
        Assert.Equal("c1_artifact_digest", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        Assert.Empty(run.Api.Requests);
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task UncertainArtifactAccessTargetSaveFencesIoAndDoesNotInventCleanupOwnership()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        run.Store.FailAfterSave = true;
        await Assert.ThrowsAsync<IOException>(() => artifact.EnsureAccess());
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown",
            (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        run.Restart();
        Assert.Equal("ArtifactAccessNotManaged", (await run.Provisioner.CleanupPrivateArtifactAccess(default)).Status);
        Assert.Empty(run.Api.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArtifactAccessCannotAdoptAnotherGenerationWithIdenticalRolePermissions(bool resume)
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        if (resume) await CreateArtifactAccessForCleanup(run, artifact);
        var role = artifact.PlanAccess()[0];
        var original = resume ? Canonical.Hash(run.Store.Current!.Objects[role.Key]) : null;
        var replaced = StorageAccessResponse(role);
        replaced["properties"]!["createdOn"] = "2026-11-18T12:01:00Z";
        run.Restart();
        artifact.QueueAccessTarget();
        if (!resume)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, StorageAccessResponse(role));
        }
        run.Api.Reply(200, replaced);
        Assert.Equal("c1_deploy_drift", (await Assert.ThrowsAsync<DomainException>(() => artifact.EnsureAccess())).Code);
        if (resume) Assert.Equal(original, Canonical.Hash(run.Store.Current!.Objects[role.Key]));
        else Assert.False(run.Store.Current!.Objects.ContainsKey(role.Key));
    }

    [Fact]
    public async Task ArtifactCleanupRejectsEvenRehashedIntentsThatChangeTheOriginalExactPath()
    {
        using var run = new Harness(delegated: true);
        using var artifact = new ArtifactHarness(run);
        var resources = await CreateArtifactAccessForCleanup(run, artifact);
        var key = resources[0].Key + "-intent";
        var intent = run.Store.Current!.Objects[key];
        var response = (JsonObject)intent.Response.DeepClone();
        response["request"]!["properties"]!["condition"] = "true";
        run.Store.Current.Objects[key] = intent with { Response = response, RequestHash = Canonical.Hash(response["request"]) };
        run.Restart();
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_artifact_access_cleanup_journal",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.CleanupPrivateArtifactAccess(default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("profile")]
    [InlineData("invocation")]
    [InlineData("runtime")]
    public async Task InvocationAllowanceCannotBeMovedBetweenCommandsProfilesIdentitiesOrRuntimes(string changed)
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        await seed.Materialize();
        var profile = run.Provisioner.ExportBlobCheckpointProfile(seed.Execution, seed.Binding);
        var id = Guid.NewGuid().ToString("D");
        var reservation = run.Provisioner.ReserveBlobCheckpoint(seed.Execution, seed.Binding, id, 8, run.Clock.GetUtcNow().AddSeconds(80));
        var command = new FabricC1Invocation(Canonical.Hash(seed.Execution), "status", InvocationId: id);
        var allowance = run.Provisioner.ExportBlobCheckpointAllowance(seed.Execution, seed.Binding, command, reservation);
        if (changed == "profile") allowance = allowance with { ProfileSha256 = new string('f', 64) };
        if (changed == "invocation") allowance = allowance with { Command = command with { InvocationId = Guid.NewGuid().ToString("D") } };
        Assert.Equal(changed == "runtime" ? "c1_checkpoint_profile" : "c1_checkpoint_allowance",
            Assert.Throws<DomainException>(() => FabricC1CheckpointProfiles.ValidateAllowance(seed.Execution, profile,
                allowance, changed == "runtime" ? "hosted-control" : "direct",
                changed == "operation" ? "discover" : "status", null, run.Clock.GetUtcNow())).Code);
    }

    [Fact]
    public async Task LostBlobSeedCreateResponseReconcilesTheSameMarkerWithoutAnotherPut()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.LoseAfterPut = true;
        await Assert.ThrowsAsync<IOException>(() => seed.Materialize());
        Assert.Contains("checkpoint-seed-direct", run.Store.Current!.PendingCreates.Keys);
        var original = Canonical.Hash(run.Store.Current.Objects["checkpoint-allocation-direct"]);
        run.Restart();
        var receipt = await seed.Materialize();
        Assert.Equal(original, receipt.AllocationReceiptSha256);
        Assert.Equal(["GET", "PUT", "GET"], seed.Handler.Methods.ToArray());
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task MissingBlobAfterUncertainCreateNeverResetsTheOriginalRunOrRetriesThePut()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.LoseBeforePut = true;
        await Assert.ThrowsAsync<IOException>(() => seed.Materialize());
        var allocation = Canonical.Hash(run.Store.Current!.Objects["checkpoint-allocation-direct"]);
        run.Restart();
        Assert.Equal("c1_deploy_checkpoint_seed_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET", "PUT", "GET"], seed.Handler.Methods.ToArray());
        Assert.Equal(allocation, Canonical.Hash(run.Store.Current!.Objects["checkpoint-allocation-direct"]));
        Assert.Contains("checkpoint-seed-direct", run.Store.Current.PendingCreates.Keys);
    }

    [Fact]
    public async Task SeedCreateRaceDoesNotOverwriteOrAdoptTheOtherWritersBlob()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.RacePut = true;
        Assert.Equal("c1_deploy_checkpoint_seed_http_412", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET", "PUT"], seed.Handler.Methods.ToArray());
        run.Restart();
        Assert.Equal("c1_deploy_checkpoint_seed_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET", "PUT", "GET"], seed.Handler.Methods.ToArray());
        Assert.Contains("checkpoint-seed-direct", run.Store.Current!.PendingCreates.Keys);
    }

    [Fact]
    public async Task EvenMatchingPreexistingSeedBytesCannotBeAdoptedWithoutAnOriginalWriteIntent()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        var allocation = run.Provisioner.PrepareBlobCheckpoint(seed.Execution, seed.Binding);
        seed.Handler.Content = JsonSerializer.SerializeToUtf8Bytes(allocation.Seed, Canonical.Json);
        Assert.Equal("c1_deploy_checkpoint_seed_foreign", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET"], seed.Handler.Methods.ToArray());
        Assert.DoesNotContain("checkpoint-seed-direct", run.Store.Current!.Objects.Keys);
    }

    [Theory]
    [InlineData("marker", "c1_deploy_checkpoint_seed_foreign")]
    [InlineData("allocation", "c1_deploy_checkpoint_seed_foreign")]
    [InlineData("body", "c1_deploy_checkpoint_seed_content")]
    [InlineData("weak-etag", "c1_deploy_checkpoint_seed_etag")]
    public async Task UnknownSeedCannotBeRecoveredFromForeignMetadataChangedBytesOrWeakEtag(string changed, string code)
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.LoseAfterPut = true;
        await Assert.ThrowsAsync<IOException>(() => seed.Materialize());
        if (changed == "marker") seed.Handler.Metadata["x-ms-meta-cohors_seed_id"] = Guid.NewGuid().ToString("D");
        if (changed == "allocation") seed.Handler.Metadata["x-ms-meta-cohors_allocation"] = new string('f', 64);
        if (changed == "weak-etag") seed.Handler.WeakEtag = true;
        if (changed == "body")
        {
            var original = JsonSerializer.Deserialize<FabricC1BlobCheckpointDocument>(seed.Handler.Content!, Canonical.Json)!;
            seed.Handler.Content = JsonSerializer.SerializeToUtf8Bytes(original with
            { Checkpoint = original.Checkpoint with { Requests = 1 } }, Canonical.Json);
        }
        run.Restart();
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => seed.Materialize())).Code);
        Assert.Single(seed.Handler.Methods, method => method == "PUT");
        Assert.Contains("checkpoint-seed-direct", run.Store.Current!.PendingCreates.Keys);
    }

    [Fact]
    public async Task MissingContainerIsNotPermissionToCreateOneFromTheSeedDataPath()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.MissingCode = "ContainerNotFound";
        Assert.Equal("c1_deploy_checkpoint_container", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET"], seed.Handler.Methods.ToArray());
        Assert.Empty(run.Store.Current!.PendingCreates);
    }

    [Fact]
    public async Task SeedCannotBorrowItsConsumersPrepaidRequestsOrCleanupReserve()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run, allocated: 80);
        Assert.Equal("c1_deploy_budget", (await Assert.ThrowsAsync<DomainException>(() => seed.Materialize())).Code);
        Assert.Empty(seed.Handler.Methods);
        Assert.Empty(seed.Credential.Scopes);
        Assert.Equal(0, run.Store.Current!.Requests);
        Assert.Equal(0, run.Store.Current.CleanupRequests);
    }

    [Fact]
    public async Task RejectedSeedCreateNeedsAFreshInvocationAndReusesItsOriginalMarker()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.RejectPut = true;
        Assert.Equal("c1_deploy_checkpoint_seed_http_403", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        Assert.Equal(["GET", "PUT"], seed.Handler.Methods.ToArray());
        Assert.Empty(run.Store.Current!.PendingCreates);
        var original = run.Store.Current.Objects["checkpoint-seed-direct-intent"].RequestHash;
        run.Restart();
        await seed.Materialize();
        Assert.Equal(original, run.Store.Current!.Objects["checkpoint-seed-direct"].RequestHash);
        Assert.Equal(["GET", "PUT", "GET", "PUT", "GET"], seed.Handler.Methods.ToArray());
    }

    [Fact]
    public async Task UncertainSeedReceiptPersistenceFencesTheProcessButRetainsTheOriginalSuccessfulReadback()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.AfterRead = () => run.Store.FailAfterSave = true;
        await Assert.ThrowsAsync<IOException>(() => seed.Materialize());
        Assert.Contains("checkpoint-seed-direct", run.Store.Current!.Objects.Keys);
        run.Store.FailAfterSave = false;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            seed.Materialize())).Code);
        run.Restart();
        var original = await seed.Materialize();
        Assert.Equal(seed.Handler.Metadata["x-ms-meta-cohors_seed_id"], original.CreationId);
        Assert.Equal(3, seed.Handler.Methods.Count);
    }

    [Fact]
    public async Task SeedRequiresTheOwnNonpublicContainerAndCannotProceedAfterCleanupFencing()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        var foundation = (JsonObject)run.Store.Current!.Objects["arm-foundation"].Response.DeepClone();
        foundation["properties"]!["outputs"]!["checkpointContainer"]!["value"]!["properties"]!["publicAccess"] = "Blob";
        run.Seed("arm-foundation", "ArmDeployment", foundation);
        Assert.Equal("c1_deploy_checkpoint_container", (await Assert.ThrowsAsync<DomainException>(() => seed.Materialize())).Code);
        Assert.Empty(seed.Handler.Methods);
        foundation["properties"]!["outputs"]!["checkpointContainer"]!["value"]!["properties"]!["publicAccess"] = "None";
        run.Seed("arm-foundation", "ArmDeployment", foundation);
        run.Store.Save(run.Store.Current! with { CleanupStartedAtUtc = run.Clock.GetUtcNow() });
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_started", (await Assert.ThrowsAsync<DomainException>(() => seed.Materialize())).Code);
        Assert.Empty(seed.Handler.Methods);
    }

    [Fact]
    public async Task CancellationCoversTheSeedReadbackBodyAndRetainsTheUncertainOriginal()
    {
        using var run = new Harness();
        using var seed = new SeedHarness(run);
        seed.Handler.LoseAfterPut = true;
        await Assert.ThrowsAsync<IOException>(() => seed.Materialize());
        run.Restart();
        using var cancellation = new CancellationTokenSource();
        seed.Handler.BlockBody = true;
        var pending = seed.Materialize(cancellation.Token);
        await seed.Handler.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Contains("checkpoint-seed-direct", run.Store.Current!.PendingCreates.Keys);
        Assert.Single(seed.Handler.Methods, method => method == "PUT");
    }

    [Fact]
    public void CleanupReserveCannotIncreaseTheOriginalRequestCeilingOrExtendActiveUse()
    {
        using var run = new Harness();
        foreach (var invalid in new[]
        {
            run.Input with { Cleanup = null },
            run.Input with { Cleanup = run.Input.Cleanup! with { ReservedRequests = run.Input.MaxControlRequests } },
            run.Input with { Cleanup = run.Input.Cleanup! with { VerificationDeadlineUtc = run.Input.ExpiresAtUtc } },
            run.Input with { Cleanup = run.Input.Cleanup! with { VerificationDeadlineUtc = run.Input.ExpiresAtUtc!.Value.AddHours(3) } }
        })
            Assert.Contains("cleanup", FabricC1Deployment.Plan(invalid, run.Clock.GetUtcNow()).MissingInputs);
    }

    [Fact]
    public async Task IdentityCleanupUsesItsReservedCallsAfterNormalExpiryAndFencesProvisioningWithoutReset()
    {
        using var run = new Harness(delegated: true);
        var application = CleanupApplication(run);
        run.Seed("runtime-app", "Application", application);
        run.Store.Save(run.Store.Current! with { StartedAtUtc = run.Clock.GetUtcNow(), Requests = 80 });
        var afterExpiry = new Clock(run.Input.ExpiresAtUtc!.Value.AddMinutes(1));
        var cleanup = new FabricC1Provisioner(run.Input, run.Api, run.Store, afterExpiry);
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        var result = await cleanup.CleanupIdentityPhase(default);
        Assert.True(result.ActiveIdentityInventoryAbsent);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Equal("runtime-app", Assert.Single(result.AbsentIdentityKeys));
        Assert.Equal(84, run.Store.Current!.Requests);
        Assert.Equal(4, run.Store.Current.CleanupRequests);
        Assert.Equal(run.Clock.GetUtcNow(), run.Store.Current.StartedAtUtc);
        Assert.Equal(afterExpiry.GetUtcNow(), run.Store.Current.CleanupStartedAtUtc);
        Assert.False(run.Store.Current.CleanupReceipts["runtime-app"].Response["providerRetentionPurged"]!.GetValue<bool>());
        Assert.Equal("c1_deploy_cleanup_started", (await Assert.ThrowsAsync<DomainException>(() => cleanup.Preflight(default))).Code);
        run.Api.Reply(404, new());
        var resumed = new FabricC1Provisioner(run.Input, run.Api, run.Store, new Clock(afterExpiry.GetUtcNow().AddMinutes(1)));
        Assert.True((await resumed.CleanupIdentityPhase(default)).ActiveIdentityInventoryAbsent);
        Assert.Equal(afterExpiry.GetUtcNow(), run.Store.Current.CleanupStartedAtUtc);
        Assert.Equal(85, run.Store.Current.Requests);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostIdentityDeleteResponseRequiresAbsenceReadbackNotAnotherDelete(bool absent)
    {
        using var run = new Harness(delegated: true);
        var application = CleanupApplication(run);
        run.Seed("runtime-app", "Application", application);
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Fail(new HttpRequestException("synthetic delete response loss"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.CleanupIdentityPhase(default));
        Assert.Contains("runtime-app", run.Store.Current!.PendingDeletes.Keys);
        run.Restart();
        run.Api.Reply(absent ? 404 : 200, absent ? new() : application);
        var result = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.Equal(absent, result.ActiveIdentityInventoryAbsent);
        Assert.Equal(!absent, result.UnresolvedIdentityKeys.Contains("runtime-app", StringComparer.Ordinal));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UncertainIdentityCreateIsReconciledReadOnlyDuringCleanupAndNeverRecreated()
    {
        using var run = new Harness(delegated: true);
        run.Api.List();
        run.Api.Fail(new HttpRequestException("synthetic create response loss"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureApplicationForRole("runtime", default));
        run.Api.List();
        var unresolved = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.False(unresolved.ActiveIdentityInventoryAbsent);
        Assert.Contains("runtime-app", unresolved.UnresolvedIdentityKeys);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        run.Restart();
        var application = CleanupApplication(run);
        run.Api.List(application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity.PrincipalObjectId });
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        Assert.True((await run.Provisioner.CleanupIdentityPhase(default)).ActiveIdentityInventoryAbsent);
        Assert.Empty(run.Store.Current!.PendingCreates);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task IdentityCleanupRefusesForeignOwnershipInitialActorAndPaidConsumers()
    {
        using var run = new Harness(delegated: true);
        var application = CleanupApplication(run);
        run.Seed("runtime-app", "Application", application);
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId }, new JsonObject { ["id"] = Guid.NewGuid().ToString("D") });
        Assert.Equal("c1_deploy_cleanup_ownership", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        using var actor = new Harness(delegated: true);
        var forbidden = CleanupApplication(actor);
        forbidden["appId"] = actor.Input.BootstrapIdentity!.ClientId!.ToUpperInvariant();
        actor.Seed("runtime-app", "Application", forbidden);
        Assert.Equal("c1_deploy_cleanup_actor", (await Assert.ThrowsAsync<DomainException>(() =>
            actor.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.Empty(actor.Api.Requests);
        using var dependent = new Harness();
        dependent.Seed("workspace-agent", "Workspace", dependent.Workspace("agent"));
        Assert.Equal("c1_deploy_cleanup_dependents", (await Assert.ThrowsAsync<DomainException>(() =>
            dependent.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.Empty(dependent.Api.Requests);
    }

    [Fact]
    public async Task PrincipalCleanupChecksItsParentApplicationOwnershipBeforeDeletingEither()
    {
        using var run = new Harness(delegated: true);
        var application = CleanupApplication(run);
        var principal = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = application["appId"]!.GetValue<string>(),
            ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
        };
        run.Seed("runtime-app", "Application", application);
        run.Seed("runtime-sp", "ServicePrincipal", principal);
        run.Api.Reply(200, principal);
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId }, new JsonObject { ["id"] = Guid.NewGuid().ToString("D") });
        Assert.Equal("c1_deploy_cleanup_ownership", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task ExhaustedCleanupReserveAndDeadlineSurviveResumeWithoutAnyLimitIncrease()
    {
        using var run = new Harness(maxRequests: 6, delegated: true);
        var application = CleanupApplication(run);
        run.Seed("runtime-app", "Application", application);
        run.Api.Reply(200, application);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        var started = run.Store.Current!.CleanupStartedAtUtc;
        run.Restart();
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupIdentityPhase(default))).Code);
        Assert.Equal(2, run.Api.Requests.Count);
        Assert.Equal(2, run.Store.Current.CleanupRequests);
        Assert.Equal(started, run.Store.Current.CleanupStartedAtUtc);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));

        using var expired = new Harness(delegated: true);
        expired.Seed("runtime-app", "Application", CleanupApplication(expired));
        var cleanup = new FabricC1Provisioner(expired.Input, expired.Api, expired.Store,
            new Clock(expired.Input.Cleanup!.VerificationDeadlineUtc!.Value));
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() => cleanup.CleanupIdentityPhase(default))).Code);
        Assert.Empty(expired.Api.Requests);
    }

    [Fact]
    public async Task GroupCleanupRetainsAdministrativeEffectsRatherThanDisablingTenantSettings()
    {
        using var run = new Harness(delegated: true);
        var group = Group(run);
        run.Seed("fabric-api-group", "SecurityGroup", group);
        run.Seed("tenant-change-SyntheticApi", "ScopedTenantSetting", new() { ["id"] = "SyntheticApi" });
        run.Seed("tenant-shared-inbound-enable", "SharedTenantSettingEffect", new() { ["id"] = FabricC1Provisioner.WorkspaceInboundSettingName });
        run.Api.Reply(200, group);
        run.Api.Reply(200, group);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(200, GroupMembers(group));
        run.Api.Reply(204, new());
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupIdentityPhase(default);
        Assert.True(result.ActiveIdentityInventoryAbsent);
        Assert.False(result.CompleteC1Cleanup);
        Assert.Contains("tenant-shared-inbound-enable", result.RetainedAdministrativeEffects);
        Assert.Contains("tenant-change-SyntheticApi", result.RetainedAdministrativeEffects);
        Assert.All(run.Api.Requests, request => Assert.Equal("graph.microsoft.com", request.Uri.Host));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static JsonObject CleanupApplication(Harness run) => new()
    {
        ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = Guid.NewGuid().ToString("D"),
        ["displayName"] = FabricC1Deployment.Name(run.Input) + "-runtime", ["signInAudience"] = "AzureADMyOrg",
        ["tags"] = new JsonArray(FabricC1Deployment.Marker(run.Input))
    };

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

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 90)]
    [InlineData(true, -5)]
    public async Task ProviderRetryAfterRetainsLongMinimumAndRoundsFutureHttpDatesUp(bool date, int seconds)
    {
        using var run = new Harness(delegated: true);
        var credential = new BoundCredential(Token(run.Input, run.Input.ControlTokenBindings!.ArmAudience!,
            run.Clock.GetUtcNow(), "user_impersonation"));
        using var handler = new JsonHandler
        {
            RetryAfter = date ? new(run.Clock.GetUtcNow().AddSeconds(seconds)) : new(TimeSpan.FromSeconds(seconds))
        };
        if (date) run.Clock.Advance(TimeSpan.FromMilliseconds(250));
        using var client = new HttpClient(handler);
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>
        { ["management.azure.com"] = client }, run.Clock);
        var response = await api.Send(HttpMethod.Get,
            new($"https://management.azure.com/subscriptions/{run.Input.SubscriptionId}?api-version=2022-12-01"));
        Assert.Equal(Math.Max(seconds, 0), response.RetryAfterSeconds);
        Assert.Equal(1, handler.Requests);
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
        run.Restart();
        var replacement = (JsonObject)receipt.DeepClone();
        replacement["id"] = Guid.NewGuid().ToString("D");
        run.Api.List(receipt);
        run.Api.List(replacement);
        Assert.Equal("c1_deploy_grant_readback", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AssignRuntimeToAgentWorkspace(default))).Code);
        Assert.Equal(assignment, run.Store.Current.Objects["runtime-agent-grant"].Id);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post);
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
    public async Task DefinitionReadResumesItsOriginalPrivateOperationWithoutAnotherDefinitionPost()
    {
        using var run = new Harness(manualTimers: true);
        var workspace = run.Workspace("agent");
        var workspaceId = workspace["id"]!.GetValue<string>();
        run.Seed("workspace-agent", "Workspace", workspace);
        run.Seed("policy-agent", "CommunicationPolicy", new() { ["id"] = workspaceId });
        var definition = new JsonObject { ["parts"] = new JsonArray(
            new JsonObject { ["path"] = "Files/Config/publish_info.json", ["payload"] = "e30=", ["payloadType"] = "InlineBase64" },
            new JsonObject { ["path"] = "Files/Config/published/stage_config.json", ["payload"] = "e30=", ["payloadType"] = "InlineBase64" }) };
        var agent = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["displayName"] = "C1_Agent", ["description"] = FabricC1Deployment.Marker(run.Input)
        };
        var operation = $"https://{FabricC1Provisioner.WorkspaceHost(workspaceId)}/v1/operations/{Guid.NewGuid():D}";
        run.Api.List(InboundSetting(true));
        run.Api.List();
        run.Api.Reply(201, agent);
        run.Api.Reply(202, new(), operationUrl: operation, retryAfterSeconds: 60);
        using var cancel = new CancellationTokenSource();
        var first = run.Provisioner.EnsureDataAgent(definition, cancel.Token);
        Assert.False(first.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(operation, run.Store.Current!.PendingOperations["published-definition"]);
        var started = run.Store.Current.StartedAtUtc;
        run.Restart();
        run.Api.List(InboundSetting(true));
        run.Api.Reply(200, agent);
        run.Api.Reply(200, new() { ["status"] = "Succeeded" });
        run.Api.Reply(200, new() { ["definition"] = definition.DeepClone() });
        var resumed = run.Provisioner.EnsureDataAgent(definition, default);
        Assert.False(resumed.IsCompleted);
        Assert.Equal(6, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests, request => request.Uri.AbsolutePath.EndsWith("/getDefinition", StringComparison.Ordinal));
        Assert.Equal(operation + "/result", run.Api.Requests[^1].Uri.AbsoluteUri);
        Assert.Equal(started, run.Store.Current.StartedAtUtc);
        Assert.Empty(run.Store.Current.PendingOperations);
        Assert.Null(run.Store.Current.PendingOperationNotBeforeUtc);
        Assert.Contains("published-definition", run.Store.Current.Objects.Keys);
    }

    [Fact]
    public async Task SourceNotebookRequiresOriginalDelegatedActorAndSeparateWorkspaces()
    {
        using var appOnly = new Harness();
        _ = new SourceNotebookHarness(appOnly);
        Assert.Equal("c1_source_actor", (await Assert.ThrowsAsync<DomainException>(() =>
            appOnly.Provisioner.PrepareSourceNotebook("released", default))).Code);
        Assert.Empty(appOnly.Api.Requests);
        using var user = new Harness(delegated: true);
        _ = new SourceNotebookHarness(user);
        Assert.Equal("c1_deploy_source_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            user.Provisioner.PrepareSourceNotebook("agent", default))).Code);
        var state = user.Store.Load()!;
        state.Objects["workspace-denied"] = state.Objects["workspace-released"];
        user.Store.Save(state);
        user.Restart();
        Assert.Equal("c1_deploy_source_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            user.Provisioner.PrepareSourceNotebook("released", default))).Code);
        Assert.Empty(user.Api.Requests);
    }

    [Theory]
    [InlineData("workspace", "c1_deploy_context")]
    [InlineData("capacity", "c1_deploy_context")]
    [InlineData("policy", "c1_source_policy")]
    [InlineData("lakehouse-workspace", "c1_deploy_context")]
    [InlineData("schema", "c1_source_schema")]
    public async Task SourceNotebookRefusesCurrentMetadataDriftBeforeCreatingAnything(string mutation, string code)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        source.QueuePreparation("released", mutation);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.PrepareSourceNotebook("released", default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-notebook-plan-released"));
    }

    [Fact]
    public async Task SourceNotebookResumesOriginalCodeInvocationAndControlWindow()
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        source.QueuePreparation("released");
        var original = await run.Provisioner.PrepareSourceNotebook("released", default);
        run.Clock.Advance(TimeSpan.FromSeconds(30));
        run.Restart();
        source.QueuePreparation("released");
        var resumed = await run.Provisioner.PrepareSourceNotebook("released", default);
        Assert.Equal(original.BindingSha256, resumed.BindingSha256);
        Assert.Equal(original.Program, resumed.Program);
        Assert.Equal(original.Definition.ToJsonString(), resumed.Definition.ToJsonString());
        Assert.All(run.Api.Requests.Where(request => request.Uri.AbsolutePath == "/v1/workspaces/" + source.WorkspaceId("released")),
            request => Assert.Equal(FabricC1Provisioner.WorkspaceHost(source.WorkspaceId("released")), request.Uri.Host));
        var state = run.Store.Load()!;
        var saved = state.Objects["source-notebook-plan-released"];
        saved.Response["programSha256"] = new string('f', 64);
        state.Objects["source-notebook-plan-released"] = saved with { RequestHash = Canonical.Hash(saved.Response) };
        run.Store.Save(state);
        run.Restart();
        source.QueuePreparation("released");
        Assert.Equal("c1_source_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.PrepareSourceNotebook("released", default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("extra-cell")]
    [InlineData("remote")]
    [InlineData("duplicate-json")]
    [InlineData("wrong-part")]
    public async Task SourceNotebookReadbackRejectsChangedOrAmbiguousExecutableContent(string mutation)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        source.QueueNotebook("released", definition =>
        {
            var part = definition["definition"]!["parts"]![0]!;
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(part["payload"]!.GetValue<string>()));
            var notebook = JsonNode.Parse(text)!;
            if (mutation == "code") notebook["cells"]![0]!["source"] = new JsonArray("raise RuntimeError('changed')");
            if (mutation == "extra-cell") notebook["cells"]!.AsArray().Add(new JsonObject { ["cell_type"] = "code", ["source"] = new JsonArray("pass") });
            text = Canonical.Serialize(notebook);
            if (mutation == "duplicate-json") text = text.Replace("\"nbformat\":4", "\"nbformat\":4,\"nbformat\":4", StringComparison.Ordinal);
            part["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            if (mutation == "remote") part["payloadType"] = "InlineUrl";
            if (mutation == "wrong-part") part["path"] = "unreviewed.py";
        });
        Assert.Equal("c1_source_definition", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureSourceNotebook("released", default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-notebook-definition-released"));
        Assert.DoesNotContain(run.Api.Requests, request => request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("released", null)]
    [InlineData("released", "dbo")]
    [InlineData("denied", null)]
    [InlineData("denied", "dbo")]
    public async Task GeneratedSourceProgramChecksActualContextNoOverwriteAndExactSparkReadback(string role, string? schema)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run, schema);
        source.QueuePreparation(role);
        var plan = await run.Provisioner.PrepareSourceNotebook(role, default);
        await AssertPythonContract("""
import copy, json, os, sys, uuid
from datetime import datetime, timedelta
from types import SimpleNamespace

data = json.load(sys.stdin)
module = {"__name__": "synthetic_source_contract"}
exec(compile(data["program"], "<generated-source-notebook>", "exec"), module)
binding = json.loads(module["SOURCE_BYTES"])
assert binding == data["binding"]
expected_ids = ["C1-A@1", "C1-B@2"] if binding["scope"] == "released" else ["C1-A@2", "C1-C@1", "C1-D@1"]
assert [row["sourceId"] + "@" + str(row["revision"]) for row in binding["rows"]] == expected_ids
if binding["scope"] == "released":
    assert all(row["marker"].startswith("SYNTHETIC-ALLOW-") for row in binding["rows"])
start = datetime.fromisoformat(binding["startedAtUtc"].replace("Z", "+00:00"))
expiry = datetime.fromisoformat(binding["expiresAtUtc"].replace("Z", "+00:00"))
now = start + timedelta(seconds=1)
notebook_id, delta_id = str(uuid.uuid4()), str(uuid.uuid4())
table = binding["lakehouseName"] + "." + (binding["schemaName"] + "." if binding["schemaName"] else "") + "c1_sources"
path = "Tables/" + (binding["schemaName"] + "/" if binding["schemaName"] else "") + "c1_sources"
context = {
    "currentWorkspaceId": binding["workspaceId"], "defaultLakehouseId": binding["lakehouseId"],
    "defaultLakehouseWorkspaceId": binding["workspaceId"], "defaultLakehouseName": binding["lakehouseName"],
    "currentNotebookId": notebook_id, "userId": binding["actorObjectId"],
    "isForPipeline": False, "isReferenceRun": False,
}
types = [("sourceId", "string"), ("productId", "string"), ("revision", "int"),
         ("state", "string"), ("releaseState", "string"), ("marker", "string")]

class Row(dict):
    def asDict(self, recursive=False):
        assert recursive
        return dict(self)

class Frame:
    def __init__(self, owner):
        self.owner, self.write = owner, self
        self.dtypes = types if not owner.options.get("schema") else types[:-1]
        self.maximum = None
    def format(self, value):
        assert value == "delta"
        return self
    def mode(self, value):
        assert value == "errorifexists"
        return self
    def saveAsTable(self, value):
        assert value == table
        self.owner.writes += 1
    def orderBy(self, *columns):
        assert columns == ("sourceId", "revision")
        return self
    def limit(self, value):
        assert value == len(binding["rows"]) + 1
        self.maximum = value
        return self
    def collect(self):
        rows = copy.deepcopy(binding["rows"])
        if self.owner.options.get("rows"):
            rows[0]["marker"] = "unreviewed"
        if self.owner.options.get("extra"):
            rows.append(rows[0])
        return [Row(row) for row in rows[:self.maximum]]

class Spark:
    version = "3.5.0-synthetic"
    def __init__(self, **options):
        self.options, self.writes = options, 0
        self.catalog = SimpleNamespace(tableExists=self.exists)
    def exists(self, name):
        assert name == table
        return self.options.get("table_exists", False)
    def createDataFrame(self, rows, ddl):
        assert rows == binding["rows"] and ddl == ", ".join(name + " " + kind for name, kind in types)
        return Frame(self)
    def table(self, name):
        assert name == table and self.writes == 1
        return Frame(self)
    def sql(self, text):
        assert text == "DESCRIBE DETAIL " + ".".join("`" + part + "`" for part in table.split("."))
        detail = {"format": "delta", "id": delta_id}
        if self.options.get("format"): detail["format"] = "parquet"
        if self.options.get("delta"): detail["id"] = str(uuid.UUID(int=0))
        return SimpleNamespace(collect=lambda: [detail])

def utilities(actual_context, existing=False):
    def exists(value):
        assert value == path
        return existing
    return SimpleNamespace(runtime=SimpleNamespace(context=actual_context), fs=SimpleNamespace(exists=exists))

result = module["materialize_source"](utilities(context), Spark(), lambda: now)
assert result["rows"] == binding["rows"] and result["notebookId"] == notebook_id
assert result["deltaTableId"] == delta_id and result["actorObjectId"] == binding["actorObjectId"]
assert result["bindingSha256"] == module["SOURCE_HASH"] and result["tableName"] == table

def rejected(actual_context=context, existing=False, expected="c1_source_context", clock=lambda: now, writes=0, **options):
    spark = Spark(**options)
    try:
        module["materialize_source"](utilities(actual_context, existing), spark, clock)
        raise AssertionError("invalid source execution accepted")
    except RuntimeError as error:
        assert str(error) == expected
    assert spark.writes == writes

for field in ("currentWorkspaceId", "defaultLakehouseId", "defaultLakehouseWorkspaceId", "userId"):
    changed = dict(context)
    changed[field] = str(uuid.uuid4())
    rejected(changed)
for field in ("isForPipeline", "isReferenceRun"):
    changed = dict(context)
    changed[field] = True
    rejected(changed)
for field in ("currentNotebookId", "defaultLakehouseName"):
    changed = dict(context)
    changed[field] = "invalid"
    rejected(changed)
rejected(table_exists=True, expected="c1_source_already_exists")
rejected(existing=True, expected="c1_source_already_exists")
rejected(clock=lambda: expiry, expected="c1_source_deadline")
rejected(clock=lambda: start - timedelta(seconds=1), expected="c1_source_deadline")
for index, count in ((1, 0), (2, 1), (3, 1)):
    ticks = iter([now] * index + [expiry])
    rejected(clock=lambda: next(ticks), expected="c1_source_deadline", writes=count)
rejected(schema=True, expected="c1_source_schema", writes=1)
rejected(rows=True, expected="c1_source_rows", writes=1)
rejected(extra=True, expected="c1_source_rows", writes=1)
rejected(format=True, expected="c1_source_table", writes=1)
rejected(delta=True, expected="c1_source_table", writes=1)
module["SOURCE_HASH"] = "0" * 64
rejected(expected="c1_source_binding")
os.environ["GITHUB_ACTIONS"] = "true"
try:
    module["main"]()
    raise AssertionError("native source execution allowed in CI")
except RuntimeError as error:
    assert str(error) == "c1_source_native_ci_forbidden"
""", new { program = plan.Program, binding = plan.Binding });
        Assert.False(run.Store.Current!.Objects.ContainsKey("materialization"));
    }

    [Fact]
    public async Task SourceJobsResumeOriginalIdsAndWaitBeforePrivateBetaReadbackForBothScopes()
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        foreach (var role in new[] { "released", "denied" })
        {
            var pending = await source.Submit(role, publicLocation: true);
            var started = run.Store.Current!.StartedAtUtc;
            var requests = run.Api.Requests.Count;
            run.Clock.Advance(TimeSpan.FromSeconds(59));
            run.Restart();
            Assert.Equal(pending.NextPollAtUtc, (await run.Provisioner.AdvanceSourceMaterialization(role, default)).NextPollAtUtc);
            Assert.Equal(requests, run.Api.Requests.Count);
            run.Clock.Advance(TimeSpan.FromSeconds(1));
            run.Api.Reply(200, source.Job(role));
            var done = await run.Provisioner.AdvanceSourceMaterialization(role, default);
            Assert.Equal("SourceReadbackObserved", done.Status);
            Assert.Equal(pending.JobId, done.JobId);
            Assert.Equal(started, run.Store.Current.StartedAtUtc);
            var request = run.Api.Requests[^1];
            Assert.Equal(FabricC1Provisioner.WorkspaceHost(source.WorkspaceId(role)), request.Uri.Host);
            Assert.EndsWith("/notebooks/" + done.NotebookId + "/jobs/execute/instances/" + done.JobId, request.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("?beta=true", request.Uri.Query);
            Assert.Equal(role == "denied", run.Store.Current.Objects.ContainsKey("materialization"));
        }
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal)));
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-grants"));
        Assert.Equal("c1_deploy_binding_incomplete", Assert.Throws<DomainException>(run.Provisioner.RequireFullBindings).Code);
    }

    [Fact]
    public async Task SourceJobThrottlingPreservesLongerOriginalMinimumWithoutAutomaticRetry()
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        await source.Submit("released");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        run.Api.Reply(429, new(), retryAfterSeconds: 90);
        Assert.Equal("c1_deploy_http_429", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        var requests = run.Api.Requests.Count;
        run.Restart();
        run.Clock.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal("Pending", (await run.Provisioner.AdvanceSourceMaterialization("released", default)).Status);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        run.Api.Reply(200, source.Job("released", "InProgress"), retryAfterSeconds: 75);
        var pending = await run.Provisioner.AdvanceSourceMaterialization("released", default);
        Assert.Equal(run.Clock.GetUtcNow().AddSeconds(75), pending.NextPollAtUtc);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unconfirmed", "c1_deploy_http_500", "c1_source_job_unknown")]
    [InlineData("missing-location", "c1_source_job_location", "c1_source_job_unknown")]
    [InlineData("foreign-location", "c1_source_job_location", "c1_source_job_unknown")]
    [InlineData("wrong-workspace", "c1_source_job_location", "c1_source_job_unknown")]
    [InlineData("missing-minimum", "c1_source_job_poll", "c1_source_job_poll")]
    [InlineData("outside-window", "c1_deploy_budget", "c1_deploy_budget")]
    public async Task SourceJobSubmissionCannotBeReplayedOrAssignedAnInventedPollingWindow(string fault, string code, string resumeCode)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        source.QueueNotebook("released");
        string? location = source.JobLocation("released");
        if (fault == "missing-location") location = null;
        if (fault == "foreign-location") location = location!.Replace("api.fabric.microsoft.com", "example.invalid", StringComparison.Ordinal);
        if (fault == "wrong-workspace") location = location!.Replace("/workspaces/" + source.WorkspaceId("released"),
            "/workspaces/" + Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        run.Api.Reply(fault == "unconfirmed" ? 500 : 202, new(), operationUrl: location,
            retryAfterSeconds: fault == "missing-minimum" ? null : fault == "outside-window" ? 600 : 60);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        var requests = run.Api.Requests.Count;
        run.Restart();
        Assert.Equal(resumeCode, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        Assert.Equal(requests, run.Api.Requests.Count);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceJobReceiptSaveUncertaintyFencesUntilOriginalJournalIsReopened()
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        source.QueueNotebook("released");
        run.Api.ReplyComputed(202, () => { run.Store.FailAfterSave = true; return new(); },
            source.JobLocation("released"), 60);
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.AdvanceSourceMaterialization("released", default));
        var requests = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Store.FailAfterSave = false;
        run.Restart();
        Assert.Equal("Pending", (await run.Provisioner.AdvanceSourceMaterialization("released", default)).Status);
        Assert.Equal(requests, run.Api.Requests.Count);
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        run.Api.Reply(200, source.Job("released"));
        Assert.Equal("SourceReadbackObserved", (await run.Provisioner.AdvanceSourceMaterialization("released", default)).Status);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-result", "c1_source_result")]
    [InlineData("duplicate-json", "c1_source_result")]
    [InlineData("rows", "c1_source_result")]
    [InlineData("actor", "c1_deploy_context")]
    [InlineData("notebook", "c1_deploy_context")]
    [InlineData("lakehouse-workspace", "c1_deploy_context")]
    [InlineData("delta", "c1_deploy_response_id")]
    [InlineData("provider-time", "c1_source_time")]
    [InlineData("script-time", "c1_source_time")]
    [InlineData("schedule", "c1_source_job_binding")]
    [InlineData("core-invoke", "c1_source_job_binding")]
    [InlineData("failure", "c1_source_result")]
    public async Task CompletedNotebookStillNeedsItsExactOriginalSourceAndActualExecutionEvidence(string mutation, string code)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        await source.Submit("released");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        var job = source.Job("released");
        var result = JsonNode.Parse(job["exitValue"]!.GetValue<string>())!.AsObject();
        if (mutation == "rows") result["rows"]![0]!["marker"] = "unreviewed";
        if (mutation == "actor") result["actorObjectId"] = Guid.NewGuid().ToString("D");
        if (mutation == "notebook") result["notebookId"] = Guid.NewGuid().ToString("D");
        if (mutation == "lakehouse-workspace") result["lakehouseWorkspaceId"] = Guid.NewGuid().ToString("D");
        if (mutation == "delta") result["deltaTableId"] = Guid.Empty.ToString("D");
        if (mutation == "script-time") result["completedAtUtc"] = run.Clock.GetUtcNow().AddMinutes(1).ToString("O");
        job["exitValue"] = Canonical.Serialize(result);
        if (mutation == "missing-result") job.Remove("exitValue");
        if (mutation == "duplicate-json") job["exitValue"] = Canonical.Serialize(result).Replace("\"scope\":\"released\"",
            "\"scope\":\"released\",\"scope\":\"released\"", StringComparison.Ordinal);
        if (mutation == "provider-time") job["startTimeUtc"] = run.Clock.GetUtcNow().AddMinutes(-2).ToString("O");
        if (mutation == "schedule") job["invokeType"] = "Scheduled";
        if (mutation == "core-invoke") job["invokeType"] = "Manual";
        if (mutation == "failure") job["failureReason"] = new JsonObject { ["message"] = "synthetic failure" };
        run.Api.Reply(200, job);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-materialization-released"));
        Assert.False(run.Store.Current.Objects.ContainsKey("materialization"));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Deduped")]
    public async Task SourceJobFailureDoesNotAdoptAnotherRunOrProduceMaterialization(string status)
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        await source.Submit("released");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        run.Api.Reply(200, source.Job("released", status));
        Assert.Equal("c1_source_job_failed", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        Assert.Equal(status, run.Store.Current!.Objects["source-notebook-job-released"].Response["observation"]!["status"]!.GetValue<string>());
        Assert.False(run.Store.Current.Objects.ContainsKey("materialization"));
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterNotebookResultCannotReplaceOriginalAcceptedTableReceipt()
    {
        using var run = new Harness(delegated: true);
        var source = new SourceNotebookHarness(run);
        await source.Submit("released");
        run.Clock.Advance(TimeSpan.FromSeconds(60));
        run.Api.Reply(200, source.Job("released"));
        await run.Provisioner.AdvanceSourceMaterialization("released", default);
        var original = run.Store.Current!.Objects["source-materialization-released"].RequestHash;
        var changed = source.Job("released");
        var result = JsonNode.Parse(changed["exitValue"]!.GetValue<string>())!;
        result["deltaTableId"] = Guid.NewGuid().ToString("D");
        changed["exitValue"] = Canonical.Serialize(result);
        run.Api.Reply(200, changed);
        Assert.Equal("c1_source_result_changed", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.AdvanceSourceMaterialization("released", default))).Code);
        Assert.Equal(original, run.Store.Current.Objects["source-materialization-released"].RequestHash);
    }

    [Fact]
    public async Task SourceAccessRequiresBothOriginalMaterializationReceiptsBeforeRequests()
    {
        using var run = new Harness(delegated: true);
        _ = new SourceNotebookHarness(run);
        Assert.Equal("c1_deploy_dependency", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Empty(run.Api.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dbo")]
    public async Task SourceAccessCreatesOnlyItsExactTableReadRoleAndClosedWorkspaceViewer(string? schema)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run, schema);
        var before = run.Api.Requests.Count;
        access.QueueBefore();
        access.QueueAfter();
        var result = await run.Provisioner.EnsureReleasedSourceAccess(default);
        var writes = run.Api.Requests.Skip(before).Where(request => request.Method != HttpMethod.Get).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.All(writes, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(FabricC1Provisioner.WorkspaceHost(access.Source.WorkspaceId("released")), request.Uri.Host);
        });
        var role = Assert.Single(writes, request => request.Uri.AbsolutePath.EndsWith("/dataAccessRoles", StringComparison.Ordinal));
        Assert.Equal("?preview=true&dataAccessRoleConflictPolicy=Abort", role.Uri.Query);
        var permissions = role.Body!["decisionRules"]![0]!["permission"]!.AsArray();
        Assert.Equal("Tables/" + (schema is null ? "" : schema + "/") + "c1_sources",
            Assert.Single(permissions, permission => permission!["attributeName"]!.GetValue<string>() == "Path")!["attributeValueIncludedIn"]![0]!.GetValue<string>());
        Assert.Equal("Read", Assert.Single(permissions, permission => permission!["attributeName"]!.GetValue<string>() == "Action")!["attributeValueIncludedIn"]![0]!.GetValue<string>());
        var member = Assert.Single(role.Body["members"]!["microsoftEntraMembers"]!.AsArray())!;
        Assert.Equal(access.PrincipalId, member["objectId"]!.GetValue<string>());
        Assert.Equal(run.Input.TenantId, member["tenantId"]!.GetValue<string>());
        Assert.Equal("Viewer", Assert.Single(writes, request => request.Uri.AbsolutePath.EndsWith("/roleAssignments", StringComparison.Ordinal)).Body!["role"]!.GetValue<string>());
        Assert.NotNull(result.Readback["workspaceAssignment"]);
        Assert.Contains("source-access-metadata", run.Store.Current!.Objects.Keys);
        Assert.DoesNotContain("source-grants", run.Store.Current.Objects.Keys);
        var firstRoleHash = run.Store.Current.Objects["source-access-role"].RequestHash;
        run.Restart();
        access.QueueBefore(existingRole: true);
        access.QueueAfter(createRole: false, createViewer: false);
        await run.Provisioner.EnsureReleasedSourceAccess(default);
        Assert.Equal(firstRoleHash, run.Store.Current.Objects["source-access-role"].RequestHash);
        Assert.Equal(2, run.Api.Requests.Skip(before).Count(request => request.Method == HttpMethod.Post));
        Assert.Equal("c1_deploy_binding_incomplete", Assert.Throws<DomainException>(run.Provisioner.RequireFullBindings).Code);
    }

    [Theory]
    [InlineData("extra-item", "c1_source_access_inventory")]
    [InlineData("foreign-sql-item", "c1_source_access_inventory")]
    [InlineData("sql-not-ready", "c1_source_access_inventory")]
    [InlineData("extra-table", "c1_source_access_inventory")]
    [InlineData("foreign-table", "c1_source_access_inventory")]
    [InlineData("source-contributor", "c1_source_workspace_roles")]
    [InlineData("denied-runtime", "c1_source_workspace_roles")]
    [InlineData("source-group", "c1_source_workspace_roles")]
    [InlineData("unowned-viewer", "c1_deploy_foreign")]
    [InlineData("default-reader-broadened", "c1_source_role_scope")]
    [InlineData("unowned-role", "c1_deploy_foreign")]
    public async Task SourceAccessRefusesMixedInventoriesAndForeignOrBroaderAccessBeforeWrites(string fault, string code)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var before = run.Api.Requests.Count;
        access.QueueBefore(fault);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.All(run.Api.Requests.Skip(before), request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.False(run.Store.Current!.Objects.ContainsKey("source-access-metadata"));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("wildcard")]
    [InlineData("member")]
    [InlineData("tenant")]
    [InlineData("missing-version")]
    public async Task SourceRoleReadbackMustMatchTheOriginalReadonlyDefinitionBeforeGrantingViewer(string fault)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var before = run.Api.Requests.Count;
        access.QueueBefore();
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () =>
        {
            var role = access.Role();
            if (fault == "write") role["decisionRules"]![0]!["permission"]![1]!["attributeValueIncludedIn"]![0] = "ReadWrite";
            if (fault == "wildcard") role["decisionRules"]![0]!["permission"]![0]!["attributeValueIncludedIn"]![0] = "*";
            if (fault == "member") role["members"]!["microsoftEntraMembers"]![0]!["objectId"] = Guid.NewGuid().ToString("D");
            if (fault == "tenant") role["members"]!["microsoftEntraMembers"]![0]!["tenantId"] = Guid.NewGuid().ToString("D");
            if (fault == "missing-version") role.Remove("eTag");
            return SourceAccessHarness.List(access.DefaultReader(), role);
        });
        Assert.Equal(fault == "missing-version" ? "c1_source_role_readback" : "c1_source_role_scope",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain("runtime-source-grant", run.Store.Current!.Objects.Keys);
        Assert.Contains("source-access-role", run.Store.Current.PendingCreates.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceRoleUnknownPostOnlyReconcilesOriginalIntentAndNeverReplays(bool roleVisible)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var before = run.Api.Requests.Count;
        access.QueueBefore();
        run.Api.Fail(new HttpRequestException("synthetic lost role response"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.EnsureReleasedSourceAccess(default));
        run.Restart();
        access.QueueBefore(existingRole: roleVisible);
        if (roleVisible)
        {
            access.QueueAfter(createRole: false);
            await run.Provisioner.EnsureReleasedSourceAccess(default);
            Assert.Contains("source-access-metadata", run.Store.Current!.Objects.Keys);
        }
        else Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Single(run.Api.Requests.Skip(before), request =>
            request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/dataAccessRoles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceRoleMutationWhileViewerIsEstablishedCannotProduceAccessMetadata()
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueBefore();
        access.QueueAfter(finalVersion: "changed-source-version");
        Assert.Equal("c1_source_role_readback", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Contains("runtime-source-grant", run.Store.Current!.Objects.Keys);
        Assert.DoesNotContain("source-access-metadata", run.Store.Current.Objects.Keys);
        Assert.Equal("source-version-1", run.Store.Current.Objects["source-access-role"].Response["role"]!["eTag"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task SourceRoleExplicitDenialIsRecordedWithoutUnknownOutcomeOrAutomaticRetry(int status)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var before = run.Api.Requests.Count;
        access.QueueBefore();
        run.Api.Reply(status, new());
        Assert.Equal("c1_deploy_http_" + status, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Contains("source-access-role", run.Store.Current!.DeniedRequests.Keys);
        Assert.DoesNotContain("source-access-role", run.Store.Current.PendingCreates.Keys);
        Assert.DoesNotContain("source-access-role", run.Store.Current.Objects.Keys);
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("deadline")]
    [InlineData("budget")]
    public async Task SourceAccessKeepsOriginalControlFencesBeforeAnyRequest(string fence)
    {
        using var run = new Harness(delegated: true);
        _ = await SourceAccessHarness.Create(run);
        var state = run.Store.Load()!;
        if (fence == "cleanup") run.Store.Save(state with { CleanupStartedAtUtc = run.Clock.GetUtcNow() });
        if (fence == "budget") run.Store.Save(state with { Requests = run.Input.MaxControlRequests!.Value });
        if (fence == "deadline") run.Clock.Advance(TimeSpan.FromMinutes(10));
        run.Restart();
        var before = run.Api.Requests.Count;
        Assert.Equal(fence == "cleanup" ? "c1_deploy_cleanup_started" : "c1_deploy_budget",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("role")]
    public async Task SourceAccessDoesNotAdoptRecreatedAssignmentsOrRoleVersions(string replaced)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueBefore();
        access.QueueAfter();
        await run.Provisioner.EnsureReleasedSourceAccess(default);
        var original = run.Store.Current!.Objects["source-access-metadata"].RequestHash;
        var before = run.Api.Requests.Count;
        run.Restart();
        access.QueueBefore(replaced == "viewer" ? "replaced-viewer" : null, existingRole: true);
        if (replaced == "role") access.QueueAfter(createRole: false, readbackVersion: "recreated-source-version");
        Assert.Equal(replaced == "viewer" ? "c1_source_access_binding" : "c1_source_role_readback",
            (await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
        Assert.All(run.Api.Requests.Skip(before), request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal(original, run.Store.Current.Objects["source-access-metadata"].RequestHash);
    }

    [Theory]
    [InlineData("released", false, false)]
    [InlineData("released", true, false)]
    [InlineData("released", true, true)]
    [InlineData("denied", false, false)]
    public async Task SourceCleanupRemovesOnlyTheOriginalMaterializedWorkspaceAndRequiresFreshAbsence(
        string scope, bool granted, bool removedViewer)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        if (granted)
        {
            access.QueueBefore();
            access.QueueAfter();
            await run.Provisioner.EnsureReleasedSourceAccess(default);
        }
        var originalStart = run.Store.Current!.StartedAtUtc;
        var before = run.Api.Requests.Count;
        access.QueueCleanupBefore(scope, removedViewer ? "removed-viewer" : null);
        run.Api.Reply(200, new());
        run.Api.Reply(404, new());
        var result = await run.Provisioner.CleanupMaterializedSourceWorkspace(scope, default);
        Assert.Equal("SourceWorkspaceInventoryAbsent", result.Status);
        Assert.False(result.CompleteC1Cleanup);
        Assert.False(result.EffectiveRevocationProven);
        var deletion = Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.Equal("/v1/workspaces/" + access.Source.WorkspaceId(scope), deletion.Uri.AbsolutePath);
        Assert.All(run.Api.Requests.Skip(before), request =>
            Assert.Equal(FabricC1Provisioner.WorkspaceHost(access.Source.WorkspaceId(scope)), request.Uri.Host));
        Assert.Equal(8, run.Store.Current.CleanupRequests);
        Assert.Equal(originalStart, run.Store.Current.StartedAtUtc);
        Assert.Equal("SourceWorkspaceAbsent", run.Store.Current.CleanupReceipts["workspace-" + scope].Kind);
        Assert.Contains("workspace-" + scope, run.Store.Current.Objects.Keys);
        var cleanupStart = run.Store.Current.CleanupStartedAtUtc;
        run.Restart();
        run.Api.Reply(404, new());
        await run.Provisioner.CleanupMaterializedSourceWorkspace(scope, default);
        Assert.Equal(cleanupStart, run.Store.Current.CleanupStartedAtUtc);
        Assert.Single(run.Api.Requests.Skip(before), request => request.Method == HttpMethod.Delete);
        Assert.Equal("c1_deploy_cleanup_started", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.EnsureReleasedSourceAccess(default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceCleanupUnknownOrDelayedDeleteNeverReplaysAndKeepsOriginalIntent(bool lostResponse)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueCleanupBefore("released");
        if (lostResponse)
        {
            run.Api.Fail(new HttpRequestException("synthetic lost workspace delete response"));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                run.Provisioner.CleanupMaterializedSourceWorkspace("released", default));
        }
        else
        {
            run.Api.Reply(200, new());
            run.Api.Reply(200, access.Workspace("released"));
            Assert.Equal("SourceWorkspaceCleanupPendingReconciliation",
                (await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default)).Status);
        }
        var started = run.Store.Current!.CleanupStartedAtUtc;
        Assert.Contains("workspace-released", run.Store.Current.PendingDeletes.Keys);
        Assert.DoesNotContain("workspace-released", run.Store.Current.CleanupReceipts.Keys);
        run.Restart();
        run.Api.Reply(200, access.Workspace("released"));
        Assert.Equal("SourceWorkspaceCleanupPendingReconciliation",
            (await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default)).Status);
        run.Api.Reply(404, new());
        Assert.Equal("SourceWorkspaceInventoryAbsent",
            (await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default)).Status);
        Assert.Equal(started, run.Store.Current.CleanupStartedAtUtc);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        Assert.DoesNotContain("workspace-released", run.Store.Current.PendingDeletes.Keys);
    }

    [Theory]
    [InlineData("workspace", "c1_deploy_foreign")]
    [InlineData("extra-item", "c1_source_access_inventory")]
    [InlineData("foreign-table", "c1_source_access_inventory")]
    [InlineData("source-group", "c1_source_workspace_roles")]
    [InlineData("role", "c1_source_role_scope")]
    public async Task SourceCleanupRejectsDriftBeforeAnyDeletion(string fault, string code)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueCleanupBefore("released", fault);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
        Assert.DoesNotContain("workspace-released", run.Store.Current!.PendingDeletes.Keys);
    }

    [Theory]
    [InlineData("creation-hash", "c1_source_cleanup_binding")]
    [InlineData("job-intent", "c1_source_job_binding")]
    [InlineData("pending-job", "c1_source_job_binding")]
    [InlineData("active-job", "c1_source_result")]
    public async Task SourceCleanupRequiresOriginalCreationAndCompletedJobWithoutProviderRequests(string fault, string code)
    {
        using var run = new Harness(delegated: true);
        await SourceAccessHarness.Create(run);
        var state = run.Store.Load()!;
        if (fault == "creation-hash")
            state.Objects["workspace-released"] = state.Objects["workspace-released"] with { RequestHash = new string('0', 64) };
        if (fault == "job-intent")
            state.Objects["source-notebook-job-released"] = state.Objects["source-notebook-job-released"] with { RequestHash = new string('0', 64) };
        if (fault == "pending-job") state.PendingCreates["source-notebook-job-released"] = state.Objects["source-notebook-job-intent-released"].RequestHash;
        if (fault == "active-job") state.Objects["source-notebook-job-released"].Response["observation"]!["status"] = "InProgress";
        run.Store.Save(state);
        run.Restart();
        var before = run.Api.Requests.Count;
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Theory]
    [InlineData("replaced-source-role", "c1_source_cleanup_binding")]
    [InlineData("unowned-source-role", "c1_deploy_foreign")]
    public async Task SourceCleanupDoesNotAdoptReplacedOrUnjournaledDataRoles(string fault, string code)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueBefore();
        access.QueueAfter();
        await run.Provisioner.EnsureReleasedSourceAccess(default);
        if (fault == "unowned-source-role")
        {
            var state = run.Store.Load()!;
            state.Objects.Remove("source-access-role");
            run.Store.Save(state);
            run.Restart();
        }
        access.QueueCleanupBefore("released", fault);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task SourceCleanupExplicitDeleteDenialIsNotAnUnknownOutcomeOrAnAbsence(int status)
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueCleanupBefore("released");
        run.Api.Reply(status, new());
        Assert.Equal("c1_deploy_http_" + status, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        Assert.Contains("cleanup-workspace-released", run.Store.Current!.DeniedRequests.Keys);
        Assert.DoesNotContain("workspace-released", run.Store.Current.PendingDeletes.Keys);
        Assert.DoesNotContain("workspace-released", run.Store.Current.CleanupReceipts.Keys);
        run.Restart();
        access.QueueCleanupBefore("released");
        run.Api.Reply(200, new());
        run.Api.Reply(404, new());
        Assert.Equal("SourceWorkspaceInventoryAbsent",
            (await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default)).Status);
        Assert.Equal(2, run.Api.Requests.Count(request => request.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task SourceCleanupCannotDeleteARestoredWorkspaceOrAnUnmaterializedOrAgentScope()
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_source_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("agent", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        run.Api.Reply(404, new());
        await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default);
        run.Api.Reply(200, access.Workspace("released"));
        Assert.Equal("c1_deploy_cleanup_restored", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        var state = run.Store.Load()!;
        state.Objects.Remove("source-materialization-denied");
        run.Store.Save(state);
        run.Restart();
        before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_dependency", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("denied", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SourceCleanupUsesTheReservedTailAfterNormalExpiryWithoutResettingCounters()
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        var original = run.Store.Load()!;
        run.Store.Save(original with { Requests = 80 });
        run.Clock.Advance(TimeSpan.FromMinutes(10));
        run.Restart();
        access.QueueCleanupBefore("released");
        run.Api.Reply(200, new());
        run.Api.Reply(404, new());
        await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default);
        Assert.Equal(88, run.Store.Current!.Requests);
        Assert.Equal(8, run.Store.Current.CleanupRequests);
        Assert.Equal(original.StartedAtUtc, run.Store.Current.StartedAtUtc);
        run.Clock.Advance(TimeSpan.FromMinutes(10));
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_cleanup_budget", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("denied", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        Assert.Equal(88, run.Store.Current.Requests);
    }

    [Fact]
    public async Task SourceCleanupUncertainIntentSaveFencesAllRequestsAndNeverReplaysDeleteAfterReopen()
    {
        using var run = new Harness(delegated: true);
        var access = await SourceAccessHarness.Create(run);
        access.QueueCleanupBefore("released");
        run.Store.AfterSave = state =>
        {
            if (state.PendingDeletes.ContainsKey("workspace-released")) run.Store.FailAfterSave = true;
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.CleanupMaterializedSourceWorkspace("released", default));
        run.Store.AfterSave = null;
        run.Store.FailAfterSave = false;
        var before = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.CleanupMaterializedSourceWorkspace("released", default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
        run.Restart();
        run.Api.Reply(200, access.Workspace("released"));
        Assert.Equal("SourceWorkspaceCleanupPendingReconciliation",
            (await run.Provisioner.CleanupMaterializedSourceWorkspace("released", default)).Status);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Delete);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedArmChildrenNeedOriginalParentAndBidirectionalReadbackWithoutTagWrites(bool network)
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network);
        QueueArmCreate(run, fixture);
        await run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default);
        var child = Assert.Single(run.Store.Current!.Objects.Values, value => value.Kind == "ArmGeneratedChild");
        Assert.Equal(fixture.Child["id"]!.GetValue<string>(), child.Id);
        Assert.Equal(fixture.Parent["id"]!.GetValue<string>(), child.Response["parentId"]!.GetValue<string>());
        Assert.False(child.Response["directDeleteAuthorized"]!.GetValue<bool>());
        Assert.False(run.Store.Current.PendingCreates.ContainsKey("arm-" + fixture.Stage));
        run.Restart();
        QueueArmResume(run, fixture);
        await run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
        Assert.DoesNotContain(run.Api.Requests, request => request.Method == HttpMethod.Patch || request.Method == HttpMethod.Delete);
        Assert.Single(run.Store.Current.Objects.Values, value => value.Kind == "ArmGeneratedChild");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedChildReverseLinkCannotPointAtAnotherParent(bool network)
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network);
        if (network) fixture.Child["properties"]!["privateEndpoint"]!["id"] = fixture.Parent["id"]!.GetValue<string>() + "-foreign";
        else fixture.Child["managedBy"] = fixture.Parent["id"]!.GetValue<string>() + "-foreign";
        QueueArmCreate(run, fixture);
        Assert.Equal("c1_deploy_arm_child_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("arm-" + fixture.Stage));
        Assert.True(run.Store.Current.PendingCreates.ContainsKey("arm-" + fixture.Stage));
        Assert.DoesNotContain(run.Store.Current.Objects.Values, value => value.Kind == "ArmGeneratedChild");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecreatedGeneratedChildIsNotAdoptedEvenAfterItReceivesMatchingTags(bool network, bool addTags)
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network);
        QueueArmCreate(run, fixture);
        await run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default);
        var original = Assert.Single(run.Store.Current!.Objects.Values, value => value.Kind == "ArmGeneratedChild");
        fixture.Child["properties"]![network ? "resourceGuid" : "uniqueId"] = Guid.NewGuid().ToString("D");
        if (addTags) fixture.Child["tags"] = FabricC1Deployment.Tags(run.Input);
        run.Restart();
        QueueArmResume(run, fixture);
        Assert.Equal("c1_deploy_arm_child_replaced", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        var retained = Assert.Single(run.Store.Current.Objects.Values, value => value.Kind == "ArmGeneratedChild");
        Assert.Equal(original.Response["instanceId"]!.GetValue<string>(), retained.Response["instanceId"]!.GetValue<string>());
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task MissingGeneratedChildIsNotTreatedAsCompletedCleanupOrRemovedFromInventory()
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: true);
        QueueArmCreate(run, fixture);
        await run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default);
        run.Restart();
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(200, fixture.Deployment);
        run.Api.Reply(200, fixture.Deployment);
        run.Api.List(fixture.Parent);
        Assert.Equal("c1_deploy_arm_inventory_incomplete", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.Single(run.Store.Current!.Objects.Values, value => value.Kind == "ArmGeneratedChild");
        Assert.Empty(run.Store.Current.CleanupReceipts);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData("mode", "c1_deploy_arm_binding")]
    [InlineData("parameters", "c1_deploy_arm_binding")]
    [InlineData("correlationId", "c1_deploy_arm_replaced")]
    [InlineData("templateHash", "c1_deploy_arm_replaced")]
    [InlineData("outputs", "c1_deploy_arm_replaced")]
    public async Task ArmResumeDoesNotTrustAReplacedDeploymentOrChangedParameters(string field, string code)
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: false);
        QueueArmCreate(run, fixture);
        await run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default);
        var changed = (JsonObject)fixture.Deployment.DeepClone();
        if (field == "parameters") changed["properties"]!["parameters"]!["synthetic"]!["value"] = "foreign";
        else if (field == "outputs") changed["properties"]!["outputs"]!["probe"]!["value"]!["properties"]!["vmId"] = Guid.NewGuid().ToString("D");
        else changed["properties"]![field] = field == "mode" ? "Complete" : Guid.NewGuid().ToString("D");
        run.Restart();
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(200, changed);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ObservedSuccessfulOutputsRemainPinnedEvenWhenChildReconciliationHasNotCompleted()
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: true);
        fixture.Child["properties"]!["privateEndpoint"]!["id"] = fixture.Parent["id"]!.GetValue<string>() + "-foreign";
        QueueArmCreate(run, fixture);
        Assert.Equal("c1_deploy_arm_child_binding", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("arm-" + fixture.Stage));
        fixture.Deployment["properties"]!["outputs"]!["privateEndpoints"]!["value"]![0]!["properties"]!["networkInterfaces"]![0]!["id"] =
            fixture.Child["id"]!.GetValue<string>() + "-replacement";
        run.Restart();
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(200, fixture.Deployment);
        Assert.Equal("c1_deploy_arm_replaced", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
        Assert.DoesNotContain(run.Store.Current.Objects.Values, value => value.Kind == "ArmGeneratedChild");
    }

    [Fact]
    public async Task LostArmCreateReconcilesWithoutReplayAndPinsTheFirstObservedOperation()
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: false);
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(404, new());
        run.Api.List();
        run.Api.Fail(new IOException("synthetic lost ARM create response"));
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default));
        Assert.True(run.Store.Current!.PendingCreates.ContainsKey("arm-" + fixture.Stage));
        run.Restart();
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(200, fixture.Deployment);
        var replaced = (JsonObject)fixture.Deployment.DeepClone();
        replaced["properties"]!["correlationId"] = Guid.NewGuid().ToString("D");
        run.Api.Reply(200, replaced);
        Assert.Equal("c1_deploy_arm_replaced", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.Single(run.Api.Requests, request => request.Method == HttpMethod.Put);
        Assert.False(run.Store.Current.Objects.ContainsKey("arm-" + fixture.Stage));
    }

    [Fact]
    public async Task UnrelatedUntaggedResourceBlocksNewArmSubmissionAndIsNeverRetagged()
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: false);
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(404, new());
        run.Api.List(new JsonObject
        {
            ["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Storage/storageAccounts/foreign",
            ["type"] = "Microsoft.Storage/storageAccounts", ["location"] = run.Input.Location
        });
        Assert.Equal("c1_deploy_arm_untagged", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.False(run.Store.Current!.PendingCreates.ContainsKey("arm-" + fixture.Stage));
    }

    [Theory]
    [InlineData("vmId", "c1_deploy_arm_parent_replaced")]
    [InlineData("location", "c1_deploy_arm_child_scope")]
    [InlineData("shared", "c1_deploy_arm_child_binding")]
    public async Task GeneratedDiskCannotOutliveItsRecordedVmInstanceRegionOrExclusiveAttachment(string drift, string code)
    {
        using var run = new Harness();
        var fixture = ArmFixture(run, network: false);
        if (drift == "vmId") fixture.Parent["properties"]!["vmId"] = Guid.NewGuid().ToString("D");
        else if (drift == "location") fixture.Parent["location"] = "swedencentral";
        else fixture.Child["managedByExtended"] = new JsonArray(fixture.Parent["id"]!.GetValue<string>() + "-other");
        QueueArmCreate(run, fixture);
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm(fixture.Stage, fixture.Template, fixture.Parameters, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("arm-" + fixture.Stage));
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
    [InlineData("applications", "federatedIdentityCredentials")]
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
    public async Task PrivateCleanupMayReadButNeverCreateFederatedIdentityCredentials()
    {
        using var run = new Harness(delegated: true);
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var credential = new BoundCredential(Token(run.Input, run.Input.ControlTokenBindings!.GraphAudience!,
            run.Clock.GetUtcNow(), "Application.ReadWrite.All"));
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>
        { ["graph.microsoft.com"] = client }, run.Clock);
        var uri = new Uri($"https://graph.microsoft.com/v1.0/applications/{Guid.NewGuid():D}/federatedIdentityCredentials");
        Assert.Equal(200, (await api.Send(HttpMethod.Get, uri)).Status);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("c1_deploy_graph_scope", (await Assert.ThrowsAsync<DomainException>(() =>
            api.Send(HttpMethod.Post, uri, new()))).Code);
        Assert.Equal(1, handler.Requests);
        Assert.Single(credential.Scopes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LazyPrivateClientUsesOnlyTheBoundHostAndNeverFallsBackWhenAbsent(bool available)
    {
        using var run = new Harness(delegated: true);
        using var handler = new JsonHandler();
        using var client = new HttpClient(handler);
        var credential = new BoundCredential(Token(run.Input, run.Input.BootstrapIdentity!.ExpectedAudience!,
            run.Clock.GetUtcNow(), "Workspace.ReadWrite.All"));
        var host = FabricC1Provisioner.WorkspaceHost(Guid.NewGuid().ToString("D"));
        var calls = new List<string>();
        var api = new FabricC1DeploymentHttp(run.Input, credential, new Dictionary<string, HttpClient>(), run.Clock,
            privateClientFactory: value => { calls.Add(value); return available ? client : null; });
        var uri = new Uri($"https://{host}/v1/operations/{Guid.NewGuid():D}");
        if (available)
        {
            Assert.Equal(200, (await api.Send(HttpMethod.Get, uri)).Status);
            Assert.Single(credential.Scopes);
            Assert.Equal(1, handler.Requests);
        }
        else
        {
            Assert.Equal("c1_deploy_private_route", (await Assert.ThrowsAsync<DomainException>(() =>
                api.Send(HttpMethod.Get, uri))).Code);
            Assert.Empty(credential.Scopes);
            Assert.Equal(0, handler.Requests);
        }
        Assert.Equal(host, Assert.Single(calls));
    }

    [Fact]
    public void FileJournalRoundTripAndCompareExchangeDoNotResetPendingCreates()
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
                using var second = new FabricC1DeploymentFileJournal(path);
                var same = Assert.IsType<FabricC1DeploymentJournal>(second.Load());
                second.Save(same with { Requests = 4 });
                Assert.Equal("c1_deploy_journal_conflict", Assert.Throws<DomainException>(() => file.Save(state)).Code);
            }
            using (var resumed = new FabricC1DeploymentFileJournal(path))
            {
                var loaded = Assert.IsType<FabricC1DeploymentJournal>(resumed.Load());
                Assert.Equal(4, loaded.Requests);
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

    private sealed record ArmCase(string Stage, JsonObject Template, JsonObject Parameters, JsonObject Group,
        JsonObject Deployment, JsonObject Parent, JsonObject Child);

    private static ArmCase ArmFixture(Harness run, bool network)
    {
        var groupId = FabricC1Deployment.GroupId(run.Input);
        var stage = network ? "private-endpoints" : "foundation";
        var parentType = network ? "Microsoft.Network/privateEndpoints" : "Microsoft.Compute/virtualMachines";
        var childType = network ? "Microsoft.Network/networkInterfaces" : "Microsoft.Compute/disks";
        var parentId = groupId + "/providers/" + parentType + "/" + FabricC1Deployment.Name(run.Input) + (network ? "-agent-pe" : "-probe");
        var childId = groupId + "/providers/" + childType + "/synthetic-generated-child";
        var group = new JsonObject { ["id"] = groupId, ["tags"] = FabricC1Deployment.Tags(run.Input) };
        run.Seed("resource-group", "ResourceGroup", group);
        var properties = network ? new JsonObject
        {
            ["networkInterfaces"] = new JsonArray(new JsonObject { ["id"] = childId })
        } : new JsonObject
        {
            ["vmId"] = Guid.NewGuid().ToString("D"),
            ["storageProfile"] = new JsonObject { ["osDisk"] = new JsonObject
            { ["deleteOption"] = "Delete", ["managedDisk"] = new JsonObject { ["id"] = childId } } }
        };
        var parent = new JsonObject
        {
            ["id"] = parentId, ["type"] = parentType, ["location"] = run.Input.Location,
            ["tags"] = FabricC1Deployment.Tags(run.Input), ["properties"] = properties
        };
        var child = new JsonObject
        {
            ["id"] = childId, ["type"] = childType, ["location"] = run.Input.Location,
            ["properties"] = new JsonObject { [network ? "resourceGuid" : "uniqueId"] = Guid.NewGuid().ToString("D") }
        };
        if (network) child["properties"]!["privateEndpoint"] = new JsonObject { ["id"] = parentId };
        else child["managedBy"] = parentId;
        var parameters = new JsonObject { ["synthetic"] = new JsonObject { ["value"] = "owned" } };
        var outputs = new JsonObject
        {
            [network ? "privateEndpoints" : "probe"] = new JsonObject
            { ["value"] = network ? new JsonArray(parent.DeepClone()) : parent.DeepClone() }
        };
        var deployment = new JsonObject
        {
            ["id"] = groupId + "/providers/Microsoft.Resources/deployments/c1-" + stage,
            ["properties"] = new JsonObject
            {
                ["mode"] = "Incremental", ["parameters"] = parameters.DeepClone(), ["templateHash"] = "synthetic-provider-hash",
                ["correlationId"] = Guid.NewGuid().ToString("D"), ["provisioningState"] = "Succeeded", ["outputs"] = outputs
            }
        };
        return new(stage, new JsonObject { ["resources"] = new JsonArray() }, parameters, group, deployment, parent, child);
    }

    private static void QueueArmCreate(Harness run, ArmCase fixture)
    {
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(404, new());
        run.Api.List();
        run.Api.Reply(202, new());
        run.Api.Reply(200, fixture.Deployment);
        run.Api.List(fixture.Parent, fixture.Child);
        run.Api.Reply(200, fixture.Parent);
        run.Api.Reply(200, fixture.Child);
    }

    private static void QueueArmResume(Harness run, ArmCase fixture)
    {
        run.Api.Reply(200, fixture.Group);
        run.Api.Reply(200, fixture.Deployment);
        run.Api.Reply(200, fixture.Deployment);
        run.Api.List(fixture.Parent, fixture.Child);
        run.Api.Reply(200, fixture.Parent);
        run.Api.Reply(200, fixture.Child);
    }

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

    private sealed record VpnCase(JsonObject App, JsonObject Principal, JsonObject Assignment);

    private static JsonObject NetworkDeploymentFixture(Harness run)
    {
        var resources = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Network/";
        var name = FabricC1Deployment.Name(run.Input);
        return new()
        {
            ["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Resources/deployments/c1-network-access",
            ["properties"] = new JsonObject
            {
                ["provisioningState"] = "Succeeded", ["outputs"] = new JsonObject
                {
                    ["firewall"] = new JsonObject { ["value"] = new JsonObject
                    {
                        ["id"] = resources + "azureFirewalls/" + name + "-firewall", ["type"] = "Microsoft.Network/azureFirewalls",
                        ["location"] = run.Input.Location, ["tags"] = FabricC1Deployment.Tags(run.Input),
                        ["properties"] = new JsonObject
                        {
                            ["sku"] = new JsonObject { ["name"] = "AZFW_VNet", ["tier"] = "Standard" },
                            ["provisioningState"] = "Succeeded",
                            ["autoscaleConfiguration"] = new JsonObject { ["minCapacity"] = 2, ["maxCapacity"] = 2 },
                            ["firewallPolicy"] = new JsonObject { ["id"] = resources + "firewallPolicies/" + name + "-firewall-policy" },
                            ["ipConfigurations"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject
                            {
                                ["privateIPAddress"] = "10.241.5.4",
                                ["subnet"] = new JsonObject { ["id"] = resources + "virtualNetworks/" + name + "-vnet/subnets/AzureFirewallSubnet" },
                                ["publicIPAddress"] = new JsonObject { ["id"] = resources + "publicIPAddresses/" + name + "-firewall-ip" }
                            }})
                        }
                    }}
                }
            }
        };
    }

    private static FabricC1GraphRights VpnRights() => new(true, ["Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All"]);

    private static VpnCase QueueOperatorVpn(Harness run, bool lostAssignment = false, bool lostIdentifier = false)
    {
        var app = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = Guid.NewGuid().ToString("D") };
        var principal = new JsonObject { ["id"] = Guid.NewGuid().ToString("D") };
        var assignment = new JsonObject
        {
            ["id"] = "synthetic-opaque-assignment", ["resourceId"] = principal["id"]!.GetValue<string>(),
            ["principalId"] = run.Input.Network!.OperatorAccess!.OperatorObjectId,
            ["appRoleId"] = Guid.Empty.ToString("D"), ["principalType"] = "User"
        };
        var result = new VpnCase(app, principal, assignment);
        JsonObject EchoCreated(JsonObject instance)
        {
            foreach (var field in run.Api.Requests[^1].Body!) instance[field.Key] = field.Value?.DeepClone();
            return instance;
        }
        run.Api.List();
        run.Api.ReplyComputed(201, () => EchoCreated(app));
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.List();
        run.Api.ReplyComputed(201, () => EchoCreated(principal));
        if (lostIdentifier)
        {
            run.Api.Fail(new HttpRequestException("synthetic lost VPN identifier response"));
            return result;
        }
        run.Api.Reply(204, new());
        run.Api.ReplyComputed(200, () =>
        {
            app["identifierUris"] = new JsonArray("api://" + app["appId"]!.GetValue<string>());
            return app;
        });
        run.Api.List();
        if (lostAssignment) run.Api.Fail(new HttpRequestException("synthetic lost VPN assignment response"));
        else
        {
            run.Api.Reply(201, assignment);
            run.Api.List(assignment);
        }
        return result;
    }

    private static void QueueOperatorVpnResume(Harness run, VpnCase fixture)
    {
        run.Api.Reply(200, fixture.App);
        run.Api.List(new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId });
        run.Api.Reply(200, fixture.Principal);
    }

    private static JsonObject VpnAssignment(JsonObject principal, string user, int index) => new()
    {
        ["id"] = "synthetic-opaque-assignment-" + index, ["resourceId"] = principal["id"]!.GetValue<string>(),
        ["principalId"] = user, ["appRoleId"] = Guid.Empty.ToString("D"), ["principalType"] = "User"
    };

    private static JsonObject[] QueueAdditionalVpnUsers(Harness run, VpnCase fixture, bool loseLast = false)
    {
        var assignments = new List<JsonObject> { fixture.Assignment };
        var users = run.Input.Network!.OperatorAccess!.VpnUsers!.ObjectIds!;
        for (var index = 1; index < users.Length; index++)
        {
            assignments.Add(VpnAssignment(fixture.Principal, users[index], index));
            if (loseLast && index == users.Length - 1) run.Api.Fail(new HttpRequestException("synthetic lost fourth-user assignment"));
            else
            {
                run.Api.Reply(201, assignments[^1]);
                run.Api.List(assignments.ToArray());
            }
        }
        return assignments.ToArray();
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
        public System.Net.Http.Headers.RetryConditionHeaderValue? RetryAfter { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent("{}") };
            response.Headers.RetryAfter = RetryAfter;
            return Task.FromResult(response);
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
    private sealed class Clock(DateTimeOffset now, bool manualTimers = false) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public DateTimeOffset[] PendingTimers
        {
            get { lock (timers) return timers.Where(timer => timer.DueAt is not null).Select(timer => timer.DueAt!.Value).ToArray(); }
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (timers)
            {
                now += elapsed;
                ready = timers.Where(timer => timer.DueAt is { } due && due <= now).ToArray();
            }
            foreach (var timer in ready) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!manualTimers) return base.CreateTimer(callback, state, dueTime, period);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class ManualTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public DateTimeOffset? DueAt { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (owner.timers)
                {
                    if (disposed) return false;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow().Add(dueTime);
                    if (!owner.timers.Contains(this)) owner.timers.Add(this);
                    return true;
                }
            }
            public void Fire()
            {
                lock (owner.timers)
                {
                    if (disposed || DueAt is not { } due || due > owner.GetUtcNow()) return;
                    DueAt = null;
                }
                callback(state);
            }
            public void Dispose()
            {
                lock (owner.timers) { disposed = true; owner.timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed record Request(HttpMethod Method, Uri Uri, JsonObject? Body, string? ETag);
    private sealed class FakeApi : IFabricC1DeploymentApi
    {
        private readonly Queue<Func<FabricC1DeploymentReply>> replies = new();
        public List<Request> Requests { get; } = [];
        public Func<Request, FabricC1DeploymentReply>? Responder { get; set; }
        public void Reply(int status, JsonObject body, string? etag = null, string? operationUrl = null, int? retryAfterSeconds = null) =>
            replies.Enqueue(() => new(status, (JsonObject)body.DeepClone(), etag, operationUrl) { RetryAfterSeconds = retryAfterSeconds });
        public void ReplyComputed(int status, Func<JsonObject> body, string? operationUrl = null, int? retryAfterSeconds = null) =>
            replies.Enqueue(() => new(status, (JsonObject)body().DeepClone(), OperationUrl: operationUrl) { RetryAfterSeconds = retryAfterSeconds });
        public void List(params JsonObject[] items) =>
            Reply(200, new JsonObject { ["value"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()) });
        public void Fail(Exception error) => replies.Enqueue(() => throw error);
        public Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null, string? ifMatch = null, CancellationToken ct = default)
        {
            var request = new Request(method, uri, (JsonObject?)body?.DeepClone(), ifMatch);
            Requests.Add(request);
            return Task.FromResult(Responder is null ? replies.Dequeue()() : Responder(request));
        }
    }
    private sealed class MemoryJournal : IFabricC1DeploymentJournal
    {
        public FabricC1DeploymentJournal? Current;
        public bool FailSave;
        public bool FailAfterSave;
        public Action<FabricC1DeploymentJournal>? AfterSave;
        public FabricC1DeploymentJournal? Load() => Current is null ? null :
            JsonSerializer.Deserialize<FabricC1DeploymentJournal>(JsonSerializer.Serialize(Current, Canonical.Json), Canonical.Json);
        public void Save(FabricC1DeploymentJournal state)
        {
            if (FailSave) throw new IOException("synthetic journal failure");
            Current = JsonSerializer.Deserialize<FabricC1DeploymentJournal>(JsonSerializer.Serialize(state, Canonical.Json), Canonical.Json);
            AfterSave?.Invoke(Current!);
            if (FailAfterSave) throw new IOException("synthetic lost journal acknowledgement");
        }
    }

    private sealed class SeedHarness : IDisposable
    {
        private readonly Harness run;
        private readonly HttpClient client;
        public FabricC1ExecutionSettings Execution { get; }
        public FabricC1BlobCheckpointBinding Binding { get; }
        public BoundCredential Credential { get; }
        public ConditionalBlobHandler Handler { get; } = new();
        public SeedHarness(Harness run, int allocated = 40)
        {
            this.run = run;
            (Execution, Binding) = CheckpointFixture(run);
            Binding = Binding with { AllocatedRequests = allocated };
            Credential = new(Token(run.Input with { BootstrapIdentity = Binding.Identity },
                Binding.Identity.ExpectedAudience!, run.Clock.GetUtcNow()));
            client = new(Handler);
        }
        public Task<FabricC1CheckpointSeedReceipt> Materialize(CancellationToken ct = default) =>
            run.Provisioner.MaterializeBlobCheckpointWithTransport(Execution, Binding, client, Credential, ct);
        public void Dispose() => client.Dispose();
    }

    private sealed class ArtifactHarness : IDisposable
    {
        public const string ReceiverTool = "# synthetic receiver tool, never executed\n";
        public const string VerifierTool = "# synthetic verifier tool, never executed\n";
        private readonly Harness run;
        private readonly HttpClient client;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "cohors-artifact-" + Guid.NewGuid().ToString("N"));
        public byte[] Bytes { get; private set; } = Encoding.UTF8.GetBytes("synthetic transport bytes, not a published deployment package");
        public string ManagedIdentityClientId { get; } = Guid.NewGuid().ToString("D");
        public JsonObject Provenance { get; }
        public JsonObject Storage { get; }
        public JsonObject Container { get; }
        public JsonObject Probe { get; }
        public FabricC1ArtifactPublicationBinding Binding { get; set; }
        public BoundCredential Credential { get; set; }
        public ConditionalBlobHandler Handler { get; } = new() { ContentMediaType = "application/octet-stream" };

        public ArtifactHarness(Harness run)
        {
            this.run = run;
            var (_, checkpoint) = CheckpointFixture(run);
            System.IO.Directory.CreateDirectory(Directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var archive = Path.Combine(Directory, "transport.zip");
            File.WriteAllBytes(archive, Bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Provenance = new()
            {
                ["contract"] = "cohors-fabric-c1-download-1", ["status"] = "VerifiedNotExecuted",
                ["repository"] = "micnhs/AMA-05-agentic-PMS-02", ["branch"] = "micnhs-cohors-fabric-umsetzung",
                ["workflow"] = ".github/workflows/cohors-fabric-contracts.yml", ["sourceCommit"] = run.Input.SourceCommit,
                ["runId"] = "123", ["runAttempt"] = "1", ["artifactId"] = "456", ["artifactName"] = "cohors-fabric-c1-123-1",
                ["outerArchiveSha256"] = Convert.ToHexStringLower(SHA256.HashData(Bytes)), ["outerArchiveLength"] = Bytes.Length,
                ["innerArchiveSha256"] = new string('e', 64), ["expiresAtUtc"] = run.Clock.GetUtcNow().AddHours(1),
                ["transportArchiveName"] = "transport.zip"
            };
            Binding = new("cohors-fabric-c1-artifact-publication-1", Canonical.Hash(run.Input), checkpoint.StorageAccountResourceId,
                ["10.0.0.6"], new string('d', 64), "", "https://storage.azure.com/", $"https://sts.windows.net/{run.Input.TenantId}/",
                120, run.Clock.GetUtcNow().AddMinutes(4));
            WriteProvenance();
            var foundation = (JsonObject)run.Store.Current!.Objects["arm-foundation"].Response.DeepClone();
            foundation["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Resources/deployments/c1-foundation";
            Storage = (JsonObject)foundation["properties"]!["outputs"]!["storage"]!["value"]!;
            Storage["properties"] = new JsonObject
            {
                ["publicNetworkAccess"] = "Disabled", ["allowBlobPublicAccess"] = false, ["allowSharedKeyAccess"] = false,
                ["supportsHttpsTrafficOnly"] = true, ["minimumTlsVersion"] = "TLS1_2",
                ["networkAcls"] = new JsonObject { ["bypass"] = "None", ["defaultAction"] = "Deny" }
            };
            Container = new()
            {
                ["id"] = checkpoint.StorageAccountResourceId + "/blobServices/default/containers/c1-artifacts",
                ["type"] = "Microsoft.Storage/storageAccounts/blobServices/containers",
                ["properties"] = new JsonObject { ["publicAccess"] = "None" }
            };
            foundation["properties"]!["outputs"]!["artifactContainer"] = new JsonObject { ["value"] = Container.DeepClone() };
            Probe = new()
            {
                ["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Compute/virtualMachines/" +
                    FabricC1Deployment.Name(run.Input) + "-probe",
                ["type"] = "Microsoft.Compute/virtualMachines", ["location"] = run.Input.Location,
                ["tags"] = FabricC1Deployment.Tags(run.Input),
                ["identity"] = new JsonObject
                {
                    ["type"] = "SystemAssigned", ["tenantId"] = run.Input.TenantId, ["principalId"] = Guid.NewGuid().ToString("D")
                },
                ["properties"] = new JsonObject
                {
                    ["vmId"] = Guid.NewGuid().ToString("D"), ["provisioningState"] = "Succeeded",
                    ["storageProfile"] = new JsonObject { ["osDisk"] = new JsonObject { ["osType"] = "Linux" } },
                    ["osProfile"] = new JsonObject
                    {
                        ["allowExtensionOperations"] = true,
                        ["linuxConfiguration"] = new JsonObject { ["provisionVMAgent"] = true }
                    }
                }
            };
            foundation["properties"]!["outputs"]!["probe"] = new JsonObject { ["value"] = Probe.DeepClone() };
            run.Seed("arm-foundation", "ArmDeployment", foundation);
            Credential = new(Token(run.Input, Binding.StorageAudience, run.Clock.GetUtcNow(), "user_impersonation"));
            client = new(Handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public void WriteProvenance()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Provenance, Canonical.Json);
            var path = Path.Combine(Directory, "download-provenance.json");
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Binding = Binding with { DownloadProvenanceSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        }

        public void QueueTarget()
        {
            run.Api.Reply(200, Storage);
            run.Api.Reply(200, Container);
        }

        public Task<FabricC1ArtifactPublicationReceipt> Publish() =>
            run.Provisioner.PublishPrivateArtifactWithTransport(Binding, Directory, client, Credential, default);

        public FabricC1StorageAccessResource[] PlanAccess() => run.Provisioner.PlanPrivateArtifactAccess(Binding, Directory);
        public Task<FabricC1ProvisionedObject[]> EnsureAccess() => run.Provisioner.EnsurePrivateArtifactAccess(Binding, Directory, default);
        public void QueueAccessTarget()
        {
            QueueTarget();
            run.Api.Reply(200, Probe);
        }

        public void UseStageArchive(string? receiver = ReceiverTool)
        {
            static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> entries)
            {
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var (name, bytes) in entries)
                    {
                        using var stream = zip.CreateEntry(name).Open();
                        stream.Write(bytes);
                    }
                return buffer.ToArray();
            }
            var tools = new List<(string, byte[])>
            { ("assets/scripts/fabric/c1_package.py", Encoding.UTF8.GetBytes(VerifierTool)) };
            if (receiver is not null) tools.Add(("assets/scripts/fabric/c1_private_stage.py", Encoding.UTF8.GetBytes(receiver)));
            var inner = Zip(tools);
            Bytes = Zip([("package.zip", inner), ("archive.json", Encoding.UTF8.GetBytes("{}"))]);
            File.WriteAllBytes(Path.Combine(Directory, "transport.zip"), Bytes);
            Provenance["outerArchiveSha256"] = Convert.ToHexStringLower(SHA256.HashData(Bytes));
            Provenance["outerArchiveLength"] = Bytes.Length;
            Provenance["innerArchiveSha256"] = Convert.ToHexStringLower(SHA256.HashData(inner));
            WriteProvenance();
        }

        public JsonObject ManagedIdentity() => new()
        {
            ["id"] = Probe["identity"]!["principalId"]!.GetValue<string>(), ["appId"] = ManagedIdentityClientId,
            ["servicePrincipalType"] = "ManagedIdentity", ["accountEnabled"] = true,
            ["alternativeNames"] = new JsonArray(Probe["id"]!.GetValue<string>())
        };

        public void QueueStageIdentity()
        {
            run.Api.Reply(200, Probe);
            run.Api.Reply(200, ManagedIdentity());
        }

        public Task<FabricC1ArtifactStageBundle> PrepareStage() =>
            run.Provisioner.PreparePrivateArtifactStage(Binding, Directory, 60, default);

        public static JsonObject GuestAgent() => new()
        {
            ["vmAgent"] = new JsonObject
            {
                ["vmAgentVersion"] = "2.4.0.2",
                ["statuses"] = new JsonArray(new JsonObject { ["code"] = "ProvisioningState/succeeded" })
            },
            ["statuses"] = new JsonArray(new JsonObject { ["code"] = "PowerState/running" })
        };

        public void QueueGuestAgent()
        {
            run.Api.Reply(200, Probe);
            run.Api.Reply(200, GuestAgent());
        }

        public Task<FabricC1ArtifactDeliveryResult> Deliver() =>
            run.Provisioner.AdvancePrivateArtifactDelivery(Binding, Directory, 60, default);

        public JsonObject Command(string execution, bool defaultStart = false)
        {
            var stage = run.Store.Current!.Objects["private-artifact-stage-plan"];
            var intent = run.Store.Current.Objects["private-artifact-stage-command-intent"];
            var profile = stage.Response["profile"]!;
            var allowance = (JsonObject)stage.Response["allowance"]!.DeepClone();
            allowance["journalReceiptSha256"] = Canonical.Hash(stage);
            var result = new JsonObject
            {
                ["status"] = "VerifiedNotExecuted", ["contract"] = "cohors-fabric-c1-private-stage-1",
                ["sourceCommit"] = profile["sourceCommit"]!.DeepClone(), ["runId"] = profile["runId"]!.DeepClone(),
                ["runAttempt"] = profile["runAttempt"]!.DeepClone(), ["files"] = 2,
                ["profileSha256"] = allowance["profileSha256"]!.DeepClone(),
                ["archiveSha256"] = Provenance["innerArchiveSha256"]!.DeepClone(),
                ["outerArchiveSha256"] = profile["outerArchiveSha256"]!.DeepClone(),
                ["httpRequests"] = 2, ["retries"] = 0, ["payloadExecuted"] = false,
                ["invocationId"] = allowance["invocationId"]!.DeepClone(),
                ["journalReceiptSha256"] = allowance["journalReceiptSha256"]!.DeepClone(),
                ["allowanceSha256"] = Canonical.HashText(Canonical.Serialize(allowance) + "\n"),
                ["privateTransport"] = new JsonObject
                {
                    ["resolvedAddresses"] = profile["privateAddresses"]!.DeepClone(),
                    ["connectedAddress"] = profile["privateAddresses"]![0]!.DeepClone()
                }
            };
            var command = (JsonObject)intent.Response["request"]!.DeepClone();
            command["id"] = intent.Id;
            command["type"] = "Microsoft.Compute/virtualMachines/runCommands";
            command["properties"]!["provisioningState"] = "Succeeded";
            command["properties"]!["instanceView"] = new JsonObject
            {
                ["executionState"] = execution, ["exitCode"] = 0, ["error"] = "",
                ["startTime"] = defaultStart ? "0001-01-01T00:00:00Z" : allowance["startedAtUtc"]!.GetValue<string>(),
                ["endTime"] = allowance["startedAtUtc"]!.DeepClone(), ["output"] = Canonical.Serialize(result)
            };
            return command;
        }

        public void Dispose()
        {
            client.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class ConditionalBlobHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? Content;
        public bool LoseAfterPut;
        public bool LoseBeforePut;
        public bool RejectPut;
        public bool RacePut;
        public bool WeakEtag;
        public string ETag = "\"seed-version-1\"";
        public string ContentMediaType = "application/json";
        public bool BlockBody;
        public string MissingCode = "BlobNotFound";
        public Action? AfterRead;
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("2023-11-03", request.Headers.GetValues("x-ms-version").Single());
            Assert.Equal("", request.RequestUri!.Query);
            HttpResponseMessage Reply(HttpStatusCode code) => new(code)
            {
                RequestMessage = request, Content = new ByteArrayContent([]),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag, WeakEtag) }
            };
            if (request.Method == HttpMethod.Get)
            {
                if (Content is null)
                {
                    var missing = Reply(HttpStatusCode.NotFound);
                    missing.Headers.Add("x-ms-error-code", MissingCode);
                    return missing;
                }
                var response = Reply(HttpStatusCode.OK);
                response.Content = BlockBody ? new StreamContent(new BlockedBlobBody(BodyStarted)) : new ByteArrayContent(Content);
                response.Content.Headers.ContentType = new(ContentMediaType);
                response.Content.Headers.ContentLength = Content.Length;
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
                foreach (var header in Metadata) response.Headers.Add(header.Key, header.Value);
                AfterRead?.Invoke();
                return response;
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("*", request.Headers.IfNoneMatch.Single().Tag);
            Assert.Empty(request.Headers.IfMatch);
            Assert.Equal("BlockBlob", request.Headers.GetValues("x-ms-blob-type").Single());
            if (RejectPut) { RejectPut = false; return Reply(HttpStatusCode.Forbidden); }
            if (LoseBeforePut) { LoseBeforePut = false; throw new IOException("synthetic unknown create before service effect"); }
            if (RacePut)
            {
                RacePut = false;
                Content = await request.Content!.ReadAsByteArrayAsync(ct);
                return Reply(HttpStatusCode.PreconditionFailed);
            }
            if (Content is not null) return Reply(HttpStatusCode.PreconditionFailed);
            Content = await request.Content!.ReadAsByteArrayAsync(ct);
            foreach (var header in request.Headers.Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)))
                Metadata.Add(header.Key, header.Value.Single());
            if (LoseAfterPut) { LoseAfterPut = false; throw new IOException("synthetic lost create acknowledgement"); }
            return Reply(HttpStatusCode.Created);
        }
    }

    private sealed class BlockedBlobBody(TaskCompletionSource started) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The read must be cancelled.");
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            WaitForCancellation(cancellationToken);
        private async Task WaitForCancellation(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private static JsonObject CheckpointAccessResponse(FabricC1CheckpointAccessResource resource) => StorageAccessResponse(resource);

    private static JsonObject StorageAccessResponse(FabricC1StorageAccessResource resource)
    {
        var response = (JsonObject)resource.Request.DeepClone();
        response["id"] = resource.Id;
        response["name"] = resource.Id.Split('/')[^1];
        response["type"] = "Microsoft.Authorization/" + (resource.Kind.EndsWith("StorageRole", StringComparison.Ordinal) ? "roleDefinitions" : "roleAssignments");
        response["properties"]!["createdOn"] = "2026-11-18T12:00:00Z";
        if (resource.Kind.EndsWith("StorageGrant", StringComparison.Ordinal))
            response["properties"]!["scope"] = resource.Id[..resource.Id.LastIndexOf("/providers/", StringComparison.Ordinal)];
        return response;
    }

    private static async Task<FabricC1CheckpointAccessResource[]> CreateCheckpointAccessForCleanup(Harness run)
    {
        var (execution, direct, hosted, control) = CheckpointRuntimes(run);
        var plan = run.Provisioner.PlanBlobCheckpointAccess(execution, direct, hosted, control);
        foreach (var resource in plan)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, CheckpointAccessResponse(resource));
            run.Api.Reply(200, CheckpointAccessResponse(resource));
        }
        await run.Provisioner.EnsureBlobCheckpointAccess(execution, direct, hosted, control, default);
        return [plan[1], plan[2], plan[0]];
    }

    private static void QueueCheckpointAccessRemoval(Harness run, IEnumerable<FabricC1CheckpointAccessResource> resources) =>
        QueueStorageAccessRemoval(run, resources);

    private static void QueueStorageAccessRemoval(Harness run, IEnumerable<FabricC1StorageAccessResource> resources)
    {
        foreach (var resource in resources)
        {
            run.Api.Reply(200, StorageAccessResponse(resource));
            run.Api.Reply(200, StorageAccessResponse(resource));
            run.Api.Reply(404, new());
        }
    }

    private static async Task<FabricC1StorageAccessResource[]> CreateArtifactAccessForCleanup(Harness run, ArtifactHarness artifact)
    {
        var resources = artifact.PlanAccess();
        artifact.QueueAccessTarget();
        foreach (var resource in resources)
        {
            run.Api.Reply(404, new());
            run.Api.Reply(201, StorageAccessResponse(resource));
            run.Api.Reply(200, StorageAccessResponse(resource));
        }
        await artifact.EnsureAccess();
        return [resources[2], resources[3], resources[0], resources[1]];
    }

    private static async Task PublishArtifactForStage(Harness run, ArtifactHarness artifact)
    {
        await CreateArtifactAccessForCleanup(run, artifact);
        artifact.QueueTarget();
        await artifact.Publish();
    }

    private static async Task PrepareArtifactDelivery(Harness run, ArtifactHarness artifact)
    {
        artifact.UseStageArchive();
        await PublishArtifactForStage(run, artifact);
        artifact.QueueStageIdentity();
        await artifact.PrepareStage();
    }

    private static async Task CompleteArtifactDelivery(Harness run, ArtifactHarness artifact)
    {
        await PrepareArtifactDelivery(run, artifact);
        artifact.QueueGuestAgent();
        run.Api.Reply(404, new());
        run.Api.Reply(201, new());
        run.Api.ReplyComputed(200, () => artifact.Command("Succeeded"));
        Assert.True((await artifact.Deliver()).ArtifactVerified);
    }

    private static (FabricC1ExecutionSettings Execution, FabricC1BlobCheckpointBinding Direct,
        FabricC1BlobCheckpointBinding Hosted, FabricC1BlobCheckpointBinding Control) CheckpointRuntimes(Harness run)
    {
        var (execution, original) = CheckpointFixture(run);
        var originalHosted = execution.Hosted!;
        var controlIdentity = originalHosted.Caller with
        { ClientId = Guid.NewGuid().ToString("D"), PrincipalObjectId = Guid.NewGuid().ToString("D") };
        execution = execution with { Hosted = originalHosted with { Caller = controlIdentity } };
        var direct = original with { ExecutionBindingHash = Canonical.Hash(execution) };
        var hosted = direct with { Runtime = "hosted" };
        var control = direct with
        { Runtime = "hosted-control", Identity = controlIdentity with { ExpectedAudience = "https://storage.azure.com/" } };
        var marker = FabricC1Deployment.Marker(run.Input);
        run.Seed("hosted-control-app", "Application", new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = controlIdentity.ClientId, ["tags"] = new JsonArray(marker)
        });
        run.Seed("hosted-control-sp", "ServicePrincipal", new()
        {
            ["id"] = controlIdentity.PrincipalObjectId, ["appId"] = controlIdentity.ClientId, ["tags"] = new JsonArray(marker)
        });
        return (execution, direct, hosted, control);
    }

    private static (FabricC1ExecutionSettings Execution, FabricC1BlobCheckpointBinding Binding) CheckpointFixture(Harness run)
    {
        var preparation = FabricC1PreparationTests.Bound();
        var identity = preparation.Identity with
        {
            TenantId = run.Input.TenantId, ClientId = Guid.NewGuid().ToString("D"),
            PrincipalObjectId = Guid.NewGuid().ToString("D"), ExpectedIssuer = $"https://sts.windows.net/{run.Input.TenantId}/"
        };
        preparation = preparation with { Identity = identity, Limits = new(70, 60, 32768, 30) };
        var execution = new FabricC1ExecutionSettings("cohors-fabric-c1-execution-1", preparation, Guid.NewGuid().ToString("D"),
            run.Input.SourceCommit!, run.Clock.GetUtcNow().AddMinutes(4), new string('b', 64), ["10.0.0.4"], "none", 24, 24, 20,
            new("99999999-9999-9999-9999-999999999999", "synthetic-agent", "synthetic-hosted-session", "synthetic-platform-user",
                new string('c', 64), preparation.Runtime.HostedProjectEndpoint!.TrimEnd('/') +
                    "/agents/synthetic-agent/endpoint/protocols/invocations?api-version=v1&agent_session_id=synthetic-hosted-session",
                identity with { ExpectedAudience = "https://ai.azure.com" }, ["10.0.0.5"]));
        var storage = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Storage/storageAccounts/c1synthetic";
        var binding = new FabricC1BlobCheckpointBinding(FabricC1BlobCheckpointStore.Contract, Canonical.Hash(run.Input),
            Canonical.Hash(execution), "direct", storage, identity with { ExpectedAudience = "https://storage.azure.com/" },
            ["10.0.0.6"], new string('e', 64), run.Input.Reviews!.BudgetPlanSha256!, 40);
        var marker = FabricC1Deployment.Marker(run.Input);
        run.Seed("runtime-app", "Application", new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = identity.ClientId, ["tags"] = new JsonArray(marker)
        });
        run.Seed("runtime-sp", "ServicePrincipal", new()
        {
            ["id"] = identity.PrincipalObjectId, ["appId"] = identity.ClientId, ["tags"] = new JsonArray(marker)
        });
        run.Seed("arm-foundation", "ArmDeployment", new()
        {
            ["id"] = FabricC1Deployment.GroupId(run.Input) + "/providers/Microsoft.Resources/deployments/foundation",
            ["properties"] = new JsonObject
            {
                ["provisioningState"] = "Succeeded", ["outputs"] = new JsonObject
                {
                    ["storage"] = new JsonObject { ["value"] = new JsonObject
                    {
                        ["id"] = storage, ["type"] = "Microsoft.Storage/storageAccounts", ["location"] = run.Input.Location,
                        ["tags"] = FabricC1Deployment.Tags(run.Input)
                    }},
                    ["checkpointContainer"] = new JsonObject { ["value"] = new JsonObject
                    {
                        ["id"] = storage + "/blobServices/default/containers/c1-checkpoints",
                        ["type"] = "Microsoft.Storage/storageAccounts/blobServices/containers",
                        ["properties"] = new JsonObject { ["publicAccess"] = "None" }
                    }}
                }
            }
        });
        return (execution, binding);
    }
    private static JsonObject SourceNotebookDefinition(JsonObject definition)
    {
        var source = Encoding.UTF8.GetString(Convert.FromBase64String(definition["parts"]![0]!["payload"]!.GetValue<string>()));
        const string begin = "# CELL ********************\n\n";
        const string end = "\n\n# METADATA ********************";
        var program = source[(source.IndexOf(begin, StringComparison.Ordinal) + begin.Length)..source.LastIndexOf(end, StringComparison.Ordinal)];
        var notebook = new JsonObject
        {
            ["nbformat"] = 4, ["nbformat_minor"] = 5,
            ["cells"] = new JsonArray(new JsonObject { ["cell_type"] = "code", ["source"] = new JsonArray(program + "\n") }),
            ["metadata"] = new JsonObject { ["language_info"] = new JsonObject { ["name"] = "python" } }
        };
        return new()
        {
            ["definition"] = new JsonObject
            {
                ["format"] = "ipynb", ["parts"] = new JsonArray(new JsonObject
                {
                    ["path"] = "artifact.content.ipynb", ["payloadType"] = "InlineBase64",
                    ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical.Serialize(notebook)))
                })
            }
        };
    }

    private static JsonObject SourceNotebookJob(Harness run, string role, string notebookId, string jobId,
        string deltaId, string status = "Completed")
    {
        var binding = run.Store.Current!.Objects["source-notebook-plan-" + role].Response["binding"]!;
        var start = DateTimeOffset.Parse(run.Store.Current.Objects["source-notebook-job-intent-" + role].Response["submittedAtUtc"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture);
        var schema = binding["schemaName"]?.GetValue<string>();
        var workspace = run.Store.Current.Objects["workspace-" + role].Id;
        var result = new JsonObject
        {
            ["contract"] = "cohors-fabric-c1-source-result-1", ["bindingSha256"] = Canonical.Hash(binding),
            ["invocationId"] = binding["invocationId"]!.DeepClone(), ["fixtureSha256"] = binding["fixtureSha256"]!.DeepClone(),
            ["scope"] = role, ["workspaceId"] = workspace, ["lakehouseId"] = run.Store.Current.Objects["lakehouse-" + role].Id,
            ["lakehouseWorkspaceId"] = workspace, ["notebookId"] = notebookId,
            ["actorObjectId"] = run.Input.BootstrapIdentity!.PrincipalObjectId,
            ["tableName"] = "C1_" + role + "." + (schema is null ? "" : schema + ".") + "c1_sources",
            ["deltaTableId"] = deltaId, ["rows"] = binding["rows"]!.DeepClone(), ["sparkVersion"] = "3.5.0-synthetic",
            ["startedAtUtc"] = start.AddSeconds(1).ToString("O"), ["completedAtUtc"] = start.AddSeconds(10).ToString("O")
        };
        return new()
        {
            ["id"] = jobId, ["itemId"] = notebookId, ["jobType"] = "RunNotebook", ["invokeType"] = "OnDemand",
            ["status"] = status, ["startTimeUtc"] = start.ToString("O"), ["endTimeUtc"] = start.AddSeconds(11).ToString("O"),
            ["failureReason"] = null, ["exitValue"] = Canonical.Serialize(result)
        };
    }

    private sealed class SourceNotebookHarness
    {
        private readonly Harness run;
        private readonly Dictionary<string, JsonObject> lakehouses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> notebookIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> jobIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> deltaIds = new(StringComparer.Ordinal);

        public SourceNotebookHarness(Harness run, string? schema = null)
        {
            this.run = run;
            run.Capacity();
            foreach (var role in new[] { "agent", "released", "denied" })
                run.Seed("workspace-" + role, "Workspace", run.Workspace(role));
            foreach (var role in new[] { "released", "denied" })
            {
                lakehouses[role] = new()
                {
                    ["id"] = Guid.NewGuid().ToString("D"), ["workspaceId"] = WorkspaceId(role),
                    ["type"] = "Lakehouse", ["displayName"] = "C1_" + role,
                    ["description"] = FabricC1Deployment.Marker(run.Input),
                    ["properties"] = new JsonObject { ["sqlEndpointProperties"] = new JsonObject
                    { ["id"] = Guid.NewGuid().ToString("D"), ["provisioningStatus"] = "Success" } }
                };
                if (schema is not null) lakehouses[role]["properties"]!["defaultSchema"] = schema;
                run.Seed("lakehouse-" + role, "Lakehouse", lakehouses[role]);
                notebookIds[role] = Guid.NewGuid().ToString("D");
                jobIds[role] = Guid.NewGuid().ToString("D");
                deltaIds[role] = Guid.NewGuid().ToString("D");
            }
            var state = run.Store.Load()!;
            foreach (var role in new[] { "released", "denied" })
            {
                var workspace = state.Objects["workspace-" + role];
                state.Objects["workspace-" + role] = workspace with
                {
                    RequestHash = Canonical.Hash(new JsonObject
                    {
                        ["displayName"] = FabricC1Deployment.Name(run.Input) + "-" + role,
                        ["capacityId"] = run.CapacityId, ["description"] = FabricC1Deployment.Marker(run.Input)
                    })
                };
                var policy = FabricC1Deployment.LockCommunicationPolicy(new());
                state.Objects["policy-" + role] = new("CommunicationPolicy", WorkspaceId(role), Canonical.Hash(policy), policy);
            }
            run.Store.Save(state);
            run.Restart();
        }

        public string WorkspaceId(string role) => run.Store.Current!.Objects["workspace-" + role].Id;
        public JsonObject Lakehouse(string role) => (JsonObject)lakehouses[role].DeepClone();
        public JsonObject Notebook(string role) => new()
        {
            ["id"] = notebookIds[role], ["workspaceId"] = WorkspaceId(role), ["type"] = "Notebook",
            ["displayName"] = "C1_Materialize_" + role, ["description"] = FabricC1Deployment.Marker(run.Input)
        };

        public void QueuePreparation(string role, string? mutation = null)
        {
            var workspace = (JsonObject)run.Store.Current!.Objects["workspace-" + role].Response.DeepClone();
            var policy = (JsonObject)run.Store.Current.Objects["policy-" + role].Response.DeepClone();
            var lakehouse = (JsonObject)lakehouses[role].DeepClone();
            if (mutation == "workspace") workspace["id"] = Guid.NewGuid().ToString("D");
            if (mutation == "capacity") workspace["capacityId"] = Guid.NewGuid().ToString("D");
            if (mutation == "policy") policy["inbound"]!["publicAccessRules"]!["defaultAction"] = "Allow";
            if (mutation == "lakehouse-workspace") lakehouse["workspaceId"] = Guid.NewGuid().ToString("D");
            if (mutation == "schema") lakehouse["properties"]!["defaultSchema"] = "unexpected";
            run.Api.List(InboundSetting(true));
            run.Api.Reply(200, workspace);
            run.Api.Reply(200, policy);
            run.Api.Reply(200, lakehouse);
        }

        public void QueueNotebook(string role, Action<JsonObject>? mutateDefinition = null)
        {
            QueuePreparation(role);
            var notebook = Notebook(role);
            if (run.Store.Current!.Objects.ContainsKey("source-notebook-" + role)) run.Api.Reply(200, notebook);
            else { run.Api.List(); run.Api.Reply(201, notebook); }
            run.Api.ReplyComputed(200, () =>
            {
                var definition = Definition(role);
                mutateDefinition?.Invoke(definition);
                return definition;
            });
        }

        private JsonObject Definition(string role)
        {
            var request = run.Api.Requests.Single(value => value.Method == HttpMethod.Post &&
                value.Uri.AbsolutePath == "/v1/workspaces/" + WorkspaceId(role) + "/notebooks");
            return SourceNotebookDefinition(request.Body!["definition"]!.AsObject());
        }

        public string JobLocation(string role, bool publicLocation = false) => publicLocation
            ? $"https://api.fabric.microsoft.com/v1/workspaces/{WorkspaceId(role)}/items/{notebookIds[role]}/jobs/instances/{jobIds[role]}"
            : $"https://{FabricC1Provisioner.WorkspaceHost(WorkspaceId(role))}/v1/workspaces/{WorkspaceId(role)}/notebooks/{notebookIds[role]}/jobs/execute/instances/{jobIds[role]}";

        public async Task<FabricC1SourceMaterializationResult> Submit(string role, bool publicLocation = false)
        {
            QueueNotebook(role);
            run.Api.Reply(202, new(), operationUrl: JobLocation(role, publicLocation), retryAfterSeconds: 60);
            return await run.Provisioner.AdvanceSourceMaterialization(role, default);
        }

        public JsonObject Job(string role, string status = "Completed") =>
            SourceNotebookJob(run, role, notebookIds[role], jobIds[role], deltaIds[role], status);
    }

    private sealed class SourceAccessHarness
    {
        private readonly Harness run;
        private readonly Dictionary<string, string> adminIds = new(StringComparer.Ordinal)
        { ["released"] = Guid.NewGuid().ToString("D"), ["denied"] = Guid.NewGuid().ToString("D") };
        private readonly string viewerId = Guid.NewGuid().ToString("D");
        public SourceNotebookHarness Source { get; }
        public string PrincipalId => run.Store.Current!.Objects["runtime-sp"].Id;

        private SourceAccessHarness(Harness run, SourceNotebookHarness source)
        {
            this.run = run;
            Source = source;
            var client = Guid.NewGuid().ToString("D");
            var marker = FabricC1Deployment.Marker(run.Input);
            run.Seed("runtime-app", "Application", new()
            { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = client, ["tags"] = new JsonArray(marker) });
            run.Seed("runtime-sp", "ServicePrincipal", new()
            { ["id"] = Guid.NewGuid().ToString("D"), ["appId"] = client, ["tags"] = new JsonArray(marker) });
        }

        public static async Task<SourceAccessHarness> Create(Harness run, string? schema = null)
        {
            var source = new SourceNotebookHarness(run, schema);
            foreach (var role in new[] { "released", "denied" })
            {
                await source.Submit(role);
                run.Clock.Advance(TimeSpan.FromSeconds(60));
                run.Api.Reply(200, source.Job(role));
                await run.Provisioner.AdvanceSourceMaterialization(role, default);
            }
            return new(run, source);
        }

        public static JsonObject List(params JsonObject[] items) =>
            new() { ["value"] = new JsonArray(items.Select(item => item.DeepClone()).ToArray()) };

        private JsonObject Admin(string scope) => new()
        {
            ["id"] = adminIds[scope], ["role"] = "Admin",
            ["principal"] = new JsonObject { ["id"] = run.Input.BootstrapIdentity!.PrincipalObjectId, ["type"] = "User" }
        };

        private JsonObject Viewer() => new()
        {
            ["id"] = viewerId, ["role"] = "Viewer",
            ["principal"] = new JsonObject { ["id"] = PrincipalId, ["type"] = "ServicePrincipal" }
        };

        public JsonObject Workspace(string scope) => (JsonObject)run.Store.Current!.Objects["workspace-" + scope].Response.DeepClone();

        public JsonObject DefaultReader(string scope = "released") => new()
        {
            ["name"] = "DefaultReader", ["eTag"] = "default-version",
            ["decisionRules"] = new JsonArray(new JsonObject
            {
                ["effect"] = "Permit", ["permission"] = new JsonArray(
                    new JsonObject { ["attributeName"] = "Path", ["attributeValueIncludedIn"] = new JsonArray("*") },
                    new JsonObject { ["attributeName"] = "Action", ["attributeValueIncludedIn"] = new JsonArray("Read") })
            }),
            ["members"] = new JsonObject { ["fabricItemMembers"] = new JsonArray(new JsonObject
            { ["itemAccess"] = new JsonArray("ReadAll"), ["sourcePath"] = Source.WorkspaceId(scope) + "/" + Source.Lakehouse(scope)["id"]!.GetValue<string>() }) }
        };

        public JsonObject Role(string version = "source-version-1")
        {
            var role = (JsonObject)run.Store.Current!.Objects["source-access-definition"].Response["role"]!.DeepClone();
            role.Remove("kind");
            role["eTag"] = version;
            return role;
        }

        public void QueueBefore(string? fault = null, bool existingRole = false)
        {
            Source.QueuePreparation("released");
            QueueItems("released", fault);
            run.Api.List(fault == "denied-runtime" ? [Admin("denied"), Viewer()] : [Admin("denied")]);
            var assignments = new List<JsonObject> { Admin("released") };
            if (run.Store.Current!.Objects.ContainsKey("runtime-source-grant") ||
                fault is "source-contributor" or "unowned-viewer" or "replaced-viewer")
            {
                var viewer = Viewer();
                if (fault == "source-contributor") viewer["role"] = "Contributor";
                if (fault == "replaced-viewer") viewer["id"] = Guid.NewGuid().ToString("D");
                assignments.Add(viewer);
            }
            if (fault == "source-group") assignments.Add(GroupAssignment());
            run.Api.List(assignments.ToArray());
            run.Api.ReplyComputed(200, () =>
            {
                var defaultRole = DefaultReader();
                if (fault == "default-reader-broadened") defaultRole["members"]!["fabricItemMembers"]![0]!["itemAccess"]![0] = "Read";
                return existingRole || fault == "unowned-role" ? List(defaultRole, Role()) : List(defaultRole);
            });
        }

        private static JsonObject GroupAssignment() => new()
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["role"] = "Viewer",
            ["principal"] = new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["type"] = "Group" }
        };

        private void QueueItems(string scope, string? fault)
        {
            var lakehouse = Source.Lakehouse(scope);
            var schema = lakehouse["properties"]!["defaultSchema"]?.GetValue<string>();
            var sql = new JsonObject
            {
                ["id"] = lakehouse["properties"]!["sqlEndpointProperties"]!["id"]!.DeepClone(),
                ["type"] = "SQLEndpoint", ["workspaceId"] = Source.WorkspaceId(scope)
            };
            if (fault == "foreign-sql-item") sql["id"] = Guid.NewGuid().ToString("D");
            if (fault == "sql-not-ready") lakehouse["properties"]!["sqlEndpointProperties"]!["provisioningStatus"] = "InProgress";
            run.Api.Reply(200, lakehouse);
            var items = new List<JsonObject> { Source.Lakehouse(scope), Source.Notebook(scope), sql };
            if (fault == "extra-item") items.Add(new() { ["id"] = Guid.NewGuid().ToString("D"), ["type"] = "Warehouse" });
            run.Api.List(items.ToArray());
            var table = new JsonObject
            {
                ["name"] = schema is null ? "c1_sources" : schema + ".c1_sources", ["type"] = "Managed", ["format"] = "Delta",
                ["location"] = "abfss://" + Source.WorkspaceId(scope) + "@onelake.dfs.fabric.microsoft.com/" +
                    lakehouse["id"]!.GetValue<string>() + "/Tables/" + (schema is null ? "" : schema + "/") + "c1_sources"
            };
            if (fault == "foreign-table") table["location"] = "abfss://" + Guid.NewGuid().ToString("D") +
                "@onelake.dfs.fabric.microsoft.com/" + lakehouse["id"]!.GetValue<string>() + "/Tables/c1_sources";
            var tables = new JsonArray(table);
            if (fault == "extra-table") tables.Add(table.DeepClone());
            run.Api.Reply(200, new() { ["data"] = tables });
        }

        public void QueueCleanupBefore(string scope, string? fault = null)
        {
            var workspace = Workspace(scope);
            if (fault == "workspace") workspace["description"] = "not-owned";
            run.Api.Reply(200, workspace);
            QueueItems(scope, fault);
            var assignments = new List<JsonObject> { Admin(scope) };
            if (scope == "released" && fault != "removed-viewer" && run.Store.Current!.Objects.ContainsKey("runtime-source-grant")) assignments.Add(Viewer());
            if (fault == "source-group") assignments.Add(GroupAssignment());
            run.Api.List(assignments.ToArray());
            var reader = DefaultReader(scope);
            if (fault == "role") reader["members"]!["fabricItemMembers"]![0]!["itemAccess"]![0] = "Read";
            run.Api.List(scope == "released" && (run.Store.Current!.Objects.ContainsKey("source-access-role") || fault == "unowned-source-role")
                ? [reader, Role(fault == "replaced-source-role" ? "source-version-2" : "source-version-1")] : [reader]);
        }

        public void QueueAfter(bool createRole = true, bool createViewer = true,
            string readbackVersion = "source-version-1", string finalVersion = "source-version-1")
        {
            if (createRole) run.Api.Reply(201, new());
            run.Api.ReplyComputed(200, () => List(DefaultReader(), Role(readbackVersion)));
            run.Api.List(createViewer ? [Admin("released")] : [Admin("released"), Viewer()]);
            if (createViewer) run.Api.Reply(201, Viewer());
            run.Api.List(Admin("released"), Viewer());
            run.Api.List(Admin("denied"));
            run.Api.ReplyComputed(200, () => List(DefaultReader(), Role(finalVersion)));
        }
    }

    private sealed class Harness : IDisposable
    {
        public Clock Clock { get; }
        public FakeApi Api { get; } = new();
        public MemoryJournal Store { get; } = new();
        public FabricC1DeploymentInputs Input { get; }
        public FabricC1Provisioner Provisioner { get; private set; }
        public string CapacityId { get; } = Guid.NewGuid().ToString("D");
        public Harness(int maxRequests = 100, string[]? requiredTenantSettingNames = null, bool delegated = false,
            bool delegatedAdministrator = true, bool operatorNetwork = false, int vpnUserCount = 1, bool manualTimers = false,
            bool sharedHub = false, int? cleanupReserve = null)
        {
            Clock = new(DateTimeOffset.Parse("2026-11-18T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), manualTimers);
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
                Cleanup = new(cleanupReserve ?? Math.Min(20, Math.Max(1, maxRequests / 3)), 600, Clock.GetUtcNow().AddHours(4).AddMinutes(10)),
                DelegatedGroupOwnerMode = delegated ? delegatedAdministrator ? "ExplicitReviewedAdministrator" : "AutomaticReviewedNonAdministrator" : null,
                ControlTokenBindings = new("https://management.core.windows.net/", $"https://sts.windows.net/{tenant}/",
                    "00000003-0000-0000-c000-000000000000", $"https://sts.windows.net/{tenant}/")
            };
            if (operatorNetwork)
            {
                var original = delegated ? Input.BootstrapIdentity!.PrincipalObjectId! : Guid.NewGuid().ToString("D");
                var users = new[] { original }.Concat(Enumerable.Range(1, vpnUserCount - 1).Select(_ => Guid.NewGuid().ToString("D"))).ToArray();
                Input = Input with { Network = Input.Network! with
                {
                    OperatorAccess = new(FabricC1Deployment.OperatorNetworkContract, "10.241.4.0/27", "10.241.5.0/26",
                        "10.241.6.0/27", "172.30.240.0/24", original,
                        ["192.168.10.0/24"], ["login.microsoftonline.com", "management.azure.com"], hash, hash,
                        new(users, new string('b', 64), new string('c', 64)))
                }};
            }
            if (sharedHub)
                Input = Input with { Network = Input.Network! with
                {
                    SharedHub = new(FabricC1Deployment.SharedHubContract, FabricC1Deployment.SharedHubSchemaSha256,
                        Guid.NewGuid().ToString("D"), new string('d', 64),
                        "/subscriptions/" + Input.SubscriptionId + "/resourceGroups/synthetic-shared-hub",
                        new string('e', 40), "10.241.6.0/27", new string('1', 64), new string('2', 64), hash, hash,
                        ["192.168.10.0/24"], ["management.azure.com", "login.microsoftonline.com"],
                        [new("control", ["client", "agents"], ["management.azure.com", "login.microsoftonline.com"])],
                        new string('f', 64))
                }};
            Provisioner = new(Input, Api, Store, Clock);
        }
        public void Capacity() => Seed("fabric-capacity", "FabricCapacity", new() { ["id"] = CapacityId });
        public void Seed(string key, string kind, JsonObject body, string requestHash = "synthetic-fixture-not-a-request")
        {
            var state = Store.Load() ?? new(Canonical.Hash(Input), new(), new());
            state.Objects[key] = new(kind, body["id"]!.GetValue<string>(), requestHash, body);
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
