using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed partial class FabricC1DeploymentTests
{
    private static string HubFixturePath(string file) => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        "tests", "fixtures", "fabric", "shared-hub", file);

    [Theory]
    [InlineData("reserved")]
    [InlineData("peered")]
    [InlineData("connected")]
    [InlineData("data-bound")]
    public async Task OriginalOwnerCollectorFixturesRetainTheirByteAndPythonWireProvenance(string stage)
    {
        static string FileHash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(HubFixturePath(file))));
        Assert.Equal("bbc2fa0bf89e44ad85e364b6cfb75ecf705d652ced75d1f2e35dd5d7a9910baa", FileHash("fixture-manifest.json"));
        Assert.Equal(FabricC1Deployment.SharedHubSchemaSha256, FileHash("shared-hub-consumer-v1.schema.json"));
        var manifest = JsonNode.Parse(File.ReadAllText(HubFixturePath("fixture-manifest.json")))!;
        Assert.True(manifest["synthetic"]!.GetValue<bool>());
        Assert.False(manifest["cloudObserved"]!.GetValue<bool>());
        Assert.Equal(FabricC1Deployment.SharedHubSchemaSha256, manifest["schemaSha256"]!.GetValue<string>());
        var entry = Assert.Single(manifest["cases"]!.AsArray(), item => item!["state"]!.GetValue<string>() == stage)!;
        var receiptFile = entry["receiptFile"]!.GetValue<string>();
        var armFile = entry["armFile"]!.GetValue<string>();
        Assert.Equal(entry["receiptFileSha256"]!.GetValue<string>(), FileHash(receiptFile));
        Assert.Equal(entry["armFileSha256"]!.GetValue<string>(), FileHash(armFile));
        var receipt = JsonNode.Parse(File.ReadAllText(HubFixturePath(receiptFile)))!.AsObject();
        var arm = JsonNode.Parse(File.ReadAllText(HubFixturePath(armFile)))!.AsObject();
        Assert.Equal(entry["receiptCanonicalSha256"]!.GetValue<string>(), FabricC1Deployment.HubWireHash(receipt));
        Assert.Equal(entry["armCanonicalSha256"]!.GetValue<string>(), FabricC1Deployment.HubWireHash(arm));
        foreach (var observation in receipt["readback"]!["resources"]!.AsArray())
        {
            var id = observation!["id"]!.GetValue<string>();
            Assert.Equal(observation["sha256"]!.GetValue<string>(), FabricC1Deployment.HubWireHash(arm[id]));
            Assert.DoesNotContain("primary", id, StringComparison.OrdinalIgnoreCase);
        }
        await AssertPythonContract("""
            import hashlib, json, sys
            data = json.load(sys.stdin)
            for name in ("receipt", "arm"):
                wire = json.dumps(data[name], sort_keys=True, separators=(",", ":"), ensure_ascii=True)
                assert hashlib.sha256(wire.encode()).hexdigest() == data[name + "Hash"]
            """, new { receipt, arm, receiptHash = FabricC1Deployment.HubWireHash(receipt), armHash = FabricC1Deployment.HubWireHash(arm) });
    }

    [Fact]
    public async Task OwnerCollectorReservationImportsWithExplicitSyntheticConsumerRebinding()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var receipt = JsonNode.Parse(File.ReadAllText(HubFixturePath("reserved.receipt.json")))!.AsObject();
        var arm = JsonNode.Parse(File.ReadAllText(HubFixturePath("reserved.arm.json")))!.AsObject();
        var fixture = new HubFixture(run);
        var originalHub = receipt["hub"]!.AsObject();
        var originalSpoke = receipt["spoke"]!.AsObject();
        var gateway = arm[originalHub["vpnGatewayId"]!.GetValue<string>()]!;
        var tenant = gateway["properties"]!["vpnClientConfiguration"]!["aadTenant"]!.GetValue<string>().TrimEnd('/').Split('/')[^1];
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [originalSpoke["subnets"]!["operatorEndpoints"]!["id"]!.GetValue<string>()] = fixture.SpokeVnet + "/subnets/operator-endpoints",
            [originalSpoke["vnetId"]!.GetValue<string>()] = fixture.SpokeVnet,
            [originalSpoke["resourceGroupId"]!.GetValue<string>()] = fixture.OwnGroup,
            [originalHub["resourceGroupId"]!.GetValue<string>()] = fixture.HubGroup,
            [receipt["hubBindingHash"]!.GetValue<string>()] = run.Input.Network!.SharedHub!.HubBindingHash!,
            [originalSpoke["addressPrefix"]!.GetValue<string>()] = run.Input.Network.VnetPrefix!,
            [originalSpoke["subnets"]!["client"]!["addressPrefix"]!.GetValue<string>()] = run.Input.Network.ClientPrefix!,
            [originalSpoke["subnets"]!["agents"]!["addressPrefix"]!.GetValue<string>()] = run.Input.Network.AgentPrefix!,
            [originalSpoke["subnets"]!["endpoints"]!["addressPrefix"]!.GetValue<string>()] = run.Input.Network.EndpointPrefix!,
            [originalSpoke["subnets"]!["operatorEndpoints"]!["addressPrefix"]!.GetValue<string>()] = run.Input.Network.SharedHub.OperatorEndpointPrefix!,
            [tenant] = run.Input.TenantId!
        };
        JsonObject Rebind(JsonObject value)
        {
            var json = value.ToJsonString();
            foreach (var (oldValue, newValue) in replacements.OrderByDescending(item => item.Key.Length))
                json = json.Replace(oldValue, newValue, StringComparison.Ordinal);
            return JsonNode.Parse(json)!.AsObject();
        }
        // The originals remain byte-pinned above; only this in-memory test universe uses our actual input hash/names.
        receipt = Rebind(receipt);
        arm = Rebind(arm);
        receipt["generation"] = run.Input.Network!.SharedHub!.Generation;
        receipt["consumerBindingHash"] = Canonical.Hash(run.Input);
        receipt["expiresAtUtc"] = run.Input.ExpiresAtUtc!.Value.ToString("O");
        receipt["vpn"]!["userSetSha256"] = run.Input.Network.SharedHub.VpnUserSetSha256;
        receipt["vpn"]!["resolvedUsersEvidenceSha256"] = run.Input.Network.SharedHub.ResolvedUsersEvidenceSha256;
        receipt["egress"]!["dnsDomains"] = fixture.Receipt["egress"]!["dnsDomains"]!.DeepClone();
        receipt["egress"]!["httpsRules"] = fixture.Receipt["egress"]!["httpsRules"]!.DeepClone();
        receipt["readback"]!["observedAtUtc"] = run.Clock.GetUtcNow().ToString("O");
        receipt["readback"]!["infrastructureCommit"] = run.Input.Network.SharedHub.InfrastructureCommit;
        receipt["readback"]!["compiledTemplateSha256"] = run.Input.Network.SharedHub.CompiledTemplateSha256;
        receipt["readback"]!["deploymentResourceId"] = "/subscriptions/" + run.Input.SubscriptionId + "/providers/Microsoft.Resources/deployments/synthetic-hub";
        foreach (var observation in receipt["readback"]!["resources"]!.AsArray())
        {
            var resource = arm[observation!["id"]!.GetValue<string>()]!.AsObject();
            observation["sha256"] = FabricC1Deployment.HubWireHash(resource);
            run.Api.Reply(200, resource);
        }
        var hubObject = receipt["hub"]!.AsObject();
        foreach (var prefix in new[]
        {
            hubObject["firewallPolicyId"]!.GetValue<string>() + "/ruleCollectionGroups/",
            hubObject["dnsPolicyId"]!.GetValue<string>() + "/dnsSecurityRules/",
            hubObject["dnsPolicyId"]!.GetValue<string>() + "/virtualNetworkLinks/"
        })
            run.Api.List(arm.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.Value!.AsObject()).ToArray());
        receipt.Remove("receiptSha256");
        receipt["receiptSha256"] = FabricC1Deployment.HubWireHash(receipt);
        var result = await run.Provisioner.ImportSharedHubReceipt(receipt, default);
        Assert.Equal("reserved", result.State);
        Assert.Equal(28, run.Api.Requests.Count);
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task HubWireDigestMatchesPythonWithoutChangingHistoricalCanonicalJson()
    {
        var samples = new[]
        {
            """{"html":"<a>&' /","unicode":"\u00e4\u2028\u007f\ud83d\ude00","controls":"\b\f\n\r\t\u0000","z":null,"a":true}""",
            """{"\ue000":1,"\ud83d\ude00":2,"\u00e4":3,"A":4,"list":[false,true,null,123456789012345678901234567890]}""",
            """[1.0,-0.0,0.0001,0.00001,1e16,1e15,1e20,1e-7,1.2345678901234567,5e-324,1.7976931348623157e308]""",
            """{"order":{"b":[3,2,1],"a":"unchanged"},"negative":-123456789012345678901}"""
        };
        await AssertPythonContract("""
            import hashlib, json, sys
            for sample in json.load(sys.stdin):
                value = json.loads(sample["json"])
                wire = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
                assert hashlib.sha256(wire.encode()).hexdigest() == sample["hash"], wire
            """, samples.Select(json => new { json, hash = FabricC1Deployment.HubWireHash(JsonNode.Parse(json)) }).ToArray());
        var html = JsonNode.Parse(samples[0])!;
        Assert.NotEqual(Canonical.Hash(html), FabricC1Deployment.HubWireHash(html));
    }

    [Fact]
    public void SharedHubPlanningDoesNotActivateAnOldStandaloneTopologyOrPaidPartialApply()
    {
        using var run = new Harness(sharedHub: true);
        var plan = FabricC1Deployment.Plan(run.Input, run.Clock.GetUtcNow());
        Assert.Empty(plan.MissingInputs);
        Assert.False(plan.CanApply);
        Assert.Equal("c1_deploy_incomplete", Assert.Throws<DomainException>(() => FabricC1Deployment.RequireComplete(plan)).Code);
        Assert.Throws<DomainException>(() => run.Provisioner.OperatorNetworkArmParameters());
        Assert.Throws<DomainException>(() => run.Provisioner.NetworkedFoundationArmParameters());
        var prior = run.Input with { Network = run.Input.Network! with
        { SharedHub = run.Input.Network.SharedHub! with { SchemaSha256 = new string('0', 64) } } };
        Assert.Contains("network.sharedHub.contract", FabricC1Deployment.Plan(prior, run.Clock.GetUtcNow()).MissingInputs);
        Assert.Empty(run.Api.Requests);
    }

    [Fact]
    public async Task ReservedHubImportBindsActualMetadataButDoesNotClaimTrafficOrInstallEgress()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        var result = await hub.Import();
        Assert.Equal("reserved", result.State);
        Assert.False(result.CompleteC1Deployment);
        Assert.Contains("not-traffic", result.EvidenceLevel, StringComparison.Ordinal);
        Assert.Equal(hub.Receipt["receiptSha256"]!.GetValue<string>(), result.ReceiptSha256);
        Assert.Equal(2, run.Store.Current!.Objects.Values.Count(item => item.Kind == "SharedHubConsumerReceipt"));
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(run.Api.Requests, request => Assert.StartsWith(hub.HubGroup, request.Uri.AbsolutePath, StringComparison.Ordinal));
        Assert.Empty(hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["ruleCollections"]!.AsArray());
        var stage = run.Provisioner.PrepareSharedHubStage("hub-spoke");
        var resources = stage.Template["resources"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.DoesNotContain(resources, resource => resource["type"]!.GetValue<string>() is
            "Microsoft.Network/azureFirewalls" or "Microsoft.Network/virtualNetworkGateways" or
            "Microsoft.Network/publicIPAddresses" or "Microsoft.Network/privateDnsZones");
        var vnet = Assert.Single(resources, resource => resource["type"]!.GetValue<string>() == "Microsoft.Network/virtualNetworks");
        Assert.Equal(["agents", "client", "endpoints", "operator-endpoints"], vnet["properties"]!["subnets"]!.AsArray()
            .Select(subnet => subnet!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("VirtualNetwork", resources.Where(resource =>
            resource["type"]!.GetValue<string>() == "Microsoft.Network/networkSecurityGroups").Select(resource => resource.ToJsonString()).Aggregate("", (a, b) => a + b), StringComparison.Ordinal);
        Assert.Throws<DomainException>(() => run.Provisioner.PrepareSharedHubStage("foundation"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("generation")]
    [InlineData("consumer")]
    [InlineData("binding")]
    [InlineData("expiry")]
    [InlineData("future")]
    [InlineData("source")]
    [InlineData("template")]
    [InlineData("primary-observation")]
    [InlineData("wildcard-egress")]
    [InlineData("dns-owner")]
    [InlineData("pool-overlap")]
    [InlineData("acceptance")]
    [InlineData("web")]
    [InlineData("missing-field")]
    public async Task InvalidOwnerReceiptIsRejectedBeforeAnyProviderCall(string fault)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        var receipt = hub.Receipt;
        switch (fault)
        {
            case "generation": receipt["generation"] = Guid.NewGuid().ToString("D"); break;
            case "consumer": receipt["consumer"] = "primary"; break;
            case "binding": receipt["consumerBindingHash"] = new string('c', 64); break;
            case "expiry": receipt["expiresAtUtc"] = run.Input.ExpiresAtUtc!.Value.AddHours(1).ToString("O"); break;
            case "future": receipt["readback"]!["observedAtUtc"] = run.Clock.GetUtcNow().AddSeconds(1).ToString("O"); break;
            case "source": receipt["readback"]!["infrastructureCommit"] = new string('0', 40); break;
            case "template": receipt["readback"]!["compiledTemplateSha256"] = new string('0', 64); break;
            case "primary-observation": receipt["readback"]!["resources"]![0]!["id"] = hub.HubGroup + "-other/providers/Microsoft.Network/virtualNetworks/primary"; break;
            case "wildcard-egress": receipt["egress"]!["httpsRules"]![0]!["sourceAddresses"] = new JsonArray("*"); break;
            case "dns-owner": receipt["dns"]!["owner"] = "fabric"; break;
            case "pool-overlap": receipt["vpn"]!["clientPool"] = run.Input.Network!.ClientPrefix; break;
            case "acceptance": receipt["readback"]!["trafficAccepted"] = true; break;
            case "web": receipt["web"] = new JsonObject(); break;
            case "missing-field": receipt["vpn"]!.AsObject().Remove("clientPool"); break;
        }
        hub.Sign();
        if (fault == "hash") receipt["receiptSha256"] = new string('0', 64);
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.ImportSharedHubReceipt(receipt, default));
        Assert.Empty(run.Api.Requests);
        Assert.False(run.Store.Current?.Objects.ContainsKey("shared-hub-receipt") ?? false);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("body")]
    public async Task HubReadbackDriftCannotBecomeAnAcceptedReceipt(string fault)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        var first = hub.Resources[hub.Receipt["readback"]!["resources"]![0]!["id"]!.GetValue<string>()];
        if (fault == "body") first["location"] = "westeurope";
        hub.Queue(fault == "etag" ? "changed-etag" : null);
        Assert.Equal("c1_hub_resource_drift", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.Single(run.Api.Requests);
        Assert.DoesNotContain(run.Store.Current!.Objects.Values, item => item.Kind == "SharedHubConsumerReceipt");
    }

    [Fact]
    public async Task HubReceiptRefreshRetainsHistoryCountersAndRejectsSameRevisionRebinding()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        var count = run.Store.Current!.Requests;
        var original = hub.Receipt["receiptSha256"]!.GetValue<string>();
        run.Clock.Advance(TimeSpan.FromSeconds(1));
        hub.Receipt["readback"]!["observedAtUtc"] = run.Clock.GetUtcNow().ToString("O");
        hub.Sign();
        await hub.Import();
        Assert.True(run.Store.Current!.Requests > count);
        Assert.Contains(run.Store.Current.Objects.Values, item => item.RequestHash == original);
        var before = run.Api.Requests.Count;
        hub.Receipt["hub"]!["wafId"] = hub.HubGroup + "/providers/Microsoft.Network/applicationGateways/new-waf";
        hub.Receipt["hub"]!["wafPrivateIp"] = "10.242.2.4";
        hub.Sign();
        Assert.Equal("c1_hub_revision", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.Equal(before, run.Api.Requests.Count);
    }

    [Fact]
    public async Task LostHubReceiptJournalAcknowledgementFencesAllFurtherReads()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        hub.Queue();
        run.Store.AfterSave = state =>
        {
            if (state.Objects.ContainsKey("shared-hub-receipt")) throw new IOException("synthetic lost receipt acknowledgement");
        };
        await Assert.ThrowsAsync<IOException>(() => run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default));
        var count = run.Api.Requests.Count;
        Assert.Equal("c1_deploy_checkpoint_journal_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.Equal(count, run.Api.Requests.Count);
    }

    [Fact]
    public async Task HubHandshakeRequiresOriginalOwnPeerAndConnectedReceiptBeforeWorkloads()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = new HubFixture(run);
        await hub.Import();
        hub.SeedSpoke();
        var initial = run.Provisioner.PrepareSharedHubStage("hub-peer");
        Assert.False(initial.Parameters["useRemoteGateways"]!["value"]!.GetValue<bool>());
        var peer = Assert.Single(initial.Template["resources"]!.AsArray())!;
        Assert.Equal("[concat(parameters('name'), '-vnet/to-shared-hub')]", peer["name"]!.GetValue<string>());
        Assert.Throws<DomainException>(() => run.Provisioner.PrepareSharedHubStage("hub-transit"));
        hub.SetState("peered", 2);
        var count = run.Api.Requests.Count;
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default));
        Assert.Equal(count, run.Api.Requests.Count);
        hub.SeedPeer("hub-peer", initial);
        await hub.Import();
        var transit = run.Provisioner.PrepareSharedHubStage("hub-transit");
        Assert.True(transit.Parameters["useRemoteGateways"]!["value"]!.GetValue<bool>());
        Assert.Throws<DomainException>(() => run.Provisioner.PrepareSharedHubStage("foundation"));
        hub.SeedPeer("hub-transit", transit);
        hub.SetState("connected", 3);
        await hub.Import();
        var foundation = run.Provisioner.PrepareSharedHubStage("foundation");
        Assert.DoesNotContain(foundation.Template["resources"]!.AsArray(), resource =>
            resource!["type"]!.GetValue<string>() is "Microsoft.Network/virtualNetworks" or "Microsoft.Network/routeTables" or
            "Microsoft.Network/privateDnsZones" or "Microsoft.Network/privateDnsZones/virtualNetworkLinks");
        Assert.DoesNotContain("operatorAccess", foundation.Parameters.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(foundation.Template["resources"]!.AsArray(), resource =>
            resource!["type"]!.GetValue<string>() == "Microsoft.Fabric/capacities");
        Assert.All(run.Api.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("peer-name")]
    [InlineData("early-transit")]
    [InlineData("outbound")]
    [InlineData("vpn-data")]
    [InlineData("route")]
    [InlineData("other-fabric-rule")]
    [InlineData("overlapping-other-rule")]
    [InlineData("deny-protocol")]
    [InlineData("allow-priority")]
    [InlineData("dns-deny-disabled")]
    public async Task PeeredMetadataCannotBroadenRoutesEgressOrVpnDataRights(string fault)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = new HubFixture(run);
        await hub.Import();
        hub.SeedSpoke();
        var request = run.Provisioner.PrepareSharedHubStage("hub-peer");
        hub.SetState("peered", 2);
        hub.SeedPeer("hub-peer", request);
        if (fault == "peer-name") hub.Receipt["peering"]!["spokeToHub"]!["id"] = hub.SpokeVnet + "/virtualNetworkPeerings/other";
        if (fault == "early-transit") hub.Receipt["peering"]!["spokeToHub"]!["useRemoteGateways"] = true;
        if (fault == "outbound")
            hub.Resources[hub.OwnGroup + "/providers/Microsoft.Network/networkSecurityGroups/" + hub.Name + "-nsg"]
                ["properties"]!["securityRules"]![1]!["properties"]!["sourceAddressPrefixes"] = new JsonArray("VirtualNetwork");
        if (fault == "vpn-data")
            hub.Resources[hub.OwnGroup + "/providers/Microsoft.Network/networkSecurityGroups/" + hub.Name + "-endpoint-nsg"]
                ["properties"]!["securityRules"]![0]!["properties"]!["sourceAddressPrefixes"] = new JsonArray("10.243.0.0/24");
        if (fault == "route")
            hub.Resources[hub.OwnGroup + "/providers/Microsoft.Network/routeTables/" + hub.Name + "-egress-routes"]
                ["properties"]!["routes"]![0]!["properties"]!["addressPrefix"] = "10.242.0.0/20";
        if (fault == "deny-protocol")
            hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/private"]["properties"]!["ruleCollections"]![0]!["rules"]![0]!["ipProtocols"] = new JsonArray("TCP");
        if (fault == "allow-priority")
            hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["priority"] = 50;
        if (fault == "dns-deny-disabled")
            hub.Resources[hub.HubGroup + "/providers/Microsoft.Network/dnsResolverPolicies/dns/dnsSecurityRules/deny"]["properties"]!["dnsSecurityRuleState"] = "Disabled";
        if (fault is "other-fabric-rule" or "overlapping-other-rule")
        {
            var rules = hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["ruleCollections"]![0]!["rules"]!.AsArray();
            var rule = rules[0]!.DeepClone(); rule["name"] = fault == "other-fabric-rule" ? "fabric-unreviewed" : "another-unreviewed";
            rules.Add(rule);
        }
        hub.RefreshHashes();
        hub.Queue();
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default));
        Assert.Equal("reserved", run.Store.Current!.Objects["shared-hub-receipt"].Response["state"]!.GetValue<string>());
        Assert.All(run.Api.Requests, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task ExtraLivePolicyOutsideReceiptCannotBeIgnored()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        var extra = (JsonObject)hub.Resources[hub.FirewallPolicy + "/ruleCollectionGroups/https"].DeepClone();
        extra["id"] = hub.FirewallPolicy + "/ruleCollectionGroups/unbound";
        hub.Queue(extraPolicy: extra);
        Assert.Equal("c1_hub_policy_inventory", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.False(run.Store.Current!.Objects.ContainsKey("shared-hub-receipt"));
        Assert.All(run.Api.Requests, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task DataBoundOperatorStageTargetsOnlyOriginalEndpointAddressesWithoutDnsWrites()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        var endpoints = hub.SeedEndpoints();
        Assert.DoesNotContain(endpoints.Template["resources"]!.AsArray(), resource =>
            resource!["type"]!.GetValue<string>() == "Microsoft.Network/privateEndpoints/privateDnsZoneGroups");
        hub.SetState("data-bound", 4);
        await hub.Import();
        var access = run.Provisioner.PrepareSharedHubStage("hub-operator-access");
        var nsg = Assert.Single(access.Template["resources"]!.AsArray())!;
        Assert.Equal("Microsoft.Network/networkSecurityGroups", nsg["type"]!.GetValue<string>());
        Assert.Equal("[concat(parameters('name'), '-operator-endpoint-nsg')]", nsg["name"]!.GetValue<string>());
        Assert.Equal("10.243.0.0/24", access.Parameters["vpnClientPrefix"]!["value"]!.GetValue<string>());
        Assert.Equal(new[] { "10.241.6.5" }, access.Parameters["operatorAddresses"]!["value"]!.AsArray()
            .Select(address => address!.GetValue<string>()).ToArray());
        Assert.All(run.Api.Requests, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Theory]
    [InlineData("recreated-nic")]
    [InlineData("wrong-reverse-link")]
    [InlineData("registry-service")]
    [InlineData("unbound-dns")]
    public async Task DataBoundReceiptDoesNotAdoptRecreatedOrBroadenedOperatorEndpoints(string fault)
    {
        using var run = new Harness(sharedHub: true, maxRequests: 1000);
        var hub = await HubFixture.Connected(run);
        hub.SeedEndpoints();
        hub.SetState("data-bound", 4);
        var endpoint = hub.Resources[hub.OwnGroup + "/providers/Microsoft.Network/privateEndpoints/" + hub.Name + "-vault-pe"];
        var nic = hub.Resources[endpoint["properties"]!["networkInterfaces"]![0]!["id"]!.GetValue<string>()];
        switch (fault)
        {
            case "recreated-nic": nic["properties"]!["resourceGuid"] = Guid.NewGuid().ToString("D"); break;
            case "wrong-reverse-link": nic["properties"]!["privateEndpoint"]!["id"] = hub.OwnGroup + "/providers/Microsoft.Network/privateEndpoints/replacement"; break;
            case "registry-service":
                endpoint["properties"]!["privateLinkServiceConnections"]![0]!["properties"]!["groupIds"] = new JsonArray("registry");
                endpoint["properties"]!["privateLinkServiceConnections"]![0]!["properties"]!["privateLinkServiceId"] =
                    hub.OwnGroup + "/providers/Microsoft.ContainerRegistry/registries/" + hub.Name + "acr";
                break;
            case "unbound-dns": endpoint["properties"]!["customDnsConfigs"]![0]!["fqdn"] = "unbound.example.invalid"; break;
        }
        hub.RefreshHashes();
        hub.Queue();
        Assert.Equal("c1_hub_endpoints", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.ImportSharedHubReceipt(hub.Receipt, default))).Code);
        Assert.Equal("connected", run.Store.Current!.Objects["shared-hub-receipt"].Response["state"]!.GetValue<string>());
        Assert.Throws<DomainException>(() => run.Provisioner.PrepareSharedHubStage("hub-operator-access"));
    }

    [Fact]
    public async Task HubTemplatesRejectForeignOrModifiedWritesBeforeReadingAnyProvider()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        var request = run.Provisioner.PrepareSharedHubStage("hub-spoke");
        request.Template["resources"]!.AsArray().Add(new JsonObject { ["type"] = "Microsoft.Network/azureFirewalls" });
        var count = run.Api.Requests.Count;
        Assert.Equal("c1_hub_template", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm("hub-spoke", request.Template, request.Parameters, default))).Code);
        Assert.Equal(count, run.Api.Requests.Count);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete })
        {
            Assert.Throws<DomainException>(() => FabricC1Deployment.RequireSharedHubMutationScope(run.Input, method,
                new Uri("https://management.azure.com" + hub.HubGroup + "/providers/Microsoft.Network/virtualNetworks/hub?api-version=2024-07-01")));
            Assert.Throws<DomainException>(() => FabricC1Deployment.RequireSharedHubMutationScope(run.Input, method,
                new Uri("https://management.azure.com/subscriptions/" + run.Input.SubscriptionId + "/providers/Microsoft.Authorization/roleDefinitions/" +
                    Guid.NewGuid().ToString("D") + "?api-version=2022-04-01")));
        }
        FabricC1Deployment.RequireSharedHubMutationScope(run.Input, HttpMethod.Delete,
            new Uri("https://management.azure.com/subscriptions/" + run.Input.SubscriptionId + "/providers/Microsoft.Authorization/roleDefinitions/" +
                run.Input.DeploymentId + "?api-version=2022-04-01"));
        await Assert.ThrowsAsync<DomainException>(() => run.Provisioner.EnsureOperatorNetworkIdentity(null!, default));
        Assert.Equal(count, run.Api.Requests.Count);
    }

    [Fact]
    public async Task SpokeStageUsesTheOriginalArmJournalAndWritesOnlyItsOwnResourceGroup()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        var request = run.Provisioner.PrepareSharedHubStage("hub-spoke");
        var group = new JsonObject { ["id"] = hub.OwnGroup, ["tags"] = FabricC1Deployment.Tags(run.Input) };
        run.Seed("resource-group", "ResourceGroup", group);
        hub.SeedSpoke(record: false);
        hub.Queue();
        run.Api.Reply(200, group);
        run.Api.Reply(404, new());
        run.Api.List();
        run.Api.Reply(202, new());
        var deployment = new JsonObject
        {
            ["id"] = hub.OwnGroup + "/providers/Microsoft.Resources/deployments/c1-hub-spoke",
            ["properties"] = new JsonObject
            {
                ["mode"] = "Incremental", ["parameters"] = request.Parameters.DeepClone(), ["provisioningState"] = "Succeeded",
                ["correlationId"] = Guid.NewGuid().ToString("D"), ["templateHash"] = "synthetic-template-hash",
                ["outputs"] = new JsonObject { ["vnet"] = new JsonObject { ["value"] = hub.Resources[hub.SpokeVnet].DeepClone() } }
            }
        };
        run.Api.Reply(200, deployment);
        run.Api.List(hub.Resources.Where(item => item.Key.StartsWith(hub.OwnGroup + "/", StringComparison.Ordinal)).Select(item => item.Value).ToArray());
        var result = await run.Provisioner.DeployArm("hub-spoke", request.Template, request.Parameters, default);
        Assert.Equal(deployment.ToJsonString(), result.ToJsonString());
        Assert.True(run.Store.Current!.Objects.ContainsKey("arm-hub-spoke"));
        Assert.False(run.Store.Current.PendingCreates.ContainsKey("arm-hub-spoke"));
        var mutation = Assert.Single(run.Api.Requests, call => call.Method != HttpMethod.Get);
        Assert.Equal(HttpMethod.Put, mutation.Method);
        Assert.Equal(deployment["id"]!.GetValue<string>(), mutation.Uri.AbsolutePath);
    }

    [Fact]
    public async Task UnknownSpokeCreateCannotReplayOrResetItsBudget()
    {
        using var run = new Harness(sharedHub: true, maxRequests: 500);
        var hub = new HubFixture(run);
        await hub.Import();
        var request = run.Provisioner.PrepareSharedHubStage("hub-spoke");
        var group = new JsonObject { ["id"] = hub.OwnGroup, ["tags"] = FabricC1Deployment.Tags(run.Input) };
        run.Seed("resource-group", "ResourceGroup", group);
        hub.Queue();
        run.Api.Reply(200, group);
        run.Api.Reply(404, new());
        run.Api.List();
        run.Api.Fail(new HttpRequestException("synthetic lost ARM acknowledgement"));
        await Assert.ThrowsAsync<HttpRequestException>(() => run.Provisioner.DeployArm("hub-spoke", request.Template, request.Parameters, default));
        var count = run.Store.Current!.Requests;
        var start = run.Store.Current.StartedAtUtc;
        Assert.True(run.Store.Current.PendingCreates.ContainsKey("arm-hub-spoke"));
        run.Restart();
        hub.Queue();
        run.Api.Reply(200, group);
        run.Api.Reply(404, new());
        Assert.Equal("c1_deploy_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            run.Provisioner.DeployArm("hub-spoke", request.Template, request.Parameters, default))).Code);
        Assert.Equal(start, run.Store.Current!.StartedAtUtc);
        Assert.True(run.Store.Current.Requests > count);
        Assert.Single(run.Api.Requests, call => call.Method == HttpMethod.Put);
    }

    private sealed class HubFixture
    {
        private readonly Harness run;
        public JsonObject Receipt { get; }
        public Dictionary<string, JsonObject> Resources { get; } = new(StringComparer.Ordinal);
        public string HubGroup => run.Input.Network!.SharedHub!.ResourceGroupId!;
        public string OwnGroup => FabricC1Deployment.GroupId(run.Input);
        public string Name => FabricC1Deployment.Name(run.Input);
        public string HubVnet => HubGroup + "/providers/Microsoft.Network/virtualNetworks/hub";
        public string SpokeVnet => OwnGroup + "/providers/Microsoft.Network/virtualNetworks/" + Name + "-vnet";
        public string FirewallPolicy => HubGroup + "/providers/Microsoft.Network/firewallPolicies/policy";
        private string Firewall => HubGroup + "/providers/Microsoft.Network/azureFirewalls/firewall";
        private string Gateway => HubGroup + "/providers/Microsoft.Network/virtualNetworkGateways/gateway";
        private string DnsPolicy => HubGroup + "/providers/Microsoft.Network/dnsResolverPolicies/dns";
        private readonly string vnetGeneration = Guid.NewGuid().ToString("D");

        public HubFixture(Harness run)
        {
            this.run = run;
            var binding = run.Input.Network!.SharedHub!;
            var hubSubnets = new JsonObject
            {
                ["firewall"] = Subnet(HubVnet, "AzureFirewallSubnet", "10.242.0.0/26"),
                ["gateway"] = Subnet(HubVnet, "GatewaySubnet", "10.242.1.0/27"),
                ["waf"] = Subnet(HubVnet, "snet-waf", "10.242.2.0/24"),
                ["endpoints"] = Subnet(HubVnet, "snet-hub-endpoints", "10.242.3.0/27")
            };
            var spokeSubnets = new JsonObject
            {
                ["client"] = Subnet(SpokeVnet, "client", run.Input.Network.ClientPrefix!),
                ["agents"] = Subnet(SpokeVnet, "agents", run.Input.Network.AgentPrefix!),
                ["endpoints"] = Subnet(SpokeVnet, "endpoints", run.Input.Network.EndpointPrefix!),
                ["operatorEndpoints"] = Subnet(SpokeVnet, "operator-endpoints", binding.OperatorEndpointPrefix!)
            };
            Receipt = new()
            {
                ["contract"] = FabricC1Deployment.SharedHubContract, ["generation"] = binding.Generation, ["revision"] = 1,
                ["hubBindingHash"] = binding.HubBindingHash, ["consumer"] = "fabric", ["consumerBindingHash"] = Canonical.Hash(run.Input),
                ["state"] = "reserved", ["expiresAtUtc"] = run.Input.ExpiresAtUtc!.Value.ToString("O"),
                ["hub"] = new JsonObject
                {
                    ["resourceGroupId"] = HubGroup, ["vnetId"] = HubVnet, ["location"] = run.Input.Location,
                    ["addressPrefix"] = "10.242.0.0/20", ["subnets"] = hubSubnets,
                    ["firewallId"] = Firewall, ["firewallPolicyId"] = FirewallPolicy, ["firewallPrivateIp"] = "10.242.0.4",
                    ["vpnGatewayId"] = Gateway, ["dnsPolicyId"] = DnsPolicy, ["dnsServerIp"] = "10.242.0.4", ["wafId"] = null, ["wafPrivateIp"] = null
                },
                ["spoke"] = new JsonObject
                {
                    ["resourceGroupId"] = OwnGroup, ["vnetId"] = SpokeVnet, ["location"] = run.Input.Location,
                    ["addressPrefix"] = run.Input.Network.VnetPrefix, ["subnets"] = spokeSubnets
                },
                ["peering"] = null,
                ["vpn"] = new JsonObject
                {
                    ["audienceClientId"] = Guid.NewGuid().ToString("D"), ["servicePrincipalObjectId"] = Guid.NewGuid().ToString("D"),
                    ["assignmentRequired"] = true, ["userSetSha256"] = binding.VpnUserSetSha256,
                    ["resolvedUsersEvidenceSha256"] = binding.ResolvedUsersEvidenceSha256, ["businessRolesChanged"] = false, ["clientPool"] = "10.243.0.0/24"
                },
                ["dns"] = new JsonObject { ["owner"] = "hub", ["registrationMode"] = "hub-owner-only-no-consumer-zone-groups", ["zones"] = new JsonArray(), ["recordSets"] = new JsonArray() },
                ["egress"] = new JsonObject
                {
                    ["defaultNextHop"] = "VirtualAppliance", ["nextHopIp"] = "10.242.0.4", ["bgpPropagation"] = true,
                    ["hubPrefixRoute"] = false, ["spokeToSpoke"] = false, ["vpnDatabaseAccess"] = false,
                    ["dnsDomains"] = JsonSerializer.SerializeToNode(binding.DnsDomains),
                    ["httpsRules"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "fabric-control", ["sourceAddresses"] = new JsonArray(run.Input.Network.ClientPrefix, run.Input.Network.AgentPrefix),
                        ["targetFqdns"] = new JsonArray("login.microsoftonline.com", "management.azure.com")
                    })
                },
                ["web"] = null, ["operatorEndpoints"] = new JsonArray(),
                ["ownership"] = new JsonObject
                {
                    ["hubLifecycleOwner"] = "hub", ["consumerMayCreateHub"] = false, ["consumerMayDeleteHub"] = false,
                    ["consumerMayChangeSharedDirectory"] = false, ["consumerMayChangeHubDns"] = false,
                    ["offboarding"] = "stop-own-work-then-owner-releases-exact-consumer-references", ["sharedFailureDomain"] = true
                },
                ["readback"] = new JsonObject
                {
                    ["observedAtUtc"] = run.Clock.GetUtcNow().ToString("O"), ["infrastructureCommit"] = binding.InfrastructureCommit,
                    ["compiledTemplateSha256"] = binding.CompiledTemplateSha256,
                    ["deploymentResourceId"] = "/subscriptions/" + run.Input.SubscriptionId + "/providers/Microsoft.Resources/deployments/synthetic-hub",
                    ["resources"] = new JsonArray(), ["trafficAccepted"] = false, ["capacityAccepted"] = false, ["costAccepted"] = false
                }
            };
            Add(HubVnet, VnetProperties("10.242.0.0/20", hubSubnets));
            Add(Firewall, new()
            {
                ["sku"] = new JsonObject { ["name"] = "AZFW_VNet", ["tier"] = "Standard" },
                ["firewallPolicy"] = new JsonObject { ["id"] = FirewallPolicy },
                ["ipConfigurations"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject
                { ["privateIPAddress"] = "10.242.0.4", ["subnet"] = new JsonObject { ["id"] = hubSubnets["firewall"]!["id"]!.DeepClone() } } })
            });
            Add(FirewallPolicy, new()
            {
                ["sku"] = new JsonObject { ["tier"] = "Standard" }, ["threatIntelMode"] = "Deny",
                ["dnsSettings"] = new JsonObject { ["enableProxy"] = true, ["requireProxyForNetworkRules"] = true }
            });
            Add(DnsPolicy, new());
            Add(HubGroup + "/providers/Microsoft.Network/dnsResolverDomainLists/allow", new()
            { ["domains"] = new JsonArray("management.azure.com", "login.microsoftonline.com") });
            Add(HubGroup + "/providers/Microsoft.Network/dnsResolverDomainLists/deny", new() { ["domains"] = new JsonArray(".") });
            foreach (var (name, action, priority) in new[] { ("allow", "Allow", 100), ("deny", "Block", 65000) })
                Add(DnsPolicy + "/dnsSecurityRules/" + name, new()
                {
                    ["dnsSecurityRuleState"] = "Enabled", ["priority"] = priority,
                    ["action"] = new JsonObject { ["actionType"] = action },
                    ["dnsResolverDomainLists"] = new JsonArray(new JsonObject { ["id"] = HubGroup + "/providers/Microsoft.Network/dnsResolverDomainLists/" + name })
                });
            Add(DnsPolicy + "/virtualNetworkLinks/hub", new() { ["virtualNetwork"] = new JsonObject { ["id"] = HubVnet } });
            Add(FirewallPolicy + "/ruleCollectionGroups/https", new() { ["priority"] = 200, ["ruleCollections"] = new JsonArray() });
            Add(FirewallPolicy + "/ruleCollectionGroups/private", new() { ["priority"] = 100, ["ruleCollections"] = new JsonArray() });
            Add(Gateway, new()
            {
                ["gatewayType"] = "Vpn", ["vpnType"] = "RouteBased", ["vpnGatewayGeneration"] = "Generation1",
                ["enableBgp"] = false, ["activeActive"] = false, ["sku"] = new JsonObject { ["name"] = "VpnGw1AZ" },
                ["vpnClientConfiguration"] = new JsonObject
                {
                    ["vpnClientAddressPool"] = new JsonObject { ["addressPrefixes"] = new JsonArray("10.243.0.0/24") },
                    ["vpnClientProtocols"] = new JsonArray("OpenVPN"), ["vpnAuthenticationTypes"] = new JsonArray("AAD"),
                    ["aadAudience"] = Receipt["vpn"]!["audienceClientId"]!.DeepClone(),
                    ["aadTenant"] = "https://login.microsoftonline.com/" + run.Input.TenantId,
                    ["aadIssuer"] = "https://sts.windows.net/" + run.Input.TenantId + "/"
                }
            });
            RefreshHashes();
        }

        public void SeedSpoke(bool record = true)
        {
            var request = run.Provisioner.PrepareSharedHubStage("hub-spoke");
            var subnets = Receipt["spoke"]!["subnets"]!.AsObject();
            var properties = VnetProperties(run.Input.Network!.VnetPrefix!, subnets);
            properties["resourceGuid"] = vnetGeneration;
            foreach (var subnet in properties["subnets"]!.AsArray())
            {
                var name = subnet!["name"]!.GetValue<string>();
                var workload = name is "client" or "agents";
                var suffix = workload ? "-nsg" : name == "endpoints" ? "-endpoint-nsg" : "-operator-endpoint-nsg";
                subnet["properties"]!["networkSecurityGroup"] = new JsonObject { ["id"] = OwnGroup + "/providers/Microsoft.Network/networkSecurityGroups/" + Name + suffix };
                if (workload)
                {
                    subnet["properties"]!["defaultOutboundAccess"] = false;
                    subnet["properties"]!["routeTable"] = new JsonObject { ["id"] = OwnGroup + "/providers/Microsoft.Network/routeTables/" + Name + "-egress-routes" };
                }
                else subnet["properties"]!["privateEndpointNetworkPolicies"] = "Enabled";
            }
            Add(SpokeVnet, properties, own: true);
            Add(OwnGroup + "/providers/Microsoft.Network/routeTables/" + Name + "-egress-routes", new()
            {
                ["disableBgpRoutePropagation"] = false, ["routes"] = new JsonArray(new JsonObject
                { ["name"] = "ReviewedHubFirewallEgress", ["properties"] = new JsonObject
                    { ["addressPrefix"] = "0.0.0.0/0", ["nextHopType"] = "VirtualAppliance", ["nextHopIpAddress"] = "10.242.0.4" } })
            }, own: true);
            foreach (var nsg in request.Template["resources"]!.AsArray().OfType<JsonObject>().Where(item => item["type"]!.GetValue<string>() == "Microsoft.Network/networkSecurityGroups"))
            {
                var suffix = nsg["name"]!.GetValue<string>().Split('\'')[3];
                var raw = nsg["properties"]!.ToJsonString();
                foreach (var (expression, replacement) in new Dictionary<string, string>
                {
                    ["[parameters('network').clientPrefix]"] = run.Input.Network.ClientPrefix!,
                    ["[parameters('network').agentPrefix]"] = run.Input.Network.AgentPrefix!,
                    ["[parameters('network').endpointPrefix]"] = run.Input.Network.EndpointPrefix!,
                    ["[parameters('network').operatorEndpointPrefix]"] = run.Input.Network.SharedHub!.OperatorEndpointPrefix!,
                    ["[parameters('firewallPrivateIp')]"] = "10.242.0.4"
                })
                    raw = raw.Replace(JsonSerializer.Serialize(expression), JsonSerializer.Serialize(replacement), StringComparison.Ordinal);
                Add(OwnGroup + "/providers/Microsoft.Network/networkSecurityGroups/" + Name + suffix, JsonNode.Parse(raw)!.AsObject(), own: true);
            }
            if (record)
                SeedDeployment("hub-spoke", request, new JsonObject { ["vnet"] = new JsonObject { ["value"] = Resources[SpokeVnet].DeepClone() } });
        }

        public void SeedPeer(string stage, FabricC1HubArmRequest request) =>
            SeedDeployment(stage, request, new JsonObject
            {
                ["peering"] = new JsonObject { ["value"] = PeerResource(SpokeVnet + "/virtualNetworkPeerings/to-shared-hub",
                    HubVnet, false, stage == "hub-transit") }
            });

        public static async Task<HubFixture> Connected(Harness run)
        {
            var hub = new HubFixture(run);
            await hub.Import();
            hub.SeedSpoke();
            hub.SeedPeer("hub-peer", run.Provisioner.PrepareSharedHubStage("hub-peer"));
            hub.SetState("peered", 2);
            await hub.Import();
            hub.SeedPeer("hub-transit", run.Provisioner.PrepareSharedHubStage("hub-transit"));
            hub.SetState("connected", 3);
            await hub.Import();
            return hub;
        }

        public FabricC1HubArmRequest SeedEndpoints()
        {
            SeedDeployment("foundation", run.Provisioner.PrepareSharedHubStage("foundation"), new());
            var workspaces = new JsonArray();
            foreach (var scope in new[] { "agent", "released", "denied" })
            {
                var id = Guid.NewGuid().ToString("D");
                run.Seed("workspace-" + scope, "Workspace", new() { ["id"] = id });
                workspaces.Add(new JsonObject { ["key"] = scope, ["id"] = id });
            }
            var endpoints = new JsonArray(new[]
            {
                ("vault", "Microsoft.KeyVault/vaults/" + Name + "kv", "vault"),
                ("blob", "Microsoft.Storage/storageAccounts/" + Name + "st", "blob"),
                ("registry", "Microsoft.ContainerRegistry/registries/" + Name + "acr", "registry"),
                ("foundry", "Microsoft.CognitiveServices/accounts/" + Name + "-ai", "account"),
                ("agent", "Microsoft.Fabric/privateLinkServicesForFabric/" + Name + "-agent-pls", "workspace"),
                ("released", "Microsoft.Fabric/privateLinkServicesForFabric/" + Name + "-released-pls", "workspace"),
                ("denied", "Microsoft.Fabric/privateLinkServicesForFabric/" + Name + "-denied-pls", "workspace")
            }.Select(target => (JsonNode)new JsonObject
            {
                ["key"] = target.Item1, ["resourceId"] = OwnGroup + "/providers/" + target.Item2,
                ["groupIds"] = new JsonArray(target.Item3)
            }).ToArray());
            var request = run.Provisioner.PrepareSharedHubStage("private-endpoints", new()
            {
                ["name"] = new JsonObject { ["value"] = Name }, ["location"] = new JsonObject { ["value"] = run.Input.Location },
                ["tenantId"] = new JsonObject { ["value"] = run.Input.TenantId },
                ["tags"] = new JsonObject { ["value"] = FabricC1Deployment.Tags(run.Input) },
                ["workspaces"] = new JsonObject { ["value"] = workspaces }, ["endpoints"] = new JsonObject { ["value"] = endpoints }
            });
            var originals = new JsonArray();
            var children = new List<(string Id, string Parent, string Instance)>();
            for (var index = 0; index < endpoints.Count; index++)
            {
                var target = endpoints[index]!;
                var key = target["key"]!.GetValue<string>();
                var id = OwnGroup + "/providers/Microsoft.Network/privateEndpoints/" + Name + "-" + key + "-pe";
                var nicId = OwnGroup + "/providers/Microsoft.Network/networkInterfaces/" + Name + "-" + key + "-nic";
                var instance = Guid.NewGuid().ToString("D");
                var isOperator = key is not ("registry" or "foundry");
                var subnet = SpokeVnet + "/subnets/" + (isOperator ? "operator-endpoints" : "endpoints");
                var prefix = isOperator ? run.Input.Network!.SharedHub!.OperatorEndpointPrefix! : run.Input.Network!.EndpointPrefix!;
                var address = string.Join(".", prefix.Split('.').Take(3)) + "." + (index + 5);
                var host = key == "vault" ? Name + "kv.vault.azure.net" : key + ".synthetic.example.invalid";
                Add(id, new()
                {
                    ["subnet"] = new JsonObject { ["id"] = subnet },
                    ["privateLinkServiceConnections"] = new JsonArray(new JsonObject { ["name"] = "c1", ["properties"] = new JsonObject
                    {
                        ["privateLinkServiceId"] = target["resourceId"]!.DeepClone(), ["groupIds"] = target["groupIds"]!.DeepClone(),
                        ["privateLinkServiceConnectionState"] = new JsonObject { ["status"] = "Approved" }
                    }}),
                    ["networkInterfaces"] = new JsonArray(new JsonObject { ["id"] = nicId }),
                    ["customDnsConfigs"] = new JsonArray(new JsonObject { ["fqdn"] = host, ["ipAddresses"] = new JsonArray(address) })
                }, own: true);
                Add(nicId, new()
                {
                    ["resourceGuid"] = instance, ["privateEndpoint"] = new JsonObject { ["id"] = id },
                    ["ipConfigurations"] = new JsonArray(new JsonObject { ["name"] = "private", ["properties"] = new JsonObject
                    {
                        ["privateIPAddress"] = address, ["subnet"] = new JsonObject { ["id"] = subnet },
                        ["privateLinkConnectionProperties"] = new JsonObject { ["fqdns"] = new JsonArray(host) }
                    }})
                }, own: true);
                Resources[nicId]["tags"] = new JsonObject();
                originals.Add(Resources[id].DeepClone());
                children.Add((nicId, id, instance));
                if (key == "vault")
                    Receipt["operatorEndpoints"] = new JsonArray(new JsonObject
                    {
                        ["privateEndpointId"] = id, ["addresses"] = new JsonArray(address),
                        ["fqdns"] = new JsonArray(host), ["port"] = 443
                    });
            }
            SeedDeployment("private-endpoints", request, new() { ["privateEndpoints"] = new JsonObject { ["value"] = originals } });
            foreach (var child in children)
                run.Seed("arm-child-" + Canonical.HashText(child.Id.ToLowerInvariant()), "ArmGeneratedChild", new()
                {
                    ["id"] = child.Id, ["parentId"] = child.Parent, ["instanceId"] = child.Instance,
                    ["parentRequestHash"] = run.Store.Current!.Objects["arm-private-endpoints"].RequestHash
                });
            return request;
        }

        private void SeedDeployment(string stage, FabricC1HubArmRequest request, JsonObject outputs)
        {
            var body = new JsonObject { ["properties"] = new JsonObject
            { ["mode"] = "Incremental", ["template"] = request.Template.DeepClone(), ["parameters"] = request.Parameters.DeepClone() }};
            run.Seed("arm-" + stage, "ArmDeployment", new()
            {
                ["id"] = OwnGroup + "/providers/Microsoft.Resources/deployments/c1-" + stage,
                ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded", ["outputs"] = outputs }
            }, Canonical.Hash(body));
        }

        public void SetState(string state, int revision)
        {
            Receipt["state"] = state;
            Receipt["revision"] = revision;
            Receipt["peering"] = new JsonObject
            {
                ["hubToSpoke"] = WirePeer(HubVnet + "/virtualNetworkPeerings/to-fabric", SpokeVnet, true, false),
                ["spokeToHub"] = WirePeer(SpokeVnet + "/virtualNetworkPeerings/to-shared-hub", HubVnet, false, state != "peered")
            };
            Resources[HubVnet + "/virtualNetworkPeerings/to-fabric"] = PeerResource(HubVnet + "/virtualNetworkPeerings/to-fabric", SpokeVnet, true, false);
            Resources[SpokeVnet + "/virtualNetworkPeerings/to-shared-hub"] = PeerResource(SpokeVnet + "/virtualNetworkPeerings/to-shared-hub", HubVnet, false, state != "peered");
            var allowed = Receipt["egress"]!["httpsRules"]![0]!.DeepClone();
            allowed["ruleType"] = "ApplicationRule"; allowed["terminateTLS"] = false;
            allowed["protocols"] = new JsonArray(new JsonObject { ["protocolType"] = "Https", ["port"] = 443 });
            Resources[FirewallPolicy + "/ruleCollectionGroups/https"]["properties"]!["ruleCollections"] = new JsonArray(new JsonObject
            {
                ["priority"] = 200, ["ruleCollectionType"] = "FirewallPolicyFilterRuleCollection",
                ["action"] = new JsonObject { ["type"] = "Allow" }, ["rules"] = new JsonArray(allowed)
            });
            Resources[FirewallPolicy + "/ruleCollectionGroups/private"]["properties"]!["ruleCollections"] = new JsonArray(new JsonObject
            {
                ["priority"] = 100, ["ruleCollectionType"] = "FirewallPolicyFilterRuleCollection",
                ["action"] = new JsonObject { ["type"] = "Deny" }, ["rules"] = new JsonArray(new JsonObject
                {
                    ["name"] = "no-implicit-private-transit", ["ruleType"] = "NetworkRule",
                    ["sourceAddresses"] = new JsonArray(Receipt["spoke"]!["subnets"]!.AsObject()
                        .Select(item => item.Value!["addressPrefix"]!.DeepClone()).Append(JsonValue.Create("10.243.0.0/24")!).ToArray()),
                    ["ipProtocols"] = new JsonArray("Any"), ["destinationPorts"] = new JsonArray("*"),
                    ["destinationAddresses"] = new JsonArray("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")
                })
            });
            RefreshHashes();
        }

        private void Add(string id, JsonObject properties, bool own = false)
        {
            properties["provisioningState"] = "Succeeded";
            Resources[id] = new()
            {
                ["id"] = id, ["etag"] = "synthetic-etag", ["location"] = run.Input.Location,
                ["type"] = string.Join("/", id[(id.IndexOf("/providers/", StringComparison.Ordinal) + "/providers/".Length)..].Split('/').Take(2)),
                ["tags"] = own ? FabricC1Deployment.Tags(run.Input) : new JsonObject { ["cohorsBinding"] = run.Input.Network!.SharedHub!.HubBindingHash },
                ["properties"] = properties
            };
        }

        private static JsonObject Subnet(string vnet, string name, string prefix) => new() { ["id"] = vnet + "/subnets/" + name, ["addressPrefix"] = prefix };
        private static JsonObject VnetProperties(string prefix, JsonObject subnets) => new()
        {
            ["addressSpace"] = new JsonObject { ["addressPrefixes"] = new JsonArray(prefix) },
            ["dhcpOptions"] = new JsonObject { ["dnsServers"] = new JsonArray("10.242.0.4") },
            ["subnets"] = new JsonArray(subnets.Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.Value!["id"]!.DeepClone(), ["name"] = item.Value["id"]!.GetValue<string>().Split('/')[^1],
                ["properties"] = new JsonObject { ["addressPrefix"] = item.Value["addressPrefix"]!.DeepClone() }
            }).ToArray())
        };
        private static JsonObject WirePeer(string id, string remote, bool hub, bool transit) => new()
        {
            ["id"] = id, ["remoteVirtualNetworkId"] = remote, ["allowVirtualNetworkAccess"] = true,
            ["allowForwardedTraffic"] = !hub, ["allowGatewayTransit"] = hub, ["useRemoteGateways"] = transit, ["peeringState"] = "Connected"
        };
        private static JsonObject PeerResource(string id, string remote, bool hub, bool transit) => new()
        {
            ["id"] = id, ["etag"] = "synthetic-etag", ["properties"] = new JsonObject
            {
                ["provisioningState"] = "Succeeded", ["remoteVirtualNetwork"] = new JsonObject { ["id"] = remote },
                ["allowVirtualNetworkAccess"] = true, ["allowForwardedTraffic"] = !hub, ["allowGatewayTransit"] = hub,
                ["useRemoteGateways"] = transit, ["peeringState"] = "Connected"
            }
        };
        public void RefreshHashes()
        {
            Receipt["readback"]!["resources"] = new JsonArray(Resources.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (JsonNode)new JsonObject
            {
                ["id"] = item.Key, ["apiVersion"] = item.Key.Contains("/dnsResolver", StringComparison.Ordinal) ? "2025-05-01" : "2024-07-01",
                ["sha256"] = FabricC1Deployment.HubWireHash(item.Value), ["etag"] = item.Value["etag"]!.DeepClone()
            }).ToArray());
            Sign();
        }
        public void Sign()
        {
            Receipt.Remove("receiptSha256");
            Receipt["receiptSha256"] = FabricC1Deployment.HubWireHash(Receipt);
        }
        public void Queue(string? firstEtag = null, JsonObject? extraPolicy = null)
        {
            var index = 0;
            foreach (var item in Receipt["readback"]!["resources"]!.AsArray())
                run.Api.Reply(200, Resources[item!["id"]!.GetValue<string>()], index++ == 0 ? firstEtag : null);
            foreach (var prefix in new[] { FirewallPolicy + "/ruleCollectionGroups/", DnsPolicy + "/dnsSecurityRules/", DnsPolicy + "/virtualNetworkLinks/" })
            {
                var entries = Resources.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.Value);
                if (prefix == FirewallPolicy + "/ruleCollectionGroups/" && extraPolicy is not null) entries = entries.Append(extraPolicy);
                run.Api.List(entries.ToArray());
            }
            if (Receipt["state"]!.GetValue<string>() != "reserved")
                run.Api.List(Resources[SpokeVnet + "/virtualNetworkPeerings/to-shared-hub"]);
        }
        public Task<FabricC1HubConsumerResult> Import()
        {
            Queue();
            return run.Provisioner.ImportSharedHubReceipt(Receipt, default);
        }
    }
}
