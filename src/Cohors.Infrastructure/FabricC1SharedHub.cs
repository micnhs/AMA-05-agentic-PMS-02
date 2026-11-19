using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SharedHubBinding(string Contract, string? SchemaSha256, string? Generation,
    string? HubBindingHash, string? ResourceGroupId, string? InfrastructureCommit, string? OperatorEndpointPrefix,
    string? VpnUserSetSha256, string? ResolvedUsersEvidenceSha256, string? AddressPlanReviewSha256,
    string? ClientPrerequisitesReviewSha256, string[]? ExcludedPrefixes, string[]? DnsDomains,
    FabricC1HubEgressRule[]? HttpsRules, string? CompiledTemplateSha256);

public sealed record FabricC1HubEgressRule(string Name, string[] SourceSegments, string[] TargetFqdns);

public sealed record FabricC1HubConsumerResult(string State, int Revision, string ReceiptSha256)
{
    public string EvidenceLevel => "hub-arm-metadata-not-traffic-capacity-cost-or-b5-acceptance";
    public bool CompleteC1Deployment => false;
}

public static partial class FabricC1Deployment
{
    public const string SharedHubContract = "cohors-shared-hub-consumer-1";
    public const string SharedHubSchemaSha256 = "43eee0651b0525f42115185540324e447a78ff0ff82eba73dd1087187c10ca19";

    public static string HubWireHash(JsonNode? value) => FabricC1HubCanonical.Hash(value);

    public static void RequireSharedHubMutationScope(FabricC1DeploymentInputs input, HttpMethod method, Uri uri)
    {
        if (input.Network?.SharedHub is null || uri.IdnHost != "management.azure.com" || method == HttpMethod.Get) return;
        var group = GroupId(input);
        if (method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            var roles = new[] { CheckpointStorageRoleId(input), ArtifactStorageRoleId(input, "publisher"), ArtifactStorageRoleId(input, "receiver") };
            if (roles.Contains(uri.AbsolutePath, StringComparer.OrdinalIgnoreCase)) return;
        }
        Guard.Against(!string.Equals(uri.AbsolutePath, group, StringComparison.OrdinalIgnoreCase) &&
            !uri.AbsolutePath.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase),
            "c1_hub_write_scope", "A Fabric consumer cannot mutate the hub, subscription or another consumer's resources/IAM.", 409);
    }

    public static string[] SharedHubMissingInputs(FabricC1DeploymentInputs input)
    {
        const string prefix = "network.sharedHub";
        if (input.Network?.SharedHub is not { } hub) return [prefix];
        var missing = new List<string>();
        void Required(string name, bool valid) { if (!valid) missing.Add(prefix + "." + name); }
        Required("contract", hub.Contract == SharedHubContract && hub.SchemaSha256 == SharedHubSchemaSha256);
        Required("exclusiveTopology", input.Network.OperatorAccess is null);
        Required("generation", Guid.TryParseExact(hub.Generation, "D", out var generation) && generation != Guid.Empty &&
            hub.Generation == generation.ToString("D") && hub.Generation != input.DeploymentId);
        Required("hubBindingHash", FabricC1Execution.Hex(hub.HubBindingHash, 64));
        Required("resourceGroupId", Guid.TryParse(input.SubscriptionId, out var subscription) &&
            hub.ResourceGroupId is { } group && Regex.IsMatch(group,
                "^/subscriptions/" + subscription.ToString("D") + "/resourceGroups/[A-Za-z0-9_.()-]+$", RegexOptions.CultureInvariant) &&
            Guid.TryParse(input.DeploymentId, out _) &&
            !string.Equals(group, GroupId(input), StringComparison.OrdinalIgnoreCase));
        Required("infrastructureCommit", FabricC1Execution.Hex(hub.InfrastructureCommit, 40));
        Required("compiledTemplateSha256", FabricC1Execution.Hex(hub.CompiledTemplateSha256, 64));
        Required("vpnUserSetSha256", FabricC1Execution.Hex(hub.VpnUserSetSha256, 64));
        Required("resolvedUsersEvidenceSha256", FabricC1Execution.Hex(hub.ResolvedUsersEvidenceSha256, 64));
        Required("addressPlanReviewSha256", FabricC1Execution.Hex(hub.AddressPlanReviewSha256, 64));
        Required("clientPrerequisitesReviewSha256", FabricC1Execution.Hex(hub.ClientPrerequisitesReviewSha256, 64));
        Required("prefixes", ValidHubSpokePrefixes(input.Network));
        Required("dnsDomains", hub.DnsDomains is { Length: > 0 and <= 128 } domains &&
            domains.All(ExactDnsHost) && domains.Distinct(StringComparer.Ordinal).Count() == domains.Length);
        Required("httpsRules", hub.HttpsRules is { Length: > 0 and <= 64 } rules &&
            rules.All(rule => rule is not null) &&
            rules.Select(rule => rule.Name).Distinct(StringComparer.Ordinal).Count() == rules.Length &&
            rules.All(rule => rule.Name is not null && Regex.IsMatch(rule.Name, "^[a-z][a-z0-9-]{1,60}$", RegexOptions.CultureInvariant) &&
                rule.SourceSegments is { Length: > 0 and <= 2 } segments &&
                segments.All(segment => segment is "client" or "agents") && segments.Distinct(StringComparer.Ordinal).Count() == segments.Length &&
                rule.TargetFqdns is { Length: > 0 and <= 64 } targets && targets.All(ExactDnsHost) &&
                targets.Distinct(StringComparer.Ordinal).Count() == targets.Length));
        return missing.ToArray();
    }

    private static bool ValidHubSpokePrefixes(FabricC1DeploymentNetwork network)
    {
        if (network.SharedHub is not { } hub || !ValidPrefixes(network) ||
            !TryPrivateNetwork(network.VnetPrefix, out var parent) ||
            !TryPrivateNetwork(hub.OperatorEndpointPrefix, out var endpoint) ||
            endpoint.PrefixLength <= parent.PrefixLength || endpoint.PrefixLength > 27 ||
            !parent.Contains(endpoint.BaseAddress)) return false;
        if (new[] { network.ClientPrefix, network.AgentPrefix, network.EndpointPrefix }
            .Any(prefix => !TryPrivateNetwork(prefix, out var subnet) || Overlaps(subnet, endpoint))) return false;
        if (hub.ExcludedPrefixes is not { Length: > 0 and <= 256 } excluded ||
            excluded.Distinct(StringComparer.Ordinal).Count() != excluded.Length) return false;
        foreach (var prefix in excluded.Concat(["192.168.0.0/24", "172.30.0.0/16", "172.31.0.0/16"]))
            if (!IPNetwork.TryParse(prefix, out var other) || other.ToString() != prefix ||
                other.BaseAddress.AddressFamily != AddressFamily.InterNetwork || other.PrefixLength == 0 || Overlaps(parent, other)) return false;
        return true;
    }
}

public sealed partial class FabricC1Provisioner
{
    private const string HubReceiptKey = "shared-hub-receipt";
    private const string HubNetworkApi = "2024-07-01";
    private static readonly string[] HubStates = ["reserved", "peered", "connected", "data-bound", "web-bound", "offboarded"];

    public async Task<FabricC1HubConsumerResult> ImportSharedHubReceipt(JsonObject receipt, CancellationToken ct)
    {
        RequireCertainCheckpointJournal();
        var original = (JsonObject)receipt.DeepClone();
        var retiring = Text(original, "state") == "offboarded";
        if (retiring) RequirePrivateOffboardingReceipt(original);
        else PlanNormalRequests(0);
        var previous = state.Objects.TryGetValue(HubReceiptKey, out var saved) ? saved.Response : null;
        if (saved is not null)
            Guard.Against(saved.Kind != "SharedHubConsumerReceipt" || saved.RequestHash != Text(saved.Response, "receiptSha256") ||
                saved.Id != Text(saved.Response, "generation"), "c1_hub_journal", "The original hub receipt journal changed.", 409);
        ValidateHubReceipt(original, previous, retiring);
        if (retiring)
        {
            privateCleanupHubReadback = original;
            try
            {
                var observations = await ReadHubObservations(original, ct, cleanup: true);
                await VerifyPrivateHubReferenceAbsence(original, observations, ct);
            }
            finally { privateCleanupHubReadback = null; }
        }
        else await ReadHubObservations(original, ct);
        ValidateHubReceipt(original, previous, retiring);
        var binding = new FabricC1ProvisionedObject("SharedHubConsumerReceipt", Text(original, "generation"), Text(original, "receiptSha256"), original);
        var objects = new Dictionary<string, FabricC1ProvisionedObject>(state.Objects, StringComparer.Ordinal)
        {
            [HubReceiptKey] = binding,
            [HubReceiptKey + "-" + original["revision"]!.GetValue<int>() + "-" + Text(original, "receiptSha256")] = binding
        };
        if (retiring) objects[PrivateOwnerKey] = PrivateOwnerAcknowledgement(original);
        var next = state with { Objects = objects };
        SaveJournal(next);
        state = next;
        return new(Text(original, "state"), original["revision"]!.GetValue<int>(), Text(original, "receiptSha256"));
    }

    private void ValidateHubReceipt(JsonObject receipt, JsonObject? previous = null, bool retirement = false)
    {
        Guard.Against(FabricC1Deployment.SharedHubMissingInputs(input).Length != 0,
            "c1_hub_inputs", "The explicit shared-hub consumer input is incomplete or contains a legacy topology.", 409);
        var expected = input.Network!.SharedHub!;
        HubFields(receipt, "contract", "generation", "revision", "hubBindingHash", "consumer", "consumerBindingHash",
            "state", "expiresAtUtc", "hub", "spoke", "peering", "vpn", "dns", "egress", "web", "operatorEndpoints",
            "ownership", "readback", "receiptSha256");
        var unsigned = (JsonObject)receipt.DeepClone();
        unsigned.Remove("receiptSha256");
        var version = receipt["revision"] is JsonValue revision && revision.TryGetValue<int>(out var parsed) ? parsed : 0;
        Guard.Against(Text(receipt, "contract") != FabricC1Deployment.SharedHubContract ||
            Text(receipt, "consumer") != "fabric" || Text(receipt, "generation") != expected.Generation ||
            Text(receipt, "hubBindingHash") != expected.HubBindingHash ||
            Text(receipt, "consumerBindingHash") != state.BindingHash ||
            Text(receipt, "receiptSha256") != FabricC1Deployment.HubWireHash(unsigned) || version < 1,
            "c1_hub_binding", "Hub receipt hash, generation, version or exact Fabric consumer binding differs.", 409);
        var stage = Text(receipt, "state");
        Guard.Against(!HubStates.Contains(stage, StringComparer.Ordinal), "c1_hub_state", "Unknown hub state.", 409);
        Guard.Against(stage == "web-bound" || receipt["web"] is not null,
            "c1_hub_web_unqualified", "Fabric human HTTPS/proxy/probe/limits are not qualified; C1 Hosted and MCP are not WAF backends.", 409);
        Guard.Against(stage == "offboarded" && !retirement, "c1_hub_offboarded", "An offboarded receipt cannot resume provisioning.", 409);
        if (retirement) RequirePrivateOffboardingReceipt(receipt);

        var hub = HubObject(receipt, "hub");
        HubFields(hub, "resourceGroupId", "vnetId", "location", "addressPrefix", "subnets", "firewallId", "firewallPolicyId",
            "firewallPrivateIp", "vpnGatewayId", "dnsPolicyId", "dnsServerIp", "wafId", "wafPrivateIp");
        var hubGroup = Text(hub, "resourceGroupId");
        Guard.Against(hubGroup != expected.ResourceGroupId || Text(hub, "location") != input.Location,
            "c1_hub_binding", "The explicitly reviewed hub owner/region changed.", 409);
        var hubVnet = HubArmId(Text(hub, "vnetId"), hubGroup, "Microsoft.Network/virtualNetworks");
        var hubRange = HubNetwork(Text(hub, "addressPrefix"));
        var hubSubnets = HubObject(hub, "subnets");
        HubFields(hubSubnets, "firewall", "gateway", "waf", "endpoints");
        ValidateHubSubnets(hubSubnets, hubVnet, hubRange);
        foreach (var (field, kind) in new[]
        {
            ("firewallId", "Microsoft.Network/azureFirewalls"), ("firewallPolicyId", "Microsoft.Network/firewallPolicies"),
            ("vpnGatewayId", "Microsoft.Network/virtualNetworkGateways"), ("dnsPolicyId", "Microsoft.Network/dnsResolverPolicies")
        }) HubArmId(Text(hub, field), hubGroup, kind);
        HubAddress(Text(hub, "firewallPrivateIp"), Text(HubObject(hubSubnets, "firewall"), "addressPrefix"));
        Guard.Against(Text(hub, "dnsServerIp") != Text(hub, "firewallPrivateIp"),
            "c1_hub_dns", "The observed hub firewall must supply the DNS proxy; no platform/public resolver fallback.", 409);
        if (hub["wafId"] is not null)
        {
            HubArmId(Text(hub, "wafId"), hubGroup, "Microsoft.Network/applicationGateways");
            HubAddress(Text(hub, "wafPrivateIp"), Text(HubObject(hubSubnets, "waf"), "addressPrefix"));
        }
        else Guard.Against(hub["wafPrivateIp"] is not null, "c1_hub_binding", "A WAF address needs its original resource identity.", 409);

        var spoke = HubObject(receipt, "spoke");
        HubFields(spoke, "resourceGroupId", "vnetId", "location", "addressPrefix", "subnets");
        var ownGroup = FabricC1Deployment.GroupId(input);
        var ownVnet = ownGroup + "/providers/Microsoft.Network/virtualNetworks/" + FabricC1Deployment.Name(input) + "-vnet";
        Guard.Against(Text(spoke, "resourceGroupId") != ownGroup || Text(spoke, "vnetId") != ownVnet ||
            Text(spoke, "location") != input.Location || Text(spoke, "addressPrefix") != input.Network.VnetPrefix,
            "c1_hub_spoke", "The receipt does not describe the original own Fabric spoke.", 409);
        var subnets = HubObject(spoke, "subnets");
        HubFields(subnets, "client", "agents", "endpoints", "operatorEndpoints");
        ValidateHubSubnets(subnets, ownVnet, HubNetwork(input.Network.VnetPrefix!));
        foreach (var (role, prefix) in HubSpokePrefixes())
        {
            var subnet = HubObject(subnets, role);
            Guard.Against(Text(subnet, "addressPrefix") != prefix ||
                Text(subnet, "id") != ownVnet + "/subnets/" + (role == "operatorEndpoints" ? "operator-endpoints" : role),
                "c1_hub_spoke", "The source-owned subnet role, allocation or ARM name differs.", 409);
        }
        var vpn = HubObject(receipt, "vpn");
        HubFields(vpn, "audienceClientId", "servicePrincipalObjectId", "assignmentRequired", "userSetSha256",
            "resolvedUsersEvidenceSha256", "businessRolesChanged", "clientPool");
        GuidValue(Text(vpn, "audienceClientId"));
        GuidValue(Text(vpn, "servicePrincipalObjectId"));
        Guard.Against(vpn["assignmentRequired"]?.GetValue<bool>() != true || vpn["businessRolesChanged"]?.GetValue<bool>() != false ||
            Text(vpn, "userSetSha256") != expected.VpnUserSetSha256 ||
            Text(vpn, "resolvedUsersEvidenceSha256") != expected.ResolvedUsersEvidenceSha256,
            "c1_hub_vpn", "The original four-user VPN binding is not an application-role or data grant.", 409);
        var pool = HubNetwork(Text(vpn, "clientPool"));
        Guard.Against(pool.PrefixLength is < 24 or > 29, "c1_hub_prefix", "The owner-issued P2S pool is outside its bounded contract.", 409);
        var allocations = new[] { hubRange, HubNetwork(input.Network.VnetPrefix!), pool };
        Guard.Against(allocations.Where((allocation, index) => allocations.Skip(index + 1).Any(other => HubOverlaps(allocation, other))).Any() ||
            allocations.Any(allocation => new[] { "192.168.0.0/24", "172.30.0.0/16", "172.31.0.0/16" }
                .Concat(expected.ExcludedPrefixes!).Any(prefix => HubOverlaps(allocation, IPNetwork.Parse(prefix)))),
            "c1_hub_prefix", "Hub, Fabric, client pool, provider-reserved and reviewed local routes overlap.", 409);
        ValidateHubPeering(receipt, ownVnet, hubVnet);
        ValidateHubDnsAndEgress(receipt);

        var ownership = new JsonObject
        {
            ["hubLifecycleOwner"] = "hub", ["consumerMayCreateHub"] = false, ["consumerMayDeleteHub"] = false,
            ["consumerMayChangeSharedDirectory"] = false, ["consumerMayChangeHubDns"] = false,
            ["offboarding"] = "stop-own-work-then-owner-releases-exact-consumer-references", ["sharedFailureDomain"] = true
        };
        Guard.Against(!JsonNode.DeepEquals(receipt["ownership"], ownership), "c1_hub_ownership", "The hub owner and cleanup boundaries cannot be widened.", 409);
        ValidateHubReadbackEnvelope(receipt);
        if (previous is null)
            Guard.Against(stage != "reserved", "c1_hub_state", "An own consumer starts with a reservation, never an adopted live connection.", 409);
        else
        {
            var priorVersion = previous["revision"]!.GetValue<int>();
            var comparable = (JsonObject)previous.DeepClone();
            comparable["readback"]!["observedAtUtc"] = receipt["readback"]!["observedAtUtc"]!.DeepClone();
            comparable["receiptSha256"] = receipt["receiptSha256"]!.DeepClone();
            Guard.Against(version < priorVersion || version == priorVersion && !JsonNode.DeepEquals(comparable, receipt) ||
                HubStateIndex(stage) < HubStateIndex(Text(previous, "state")) ||
                stage != "offboarded" && HubStateIndex(stage) > HubStateIndex(Text(previous, "state")) + 1,
                "c1_hub_revision", "Hub revisions cannot roll back, change in place or resurrect retired consumers.", 409);
            foreach (var field in new[] { "generation", "hubBindingHash", "consumerBindingHash", "expiresAtUtc", "spoke", "vpn" })
                Guard.Against(!JsonNode.DeepEquals(previous[field], receipt[field]), "c1_hub_revision",
                    "Original consumer identity, allocation, VPN set and deadline remain immutable.", 409);
            foreach (var field in hub.Select(field => field.Key).Where(field => field is not ("wafId" or "wafPrivateIp")))
                Guard.Against(!JsonNode.DeepEquals(previous["hub"]?[field], hub[field]),
                    "c1_hub_revision", "The original hub network identity or allocation changed.", 409);
            Guard.Against(SourceUtc(HubObject(receipt, "readback"), "observedAtUtc") <
                SourceUtc(HubObject(previous, "readback"), "observedAtUtc"),
                "c1_hub_revision", "A newer revision cannot substitute an older observation.", 409);
        }
        if (stage == "peered") RequireHubPeerReceipt(receipt, remoteGateways: false);
        if (stage is "connected" or "data-bound") RequireHubPeerReceipt(receipt, remoteGateways: true);
        if (stage == "data-bound") Owned("arm-private-endpoints", "ArmDeployment");
    }

    private Dictionary<string, string> HubSpokePrefixes() => new(StringComparer.Ordinal)
    {
        ["client"] = input.Network!.ClientPrefix!, ["agents"] = input.Network.AgentPrefix!,
        ["endpoints"] = input.Network.EndpointPrefix!, ["operatorEndpoints"] = input.Network.SharedHub!.OperatorEndpointPrefix!
    };

    private static int HubStateIndex(string state) => Array.IndexOf(HubStates, state);

    private static JsonObject HubObject(JsonObject parent, string field) => parent[field] as JsonObject ??
        throw new DomainException("c1_hub_shape", "The complete versioned hub object is required.", 409);

    private static JsonArray HubArray(JsonObject parent, string field) => parent[field] as JsonArray ??
        throw new DomainException("c1_hub_shape", "The complete versioned hub list is required.", 409);

    private static void HubFields(JsonObject value, params string[] fields) =>
        Guard.Against(value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)),
            "c1_hub_shape", "Only the exact owner-versioned fields are accepted.", 409);

    private static string HubArmId(string value, string scope, string kind)
    {
        var types = kind.Split('/');
        var pattern = Regex.Escape(scope) + "/providers/" + Regex.Escape(types[0]) +
            string.Concat(types.Skip(1).Select(type => "/" + Regex.Escape(type) + "/[A-Za-z0-9][A-Za-z0-9_.()-]{0,127}"));
        Guard.Against(!Regex.IsMatch(value, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant),
            "c1_hub_scope", "A hub reference left its exact owner scope or resource type.", 409);
        return value;
    }

    private static IPNetwork HubNetwork(string prefix)
    {
        Guard.Against(!IPNetwork.TryParse(prefix, out var network) || network.ToString() != prefix ||
            network.BaseAddress.AddressFamily != AddressFamily.InterNetwork ||
            !new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Select(IPNetwork.Parse)
                .Any(root => network.PrefixLength >= root.PrefixLength && root.Contains(network.BaseAddress)),
            "c1_hub_prefix", "An allocated canonical RFC1918 IPv4 range is required.", 409);
        return network;
    }

    private static bool HubOverlaps(IPNetwork first, IPNetwork second) =>
        first.Contains(second.BaseAddress) || second.Contains(first.BaseAddress);

    private static void HubAddress(string value, string prefix)
    {
        var network = HubNetwork(prefix);
        Guard.Against(!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != value || !network.Contains(address),
            "c1_hub_address", "The observed IPv4 address is outside its bound subnet.", 409);
        var bytes = address!.GetAddressBytes();
        var start = network.BaseAddress.GetAddressBytes();
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) -
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(start);
        Guard.Against(offset < 4 || offset == (1UL << (32 - network.PrefixLength)) - 1,
            "c1_hub_address", "Azure-reserved addresses cannot be selected.", 409);
    }

    private static void ValidateHubSubnets(JsonObject subnets, string vnet, IPNetwork parent)
    {
        var ranges = new List<IPNetwork>();
        foreach (var field in subnets)
        {
            var subnet = HubObject(subnets, field.Key);
            HubFields(subnet, "id", "addressPrefix");
            var id = Text(subnet, "id");
            Guard.Against(!id.StartsWith(vnet + "/subnets/", StringComparison.Ordinal) ||
                !Regex.IsMatch(id[(vnet.Length + "/subnets/".Length)..], "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\z", RegexOptions.CultureInvariant),
                "c1_hub_subnet", "A subnet must belong to its own recorded VNet.", 409);
            var range = HubNetwork(Text(subnet, "addressPrefix"));
            Guard.Against(range.PrefixLength <= parent.PrefixLength || !parent.Contains(range.BaseAddress) ||
                range.PrefixLength > (field.Key == "firewall" ? 26 : field.Key == "waf" ? 24 : 27) ||
                ranges.Any(other => HubOverlaps(other, range)), "c1_hub_subnet", "Subnets overlap or lack their bounded provider-sized allocation.", 409);
            ranges.Add(range);
        }
    }

    private static void ValidateHubPeering(JsonObject receipt, string spokeVnet, string hubVnet)
    {
        var state = Text(receipt, "state");
        if (state is "reserved" or "offboarded")
        {
            Guard.Against(receipt["peering"] is not null, "c1_hub_peering", "A reservation or retired consumer has no peering references.", 409);
            return;
        }
        var peers = HubObject(receipt, "peering");
        HubFields(peers, "hubToSpoke", "spokeToHub");
        foreach (var direction in new[] { "hubToSpoke", "spokeToHub" })
        {
            var peer = HubObject(peers, direction);
            HubFields(peer, "id", "remoteVirtualNetworkId", "allowVirtualNetworkAccess", "allowForwardedTraffic",
                "allowGatewayTransit", "useRemoteGateways", "peeringState");
            var outward = direction == "spokeToHub";
            var own = outward ? spokeVnet : hubVnet;
            Guard.Against(Text(peer, "id") != own + "/virtualNetworkPeerings/" + (outward ? "to-shared-hub" : "to-fabric") ||
                Text(peer, "remoteVirtualNetworkId") != (outward ? hubVnet : spokeVnet) ||
                peer["allowVirtualNetworkAccess"]?.GetValue<bool>() != true ||
                peer["allowForwardedTraffic"]?.GetValue<bool>() != outward ||
                peer["allowGatewayTransit"]?.GetValue<bool>() != !outward ||
                peer["useRemoteGateways"]?.GetValue<bool>() != (outward && state != "peered") ||
                Text(peer, "peeringState") != "Connected",
                "c1_hub_peering", "Both exact peering directions and gateway-transit phases must be observed.", 409);
        }
    }

    private void ValidateHubDnsAndEgress(JsonObject receipt)
    {
        var dns = HubObject(receipt, "dns");
        HubFields(dns, "owner", "registrationMode", "zones", "recordSets");
        Guard.Against(Text(dns, "owner") != "hub" || Text(dns, "registrationMode") != "hub-owner-only-no-consumer-zone-groups",
            "c1_hub_dns", "Consumer DNS writes/joins and PE zone-group side effects are not allowed.", 409);
        var group = input.Network!.SharedHub!.ResourceGroupId!;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in HubArray(dns, "zones"))
        {
            if (item is not JsonObject zone) throw new DomainException("c1_hub_dns", "A complete owned DNS zone is required.", 409);
            HubFields(zone, "name", "id");
            var name = Text(zone, "name");
            Guard.Against(!Uri.CheckHostName(name).Equals(UriHostNameType.Dns) || name.Contains('*') || !names.Add(name) ||
                Text(zone, "id") != group + "/providers/Microsoft.Network/privateDnsZones/" + name,
                "c1_hub_dns", "Private DNS namespaces must have one explicit hub-owned zone each.", 409);
        }
        var records = HubArray(dns, "recordSets");
        Guard.Against(records.Any(item => item is not JsonValue || !names.Any(zone =>
                item!.GetValue<string>().StartsWith(group + "/providers/Microsoft.Network/privateDnsZones/" + zone + "/A/", StringComparison.Ordinal))) ||
            records.Select(item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != records.Count,
            "c1_hub_dns", "Each referenced record must belong to a bound hub-owned zone.", 409);
        var egress = HubObject(receipt, "egress");
        HubFields(egress, "defaultNextHop", "nextHopIp", "bgpPropagation", "hubPrefixRoute", "spokeToSpoke",
            "vpnDatabaseAccess", "dnsDomains", "httpsRules");
        Guard.Against(Text(egress, "defaultNextHop") != "VirtualAppliance" ||
            Text(egress, "nextHopIp") != Text(HubObject(receipt, "hub"), "firewallPrivateIp") ||
            egress["bgpPropagation"]?.GetValue<bool>() != true || egress["hubPrefixRoute"]?.GetValue<bool>() != false ||
            egress["spokeToSpoke"]?.GetValue<bool>() != false || egress["vpnDatabaseAccess"]?.GetValue<bool>() != false,
            "c1_hub_egress", "No broad transit route or implicit spoke/VPN data access is allowed.", 409);
        var expected = input.Network.SharedHub!;
        var prefixes = HubSpokePrefixes();
        var rules = Text(receipt, "state") == "offboarded" ? new JsonArray() : new JsonArray(expected.HttpsRules!.Select(rule => (JsonNode)new JsonObject
        {
            ["name"] = "fabric-" + rule.Name,
            ["sourceAddresses"] = new JsonArray(rule.SourceSegments.Select(segment => (JsonNode)JsonValue.Create(prefixes[segment])!).ToArray()),
            ["targetFqdns"] = new JsonArray(rule.TargetFqdns.Order(StringComparer.Ordinal).Select(host => (JsonNode)JsonValue.Create(host)!).ToArray())
        }).ToArray());
        Guard.Against(!JsonNode.DeepEquals(egress["httpsRules"], rules) ||
            !JsonNode.DeepEquals(egress["dnsDomains"], JsonSerializer.SerializeToNode(expected.DnsDomains)),
            "c1_hub_egress", "Only the exact reviewed own source-scoped DNS/HTTPS policy may be consumed.", 409);
        var endpoints = HubArray(receipt, "operatorEndpoints");
        Guard.Against(Text(receipt, "state") != "data-bound" && (endpoints.Count != 0 || records.Count != 0),
            "c1_hub_state", "Data references require a later observed binding, not reservation or peering alone.", 409);
        Guard.Against(endpoints.Count > 16, "c1_hub_endpoints", "Operator access must remain an exact bounded endpoint set.", 409);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in endpoints)
        {
            if (item is not JsonObject endpoint) throw new DomainException("c1_hub_endpoints", "An explicit operator endpoint is required.", 409);
            HubFields(endpoint, "privateEndpointId", "addresses", "fqdns", "port");
            HubArmId(Text(endpoint, "privateEndpointId"), FabricC1Deployment.GroupId(input), "Microsoft.Network/privateEndpoints");
            Guard.Against(!seen.Add(Text(endpoint, "privateEndpointId")) || endpoint["port"]?.GetValue<int>() != 443,
                "c1_hub_endpoints", "VPN exceptions must reference unique own PEs on HTTPS443.", 409);
            var addresses = HubArray(endpoint, "addresses");
            var fqdns = HubArray(endpoint, "fqdns");
            Guard.Against(addresses.Count is < 1 or > 16 || fqdns.Count is < 1 or > 16 ||
                addresses.Select(address => address!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != addresses.Count ||
                fqdns.Select(host => host!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != fqdns.Count ||
                fqdns.Any(host => host is null || Uri.CheckHostName(host.GetValue<string>()) != UriHostNameType.Dns ||
                    host.GetValue<string>().Contains('*') || host.GetValue<string>() != host.GetValue<string>().ToLowerInvariant()),
                "c1_hub_endpoints", "Actual operator endpoint addresses and DNS names are required.", 409);
            foreach (var address in addresses) HubAddress(address!.GetValue<string>(), expected.OperatorEndpointPrefix!);
        }
    }

    private void ValidateHubReadbackEnvelope(JsonObject receipt)
    {
        var readback = HubObject(receipt, "readback");
        HubFields(readback, "observedAtUtc", "infrastructureCommit", "compiledTemplateSha256", "deploymentResourceId",
            "resources", "trafficAccepted", "capacityAccepted", "costAccepted");
        var observed = SourceUtc(readback, "observedAtUtc");
        var expiry = SourceUtc(receipt, "expiresAtUtc");
        var now = time.GetUtcNow();
        var retiring = Text(receipt, "state") == "offboarded";
        if (retiring) RequirePrivateOffboardingReceipt(receipt);
        Guard.Against(observed > now || !retiring && now >= expiry || now >= observed.AddHours(24) ||
            input.ExpiresAtUtc != expiry || !retiring && input.ExpiresAtUtc <= now ||
            Text(readback, "infrastructureCommit") != input.Network!.SharedHub!.InfrastructureCommit ||
            Text(readback, "compiledTemplateSha256") != input.Network.SharedHub.CompiledTemplateSha256 ||
            !Regex.IsMatch(Text(readback, "deploymentResourceId"),
                "\\A/subscriptions/" + input.SubscriptionId + "/providers/Microsoft.Resources/deployments/[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant) ||
            new[] { "trafficAccepted", "capacityAccepted", "costAccepted" }.Any(field => readback[field]?.GetValue<bool>() != false),
            "c1_hub_readback", "A fresh bound source/template/deadline receipt is required; metadata cannot grant traffic/cost/capacity acceptance.", 409);
        var resources = HubArray(readback, "resources");
        var hub = HubObject(receipt, "hub");
        var scopes = new[] { Text(hub, "resourceGroupId"), FabricC1Deployment.GroupId(input) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guard.Against(resources.Count is < 5 or > 96, "c1_hub_readback", "The bounded actual resource readback set is incomplete.", 409);
        foreach (var item in resources)
        {
            if (item is not JsonObject resource) throw new DomainException("c1_hub_shape", "A complete ARM observation is required.", 409);
            HubFields(resource, "id", "apiVersion", "sha256", "etag");
            var id = Text(resource, "id");
            Guard.Against(!scopes.Any(scope => id.StartsWith(scope + "/providers/", StringComparison.Ordinal)) ||
                !Regex.IsMatch(id, "\\A/subscriptions/[0-9a-f-]{36}/resourceGroups/[A-Za-z0-9_.()-]+/providers/[A-Za-z0-9._/-]+\\z", RegexOptions.CultureInvariant) ||
                id.Split('/').Any(part => part is "." or "..") || !seen.Add(id) ||
                Text(resource, "apiVersion") is not ("2024-07-01" or "2025-03-01" or "2020-06-01" or "2025-05-01" or "2025-07-01" or "2024-11-01" or "2024-11-30" or "2024-03-01") ||
                !FabricC1Execution.Hex(Text(resource, "sha256"), 64) ||
                resource["etag"] is not null && (resource["etag"] is not JsonValue etag || !etag.TryGetValue<string>(out _)),
                "c1_hub_readback", "Unbound, duplicate or unversioned ARM observations are not followed.", 409);
        }
        var required = new List<string> { Text(hub, "vnetId"), Text(hub, "firewallId"), Text(hub, "firewallPolicyId"),
            Text(hub, "vpnGatewayId"), Text(hub, "dnsPolicyId") };
        if (Text(receipt, "state") is not ("reserved" or "offboarded"))
        {
            required.Add(Text(HubObject(receipt, "spoke"), "vnetId"));
            required.Add(Text(HubObject(HubObject(receipt, "peering"), "hubToSpoke"), "id"));
            required.Add(Text(HubObject(HubObject(receipt, "peering"), "spokeToHub"), "id"));
        }
        Guard.Against(required.Any(id => !seen.Contains(id)), "c1_hub_readback", "Actual hub/VNet/peering observations are missing.", 409);
    }
}
