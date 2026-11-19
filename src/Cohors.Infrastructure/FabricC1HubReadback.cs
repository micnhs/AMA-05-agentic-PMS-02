using System.Net;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed partial class FabricC1Provisioner
{
    private async Task<Dictionary<string, JsonObject>> ReadHubObservations(JsonObject receipt, CancellationToken ct,
        bool hubOnly = false, bool cleanup = false)
    {
        var observed = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in HubArray(HubObject(receipt, "readback"), "resources").OfType<JsonObject>())
        {
            var id = Text(item, "id");
            if (hubOnly && !id.StartsWith(input.Network!.SharedHub!.ResourceGroupId + "/", StringComparison.Ordinal)) continue;
            var response = await Send(HttpMethod.Get, new("https://management.azure.com" + id +
                "?api-version=" + Text(item, "apiVersion")), ct: ct, cleanup: cleanup);
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
            var current = await ReadAll("https://management.azure.com" + parent + "/" + collection + "?api-version=" + version, false, ct, cleanup);
            Guard.Against(current.Length != expected.Length || current.Select(item => Text(item, "id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Length ||
                current.Any(item => !expected.Any(known =>
                SameArmId(Text(item, "id"), known.Key) && JsonNode.DeepEquals(item["properties"], known.Value["properties"]))),
                "c1_hub_policy_inventory", "A live policy collection has injected, missing or changed entries outside its receipt.", 409);
        }
        if (!hubOnly && Text(receipt, "state") is not ("reserved" or "offboarded"))
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

        if (!hubOnly && Text(receipt, "state") is not ("reserved" or "offboarded"))
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
        if (Text(receipt, "state") is "reserved" or "offboarded")
        {
            Guard.Against(rules.Any(item => item.rule["name"]?.GetValue<string>().StartsWith("fabric-", StringComparison.Ordinal) == true),
                "c1_hub_egress", "A reserved or offboarded consumer must not retain installed Fabric rules.", 409);
            if (Text(receipt, "state") == "reserved") return;
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
        string[] sourceRanges = Text(receipt, "state") == "offboarded"
            ? [Text(HubObject(receipt, "spoke"), "addressPrefix")] : ranges;
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
                    if (Text(receipt, "state") == "offboarded")
                    {
                        Guard.Against(rule["destinationIpGroups"] is not (null or JsonArray { Count: 0 }) ||
                            rule["destinationAddresses"] is JsonArray destinations &&
                            destinations.Any(destination => HubDestinationTouchesSpoke(destination!.GetValue<string>(), receipt)),
                            "c1_hub_egress", "A renamed/shared allow rule cannot retain an operator or transit destination in the retired spoke.", 409);
                        Guard.Against(rule["ruleType"]?.GetValue<string>() is not ("ApplicationRule" or "NetworkRule"),
                            "c1_hub_egress", "An unknown allow rule cannot prove removal of the retired consumer's references.", 409);
                    }
                    foreach (var source in HubArray(rule, "sourceAddresses"))
                    {
                        if (!IPNetwork.TryParse(source?.GetValue<string>(), out var subnet))
                            throw new DomainException("c1_hub_egress", "A foreign consumer allow rule requires an explicit disjoint source range.", 409);
                        Guard.Against(sourceRanges.Any(range => HubOverlaps(subnet, IPNetwork.Parse(range))),
                            "c1_hub_egress", "Another consumer's allow rule overlaps the Fabric source scope.", 409);
                    }
                }
            }
        if (Text(receipt, "state") == "offboarded") return;
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

    private static bool HubDestinationTouchesSpoke(string value, JsonObject receipt)
    {
        var spoke = IPNetwork.Parse(Text(HubObject(receipt, "spoke"), "addressPrefix"));
        if (value == "*") return true;
        if (IPAddress.TryParse(value, out var address)) return spoke.Contains(address);
        if (IPNetwork.TryParse(value, out var range)) return HubOverlaps(spoke, range);
        throw new DomainException("c1_hub_egress", "An unresolved destination cannot prove disjointness from the retired spoke.", 409);
    }

    private void VerifyHubOperatorEndpoint(JsonObject receipt, JsonObject exception, Func<string, JsonObject> resource)
    {
        var id = Text(exception, "privateEndpointId");
        var endpoint = ObserveOwnHubEndpoint(receipt, id, resource);
        Guard.Against(!endpoint.Operator ||
            !HubArray(exception, "addresses").Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(endpoint.Addresses.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !HubArray(exception, "fqdns").Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal)
                .SequenceEqual(endpoint.Fqdns.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "c1_hub_endpoints", "VPN may reach only the exact observed own workspace, vault or blob PE addresses/names.", 409);
    }

    private sealed record HubEndpointObservation(JsonObject Intake, bool Operator, string[] Addresses, string[] Fqdns,
        Dictionary<string, string[]> DnsAddresses, JsonObject Endpoint, JsonObject Nic);

    private HubEndpointObservation ObserveOwnHubEndpoint(JsonObject receipt, string id, Func<string, JsonObject> resource)
    {
        var endpoint = resource(id);
        RequireArmOwnership(endpoint);
        Guard.Against(RequireTypedArmResource(endpoint, "Microsoft.Network/privateEndpoints") != id,
            "c1_hub_endpoints", "The observed private endpoint identity differs.", 409);
        var creation = Owned("arm-private-endpoints", "ArmDeployment");
        var originals = creation["properties"]?["outputs"]?["privateEndpoints"]?["value"] as JsonArray ??
            throw new DomainException("c1_hub_endpoints", "The original own private-endpoint output inventory is missing.", 409);
        var matches = originals.OfType<JsonObject>().Where(item => Text(item, "id") == id).ToArray();
        Guard.Against(matches.Length != 1, "c1_hub_endpoints", "An operator endpoint cannot be adopted from same-name or unjournaled ARM metadata.", 409);
        var properties = HubObject(endpoint, "properties");
        var links = HubArray(properties, "privateLinkServiceConnections");
        Guard.Against(Text(properties, "provisioningState") != "Succeeded" ||
            links.Count != 1 ||
            properties["manualPrivateLinkServiceConnections"] is not (null or JsonArray { Count: 0 }),
            "c1_hub_endpoints", "Only one approved original private-link connection is allowed.", 409);
        var connection = links[0]?["properties"] as JsonObject ??
            throw new DomainException("c1_hub_endpoints", "The exact endpoint connection is missing.", 409);
        var groups = HubArray(connection, "groupIds");
        var group = FabricC1Deployment.GroupId(input);
        var service = Text(connection, "privateLinkServiceId");
        var targets = HubEndpointTargets().Where(target => target.Value.Id == service &&
            JsonNode.DeepEquals(groups, new JsonArray(target.Value.Group))).ToArray();
        Guard.Against(targets.Length != 1 || connection["privateLinkServiceConnectionState"]?["status"]?.GetValue<string>() != "Approved",
            "c1_hub_endpoints", "The observed service/group/approval is not an original own target.", 409);
        var target = targets[0];
        var isOperator = target.Value.Group is "vault" or "blob" or "workspace";
        var subnet = HubObject(HubObject(HubObject(receipt, "spoke"), "subnets"), isOperator ? "operatorEndpoints" : "endpoints");
        Guard.Against(id != group + "/providers/Microsoft.Network/privateEndpoints/" + FabricC1Deployment.Name(input) + "-" + target.Key + "-pe" ||
            properties["subnet"]?["id"]?.GetValue<string>() != Text(subnet, "id"),
            "c1_hub_endpoints", "The endpoint escaped its exact original target name or isolated subnet.", 409);
        var interfaces = HubArray(properties, "networkInterfaces");
        Guard.Against(interfaces.Count != 1, "c1_hub_endpoints", "The endpoint requires its exact original NIC observation.", 409);
        var nicId = Text(interfaces[0]!.AsObject(), "id");
        HubArmId(nicId, group, "Microsoft.Network/networkInterfaces");
        var nic = resource(nicId);
        Guard.Against(RequireTypedArmResource(nic, "Microsoft.Network/networkInterfaces") != nicId,
            "c1_hub_endpoints", "The observed NIC identity differs.", 409);
        if (nic["tags"] is not (null or JsonObject { Count: 0 })) RequireArmOwnership(nic);
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
        var configurations = HubArray(HubObject(nic, "properties"), "ipConfigurations");
        var dns = properties["customDnsConfigs"] as JsonArray ?? new JsonArray();
        Guard.Against(configurations.Count is < 1 or > 16 || configurations.Any(config => config is not JsonObject) ||
            dns.Count > 64 || dns.Any(config => config is not JsonObject),
            "c1_hub_endpoints", "Complete, bounded provider NIC/DNS metadata is required.", 409);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddDns(string host, IEnumerable<string> values)
        {
            Guard.Against(host != host.ToLowerInvariant() || host.Contains('*') ||
                Uri.CheckHostName(host) != UriHostNameType.Dns,
                "c1_hub_endpoints", "Only exact provider-returned DNS names are allowed.", 409);
            if (!names.TryGetValue(host, out var set)) names[host] = set = new(StringComparer.Ordinal);
            foreach (var value in values)
            {
                Guard.Against(!addresses.Contains(value), "c1_hub_endpoints", "Provider DNS metadata refers to an unobserved NIC address.", 409);
                set.Add(value);
            }
        }
        foreach (var config in configurations.OfType<JsonObject>())
        {
            var value = HubObject(config, "properties");
            var address = Text(value, "privateIPAddress");
            Guard.Against(value["subnet"]?["id"]?.GetValue<string>() != Text(subnet, "id") ||
                value["publicIPAddress"] is not null || !addresses.Add(address),
                "c1_hub_endpoints", "The original NIC must contain distinct private addresses in its bound subnet.", 409);
            HubAddress(address, Text(subnet, "addressPrefix"));
        }
        foreach (var config in configurations.OfType<JsonObject>())
            if (config["properties"]?["privateLinkConnectionProperties"]?["fqdns"] is JsonArray fqdns)
                foreach (var host in fqdns) AddDns(host!.GetValue<string>(), [Text(HubObject(config, "properties"), "privateIPAddress")]);
        foreach (var config in dns.OfType<JsonObject>())
            AddDns(Text(config, "fqdn"), HubArray(config, "ipAddresses").Select(address => address!.GetValue<string>()));
        Guard.Against(names.Count is < 1 or > 64 || names.Values.Any(values => values.Count == 0),
            "c1_hub_endpoints", "An endpoint needs actual per-name NIC address observations, not guessed aliases.", 409);
        var fqdnSet = names.Keys.Order(StringComparer.Ordinal).ToArray();
        return new(new JsonObject
        {
            ["id"] = id, ["serviceId"] = service, ["subnetId"] = Text(subnet, "id"),
            ["groupIds"] = groups.DeepClone(), ["fqdns"] = Strings(fqdnSet)
        }, isOperator, addresses.Order(StringComparer.Ordinal).ToArray(), fqdnSet,
            names.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
            (JsonObject)endpoint.DeepClone(), (JsonObject)nic.DeepClone());
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
}
