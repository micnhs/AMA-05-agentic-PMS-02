using System.Net;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed partial class FabricC1Provisioner
{
    private async Task<Dictionary<string, JsonObject>> ReadHubObservations(JsonObject receipt, CancellationToken ct, bool hubOnly = false)
    {
        var observed = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in HubArray(HubObject(receipt, "readback"), "resources").OfType<JsonObject>())
        {
            var id = Text(item, "id");
            if (hubOnly && !id.StartsWith(input.Network!.SharedHub!.ResourceGroupId + "/", StringComparison.Ordinal)) continue;
            var response = await Send(HttpMethod.Get, new("https://management.azure.com" + id +
                "?api-version=" + Text(item, "apiVersion")), ct: ct);
            RequireStatus(response, 200);
            Guard.Against(!SameArmId(Text(response.Body, "id"), id) ||
                FabricC1Deployment.HubWireHash(response.Body) != Text(item, "sha256") ||
                (response.ETag ?? response.Body["etag"]?.GetValue<string>()) != item["etag"]?.GetValue<string>() ||
                response.Body["etag"] is not null && response.Body["etag"]?.GetValue<string>() != item["etag"]?.GetValue<string>(),
                "c1_hub_resource_drift", "An actual ARM body/hash/ETag differs from the exact owner-issued observation.", 409);
            observed.Add(id, (JsonObject)response.Body.DeepClone());
        }
        VerifyHubObservationSemantics(receipt, observed, hubOnly);
        var hub = HubObject(receipt, "hub");
        foreach (var (parent, collection, version) in new[]
        {
            (Text(hub, "firewallPolicyId"), "ruleCollectionGroups", HubNetworkApi),
            (Text(hub, "dnsPolicyId"), "dnsSecurityRules", "2025-05-01"),
            (Text(hub, "dnsPolicyId"), "virtualNetworkLinks", "2025-05-01")
        })
        {
            var prefix = parent + "/" + collection + "/";
            var expected = observed.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var current = await ReadAll("https://management.azure.com" + parent + "/" + collection + "?api-version=" + version, false, ct);
            Guard.Against(current.Length != expected.Length || current.Select(item => Text(item, "id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Length ||
                current.Any(item => !expected.Any(known =>
                SameArmId(Text(item, "id"), known.Key) && JsonNode.DeepEquals(item["properties"], known.Value["properties"]))),
                "c1_hub_policy_inventory", "A live policy collection has injected, missing or changed entries outside its receipt.", 409);
        }
        if (!hubOnly && Text(receipt, "state") != "reserved")
        {
            var peer = HubObject(HubObject(receipt, "peering"), "spokeToHub");
            var current = await ReadAll("https://management.azure.com" + Text(HubObject(receipt, "spoke"), "vnetId") +
                "/virtualNetworkPeerings?api-version=" + HubNetworkApi, false, ct);
            Guard.Against(current.Length != 1 || Text(current[0], "id") != Text(peer, "id"),
                "c1_hub_peering", "The live spoke peer inventory gained another transit or an unbound peer.", 409);
            VerifyOwnHubPeer(current[0], Text(hub, "vnetId"), Text(receipt, "state") != "peered");
        }
        return observed;
    }

    private void VerifyHubObservationSemantics(JsonObject receipt, IReadOnlyDictionary<string, JsonObject> observed, bool hubOnly)
    {
        var hub = HubObject(receipt, "hub");
        var spoke = HubObject(receipt, "spoke");
        JsonObject Resource(string id) => observed.TryGetValue(id, out var resource) ? resource :
            throw new DomainException("c1_hub_readback", "A referenced live resource has no exact owner-issued observation.", 409);
        static JsonObject Ready(JsonObject resource)
        {
            var properties = HubObject(resource, "properties");
            Guard.Against(properties["provisioningState"]?.GetValue<string>() != "Succeeded",
                "c1_hub_resource_state", "ARM metadata must be completely provisioned; accepted/running is not ready.", 409);
            return properties;
        }
        var hubVnet = Resource(Text(hub, "vnetId"));
        foreach (var field in new[] { "vnetId", "firewallId", "vpnGatewayId" })
            Guard.Against(Resource(Text(hub, field))["tags"]?["cohorsBinding"]?.GetValue<string>() != input.Network!.SharedHub!.HubBindingHash,
                "c1_hub_ownership", "The actual hub resource does not belong to its bound generation.", 409);
        var hubNetwork = Ready(hubVnet);
        VerifyHubVnetAddresses(hubNetwork, hub);
        var firewall = Ready(Resource(Text(hub, "firewallId")));
        var firewallIps = HubArray(firewall, "ipConfigurations");
        Guard.Against(firewall["sku"]?["name"]?.GetValue<string>() != "AZFW_VNet" ||
            firewall["sku"]?["tier"]?.GetValue<string>() != "Standard" ||
            firewall["firewallPolicy"]?["id"]?.GetValue<string>() != Text(hub, "firewallPolicyId") ||
            firewallIps.Count != 1 || firewallIps[0]?["properties"]?["privateIPAddress"]?.GetValue<string>() != Text(hub, "firewallPrivateIp") ||
            firewallIps[0]?["properties"]?["subnet"]?["id"]?.GetValue<string>() != Text(HubObject(HubObject(hub, "subnets"), "firewall"), "id"),
            "c1_hub_firewall", "DNS/egress must bind the actual selected hub firewall and its own subnet/policy.", 409);
        var firewallPolicy = Ready(Resource(Text(hub, "firewallPolicyId")));
        Guard.Against(firewallPolicy["dnsSettings"]?["enableProxy"]?.GetValue<bool>() != true ||
            firewallPolicy["dnsSettings"]?["requireProxyForNetworkRules"]?.GetValue<bool>() != true ||
            firewallPolicy["dnsSettings"]?["servers"] is not (null or JsonArray { Count: 0 }) ||
            firewallPolicy["sku"]?["tier"]?.GetValue<string>() != "Standard" ||
            firewallPolicy["threatIntelMode"]?.GetValue<string>() != "Deny" ||
            !JsonNode.DeepEquals(hubNetwork["dhcpOptions"]?["dnsServers"], new JsonArray(Text(hub, "dnsServerIp"))),
            "c1_hub_dns", "The read-back firewall proxy and hub resolver settings differ.", 409);
        var vpn = HubObject(receipt, "vpn");
        var gateway = Ready(Resource(Text(hub, "vpnGatewayId")));
        var client = HubObject(gateway, "vpnClientConfiguration");
        Guard.Against(gateway["gatewayType"]?.GetValue<string>() != "Vpn" ||
            gateway["vpnType"]?.GetValue<string>() != "RouteBased" ||
            gateway["vpnGatewayGeneration"]?.GetValue<string>() != "Generation1" ||
            gateway["sku"]?["name"]?.GetValue<string>() != "VpnGw1AZ" ||
            gateway["enableBgp"]?.GetValue<bool>() != false || gateway["activeActive"]?.GetValue<bool>() != false ||
            !JsonNode.DeepEquals(client["vpnClientAddressPool"]?["addressPrefixes"], new JsonArray(Text(vpn, "clientPool"))) ||
            !JsonNode.DeepEquals(client["vpnClientProtocols"], new JsonArray("OpenVPN")) ||
            !JsonNode.DeepEquals(client["vpnAuthenticationTypes"], new JsonArray("AAD")) ||
            Text(client, "aadAudience") != Text(vpn, "audienceClientId") ||
            Text(client, "aadTenant").TrimEnd('/') != $"https://login.microsoftonline.com/{input.TenantId}" ||
            Text(client, "aadIssuer") != $"https://sts.windows.net/{input.TenantId}/",
            "c1_hub_vpn", "The actual gateway, P2S pool and network-only audience do not match the unchanged tenant binding.", 409);
        VerifyHubDnsPolicy(receipt, observed, Resource);
        var groups = observed.Values.Where(resource => Text(resource, "id").StartsWith(Text(hub, "firewallPolicyId") +
            "/ruleCollectionGroups/", StringComparison.Ordinal)).ToArray();
        Guard.Against(groups.Length != 2, "c1_hub_egress", "The owner-issued firewall policy group observations are incomplete.", 409);
        foreach (var group in groups) Ready(group);
        VerifyHubFirewallRules(receipt, groups);

        if (!hubOnly && Text(receipt, "state") != "reserved")
        {
            var own = Resource(Text(spoke, "vnetId"));
            RequireArmOwnership(own);
            var ownProperties = Ready(own);
            VerifyHubVnetAddresses(ownProperties, spoke);
            VerifyHubSpokePolicy(receipt, own, Resource);
            Guard.Against(!JsonNode.DeepEquals(ownProperties["dhcpOptions"]?["dnsServers"], new JsonArray(Text(hub, "dnsServerIp"))),
                "c1_hub_dns", "The own spoke must use the exact observed hub DNS proxy.", 409);
            foreach (var direction in new[] { "hubToSpoke", "spokeToHub" })
            {
                var peer = HubObject(HubObject(receipt, "peering"), direction);
                var actual = Ready(Resource(Text(peer, "id")));
                foreach (var field in new[] { "allowVirtualNetworkAccess", "allowForwardedTraffic", "allowGatewayTransit", "useRemoteGateways", "peeringState" })
                    Guard.Against(!JsonNode.DeepEquals(actual[field], peer[field]),
                        "c1_hub_peering", "The receipt flags must equal both actual connected ARM peering readbacks.", 409);
                Guard.Against(actual["remoteVirtualNetwork"]?["id"]?.GetValue<string>() != Text(peer, "remoteVirtualNetworkId"),
                    "c1_hub_peering", "The actual peer points to another VNet.", 409);
            }
        }
        foreach (var zone in HubArray(HubObject(receipt, "dns"), "zones").OfType<JsonObject>()) Resource(Text(zone, "id"));
        foreach (var record in HubArray(HubObject(receipt, "dns"), "recordSets")) Resource(record!.GetValue<string>());
        if (!hubOnly)
            foreach (var endpoint in HubArray(receipt, "operatorEndpoints").OfType<JsonObject>())
                VerifyHubOperatorEndpoint(receipt, endpoint, Resource);
    }

    private static void VerifyHubVnetAddresses(JsonObject actual, JsonObject expected)
    {
        Guard.Against(!JsonNode.DeepEquals(actual["addressSpace"]?["addressPrefixes"], new JsonArray(Text(expected, "addressPrefix"))),
            "c1_hub_prefix", "The actual VNet allocation differs from its receipt.", 409);
        var subnets = HubArray(actual, "subnets");
        var definitions = HubObject(expected, "subnets");
        Guard.Against(subnets.Count != definitions.Count, "c1_hub_subnet", "The live subnet inventory differs.", 409);
        foreach (var definition in definitions.Select(field => field.Value!.AsObject()))
        {
            var matches = subnets.OfType<JsonObject>().Where(item => item["id"]?.GetValue<string>() == Text(definition, "id")).ToArray();
            Guard.Against(matches.Length != 1 || matches[0]["properties"]?["addressPrefix"]?.GetValue<string>() != Text(definition, "addressPrefix"),
                "c1_hub_subnet", "A subnet ID or prefix changed in the actual VNet.", 409);
        }
    }

    private static void VerifyHubFirewallRules(JsonObject receipt, JsonObject[] groups)
    {
        Guard.Against(!groups.Select(group => group["properties"]?["priority"]?.GetValue<int>() ?? -1).Order().SequenceEqual([100, 200]),
            "c1_hub_egress", "The private-transit deny group must precede the reviewed HTTPS allow group.", 409);
        var rules = groups.SelectMany(group => HubArray(HubObject(group, "properties"), "ruleCollections").OfType<JsonObject>())
            .SelectMany(collection => HubArray(collection, "rules").OfType<JsonObject>().Select(rule => (collection, rule))).ToArray();
        var expectedRules = HubArray(HubObject(receipt, "egress"), "httpsRules").OfType<JsonObject>().ToArray();
        if (Text(receipt, "state") == "reserved")
        {
            Guard.Against(rules.Any(item => item.rule["name"]?.GetValue<string>().StartsWith("fabric-", StringComparison.Ordinal) == true),
                "c1_hub_egress", "A reservation must not claim already installed Fabric rules.", 409);
            return;
        }
        Guard.Against(rules.Count(item => item.rule["name"]?.GetValue<string>().StartsWith("fabric-", StringComparison.Ordinal) == true) != expectedRules.Length,
            "c1_hub_egress", "An extra or missing Fabric rule changes the consumer policy.", 409);
        foreach (var expected in expectedRules)
        {
            var matching = rules.Where(item => item.rule["name"]?.GetValue<string>() == Text(expected, "name")).ToArray();
            Guard.Against(matching.Length != 1, "c1_hub_egress", "The source-scoped rule is absent or ambiguous in actual firewall configuration.", 409);
            var (collection, actual) = matching[0];
            Guard.Against(collection["action"]?["type"]?.GetValue<string>() != "Allow" ||
                actual["ruleType"]?.GetValue<string>() != "ApplicationRule" ||
                actual["terminateTLS"]?.GetValue<bool>() != false ||
                !JsonNode.DeepEquals(actual["sourceAddresses"], expected["sourceAddresses"]) ||
                !JsonNode.DeepEquals(actual["targetFqdns"], expected["targetFqdns"]) ||
                !JsonNode.DeepEquals(actual["protocols"], new JsonArray(new JsonObject { ["protocolType"] = "Https", ["port"] = 443 })) ||
                collection["priority"]?.GetValue<int>() != 200 ||
                collection["ruleCollectionType"]?.GetValue<string>() != "FirewallPolicyFilterRuleCollection" ||
                new[] { "targetUrls", "fqdnTags", "webCategories", "sourceIpGroups", "httpHeadersToInsert" }
                    .Any(field => actual[field] is not (null or JsonArray { Count: 0 })),
                "c1_hub_egress", "The observed rule broadens scope, protocols or TLS handling.", 409);
        }
        var ranges = HubObject(HubObject(receipt, "spoke"), "subnets").Select(item => Text(item.Value!.AsObject(), "addressPrefix")).ToArray();
        foreach (var group in groups)
            foreach (var collection in HubArray(HubObject(group, "properties"), "ruleCollections").OfType<JsonObject>())
            {
                Guard.Against(collection["ruleCollectionType"]?.GetValue<string>() != "FirewallPolicyFilterRuleCollection" ||
                    collection["action"]?["type"]?.GetValue<string>() is not ("Allow" or "Deny"),
                    "c1_hub_egress", "Only reviewed filtering collections are allowed, never an implicit NAT/transit policy.", 409);
                if (collection["action"]?["type"]?.GetValue<string>() != "Allow") continue;
                Guard.Against(group["properties"]?["priority"]?.GetValue<int>() != 200,
                    "c1_hub_egress", "An allow collection cannot precede the explicit private-transit deny.", 409);
                foreach (var rule in HubArray(collection, "rules").OfType<JsonObject>())
                {
                    if (expectedRules.Any(expected => Text(expected, "name") == Text(rule, "name"))) continue;
                    Guard.Against(rule["sourceIpGroups"] is not (null or JsonArray { Count: 0 }),
                        "c1_hub_egress", "Unbound IP groups may not implicitly authorize the Fabric spoke.", 409);
                    foreach (var source in HubArray(rule, "sourceAddresses"))
                    {
                        if (!IPNetwork.TryParse(source?.GetValue<string>(), out var subnet))
                            throw new DomainException("c1_hub_egress", "A foreign consumer allow rule requires an explicit disjoint source range.", 409);
                        Guard.Against(ranges.Any(range => HubOverlaps(subnet, IPNetwork.Parse(range))),
                            "c1_hub_egress", "Another consumer's allow rule overlaps the Fabric source scope.", 409);
                    }
                }
            }
        Guard.Against(!rules.Any(item => item.collection["action"]?["type"]?.GetValue<string>() == "Deny" &&
            item.collection["priority"]?.GetValue<int>() == 100 &&
            item.collection["ruleCollectionType"]?.GetValue<string>() == "FirewallPolicyFilterRuleCollection" &&
            item.rule["name"]?.GetValue<string>() == "no-implicit-private-transit" &&
            item.rule["ruleType"]?.GetValue<string>() == "NetworkRule" &&
            JsonNode.DeepEquals(item.rule["ipProtocols"], new JsonArray("Any")) &&
            JsonNode.DeepEquals(item.rule["destinationPorts"], new JsonArray("*")) &&
            JsonNode.DeepEquals(item.rule["destinationAddresses"], new JsonArray("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")) &&
            item.rule["sourceAddresses"] is JsonArray sources && ranges.Append(Text(HubObject(receipt, "vpn"), "clientPool"))
                .All(range => sources.Any(source => source?.GetValue<string>() == range))),
            "c1_hub_egress", "The observed shared firewall must explicitly deny unreviewed private transit from the Fabric spoke.", 409);
    }

    private void VerifyHubOperatorEndpoint(JsonObject receipt, JsonObject exception, Func<string, JsonObject> resource)
    {
        var id = Text(exception, "privateEndpointId");
        var endpoint = resource(id);
        RequireArmOwnership(endpoint);
        var creation = Owned("arm-private-endpoints", "ArmDeployment");
        var originals = creation["properties"]?["outputs"]?["privateEndpoints"]?["value"] as JsonArray ??
            throw new DomainException("c1_hub_endpoints", "The original own private-endpoint output inventory is missing.", 409);
        var matches = originals.OfType<JsonObject>().Where(item => Text(item, "id") == id).ToArray();
        Guard.Against(matches.Length != 1, "c1_hub_endpoints", "An operator endpoint cannot be adopted from same-name or unjournaled ARM metadata.", 409);
        var properties = HubObject(endpoint, "properties");
        var operatorSubnet = HubObject(HubObject(HubObject(receipt, "spoke"), "subnets"), "operatorEndpoints");
        var links = HubArray(properties, "privateLinkServiceConnections");
        Guard.Against(Text(properties, "provisioningState") != "Succeeded" ||
            properties["subnet"]?["id"]?.GetValue<string>() != Text(operatorSubnet, "id") || links.Count != 1 ||
            properties["manualPrivateLinkServiceConnections"] is not (null or JsonArray { Count: 0 }),
            "c1_hub_endpoints", "An operator exception must bind its own approved dedicated private endpoint.", 409);
        var connection = links[0]?["properties"] as JsonObject ??
            throw new DomainException("c1_hub_endpoints", "The exact endpoint connection is missing.", 409);
        var groups = HubArray(connection, "groupIds");
        var name = FabricC1Deployment.Name(input);
        var group = FabricC1Deployment.GroupId(input);
        var service = Text(connection, "privateLinkServiceId");
        var operatorService = groups.Count == 1 && (groups[0]!.GetValue<string>() switch
        {
            "vault" => service == group + "/providers/Microsoft.KeyVault/vaults/" + name + "kv",
            "blob" => service == group + "/providers/Microsoft.Storage/storageAccounts/" + name + "st",
            "workspace" => new[] { "agent", "released", "denied" }.Any(scope =>
                service == group + "/providers/Microsoft.Fabric/privateLinkServicesForFabric/" + name + "-" + scope + "-pls"),
            _ => false
        });
        Guard.Against(!operatorService || connection["privateLinkServiceConnectionState"]?["status"]?.GetValue<string>() != "Approved",
            "c1_hub_endpoints", "VPN may reach only exact own workspace, vault or artifact-blob PEs, never database/VM/registry/Hosted services.", 409);
        var interfaces = HubArray(properties, "networkInterfaces");
        Guard.Against(interfaces.Count != 1, "c1_hub_endpoints", "The endpoint requires its exact original NIC observation.", 409);
        var nicId = Text(interfaces[0]!.AsObject(), "id");
        HubArmId(nicId, group, "Microsoft.Network/networkInterfaces");
        var nic = resource(nicId);
        GuidValue(Text(HubObject(nic, "properties"), "resourceGuid"));
        var childKey = "arm-child-" + Canonical.HashText(nicId.ToLowerInvariant());
        Guard.Against(!state.Objects.TryGetValue(childKey, out var originalNic) || originalNic.Kind != "ArmGeneratedChild" ||
            originalNic.Id != nicId || originalNic.Response["parentId"]?.GetValue<string>() != id ||
            originalNic.Response["parentRequestHash"]?.GetValue<string>() != state.Objects["arm-private-endpoints"].RequestHash ||
            originalNic.Response["instanceId"]?.GetValue<string>() != nic["properties"]?["resourceGuid"]?.GetValue<string>() ||
            !JsonNode.DeepEquals(matches[0]["properties"]?["networkInterfaces"], interfaces),
            "c1_hub_endpoints", "The endpoint NIC must remain its original generated instance and creation binding.", 409);
        Guard.Against(nic["properties"]?["privateEndpoint"]?["id"]?.GetValue<string>() != id,
            "c1_hub_endpoints", "NIC reverse binding must point to the exact endpoint.", 409);
        var configurations = HubArray(HubObject(nic, "properties"), "ipConfigurations").OfType<JsonObject>().ToArray();
        Guard.Against(configurations.Any(config => config["properties"]?["subnet"]?["id"]?.GetValue<string>() != Text(operatorSubnet, "id") ||
            config["properties"]?["publicIPAddress"] is not null), "c1_hub_endpoints", "An endpoint NIC must remain in its private operator subnet.", 409);
        var addresses = configurations.Select(config => Text(HubObject(config, "properties"), "privateIPAddress")).Order(StringComparer.Ordinal).ToArray();
        var expectedAddresses = HubArray(exception, "addresses").Select(address => address!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        Guard.Against(!addresses.SequenceEqual(expectedAddresses, StringComparer.Ordinal),
            "c1_hub_endpoints", "The exact endpoint NIC IP set differs.", 409);
        var dns = HubArray(properties, "customDnsConfigs").OfType<JsonObject>().ToArray();
        var expectedNames = HubArray(exception, "fqdns").Select(host => host!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        var names = dns.Select(config => Text(config, "fqdn")).Concat(configurations.SelectMany(config =>
            config["properties"]?["privateLinkConnectionProperties"]?["fqdns"] is JsonArray fqdns ?
                fqdns.Select(host => host!.GetValue<string>()) : [])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Guard.Against(!names.SequenceEqual(expectedNames, StringComparer.Ordinal) ||
            dns.Any(config => HubArray(config, "ipAddresses").Any(address => !expectedAddresses.Contains(address!.GetValue<string>(), StringComparer.Ordinal))),
            "c1_hub_endpoints", "The service DNS/IP observations differ; no address-only or wildcard exception.", 409);
    }
}
