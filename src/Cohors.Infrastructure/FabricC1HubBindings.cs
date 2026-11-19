using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1HubDnsSelection(string PrivateEndpointId, string Fqdn, string Zone, string Name);
public sealed record FabricC1HubBindingsCandidate(JsonObject Consumer, JsonObject Observations, string SubjectSha256)
{
    public string Status => "AwaitingBoundDnsEvidence";
    public string OwnerIntakeContract => "cohors-shared-hub-bindings-1";
    public bool OwnerRevisionCreated => false;
    public bool TrafficAccepted => false;
}

public sealed record FabricC1HubBindingsExport(JsonObject Consumer, Dictionary<string, byte[]> EvidenceFiles)
{
    public string Status => "AwaitingOwnerRevision";
    public string OwnerIntakeContract => "cohors-shared-hub-bindings-1";
    public bool OwnerRevisionCreated => false;
    public bool TrafficAccepted => false;
}

public static partial class FabricC1Deployment
{
    public static string HubPrerequisiteSubject(JsonObject consumer)
    {
        static JsonNode? WithoutEvidence(JsonNode? value) => value switch
        {
            JsonObject fields => new JsonObject(fields.Where(field => field.Key != "key" &&
                !field.Key.EndsWith("Sha256", StringComparison.Ordinal))
                .Select(field => KeyValuePair.Create(field.Key, WithoutEvidence(field.Value)))),
            JsonArray values => new JsonArray(values.Select(WithoutEvidence).ToArray()),
            _ => value?.DeepClone()
        };
        return HubWireHash(WithoutEvidence(consumer));
    }
}

public sealed partial class FabricC1Provisioner
{
    private const string HubBindingsCandidateKey = "shared-hub-data-candidate";
    private const string HubBindingsExportKey = "shared-hub-data-export";
    private const string HubBindingsEvidenceKey = "shared-hub-data-evidence";

    public async Task<FabricC1HubBindingsCandidate> PrepareSharedHubBindings(
        IReadOnlyCollection<FabricC1HubDnsSelection> selections, CancellationToken ct)
    {
        PlanNormalRequests(0);
        var receipt = RequireStoredHubReceipt();
        Guard.Against(Text(receipt, "state") != "connected", "c1_hub_registration_state",
            "Data registration starts only after the original connected handshake, never from an adopted/live consumer.", 409);
        var selected = ValidateHubDnsSelections(receipt, selections);
        var request = PrepareSharedHubPrivateEndpoints();
        var original = state.Objects.TryGetValue("arm-private-endpoints", out var completed) ? completed :
            throw new DomainException("c1_hub_registration_missing", "The actual original endpoint stage has not completed.", 409);
        var creationHash = Canonical.Hash(new JsonObject { ["properties"] = new JsonObject
        { ["mode"] = "Incremental", ["template"] = request.Template.DeepClone(), ["parameters"] = request.Parameters.DeepClone() } });
        Guard.Against(original.Kind != "ArmDeployment" || original.RequestHash != creationHash ||
            original.Response["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded" ||
            state.PendingCreates.Count != 0 || state.PendingOperations.Count != 0 || state.PendingDeletes.Count != 0,
            "c1_hub_registration_missing", "Registration needs a completed original endpoint request and no unknown mutations.", 409);

        // Own PE creation may change VNet metadata. Recheck its source-owned semantics/instance,
        // rather than adopting it or requiring an impossible pre-registration owner snapshot.
        await ReadHubObservations(receipt, ct, hubOnly: true);
        var observed = await ReadOwnHubRegistrationNetwork(receipt, ct);
        var expectedIds = HubEndpointTargets().Keys.Select(key => FabricC1Deployment.GroupId(input) +
            "/providers/Microsoft.Network/privateEndpoints/" + FabricC1Deployment.Name(input) + "-" + key + "-pe")
            .Order(StringComparer.Ordinal).ToArray();
        var listed = await ReadAll("https://management.azure.com" + FabricC1Deployment.GroupId(input) +
            "/providers/Microsoft.Network/privateEndpoints?api-version=" + HubNetworkApi, false, ct);
        Guard.Against(!listed.Select(endpoint => Text(endpoint, "id")).Order(StringComparer.Ordinal).SequenceEqual(expectedIds),
            "c1_hub_registration_inventory", "Missing, duplicate or unjournaled endpoints prevent a closed consumer export.", 409);
        foreach (var id in expectedIds)
        {
            var endpoint = await Read("https://management.azure.com" + id + "?api-version=" + HubNetworkApi, ct);
            Guard.Against(Text(endpoint, "id") != id, "c1_hub_endpoints", "An endpoint GET returned another identity.", 409);
            observed.Add(id, endpoint);
            var interfaces = HubArray(HubObject(endpoint, "properties"), "networkInterfaces");
            Guard.Against(interfaces.Count != 1 || interfaces[0] is not JsonObject,
                "c1_hub_endpoints", "The endpoint must expose its exact generated NIC.", 409);
            var nicId = HubArmId(Text(interfaces[0]!.AsObject(), "id"), FabricC1Deployment.GroupId(input), "Microsoft.Network/networkInterfaces");
            var childKey = "arm-child-" + Canonical.HashText(nicId.ToLowerInvariant());
            Guard.Against(!state.Objects.TryGetValue(childKey, out var child) || child.Kind != "ArmGeneratedChild" ||
                child.Response["parentId"]?.GetValue<string>() != id, "c1_hub_endpoints",
                "An unjournaled NIC is not followed, even if it is in the own resource group.", 409);
            Guard.Against(observed.ContainsKey(nicId), "c1_hub_endpoints", "Private endpoints cannot share an adopted NIC.", 409);
            observed.Add(nicId, await Read("https://management.azure.com" + nicId + "?api-version=" + HubNetworkApi, ct));
        }
        var endpoints = expectedIds.Select(id => ObserveOwnHubEndpoint(receipt, id, key => observed[key])).ToArray();
        var consumer = HubConsumerSlot(receipt, "data-bound");
        consumer["privateEndpoints"] = new JsonArray(endpoints.Select(endpoint => (JsonNode)endpoint.Intake.DeepClone()).ToArray());
        consumer["operatorEndpoints"] = new JsonArray(endpoints.Where(endpoint => endpoint.Operator).Select(endpoint => (JsonNode)new JsonObject
        {
            ["privateEndpointId"] = Text(endpoint.Intake, "id"), ["addresses"] = Strings(endpoint.Addresses),
            ["fqdns"] = Strings(endpoint.Fqdns), ["port"] = 443
        }).ToArray());
        var records = HubArray(consumer, "dnsRecords");
        foreach (var selection in selected)
        {
            var endpoint = endpoints.SingleOrDefault(endpoint => Text(endpoint.Intake, "id") == selection.PrivateEndpointId);
            Guard.Against(endpoint is null || !endpoint.DnsAddresses.ContainsKey(selection.Fqdn),
                "c1_hub_dns_selection", "Each selected DNS name must be actually returned by its original PE/NIC.", 409);
            records.Add(new JsonObject
            {
                ["zone"] = selection.Zone, ["name"] = selection.Name, ["addresses"] = Strings(endpoint!.DnsAddresses[selection.Fqdn]),
                ["sourceResourceId"] = selection.PrivateEndpointId, ["dnsEvidenceSha256"] = null
            });
        }
        Guard.Against(endpoints.Any(endpoint => !selected.Any(selection => selection.PrivateEndpointId == Text(endpoint.Intake, "id"))),
            "c1_hub_dns_selection", "Every original endpoint requires an explicit registration selection.", 409);
        var observation = new JsonObject
        {
            ["observedAtUtc"] = time.GetUtcNow().ToString("O"), ["hubReceiptSha256"] = Text(receipt, "receiptSha256"),
            ["endpointRequestSha256"] = creationHash, ["resolverIp"] = Text(HubObject(receipt, "hub"), "dnsServerIp"),
            ["selections"] = JsonSerializer.SerializeToNode(selected, Canonical.Json),
            ["resources"] = new JsonObject(observed.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value.DeepClone())))
        };
        var hash = Canonical.Hash(new { state.BindingHash, EndpointRequestSha256 = creationHash, Selections = selected });
        var subject = FabricC1Deployment.HubPrerequisiteSubject(consumer);
        if (state.Objects.TryGetValue(HubBindingsCandidateKey, out var previous))
        {
            Guard.Against(previous.Kind != "SharedHubBindingsCandidate" || previous.RequestHash != hash ||
                previous.Response["subjectSha256"]?.GetValue<string>() != subject,
                "c1_hub_registration_drift", "An existing registration cannot silently rebind endpoint addresses, names or the original selection.", 409);
            return new((JsonObject)HubObject(previous.Response, "consumer").DeepClone(),
                (JsonObject)HubObject(previous.Response, "observations").DeepClone(), subject);
        }
        PlanNormalRequests(0);
        SaveObject(HubBindingsCandidateKey, "SharedHubBindingsCandidate", input.Network!.SharedHub!.Generation!, hash,
            new JsonObject { ["consumer"] = consumer.DeepClone(), ["observations"] = observation.DeepClone(), ["subjectSha256"] = subject });
        return new(consumer, observation, subject);
    }

    public FabricC1HubBindingsExport ExportSharedHubBindings(IReadOnlyCollection<byte[]> evidenceFiles)
    {
        PlanNormalRequests(0);
        var candidate = Owned(HubBindingsCandidateKey, "SharedHubBindingsCandidate");
        var consumer = (JsonObject)HubObject(candidate, "consumer").DeepClone();
        var observation = HubObject(candidate, "observations");
        Guard.Against(time.GetUtcNow() >= SourceUtc(observation, "observedAtUtc").AddHours(24),
            "c1_hub_registration_stale", "Refresh the original endpoint observations before qualifying DNS evidence.", 409);
        var files = VerifyHubEvidenceFiles(evidenceFiles, consumer, "dns-chain", retirement: false);
        var selections = JsonSerializer.Deserialize<FabricC1HubDnsSelection[]>(observation["selections"], Canonical.Json) ??
            throw new DomainException("c1_hub_dns_selection", "The original DNS selection journal is missing.", 409);
        foreach (var record in HubArray(consumer, "dnsRecords").OfType<JsonObject>())
        {
            var selection = selections.Single(item => item.PrivateEndpointId == Text(record, "sourceResourceId") &&
                item.Zone == Text(record, "zone") && item.Name == Text(record, "name"));
            var matching = files.Where(file => file.Value.Value["dns"] is JsonObject dns &&
                Text(dns, "privateEndpointId") == selection.PrivateEndpointId &&
                Text(dns, "fqdn") == selection.Fqdn && Text(dns, "zone") == selection.Zone && Text(dns, "name") == selection.Name).ToArray();
            Guard.Against(matching.Length != 1, "c1_hub_dns_evidence", "Each exact DNS registration needs one original qualified evidence file.", 409);
            Guard.Against(SourceUtc(matching[0].Value.Value, "observedAtUtc") < SourceUtc(observation, "observedAtUtc"),
                "c1_hub_dns_evidence", "A DNS proof cannot precede its original endpoint/NIC observation.", 409);
            var proof = HubObject(matching[0].Value.Value, "dns");
            VerifyHubDnsProof(proof, selection, observation);
            record["dnsEvidenceSha256"] = matching[0].Key;
        }
        Guard.Against(files.Count != HubArray(consumer, "dnsRecords").Count, "c1_hub_dns_evidence",
            "Unrelated or duplicate evidence files cannot be included in this consumer handoff.", 409);
        foreach (var endpoint in HubArray(consumer, "privateEndpoints").OfType<JsonObject>())
        {
            var covered = files.Values.Where(file => file.Value["dns"]?["privateEndpointId"]?.GetValue<string>() == Text(endpoint, "id"))
                .SelectMany(file => HubArray(HubObject(file.Value, "dns"), "cnameChain").Select(host => host!.GetValue<string>()))
                .ToHashSet(StringComparer.Ordinal);
            Guard.Against(HubArray(endpoint, "fqdns").Any(host => !covered.Contains(host!.GetValue<string>())),
                "c1_hub_dns_evidence", "Each provider-returned endpoint name must be covered by an actual registration/CNAME proof.", 409);
        }
        var hash = FabricC1Deployment.HubWireHash(consumer);
        if (state.Objects.TryGetValue(HubBindingsExportKey, out var prior))
            Guard.Against(prior.Kind != "SharedHubBindingsExport" || prior.RequestHash != hash ||
                !JsonNode.DeepEquals(prior.Response, consumer), "c1_hub_registration_drift",
                "The already exported owner input cannot change silently on resume.", 409);
        var storedBytes = new JsonObject(files.OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => KeyValuePair.Create(file.Key, (JsonNode?)JsonValue.Create(Convert.ToBase64String(file.Value.Bytes)))));
        if (state.Objects.TryGetValue(HubBindingsEvidenceKey, out var originalBytes))
            Guard.Against(originalBytes.Kind != "SharedHubBindingsEvidence" ||
                originalBytes.RequestHash != Canonical.Hash(originalBytes.Response) || !JsonNode.DeepEquals(originalBytes.Response, storedBytes),
                "c1_hub_registration_drift", "Original exported evidence bytes cannot be regenerated or rebound on resume.", 409);
        else SaveObject(HubBindingsEvidenceKey, "SharedHubBindingsEvidence", input.Network!.SharedHub!.Generation!,
            Canonical.Hash(storedBytes), storedBytes);
        SaveObject(HubBindingsExportKey, "SharedHubBindingsExport", input.Network!.SharedHub!.Generation!, hash, consumer);
        return new(consumer, files.ToDictionary(file => file.Key, file => file.Value.Bytes, StringComparer.Ordinal));
    }

    public FabricC1HubBindingsExport RestoreSharedHubBindingsExport() => ExportSharedHubBindings(ReadHubEvidenceBytes());

    private byte[][] ReadHubEvidenceBytes()
    {
        var saved = Owned(HubBindingsEvidenceKey, "SharedHubBindingsEvidence");
        Guard.Against(state.Objects[HubBindingsEvidenceKey].RequestHash != Canonical.Hash(saved) || saved.Count is < 1 or > 128,
            "c1_hub_evidence", "The original durable evidence inventory changed.", 409);
        return saved.Select(file =>
        {
            var bytes = DecodeHubEvidence(file.Value?.GetValue<string>());
            Guard.Against(Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Key,
                "c1_hub_evidence", "Original durable evidence bytes differ from their file digest.", 409);
            return bytes;
        }).ToArray();
    }

    private static byte[] DecodeHubEvidence(string? encoded)
    {
        Guard.Against(encoded is null || encoded.Length is < 4 or > 349528,
            "c1_hub_evidence", "Original evidence bytes are missing or oversized.", 409);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded!); }
        catch (FormatException) { throw new DomainException("c1_hub_evidence", "Original evidence encoding is invalid.", 409); }
        Guard.Against(bytes.Length is < 1 or > 262144, "c1_hub_evidence", "Original evidence byte length is invalid.", 409);
        return bytes;
    }

    private FabricC1HubDnsSelection[] ValidateHubDnsSelections(JsonObject receipt, IReadOnlyCollection<FabricC1HubDnsSelection> selections)
    {
        if (selections is null || selections.Count is < 1 or > 128 || selections.Any(item => item is null))
            throw new DomainException("c1_hub_dns_selection", "Explicit bounded DNS registrations are required, not inferred aliases or wildcard records.", 409);
        var zones = HubArray(HubObject(receipt, "dns"), "zones").OfType<JsonObject>().Select(zone => Text(zone, "name")).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in selections)
        {
            Guard.Against(string.IsNullOrWhiteSpace(item.PrivateEndpointId), "c1_hub_dns_selection",
                "An exact original endpoint identity is required.", 409);
            HubArmId(item.PrivateEndpointId, FabricC1Deployment.GroupId(input), "Microsoft.Network/privateEndpoints");
            Guard.Against(string.IsNullOrWhiteSpace(item.Fqdn) || string.IsNullOrWhiteSpace(item.Zone) || string.IsNullOrWhiteSpace(item.Name) ||
                !zones.Contains(item.Zone) || item.Fqdn != item.Fqdn.ToLowerInvariant() ||
                Uri.CheckHostName(item.Fqdn) != UriHostNameType.Dns || item.Fqdn.Contains('*') ||
                !Regex.IsMatch(item.Name, @"\A[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?\z", RegexOptions.CultureInvariant) ||
                Uri.CheckHostName(item.Name + "." + item.Zone) != UriHostNameType.Dns ||
                !seen.Add(item.Zone + "/" + item.Name) || !hosts.Add(item.PrivateEndpointId + "/" + item.Fqdn),
                "c1_hub_dns_selection", "Only unique literal records in already bound hub zones may be proposed.", 409);
        }
        return selections.OrderBy(item => item.Zone, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private JsonObject HubConsumerSlot(JsonObject receipt, string stage) => new()
    {
        ["bindingHash"] = state.BindingHash, ["deploymentId"] = input.DeploymentId, ["sourceCommit"] = input.SourceCommit,
        ["expiresAtUtc"] = receipt["expiresAtUtc"]!.DeepClone(), ["state"] = stage, ["spoke"] = receipt["spoke"]!.DeepClone(),
        ["edgePolicy"] = new JsonObject
        {
            ["dnsDomains"] = JsonSerializer.SerializeToNode(input.Network!.SharedHub!.DnsDomains, Canonical.Json),
            ["httpsRules"] = JsonSerializer.SerializeToNode(input.Network.SharedHub.HttpsRules, Canonical.Json)
        },
        ["privateEndpoints"] = new JsonArray(), ["dnsRecords"] = new JsonArray(), ["operatorEndpoints"] = new JsonArray(),
        ["web"] = null, ["offboarding"] = null
    };

    private async Task<Dictionary<string, JsonObject>> ReadOwnHubRegistrationNetwork(JsonObject receipt, CancellationToken ct)
    {
        var group = FabricC1Deployment.GroupId(input);
        var name = FabricC1Deployment.Name(input);
        var vnet = Text(HubObject(receipt, "spoke"), "vnetId");
        var ids = new[] { vnet, group + "/providers/Microsoft.Network/routeTables/" + name + "-egress-routes" }
            .Concat(new[] { "-nsg", "-endpoint-nsg", "-operator-endpoint-nsg" }
                .Select(suffix => group + "/providers/Microsoft.Network/networkSecurityGroups/" + name + suffix));
        var observed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var actual = await Read("https://management.azure.com" + id + "?api-version=" + HubNetworkApi, ct);
            Guard.Against(Text(actual, "id") != id, "c1_hub_spoke", "An own network GET returned a different resource.", 409);
            RequireArmOwnership(actual);
            observed.Add(id, actual);
        }
        var originalVnet = Owned("arm-hub-spoke", "ArmDeployment")["properties"]?["outputs"]?["vnet"]?["value"] as JsonObject ??
            throw new DomainException("c1_hub_spoke", "The original source-created spoke instance is missing.", 409);
        Guard.Against(Text(HubObject(originalVnet, "properties"), "resourceGuid") !=
            Text(HubObject(observed[vnet], "properties"), "resourceGuid"), "c1_hub_spoke",
            "A recreated VNet cannot inherit the original registration or workload authority.", 409);
        VerifyHubVnetAddresses(HubObject(observed[vnet], "properties"), HubObject(receipt, "spoke"));
        VerifyHubSpokePolicy(receipt, observed[vnet], id => observed[id]);
        Guard.Against(!JsonNode.DeepEquals(observed[vnet]["properties"]?["dhcpOptions"]?["dnsServers"],
            new JsonArray(Text(HubObject(receipt, "hub"), "dnsServerIp"))),
            "c1_hub_dns", "The original spoke DNS proxy changed during endpoint creation.", 409);
        var peers = await ReadAll("https://management.azure.com" + vnet + "/virtualNetworkPeerings?api-version=" + HubNetworkApi, false, ct);
        Guard.Against(peers.Length != 1, "c1_hub_peering", "No missing or additional own peering can be exported.", 409);
        VerifyOwnHubPeer(peers[0], Text(HubObject(receipt, "hub"), "vnetId"), true);
        Guard.Against(peers[0]["properties"]?["peeringState"]?.GetValue<string>() != "Connected",
            "c1_hub_peering", "Data registration requires the actual connected original peer.", 409);
        observed.Add(Text(peers[0], "id"), peers[0]);
        return observed;
    }

    private Dictionary<string, (byte[] Bytes, JsonObject Value)> VerifyHubEvidenceFiles(
        IReadOnlyCollection<byte[]> files, JsonObject consumer, string purpose, bool retirement)
    {
        if (files is null || files.Count is < 1 or > 128 || files.Any(file => file is null))
            throw new DomainException("c1_hub_evidence", "Original bounded evidence files are required.", 409);
        Guard.Against(retirement && state.CleanupStartedAtUtc is null, "c1_hub_evidence",
            "Retirement evidence requires the original durable stop/cleanup fence.", 409);
        var deadline = retirement ? PlanCleanupRequests(0).Deadline : PlanNormalRequests(0).Deadline;
        var result = new Dictionary<string, (byte[] Bytes, JsonObject Value)>(StringComparer.Ordinal);
        var subject = FabricC1Deployment.HubPrerequisiteSubject(consumer);
        foreach (var bytes in files)
        {
            Guard.Against(bytes.Length is < 1 or > 262144, "c1_hub_evidence", "An evidence file is empty or oversized.", 409);
            var value = JsonNode.Parse(bytes) as JsonObject ??
                throw new DomainException("c1_hub_evidence", "An original evidence JSON object is required.", 409);
            var observed = SourceUtc(value, "observedAtUtc");
            var expires = SourceUtc(value, "expiresAtUtc");
            var now = time.GetUtcNow();
            Guard.Against(Text(value, "contract") != "cohors-hub-prerequisite-evidence-1" ||
                Text(value, "generation") != input.Network!.SharedHub!.Generation ||
                Text(value, "consumerBindingHash") != state.BindingHash || Text(value, "sourceCommit") != input.SourceCommit ||
                Text(value, "purpose") != purpose || Text(value, "subjectSha256") != subject || value["passed"]?.GetValue<bool>() != true ||
                observed > now || expires <= observed || expires > observed.AddHours(24) || expires > deadline ||
                now >= expires || now >= observed.AddHours(24) ||
                (!retirement && expires > input.ExpiresAtUtc) ||
                (retirement && expires > input.Cleanup!.VerificationDeadlineUtc),
                "c1_hub_evidence", "Evidence must be fresh, source/subject/generation-bound and within the original time boundary.", 409);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Guard.Against(!result.TryAdd(hash, (bytes.ToArray(), value)), "c1_hub_evidence", "Duplicate evidence bytes are not additional proofs.", 409);
        }
        return result;
    }

    private static void VerifyHubDnsProof(JsonObject proof, FabricC1HubDnsSelection selected, JsonObject observation)
    {
        var resources = HubObject(observation, "resources");
        var endpoint = resources[selected.PrivateEndpointId] as JsonObject ??
            throw new DomainException("c1_hub_dns_evidence", "Original endpoint observations are absent.", 409);
        var nicId = endpoint["properties"]?["networkInterfaces"]?[0]?["id"]?.GetValue<string>() ??
            throw new DomainException("c1_hub_dns_evidence", "The original provider NIC is absent.", 409);
        Guard.Against(Text(proof, "privateEndpointId") != selected.PrivateEndpointId || Text(proof, "fqdn") != selected.Fqdn ||
            Text(proof, "zone") != selected.Zone || Text(proof, "name") != selected.Name ||
            Text(proof, "endpointObservationSha256") != FabricC1Deployment.HubWireHash(endpoint) ||
            Text(proof, "nicObservationSha256") != FabricC1Deployment.HubWireHash(resources[nicId]) ||
            Text(proof, "resolverIp") != Text(observation, "resolverIp") ||
            !JsonNode.DeepEquals(proof["endpointReadback"], endpoint) || !JsonNode.DeepEquals(proof["nicReadback"], resources[nicId]) ||
            proof["privateAddressResolutionAccepted"]?.GetValue<bool>() != false || proof["trafficAccepted"]?.GetValue<bool>() != false,
            "c1_hub_dns_evidence", "DNS evidence cannot substitute an address/name from another endpoint generation.", 409);
        var target = selected.Name + "." + selected.Zone;
        var chain = HubArray(proof, "cnameChain").Select(item => item!.GetValue<string>()).ToArray();
        Guard.Against(chain.Length is < 1 or > 17 || chain[0] != selected.Fqdn || chain[^1] != target ||
            chain.Distinct(StringComparer.Ordinal).Count() != chain.Length ||
            chain.Any(host => Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains('*') || host != host.ToLowerInvariant()),
            "c1_hub_dns_evidence", "A real, finite CNAME chain must bind the observed provider name to this literal private record.", 409);
        var exchanges = HubArray(proof, "dnsExchanges");
        Guard.Against(exchanges.Count != chain.Length - 1, "c1_hub_dns_evidence",
            "Every CNAME step requires its original request/response bytes; a passed/hash wrapper is insufficient.", 409);
        for (var index = 0; index < exchanges.Count; index++)
        {
            var exchange = exchanges[index] as JsonObject ??
                throw new DomainException("c1_hub_dns_evidence", "The original DNS exchange is missing.", 409);
            var question = Convert.FromBase64String(Text(exchange, "queryBase64"));
            var answer = Convert.FromBase64String(Text(exchange, "responseBase64"));
            Guard.Against(FabricC1HubDns.ReadCname(question, answer, chain[index]) != chain[index + 1],
                "c1_hub_dns_evidence", "The original DNS response does not prove the selected next canonical name.", 409);
        }
    }
}
