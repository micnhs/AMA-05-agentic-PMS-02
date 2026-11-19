using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1HubArmRequest(JsonObject Template, JsonObject Parameters);

public sealed partial class FabricC1Provisioner
{
    public FabricC1HubArmRequest PrepareSharedHubStage(string stage, JsonObject? endpointParameters = null)
    {
        RequireCertainCheckpointJournal();
        PlanNormalRequests(0);
        var receipt = RequireStoredHubReceipt();
        var minimum = stage switch
        {
            "hub-spoke" or "hub-peer" => "reserved",
            "hub-transit" => "peered",
            "foundation" or "private-endpoints" => "connected",
            "hub-operator-access" => "data-bound",
            _ => throw new DomainException("c1_hub_stage", "Only the source-owned spoke stages are supported; no hub/network-access stage.", 409)
        };
        var current = Text(receipt, "state");
        Guard.Against(HubStateIndex(current) < HubStateIndex(minimum) ||
            (stage is "hub-spoke" or "hub-peer") && current != "reserved" ||
            stage == "hub-transit" && current != "peered",
            "c1_hub_stage", "The current owner receipt does not admit this handshake stage.", 409);
        if (stage != "hub-spoke") Owned("arm-hub-spoke", "ArmDeployment");
        if (stage is "foundation" or "private-endpoints" or "hub-operator-access") Owned("arm-hub-transit", "ArmDeployment");
        if (stage == "private-endpoints") Owned("arm-foundation", "ArmDeployment");
        var network = new JsonObject
        {
            ["vnetPrefix"] = input.Network!.VnetPrefix, ["clientPrefix"] = input.Network.ClientPrefix,
            ["agentPrefix"] = input.Network.AgentPrefix, ["endpointPrefix"] = input.Network.EndpointPrefix,
            ["operatorEndpointPrefix"] = input.Network.SharedHub!.OperatorEndpointPrefix
        };
        var common = new JsonObject
        {
            ["name"] = HubParameter(FabricC1Deployment.Name(input)), ["location"] = HubParameter(input.Location),
            ["tags"] = HubParameter(FabricC1Deployment.Tags(input)), ["network"] = HubParameter(network),
            ["firewallPrivateIp"] = HubParameter(Text(HubObject(receipt, "hub"), "firewallPrivateIp"))
        };
        switch (stage)
        {
            case "hub-spoke":
                return new(HubTemplate("c1.hub-spoke.json"), common);
            case "hub-peer":
            case "hub-transit":
                return new(HubTemplate("c1.hub-peering.json"), new()
                {
                    ["name"] = common["name"]!.DeepClone(), ["hubVnetId"] = HubParameter(Text(HubObject(receipt, "hub"), "vnetId")),
                    ["useRemoteGateways"] = HubParameter(JsonValue.Create(stage == "hub-transit"))
                });
            case "foundation":
                var foundation = HubTemplate("Foundation.json");
                var foundationResources = HubArray(foundation, "resources");
                var networkTypes = new HashSet<string>(StringComparer.Ordinal)
                {
                    "Microsoft.Network/routeTables", "Microsoft.Network/networkSecurityGroups", "Microsoft.Network/virtualNetworks",
                    "Microsoft.Network/privateDnsZones", "Microsoft.Network/privateDnsZones/virtualNetworkLinks"
                };
                // Reuse the original workload definitions; the explicit spoke stage owns all networking.
                for (var index = foundationResources.Count - 1; index >= 0; index--)
                {
                    var resource = foundationResources[index]!.AsObject();
                    if (networkTypes.Contains(Text(resource, "type"))) { foundationResources.RemoveAt(index); continue; }
                    if (resource["dependsOn"] is JsonArray dependencies)
                        for (var dependency = dependencies.Count - 1; dependency >= 0; dependency--)
                            if (dependencies[dependency]!.GetValue<string>() ==
                                "[resourceId('Microsoft.Network/virtualNetworks', concat(parameters('name'), '-vnet'))]")
                                dependencies.RemoveAt(dependency);
                }
                foundation.Remove("variables");
                HubObject(foundation, "parameters").Remove("firewallPrivateIp");
                HubObject(foundation, "parameters").Remove("network");
                var parameters = FabricC1Deployment.ArmParameters(input);
                parameters.Remove("network");
                return new(foundation, parameters);
            case "private-endpoints":
                var endpoints = HubTemplate("WorkspaceNetwork.json");
                var resources = HubArray(endpoints, "resources");
                for (var index = resources.Count - 1; index >= 0; index--)
                    if (resources[index]!["type"]!.GetValue<string>() == "Microsoft.Network/privateEndpoints/privateDnsZoneGroups")
                        resources.RemoveAt(index);
                var privateParameters = endpointParameters is null ? throw new DomainException("c1_hub_endpoints",
                    "Private endpoint parameters must come from the original own workspace/service receipts.", 409) :
                    (JsonObject)endpointParameters.DeepClone();
                ValidateHubEndpointParameters(privateParameters);
                return new(endpoints, privateParameters);
            default:
                var template = HubTemplate("c1.hub-spoke.json");
                var nsg = HubArray(template, "resources").OfType<JsonObject>().Single(resource =>
                    resource["name"]?.GetValue<string>() == "[concat(parameters('name'), '-operator-endpoint-nsg')]");
                var access = HubArray(receipt, "operatorEndpoints");
                Guard.Against(access.Count == 0, "c1_hub_endpoints", "An empty operator binding grants no VPN access.", 409);
                HubArray(HubObject(nsg, "properties"), "securityRules").Add(new JsonObject
                {
                    ["name"] = "BoundOperatorPrivateHttps",
                    ["properties"] = new JsonObject
                    {
                        ["priority"] = 210, ["direction"] = "Inbound", ["access"] = "Allow", ["protocol"] = "Tcp",
                        ["sourcePortRange"] = "*", ["destinationPortRange"] = "443",
                        ["sourceAddressPrefix"] = "[parameters('vpnClientPrefix')]",
                        ["destinationAddressPrefixes"] = "[parameters('operatorAddresses')]"
                    }
                });
                template["resources"] = new JsonArray(nsg.DeepClone());
                template["outputs"] = new JsonObject
                {
                    ["operatorNsg"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["value"] = "[reference(resourceId('Microsoft.Network/networkSecurityGroups', concat(parameters('name'), '-operator-endpoint-nsg')), '2024-07-01', 'Full')]"
                    }
                };
                var declarations = HubObject(template, "parameters");
                declarations.Remove("firewallPrivateIp");
                declarations["vpnClientPrefix"] = new JsonObject { ["type"] = "string" };
                declarations["operatorAddresses"] = new JsonObject { ["type"] = "array", ["minLength"] = 1 };
                common.Remove("firewallPrivateIp");
                common["vpnClientPrefix"] = HubParameter(Text(HubObject(receipt, "vpn"), "clientPool"));
                common["operatorAddresses"] = HubParameter(JsonSerializer.SerializeToNode(access.OfType<JsonObject>()
                    .SelectMany(endpoint => HubArray(endpoint, "addresses").Select(address => address!.GetValue<string>()))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
                return new(template, common);
        }
    }

    private JsonObject RequireStoredHubReceipt()
    {
        var stored = state.Objects.TryGetValue(HubReceiptKey, out var saved) ? saved :
            throw new DomainException("c1_hub_receipt_missing", "Import the owner-issued receipt and actual ARM observations first.", 409);
        Guard.Against(stored.Kind != "SharedHubConsumerReceipt" || stored.RequestHash != Text(stored.Response, "receiptSha256") ||
            stored.Id != input.Network?.SharedHub?.Generation, "c1_hub_journal", "Original hub receipt binding changed.", 409);
        ValidateHubReceipt(stored.Response, stored.Response);
        return (JsonObject)stored.Response.DeepClone();
    }

    private void RequireHubPeerReceipt(JsonObject receipt, bool remoteGateways)
    {
        var stage = remoteGateways ? "hub-transit" : "hub-peer";
        var key = "arm-" + stage;
        var original = Owned(key, "ArmDeployment");
        var parameters = new JsonObject
        {
            ["name"] = HubParameter(FabricC1Deployment.Name(input)), ["hubVnetId"] = HubParameter(Text(HubObject(receipt, "hub"), "vnetId")),
            ["useRemoteGateways"] = HubParameter(JsonValue.Create(remoteGateways))
        };
        var request = new JsonObject { ["properties"] = new JsonObject
        {
            ["mode"] = "Incremental", ["template"] = HubTemplate("c1.hub-peering.json"), ["parameters"] = parameters
        }};
        Guard.Against(state.Objects[key].RequestHash != Canonical.Hash(request) ||
            Text(original, "id") != FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Resources/deployments/c1-" + stage ||
            original["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_hub_peering", "Gateway handshakes require the completed original own peering request, not adopted ARM metadata.", 409);
        var peer = original["properties"]?["outputs"]?["peering"]?["value"] as JsonObject ??
            throw new DomainException("c1_hub_peering", "The original own peer output is missing.", 409);
        VerifyOwnHubPeer(peer, Text(HubObject(receipt, "hub"), "vnetId"), remoteGateways);
    }

    private void VerifyOwnHubPeer(JsonObject peer, string hubVnet, bool remoteGateways)
    {
        var expected = FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Network/virtualNetworks/" +
            FabricC1Deployment.Name(input) + "-vnet/virtualNetworkPeerings/to-shared-hub";
        var properties = HubObject(peer, "properties");
        Guard.Against(Text(peer, "id") != expected || properties["remoteVirtualNetwork"]?["id"]?.GetValue<string>() != hubVnet ||
            properties["allowVirtualNetworkAccess"]?.GetValue<bool>() != true ||
            properties["allowForwardedTraffic"]?.GetValue<bool>() != true ||
            properties["allowGatewayTransit"]?.GetValue<bool>() != false ||
            properties["useRemoteGateways"]?.GetValue<bool>() != remoteGateways ||
            properties["peeringState"]?.GetValue<string>() is not ("Initiated" or "Connected"),
            "c1_hub_peering", "Only the exact own peer and source-owned phase flags are allowed.", 409);
    }

    private async Task<bool> VerifyInventoriedHubPeer(JsonObject resource, string? candidateStage, string? candidateHash,
        JsonObject? candidate, CancellationToken ct)
    {
        if (input.Network?.SharedHub is null ||
            !string.Equals(resource["type"]?.GetValue<string>(), "Microsoft.Network/virtualNetworks/virtualNetworkPeerings", StringComparison.OrdinalIgnoreCase))
            return false;
        var stage = candidateStage is "hub-peer" or "hub-transit" ? candidateStage :
            state.Objects.ContainsKey("arm-hub-transit") ? "hub-transit" : "hub-peer";
        var saved = stage == candidateStage ? candidate : Owned("arm-" + stage, "ArmDeployment");
        var receipt = RequireStoredHubReceipt();
        Guard.Against(saved is null || saved["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded" ||
            saved["id"]?.GetValue<string>() != FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Resources/deployments/c1-" + stage,
            "c1_hub_peering", "An untagged own peer needs its completed original deployment.", 409);
        var output = saved!["properties"]?["outputs"]?["peering"]?["value"] as JsonObject ??
            throw new DomainException("c1_hub_peering", "The exact original own peer output is missing.", 409);
        Guard.Against(Text(resource, "id") != Text(output, "id"), "c1_hub_peering", "No unrelated untagged peering may be adopted.", 409);
        VerifyOwnHubPeer(output, Text(HubObject(receipt, "hub"), "vnetId"), stage == "hub-transit");
        var actual = await Read("https://management.azure.com" + Text(resource, "id") + "?api-version=" + HubNetworkApi, ct);
        VerifyOwnHubPeer(actual, Text(HubObject(receipt, "hub"), "vnetId"), stage == "hub-transit");
        Guard.Against(stage == candidateStage && !FabricC1Execution.Hex(candidateHash, 64),
            "c1_hub_peering", "The peering output requires the original durable request hash.", 409);
        return true;
    }

    private async Task ValidateSharedHubArmStage(string stage, JsonObject template, JsonObject parameters, CancellationToken ct)
    {
        var expected = PrepareSharedHubStage(stage, stage == "private-endpoints" ? parameters : null);
        Guard.Against(!JsonNode.DeepEquals(template, expected.Template) || !JsonNode.DeepEquals(parameters, expected.Parameters),
            "c1_hub_template", "Only the exact embedded spoke template and readback-derived parameters are allowed.", 409);
        var receipt = RequireStoredHubReceipt();
        var reconcilingOwnUpdate = (stage is "hub-transit" or "hub-operator-access") &&
            (state.PendingCreates.ContainsKey("arm-" + stage) || state.Objects.ContainsKey("arm-" + stage));
        var networkedWorkload = stage is "foundation" or "private-endpoints" or "hub-operator-access";
        await ReadHubObservations(receipt, ct, hubOnly: reconcilingOwnUpdate || networkedWorkload);
        if (networkedWorkload) await ReadOwnHubRegistrationNetwork(receipt, ct);
        if (stage is "hub-peer" or "hub-transit")
        {
            var vnet = Text(HubObject(receipt, "spoke"), "vnetId");
            var current = await Read("https://management.azure.com" + vnet + "?api-version=" + HubNetworkApi, ct);
            RequireArmOwnership(current);
            VerifyHubVnetAddresses(HubObject(current, "properties"), HubObject(receipt, "spoke"));
            var original = Owned("arm-hub-spoke", "ArmDeployment")["properties"]?["outputs"]?["vnet"]?["value"] as JsonObject ??
                throw new DomainException("c1_hub_spoke", "Original spoke creation output is missing.", 409);
            Guard.Against(Text(HubObject(original, "properties"), "resourceGuid") != Text(HubObject(current, "properties"), "resourceGuid"),
                "c1_hub_spoke", "A recreated VNet cannot inherit the original peering authority.", 409);
            var peers = await ReadAll("https://management.azure.com" + vnet + "/virtualNetworkPeerings?api-version=" + HubNetworkApi, false, ct);
            var peerId = vnet + "/virtualNetworkPeerings/to-shared-hub";
            Guard.Against(peers.Any(peer => Text(peer, "id") != peerId) || peers.Length > 1 ||
                stage == "hub-peer" && peers.Length != 0 && !state.PendingCreates.ContainsKey("arm-hub-peer") && !state.Objects.ContainsKey("arm-hub-peer") ||
                stage == "hub-transit" && !state.Objects.ContainsKey("arm-hub-peer"),
                "c1_hub_peering", "No unjournaled peer or additional transit relationship may be adopted.", 409);
            foreach (var peer in peers)
                VerifyOwnHubPeer(peer, Text(HubObject(receipt, "hub"), "vnetId"),
                    stage == "hub-transit" && (state.Objects.ContainsKey("arm-hub-transit") ||
                        state.PendingCreates.ContainsKey("arm-hub-transit") && peer["properties"]?["useRemoteGateways"]?.GetValue<bool>() == true));
        }
    }

    private void ValidateHubEndpointParameters(JsonObject parameters)
    {
        HubFields(parameters, "name", "location", "tenantId", "tags", "workspaces", "endpoints");
        var own = FabricC1Deployment.ArmParameters(input);
        foreach (var field in new[] { "name", "location", "tenantId", "tags" })
            Guard.Against(!JsonNode.DeepEquals(parameters[field], own[field]), "c1_hub_endpoints", "Private endpoint ownership parameters changed.", 409);
        var workspaces = parameters["workspaces"]?["value"] as JsonArray ??
            throw new DomainException("c1_hub_endpoints", "The three original workspaces are required.", 409);
        var expected = new JsonArray(new[] { "agent", "released", "denied" }.Select(scope => (JsonNode)new JsonObject
        { ["key"] = scope, ["id"] = Text(Owned("workspace-" + scope, "Workspace"), "id") }).ToArray());
        Guard.Against(!JsonNode.DeepEquals(workspaces, expected), "c1_hub_endpoints", "Workspace private links must use the exact own workspace set.", 409);
        var endpoints = parameters["endpoints"]?["value"] as JsonArray ??
            throw new DomainException("c1_hub_endpoints", "Own private endpoint definitions are required.", 409);
        var allowed = HubEndpointTargets();
        Guard.Against(endpoints.Count != allowed.Count, "c1_hub_endpoints", "The exact isolated endpoint set is required.", 409);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in endpoints)
        {
            if (item is not JsonObject endpoint) throw new DomainException("c1_hub_endpoints", "A complete endpoint definition is required.", 409);
            HubFields(endpoint, "key", "resourceId", "groupIds");
            Guard.Against(!allowed.TryGetValue(Text(endpoint, "key"), out var target) || !seen.Add(Text(endpoint, "key")) ||
                Text(endpoint, "resourceId") != target.Id || !JsonNode.DeepEquals(endpoint["groupIds"], new JsonArray(target.Group)),
                "c1_hub_endpoints", "No foreign target, shared-zone join or additional endpoint service is allowed.", 409);
        }
    }

    private Dictionary<string, (string Id, string Group)> HubEndpointTargets()
    {
        var group = FabricC1Deployment.GroupId(input);
        var name = FabricC1Deployment.Name(input);
        var targets = new Dictionary<string, (string Id, string Group)>(StringComparer.Ordinal)
        {
            ["vault"] = (group + "/providers/Microsoft.KeyVault/vaults/" + name + "kv", "vault"),
            ["blob"] = (group + "/providers/Microsoft.Storage/storageAccounts/" + name + "st", "blob"),
            ["registry"] = (group + "/providers/Microsoft.ContainerRegistry/registries/" + name + "acr", "registry"),
            ["foundry"] = (group + "/providers/Microsoft.CognitiveServices/accounts/" + name + "-ai", "account")
        };
        foreach (var scope in new[] { "agent", "released", "denied" })
            targets[scope] = (group + "/providers/Microsoft.Fabric/privateLinkServicesForFabric/" + name + "-" + scope + "-pls", "workspace");
        return targets;
    }

    public FabricC1HubArmRequest PrepareSharedHubPrivateEndpoints()
    {
        var parameters = FabricC1Deployment.ArmParameters(input);
        foreach (var field in new[] { "capacityAdministrators", "network", "compute" }) parameters.Remove(field);
        parameters["workspaces"] = HubParameter(new JsonArray(new[] { "agent", "released", "denied" }
            .Select(scope => (JsonNode)new JsonObject
            { ["key"] = scope, ["id"] = Text(Owned("workspace-" + scope, "Workspace"), "id") }).ToArray()));
        parameters["endpoints"] = HubParameter(new JsonArray(HubEndpointTargets().Select(target => (JsonNode)new JsonObject
        { ["key"] = target.Key, ["resourceId"] = target.Value.Id, ["groupIds"] = new JsonArray(target.Value.Group) }).ToArray()));
        return PrepareSharedHubStage("private-endpoints", parameters);
    }

    private static JsonObject HubTemplate(string name)
    {
        using var stream = typeof(FabricC1Provisioner).Assembly.GetManifestResourceStream("Cohors.Fabric.C1." + name) ??
            throw new DomainException("c1_hub_template", "The source-owned hub consumer template is missing.", 409);
        return JsonNode.Parse(stream) as JsonObject ?? throw new DomainException("c1_hub_template", "The template is not complete JSON.", 409);
    }

    private static JsonObject HubParameter(string? value) => new() { ["value"] = value };
    private static JsonObject HubParameter(JsonNode? value) => new() { ["value"] = value?.DeepClone() };
}
