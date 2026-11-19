using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed partial class FabricC1Provisioner
{
    private static void VerifyHubDnsPolicy(JsonObject receipt, IReadOnlyDictionary<string, JsonObject> observed,
        Func<string, JsonObject> resource)
    {
        var hub = HubObject(receipt, "hub");
        var id = Text(hub, "dnsPolicyId");
        Guard.Against(resource(id)["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_hub_dns", "The original DNS Resolver Policy is not ready.", 409);
        var rules = observed.Where(item => item.Key.StartsWith(id + "/dnsSecurityRules/", StringComparison.Ordinal))
            .Select(item => HubObject(item.Value, "properties")).ToArray();
        Guard.Against(rules.Length != 2, "c1_hub_dns", "The exact allow/block DNS policy observations are required.", 409);
        foreach (var (action, priority) in new[] { ("Allow", 100), ("Block", 65000) })
        {
            var matches = rules.Where(rule => rule["action"]?["actionType"]?.GetValue<string>() == action).ToArray();
            Guard.Against(matches.Length != 1, "c1_hub_dns", "The DNS allow/block actions cannot be substituted.", 409);
            var rule = matches[0];
            var lists = HubArray(rule, "dnsResolverDomainLists");
            Guard.Against(rule["priority"]?.GetValue<int>() != priority || rule["dnsSecurityRuleState"]?.GetValue<string>() != "Enabled" ||
                rule["provisioningState"]?.GetValue<string>() != "Succeeded" || lists.Count != 1,
                "c1_hub_dns", "The DNS ordering, state or domain-list binding changed.", 409);
            var domainId = HubArmId(Text(lists[0]!.AsObject(), "id"), Text(hub, "resourceGroupId"), "Microsoft.Network/dnsResolverDomainLists");
            var domains = HubArray(HubObject(resource(domainId), "properties"), "domains");
            if (action == "Block")
                Guard.Against(!JsonNode.DeepEquals(domains, new JsonArray(".")), "c1_hub_dns", "Unmatched DNS must remain blocked.", 409);
            else
            {
                Guard.Against(domains.Any(domain => domain is null || domain.GetValue<string>().Contains('*') ||
                    Uri.CheckHostName(domain.GetValue<string>()) != UriHostNameType.Dns),
                    "c1_hub_dns", "Unbound wildcard/root DNS allowance is not accepted.", 409);
                if (Text(receipt, "state") is not ("reserved" or "offboarded"))
                    Guard.Against(HubArray(HubObject(receipt, "egress"), "dnsDomains").Any(expected =>
                        !domains.Any(domain => JsonNode.DeepEquals(domain, expected))), "c1_hub_dns",
                        "A reviewed Fabric DNS domain is missing from the installed shared allow list.", 409);
            }
        }
        var links = observed.Where(item => item.Key.StartsWith(id + "/virtualNetworkLinks/", StringComparison.Ordinal)).ToArray();
        Guard.Against(links.Length != 1 || links[0].Value["properties"]?["virtualNetwork"]?["id"]?.GetValue<string>() != Text(hub, "vnetId") ||
            links[0].Value["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_hub_dns", "The DNS filtering policy must bind only the original hub DNS-proxy VNet.", 409);
    }

    private void VerifyHubSpokePolicy(JsonObject receipt, JsonObject vnet, Func<string, JsonObject> resource)
    {
        var group = FabricC1Deployment.GroupId(input);
        var name = FabricC1Deployment.Name(input);
        var hub = HubObject(receipt, "hub");
        var original = Owned("arm-hub-spoke", "ArmDeployment")["properties"]?["outputs"]?["vnet"]?["value"] as JsonObject ??
            throw new DomainException("c1_hub_spoke", "The original own VNet output is missing.", 409);
        Guard.Against(GuidValue(Text(HubObject(vnet, "properties"), "resourceGuid")) !=
            GuidValue(Text(HubObject(original, "properties"), "resourceGuid")),
            "c1_hub_spoke", "A recreated VNet cannot inherit the original consumer.", 409);
        var routeId = group + "/providers/Microsoft.Network/routeTables/" + name + "-egress-routes";
        var route = resource(routeId);
        RequireArmOwnership(route);
        var routeProperties = HubObject(route, "properties");
        var routes = HubArray(routeProperties, "routes");
        Guard.Against(routeProperties["provisioningState"]?.GetValue<string>() != "Succeeded" ||
            routeProperties["disableBgpRoutePropagation"]?.GetValue<bool>() != false || routes.Count != 1 ||
            routes[0]?["properties"]?["addressPrefix"]?.GetValue<string>() != "0.0.0.0/0" ||
            routes[0]?["properties"]?["nextHopType"]?.GetValue<string>() != "VirtualAppliance" ||
            routes[0]?["properties"]?["nextHopIpAddress"]?.GetValue<string>() != Text(hub, "firewallPrivateIp") ||
            routes[0]?["properties"]?["hasBgpOverride"]?.GetValue<bool>() == true,
            "c1_hub_routes", "Only the reviewed workload default route is allowed; gateway BGP and specific hub returns remain intact.", 409);
        var definitions = HubArray(HubTemplate("c1.hub-spoke.json"), "resources").OfType<JsonObject>()
            .Where(item => Text(item, "type") == "Microsoft.Network/networkSecurityGroups").ToArray();
        foreach (var subnet in HubArray(HubObject(vnet, "properties"), "subnets").OfType<JsonObject>())
        {
            var role = Text(subnet, "name");
            var workload = role is "client" or "agents";
            var properties = HubObject(subnet, "properties");
            var suffix = workload ? "-nsg" : role == "endpoints" ? "-endpoint-nsg" : "-operator-endpoint-nsg";
            var nsgId = group + "/providers/Microsoft.Network/networkSecurityGroups/" + name + suffix;
            Guard.Against(properties["networkSecurityGroup"]?["id"]?.GetValue<string>() != nsgId ||
                workload && (properties["routeTable"]?["id"]?.GetValue<string>() != routeId || properties["defaultOutboundAccess"]?.GetValue<bool>() != false) ||
                !workload && (properties["routeTable"] is not null || properties["privateEndpointNetworkPolicies"]?.GetValue<string>() != "Enabled"),
                "c1_hub_routes", "A subnet gained a broad route, lost its NSG or permits default/public outbound.", 409);
            var nsg = resource(nsgId);
            RequireArmOwnership(nsg);
            var expected = (JsonArray)HubArray(HubObject(definitions.Single(item =>
                Text(item, "name") == "[concat(parameters('name'), '" + suffix + "')]"), "properties"), "securityRules").DeepClone();
            if (role == "operator-endpoints" && state.Objects.ContainsKey("arm-hub-operator-access"))
                expected.Add(new JsonObject { ["name"] = "BoundOperatorPrivateHttps", ["properties"] = new JsonObject
                {
                    ["priority"] = 210, ["direction"] = "Inbound", ["access"] = "Allow", ["protocol"] = "Tcp",
                    ["sourcePortRange"] = "*", ["destinationPortRange"] = "443",
                    ["sourceAddressPrefix"] = Text(HubObject(receipt, "vpn"), "clientPool"),
                    ["destinationAddressPrefixes"] = new JsonArray(HubArray(receipt, "operatorEndpoints").OfType<JsonObject>()
                        .SelectMany(endpoint => HubArray(endpoint, "addresses").Select(address => address!.GetValue<string>()))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray())
                }});
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["[parameters('network').clientPrefix]"] = input.Network!.ClientPrefix!,
                ["[parameters('network').agentPrefix]"] = input.Network.AgentPrefix!,
                ["[parameters('network').endpointPrefix]"] = input.Network.EndpointPrefix!,
                ["[parameters('network').operatorEndpointPrefix]"] = input.Network.SharedHub!.OperatorEndpointPrefix!,
                ["[parameters('firewallPrivateIp')]"] = Text(hub, "firewallPrivateIp")
            };
            foreach (var rule in expected.OfType<JsonObject>())
                foreach (var field in HubObject(rule, "properties").ToArray())
                {
                    if (field.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && replacements.TryGetValue(text, out var replacement))
                        rule["properties"]![field.Key] = replacement;
                    else if (field.Value is JsonArray array)
                        for (var index = 0; index < array.Count; index++)
                            if (array[index] is JsonValue value && value.TryGetValue<string>(out var item) && replacements.TryGetValue(item, out var mapped))
                                array[index] = mapped;
                }
            var actual = HubArray(HubObject(nsg, "properties"), "securityRules").OfType<JsonObject>().ToArray();
            Guard.Against(actual.Length != expected.Count, "c1_hub_nsg", "No extra NSG rule may broaden consumer/VPN access.", 409);
            foreach (var rule in expected.OfType<JsonObject>())
            {
                var matching = actual.Where(item => Text(item, "name") == Text(rule, "name")).ToArray();
                Guard.Against(matching.Length != 1, "c1_hub_nsg", "An expected source-owned NSG rule is missing or duplicated.", 409);
                var wanted = HubObject(rule, "properties");
                var seen = HubObject(matching[0], "properties");
                Guard.Against(wanted.Any(field => !JsonNode.DeepEquals(field.Value, seen[field.Key])) ||
                    seen.Any(field => !wanted.ContainsKey(field.Key) && field.Key != "provisioningState" &&
                        field.Value is not (null or JsonArray { Count: 0 } or JsonObject { Count: 0 }) &&
                        field.Value.ToJsonString() != "\"\""),
                    "c1_hub_nsg", "NSG selectors, priorities, protocols or actions differ from the exact consumer source.", 409);
            }
        }
    }
}
