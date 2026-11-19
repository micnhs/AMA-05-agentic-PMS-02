using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed partial class FabricC1Provisioner
{
    private static readonly (string Purpose, string Key, string Kind)[] PrivateRetirementProofs =
    [
        ("stoppedWorkEvidenceSha256", PrivateWorkKey, "PrivateWorkStopped"),
        ("credentialsAndGrantsRevokedEvidenceSha256", PrivateAccessKey, "PrivateCredentialsRevoked"),
        ("spokePeeringAbsentEvidenceSha256", PrivatePeerKey, "PrivateSpokePeeringAbsent")
    ];

    private string PrivateSpokePeerId() => FabricC1Deployment.GroupId(input) +
        "/providers/Microsoft.Network/virtualNetworks/" + FabricC1Deployment.Name(input) + "-vnet/virtualNetworkPeerings/to-shared-hub";

    private JsonObject PrivateOriginalHub() => HubObject(RequirePrivateCleanupOriginal(), "hubReceipt");

    private FabricC1ProvisionedObject? PrivatePeerCreation(JsonObject receipt)
    {
        var stage = state.Objects.ContainsKey("arm-hub-transit") ? "hub-transit" : "hub-peer";
        if (!state.Objects.TryGetValue("arm-" + stage, out var saved)) return null;
        RequireHubPeerReceipt(receipt, stage == "hub-transit");
        return saved;
    }

    private async Task<bool> RemovePrivateSpokePeering(CancellationToken ct)
    {
        RequirePrivateProof(PrivateWorkKey, "PrivateWorkStopped");
        RequirePrivateProof(PrivateAccessKey, "PrivateCredentialsRevoked");
        var receipt = PrivateOriginalHub();
        if (state.Objects.ContainsKey(PrivatePeerKey))
        {
            RequirePrivateProof(PrivatePeerKey, "PrivateSpokePeeringAbsent");
            ClearPrivatePeerPending();
            return true;
        }
        if (!PrivatePollDue(PrivatePeerKey)) return false;
        Guard.Against(new[] { "arm-hub-peer", "arm-hub-transit", "arm-hub-spoke" }
            .Any(key => state.PendingCreates.ContainsKey(key) || state.PendingOperations.ContainsKey(key)),
            "c1_private_cleanup_peer_unknown", "An unknown original network mutation must be read-only reconciled before removing transit.", 409);
        var original = PrivatePeerCreation(receipt);
        var uri = new Uri("https://management.azure.com" + PrivateSpokePeerId() + "?api-version=" + HubNetworkApi);
        var current = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            RecordPrivatePeerAbsence(original, uri);
            return true;
        }
        Guard.Against(original is null, "c1_private_cleanup_peer_unknown",
            "An existing unjournaled peer is never adopted or deleted, even under the own VNet.", 409);
        var snapshot = original!.Response["properties"]!["outputs"]!["peering"]!["value"]!.AsObject();
        var hubVnet = Text(HubObject(receipt, "hub"), "vnetId");
        var remote = original.Id.EndsWith("/c1-hub-transit", StringComparison.Ordinal);
        VerifyOwnHubPeer(current.Body, hubVnet, remote);
        VerifyOwnHubPeer(snapshot, hubVnet, remote);
        var observation = HubArray(HubObject(receipt, "readback"), "resources").OfType<JsonObject>()
            .SingleOrDefault(item => Text(item, "id") == PrivateSpokePeerId());
        var expectedEtag = observation?["etag"]?.GetValue<string>() ?? snapshot["etag"]?.GetValue<string>();
        Guard.Against(string.IsNullOrWhiteSpace(expectedEtag) ||
            PrivateQuotedETag(expectedEtag!) != PrivateETag(current) ||
            observation is not null && Text(observation, "sha256") != FabricC1Deployment.HubWireHash(current.Body),
            "c1_private_cleanup_peer_replaced", "The peer differs from its original output/last admitted owner observation.", 409);
        var vnetId = Text(HubObject(receipt, "spoke"), "vnetId");
        var vnet = await Send(HttpMethod.Get, new("https://management.azure.com" + vnetId + "?api-version=" + HubNetworkApi),
            ct: ct, cleanup: true);
        RequireStatus(vnet, 200);
        RequireArmOwnership(vnet.Body);
        var originalVnet = Owned("arm-hub-spoke", "ArmDeployment")["properties"]?["outputs"]?["vnet"]?["value"] as JsonObject ??
            throw new DomainException("c1_private_cleanup_peer_replaced", "The original own VNet instance is missing.", 409);
        Guard.Against(Text(vnet.Body, "id") != vnetId ||
            GuidValue(Text(HubObject(originalVnet, "properties"), "resourceGuid")) !=
            GuidValue(Text(HubObject(vnet.Body, "properties"), "resourceGuid")),
            "c1_private_cleanup_peer_replaced", "A recreated VNet cannot inherit authority to remove the original peering.", 409);
        var peers = await ReadAll("https://management.azure.com" + vnetId + "/virtualNetworkPeerings?api-version=" + HubNetworkApi,
            false, ct, cleanup: true);
        Guard.Against(peers.Length != 1 || Text(peers[0], "id") != PrivateSpokePeerId() ||
            !JsonNode.DeepEquals(peers[0]["properties"], current.Body["properties"]),
            "c1_private_cleanup_peer_replaced", "Additional or changed spoke peers require review, never a broad network cleanup.", 409);
        var intentKey = PrivatePeerKey + "-intent";
        if (state.Objects.TryGetValue(intentKey, out var previous))
        {
            var intent = RequirePrivateProof(intentKey, "PrivatePeerDeleteIntent");
            Guard.Against(Text(intent, "uri") != uri.AbsoluteUri || Text(intent, "creationRequestSha256") != original.RequestHash ||
                Text(intent, "ifMatch") != PrivateETag(current) ||
                state.PendingDeletes.GetValueOrDefault(PrivatePeerKey) != previous.RequestHash,
                "c1_private_cleanup_peer_unknown", "An original peer deletion is uncertain or changed; no replay.", 409);
            SetPrivatePoll(PrivatePeerKey, current.RetryAfterSeconds ?? 5);
            return false;
        }
        SavePrivateActionIntent(PrivatePeerKey, "PrivatePeerDeleteIntent", new()
        {
            ["uri"] = uri.AbsoluteUri, ["creationRequestSha256"] = original.RequestHash,
            ["originalPeer"] = snapshot.DeepClone(), ["originalVnet"] = originalVnet.DeepClone(),
            ["before"] = current.Body.DeepClone(), ["vnet"] = vnet.Body.DeepClone(),
            ["ifMatch"] = PrivateETag(current), ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
        });
        var deleted = await Send(HttpMethod.Delete, uri, ifMatch: PrivateETag(current), ct: ct, cleanup: true);
        RequireStatus(deleted, 200, 202, 204, 404);
        if (deleted.RetryAfterSeconds is not null || deleted.Status == 202)
        {
            SetPrivatePoll(PrivatePeerKey, deleted.RetryAfterSeconds ?? 5);
            if (!PrivatePollDue(PrivatePeerKey)) return false;
        }
        var readback = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(readback, 200, 404);
        if (readback.Status != 404)
        {
            SetPrivatePoll(PrivatePeerKey, readback.RetryAfterSeconds ?? 5);
            return false;
        }
        RecordPrivatePeerAbsence(original, uri);
        return true;
    }

    private void RecordPrivatePeerAbsence(FabricC1ProvisionedObject? creation, Uri uri)
    {
        SavePrivateProof(PrivatePeerKey, "PrivateSpokePeeringAbsent", new()
        {
            ["uri"] = uri.AbsoluteUri, ["status"] = 404,
            ["originalCreation"] = creation is null ? null : JsonSerializer.SerializeToNode(creation, Canonical.Json),
            ["deleteIntent"] = state.Objects.GetValueOrDefault(PrivatePeerKey + "-intent")?.Response.DeepClone(),
            ["workStoppedSha256"] = state.Objects[PrivateWorkKey].RequestHash,
            ["credentialsRevokedSha256"] = state.Objects[PrivateAccessKey].RequestHash,
            ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
        });
        ClearPrivatePeerPending();
    }

    private void ClearPrivatePeerPending()
    {
        if (!state.PendingDeletes.TryGetValue(PrivatePeerKey, out var intent)) return;
        Guard.Against(intent != state.Objects.GetValueOrDefault(PrivatePeerKey + "-intent")?.RequestHash,
            "c1_private_cleanup_intent", "An original peer deletion intent changed before absence reconciliation.", 409);
        var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        pending.Remove(PrivatePeerKey);
        PlanCleanupRequests(0);
        SaveCheckpointJournal(state with { PendingDeletes = pending });
    }

    private FabricC1HubBindingsExport ExportPrivateOffboarding()
    {
        if (state.Objects.ContainsKey(PrivateExportKey)) return RestorePrivateOffboardingExport();
        var original = PrivateOriginalHub();
        var consumer = HubConsumerSlot(original, "offboarded");
        var offboarding = new JsonObject(PrivateRetirementProofs
            .Select(proof => KeyValuePair.Create(proof.Purpose, (JsonNode?)null)));
        consumer["offboarding"] = offboarding;
        var files = new JsonObject();
        var (_, deadline) = PlanCleanupRequests(0);
        foreach (var (purpose, key, kind) in PrivateRetirementProofs)
        {
            var proof = RequirePrivateProof(key, kind);
            var observed = SourceUtc(proof, "observedAtUtc");
            var expiry = observed.AddHours(24) < deadline ? observed.AddHours(24) : deadline;
            Guard.Against(observed < state.CleanupStartedAtUtc || time.GetUtcNow() >= expiry,
                "c1_private_cleanup_evidence", "Original prerequisite observations cannot be renewed to survive the cleanup tail.", 409);
            var evidence = HubPrerequisiteEvidence(consumer, purpose, expiry);
            evidence["observedAtUtc"] = SourceUtcText(observed);
            evidence["proof"] = proof.DeepClone();
            evidence["stopFence"] = Owned(LifecycleStopKey, "PrivateLifecycleStop").DeepClone();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, Canonical.Json);
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            offboarding[purpose] = digest;
            files[digest] = Convert.ToBase64String(bytes);
        }
        var body = new JsonObject { ["consumer"] = consumer, ["evidenceFiles"] = files };
        SavePrivateProof(PrivateExportKey, "PrivateOffboardingExport", body);
        return RestorePrivateOffboardingExport();
    }

    public FabricC1HubBindingsExport RestorePrivateOffboardingExport()
    {
        var original = PrivateOriginalHub();
        var saved = RequirePrivateProof(PrivateExportKey, "PrivateOffboardingExport");
        var consumer = HubObject(saved, "consumer");
        var expected = HubConsumerSlot(original, "offboarded");
        expected["offboarding"] = consumer["offboarding"]?.DeepClone();
        Guard.Against(!JsonNode.DeepEquals(consumer, expected), "c1_private_cleanup_evidence",
            "The original own offboarded slot cannot change identity, allocation, policy, deadline or references.", 409);
        var offboarding = HubObject(consumer, "offboarding");
        HubFields(offboarding, PrivateRetirementProofs.Select(proof => proof.Purpose).ToArray());
        var stored = HubObject(saved, "evidenceFiles");
        Guard.Against(stored.Count != PrivateRetirementProofs.Length, "c1_private_cleanup_evidence",
            "Exactly the original three prerequisite byte files are required.", 409);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var (_, deadline) = PlanCleanupRequests(0);
        foreach (var (purpose, key, kind) in PrivateRetirementProofs)
        {
            var proof = RequirePrivateProof(key, kind);
            var hash = Text(offboarding, purpose);
            var bytes = DecodeHubEvidence(stored[hash]?.GetValue<string>());
            Guard.Against(Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash, "c1_private_cleanup_evidence",
                "Persisted prerequisite bytes no longer match their exported SHA256.", 409);
            var verified = VerifyHubEvidenceFiles([bytes], consumer, purpose, retirement: true);
            var value = verified[hash].Value;
            Guard.Against(!JsonNode.DeepEquals(value["proof"], proof) ||
                !JsonNode.DeepEquals(value["stopFence"], Owned(LifecycleStopKey, "PrivateLifecycleStop")) ||
                SourceUtc(value, "observedAtUtc") != SourceUtc(proof, "observedAtUtc") ||
                SourceUtc(value, "observedAtUtc") < state.CleanupStartedAtUtc ||
                SourceUtc(value, "expiresAtUtc") > deadline,
                "c1_private_cleanup_evidence", "A passed/hash wrapper cannot replace the original observations or extend their cleanup deadline.", 409);
            result.Add(hash, bytes);
        }
        return new((JsonObject)consumer.DeepClone(), result);
    }

    private void RequirePrivateOffboardingReceipt(JsonObject receipt)
    {
        Guard.Against(Text(receipt, "state") != "offboarded", "c1_hub_offboarded", "Only the terminal receipt uses the cleanup window.", 409);
        var export = RestorePrivateOffboardingExport();
        var original = PrivateOriginalHub();
        Guard.Against(Text(receipt, "generation") != input.Network!.SharedHub!.Generation ||
            Text(receipt, "consumerBindingHash") != state.BindingHash ||
            !JsonNode.DeepEquals(receipt["spoke"], export.Consumer["spoke"]) ||
            !JsonNode.DeepEquals(receipt["expiresAtUtc"], export.Consumer["expiresAtUtc"]) ||
            receipt["revision"] is not JsonValue version || !version.TryGetValue<int>(out var revision) ||
            revision <= original["revision"]!.GetValue<int>(),
            "c1_private_cleanup_ack", "Only a later owner revision for the exact exported retirement may acknowledge cleanup.", 409);
        Guard.Against(SourceUtc(HubObject(receipt, "readback"), "observedAtUtc") <
            SourceUtc(RequirePrivateProof(PrivatePeerKey, "PrivateSpokePeeringAbsent"), "observedAtUtc"),
            "c1_private_cleanup_ack", "An owner acknowledgement cannot predate the observed removal of the own peer.", 409);
    }

    private IEnumerable<Uri> PrivateHubReadUris(JsonObject receipt)
    {
        var hub = HubObject(receipt, "hub");
        foreach (var observation in HubArray(HubObject(receipt, "readback"), "resources").OfType<JsonObject>())
            yield return new("https://management.azure.com" + Text(observation, "id") + "?api-version=" + Text(observation, "apiVersion"));
        foreach (var (parent, collection, apiVersion) in new[]
        {
            (Text(hub, "firewallPolicyId"), "ruleCollectionGroups", HubNetworkApi),
            (Text(hub, "dnsPolicyId"), "dnsSecurityRules", "2025-05-01"),
            (Text(hub, "dnsPolicyId"), "virtualNetworkLinks", "2025-05-01"),
            (Text(hub, "vnetId"), "virtualNetworkPeerings", HubNetworkApi)
        }) yield return new("https://management.azure.com" + parent + "/" + collection + "?api-version=" + apiVersion);
        foreach (var zone in PrivateRetirementZones(receipt))
            foreach (var collection in new[] { "virtualNetworkLinks", "A" })
                yield return new("https://management.azure.com" + zone + "/" + collection + "?api-version=2020-06-01");
        foreach (var record in PrivateRetirementRecords())
            yield return new("https://management.azure.com" + record + "?api-version=2020-06-01");
        var previousWaf = HubObject(PrivateOriginalHub(), "hub")["wafId"]?.GetValue<string>();
        if (previousWaf is not null && hub["wafId"] is null)
            yield return new("https://management.azure.com" + previousWaf + "?api-version=2025-03-01");
    }

    private string[] PrivateRetirementZones(JsonObject receipt) =>
        new[] { receipt, PrivateOriginalHub() }.SelectMany(value => HubArray(HubObject(value, "dns"), "zones")
            .OfType<JsonObject>().Select(zone => HubArmId(Text(zone, "id"), input.Network!.SharedHub!.ResourceGroupId!,
                "Microsoft.Network/privateDnsZones"))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private string[] PrivateRetirementRecords()
    {
        var records = HubArray(HubObject(PrivateOriginalHub(), "dns"), "recordSets").Select(record => record!.GetValue<string>()).ToList();
        if (state.Objects.TryGetValue(HubBindingsExportKey, out var exported))
            records.AddRange(HubArray(exported.Response, "dnsRecords").OfType<JsonObject>().Select(record =>
                input.Network!.SharedHub!.ResourceGroupId + "/providers/Microsoft.Network/privateDnsZones/" +
                Text(record, "zone") + "/A/" + Text(record, "name")));
        foreach (var record in records)
            HubArmId(record, input.Network!.SharedHub!.ResourceGroupId!, "Microsoft.Network/privateDnsZones/A");
        return records.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task VerifyPrivateHubReferenceAbsence(JsonObject receipt,
        IReadOnlyDictionary<string, JsonObject> observed, CancellationToken ct)
    {
        var hub = HubObject(receipt, "hub");
        var spoke = Text(HubObject(receipt, "spoke"), "vnetId");
        var peerId = Text(hub, "vnetId") + "/virtualNetworkPeerings/to-fabric";
        Guard.Against(observed.ContainsKey(peerId) || observed.ContainsKey(PrivateSpokePeerId()) ||
            PrivateRetirementRecords().Any(observed.ContainsKey),
            "c1_private_cleanup_ack", "A terminal owner receipt still observes an original Fabric reference.", 409);
        var peers = await ReadAll("https://management.azure.com" + Text(hub, "vnetId") +
            "/virtualNetworkPeerings?api-version=" + HubNetworkApi, false, ct, cleanup: true);
        VerifyPrivateHubCollection(Text(hub, "vnetId") + "/virtualNetworkPeerings/", observed, peers);
        Guard.Against(peers.Any(peer => SameArmId(Text(peer, "id"), peerId) ||
            SameArmId(peer["properties"]?["remoteVirtualNetwork"]?["id"]?.GetValue<string>(), spoke)),
            "c1_private_cleanup_ack", "The hub owner's exact Fabric peering half is still present.", 409);
        foreach (var zone in PrivateRetirementZones(receipt))
        {
            var links = await ReadAll("https://management.azure.com" + zone + "/virtualNetworkLinks?api-version=2020-06-01",
                false, ct, cleanup: true);
            VerifyPrivateHubCollection(zone + "/virtualNetworkLinks/", observed, links);
            Guard.Against(links.Any(link => SameArmId(link["properties"]?["virtualNetwork"]?["id"]?.GetValue<string>(), spoke)),
                "c1_private_cleanup_ack", "A hub-owned DNS link still retains the original Fabric spoke.", 409);
            var records = await ReadAll("https://management.azure.com" + zone + "/A?api-version=2020-06-01", false, ct, cleanup: true);
            VerifyPrivateHubCollection(zone + "/A/", observed, records);
            Guard.Against(records.Any(record => record["properties"]?["metadata"]?["cohorsConsumer"]?.GetValue<string>() == state.BindingHash ||
                record["properties"]?["aRecords"] is JsonArray addresses &&
                addresses.OfType<JsonObject>().Any(address => HubDestinationTouchesSpoke(Text(address, "ipv4Address"), receipt))),
                "c1_private_cleanup_ack", "A hub DNS record still retains the original Fabric consumer.", 409);
        }
        foreach (var record in PrivateRetirementRecords())
            RequireStatus(await Send(HttpMethod.Get, new("https://management.azure.com" + record + "?api-version=2020-06-01"),
                ct: ct, cleanup: true), 404);
        if (hub["wafId"] is not null)
        {
            var id = Text(hub, "wafId");
            Guard.Against(!observed.ContainsKey(id), "c1_private_cleanup_ack", "The remaining shared WAF needs a fresh owner observation.", 409);
            var properties = HubObject(observed[id], "properties");
            foreach (var (field, name) in new[]
            {
                ("httpListeners", "https-fabric"), ("backendHttpSettingsCollection", "https-fabric"),
                ("backendAddressPools", "backend-fabric"), ("probes", "ready-fabric"), ("sslCertificates", "cert-fabric"),
                ("requestRoutingRules", "route-fabric"), ("trustedRootCertificates", "root-fabric")
            })
                Guard.Against(properties[field] is JsonArray values && values.OfType<JsonObject>().Any(value => Text(value, "name") == name),
                    "c1_private_cleanup_ack", "The shared WAF still retains a Fabric listener/backend/certificate reference.", 409);
            Guard.Against(properties["backendAddressPools"] is JsonArray pools && pools.OfType<JsonObject>()
                .SelectMany(pool => (pool["properties"]?["backendAddresses"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                .Any(address => address["ipAddress"] is JsonValue ip && HubDestinationTouchesSpoke(ip.GetValue<string>(), receipt) ||
                    address["fqdn"] is JsonValue fqdn && PrivateRetirementEndpointNames().Contains(fqdn.GetValue<string>(), StringComparer.OrdinalIgnoreCase)),
                "c1_private_cleanup_ack", "A renamed WAF backend cannot retain an original Fabric address or endpoint name.", 409);
        }
        else if (HubObject(PrivateOriginalHub(), "hub")["wafId"] is JsonValue previousWaf)
            RequireStatus(await Send(HttpMethod.Get,
                new("https://management.azure.com" + previousWaf.GetValue<string>() + "?api-version=2025-03-01"), ct: ct, cleanup: true), 404);
        var ownNames = PrivateRetirementEndpointNames();
        foreach (var group in observed.Where(pair => pair.Key.StartsWith(Text(hub, "firewallPolicyId") +
            "/ruleCollectionGroups/", StringComparison.Ordinal)).Select(pair => pair.Value))
            foreach (var collection in HubArray(HubObject(group, "properties"), "ruleCollections").OfType<JsonObject>())
                if (collection["action"]?["type"]?.GetValue<string>() == "Allow")
                    foreach (var rule in HubArray(collection, "rules").OfType<JsonObject>())
                        Guard.Against(new[] { "targetFqdns", "destinationFqdns" }.Any(field => rule[field] is JsonArray names &&
                            names.Any(name => ownNames.Contains(name!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))),
                            "c1_private_cleanup_ack", "A renamed firewall rule still retains an original own endpoint name.", 409);
    }

    private static void VerifyPrivateHubCollection(string prefix, IReadOnlyDictionary<string, JsonObject> observed, JsonObject[] current)
    {
        var expected = observed.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        Guard.Against(current.Length != expected.Length ||
            current.Select(item => Text(item, "id")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Length ||
            current.Any(item => !expected.Any(previous => SameArmId(previous.Key, Text(item, "id")) &&
                JsonNode.DeepEquals(previous.Value["properties"], item["properties"]))),
            "c1_private_cleanup_ack", "A fresh exact hub-owned reference collection is required; nested foreign references are never followed.", 409);
    }

    private string[] PrivateRetirementEndpointNames()
    {
        var names = HubArray(PrivateOriginalHub(), "operatorEndpoints").OfType<JsonObject>()
            .SelectMany(endpoint => HubArray(endpoint, "fqdns").Select(name => name!.GetValue<string>()));
        if (state.Objects.TryGetValue(HubBindingsExportKey, out var exported))
            names = names.Concat(HubArray(exported.Response, "privateEndpoints").OfType<JsonObject>()
                .SelectMany(endpoint => HubArray(endpoint, "fqdns").Select(name => name!.GetValue<string>())));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private FabricC1ProvisionedObject PrivateOwnerAcknowledgement(JsonObject receipt)
    {
        PlanCleanupRequests(0);
        if (state.Objects.ContainsKey(PrivateOwnerKey))
        {
            RequirePrivateProof(PrivateOwnerKey, "PrivateOwnerAcknowledgement");
            return state.Objects[PrivateOwnerKey];
        }
        var proof = new JsonObject
        {
            ["receipt"] = receipt.DeepClone(), ["exportSha256"] = state.Objects[PrivateExportKey].RequestHash,
            ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
        };
        return new("PrivateOwnerAcknowledgement", state.BindingHash, Canonical.Hash(proof), proof);
    }

    private bool IsPrivateCleanupUri(HttpMethod method, Uri uri)
    {
        if (input.Network?.SharedHub is null || state.CleanupStartedAtUtc is null ||
            !state.Objects.TryGetValue(LifecycleStopKey, out var stop) || stop.Kind != "PrivateLifecycleStop" ||
            stop.Id != state.BindingHash || stop.RequestHash != Canonical.Hash(stop.Response) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        if (uri.IdnHost != "management.azure.com")
        {
            foreach (var role in new[] { "released", "denied" })
                if (state.Objects.ContainsKey("source-notebook-job-" + role) && PrivateNotebook(role) is { } notebook &&
                    (method == HttpMethod.Get && uri == notebook.Read ||
                     method == HttpMethod.Post && uri == notebook.Cancel && PrivateStopIntentAllows(
                         PrivateCleanupPrefix + "notebook-stop-" + role, uri))) return true;
            return false;
        }
        var group = FabricC1Deployment.GroupId(input);
        if (method == HttpMethod.Get && uri.AbsolutePath == group + "/resources" &&
            PrivateCollectionQuery(uri, "2021-04-01")) return true;
        if (privateCleanupHubReadback is not null && method == HttpMethod.Get &&
            PrivateHubReadUris(privateCleanupHubReadback).Any(expected =>
                uri == expected || uri.AbsolutePath == expected.AbsolutePath &&
                PrivateCollectionQuery(uri, expected.Query["?api-version=".Length..]))) return true;
        if (!uri.AbsolutePath.StartsWith(group + "/providers/", StringComparison.Ordinal)) return false;
        var peer = PrivateSpokePeerId();
        var vnet = peer[..peer.LastIndexOf("/virtualNetworkPeerings/", StringComparison.Ordinal)];
        if (uri.Query == "?api-version=" + HubNetworkApi)
        {
            if (method == HttpMethod.Get && (uri.AbsolutePath == peer || uri.AbsolutePath == vnet ||
                uri.AbsolutePath == vnet + "/virtualNetworkPeerings")) return true;
            if (method == HttpMethod.Delete && uri.AbsolutePath == peer &&
                PrivateDeleteIntentAllows(PrivatePeerKey, "PrivatePeerDeleteIntent", uri) &&
                state.Objects.ContainsKey(PrivateWorkKey) && state.Objects.ContainsKey(PrivateAccessKey)) return true;
        }
        if (PrivateFoundationOutput("probe", "Microsoft.Compute/virtualMachines") is { } probe && uri.Query == ArtifactCommandApi)
        {
            var id = Text(probe, "id");
            if (method == HttpMethod.Get && (uri.AbsolutePath == id || uri.AbsolutePath == id + "/instanceView")) return true;
            if (method == HttpMethod.Post && uri.AbsolutePath == id + "/deallocate" && PrivateStopIntentAllows(PrivateProbeStopKey, uri)) return true;
        }
        if (PrivateFoundationOutput("capacity", "Microsoft.Fabric/capacities") is { } capacity && uri.Query == PrivateCapacityApi)
        {
            var id = Text(capacity, "id");
            if (method == HttpMethod.Get && uri.AbsolutePath == id) return true;
            if (method == HttpMethod.Post && uri.AbsolutePath == id + "/suspend" && PrivateStopIntentAllows(PrivateCapacityStopKey, uri)) return true;
        }
        if (!state.Objects.ContainsKey(PrivateOwnerKey)) return false;
        foreach (var target in PrivateArmCleanupTargets())
            if (uri.AbsoluteUri == "https://management.azure.com" + target.Id + "?api-version=" + target.Version)
                return method == HttpMethod.Get || method == HttpMethod.Delete &&
                    PrivateDeleteIntentAllows(target.Key, "PrivateResourceDeleteIntent", uri);
        return method == HttpMethod.Get && state.Objects.Values.Any(item => item.Kind == "ArmGeneratedChild" &&
            uri.AbsolutePath == item.Id && uri.Query == (item.Id.Contains("/networkInterfaces/", StringComparison.Ordinal)
                ? "?api-version=2024-05-01" : "?api-version=2024-03-02"));
    }

    private bool PrivateStopIntentAllows(string key, Uri uri) =>
        state.Objects.TryGetValue(key + "-intent", out var intent) && intent.Kind == "PrivateStopIntent" &&
        intent.Id == state.BindingHash && intent.RequestHash == Canonical.Hash(intent.Response) &&
        intent.Response["method"]?.GetValue<string>() == "POST" && intent.Response["uri"]?.GetValue<string>() == uri.AbsoluteUri &&
        state.PendingDeletes.GetValueOrDefault(key) == intent.RequestHash;

    private bool PrivateDeleteIntentAllows(string key, string kind, Uri uri) =>
        state.Objects.TryGetValue(key + "-intent", out var intent) && intent.Kind == kind &&
        intent.Id == state.BindingHash && intent.RequestHash == Canonical.Hash(intent.Response) &&
        intent.Response["uri"]?.GetValue<string>() == uri.AbsoluteUri &&
        state.PendingDeletes.GetValueOrDefault(key) == intent.RequestHash;

    private static bool PrivateCollectionQuery(Uri uri, string apiVersion)
    {
        var parameters = uri.Query.TrimStart('?').Split('&');
        return parameters.Count(parameter => parameter == "api-version=" + apiVersion) == 1 &&
            parameters.All(parameter => parameter == "api-version=" + apiVersion ||
                parameter.StartsWith("$skiptoken=", StringComparison.OrdinalIgnoreCase) ||
                parameter.StartsWith("%24skiptoken=", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record PrivateArmCleanupTarget(string Key, string Id, string Type, string Version,
        string CreationRequestHash, JsonObject Original);

    private PrivateArmCleanupTarget[] PrivateArmCleanupTargets()
    {
        var targets = new Dictionary<string, PrivateArmCleanupTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, output, type, version) in new[]
        {
            ("foundation", "probe", "Microsoft.Compute/virtualMachines", "2024-07-01"),
            ("foundation", "capacity", "Microsoft.Fabric/capacities", "2023-11-01"),
            ("hub-spoke", "vnet", "Microsoft.Network/virtualNetworks", HubNetworkApi),
            ("hub-spoke", "egressRoutes", "Microsoft.Network/routeTables", HubNetworkApi),
            ("hub-operator-access", "operatorNsg", "Microsoft.Network/networkSecurityGroups", HubNetworkApi)
        })
        {
            if (!state.Objects.TryGetValue("arm-" + stage, out var creation)) continue;
            var snapshot = creation.Response["properties"]?["outputs"]?[output]?["value"] as JsonObject;
            if (snapshot is null) continue;
            Guard.Against(creation.Kind != "ArmDeployment" || !FabricC1Execution.Hex(creation.RequestHash, 64) ||
                creation.Response["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
                "c1_private_cleanup_instance", "Final inventory deletion needs the unchanged completed original ARM request.", 409);
            var id = RequireTypedArmResource(snapshot, type);
            RequireArmOwnership(snapshot);
            targets.Add(id, new(PrivateCleanupPrefix + "resource-" + Canonical.HashText(id.ToLowerInvariant()),
                id, type, version, creation.RequestHash, snapshot));
        }
        return targets.Values.OrderBy(target => target.Type == "Microsoft.Compute/virtualMachines" ? 0 :
            target.Type == "Microsoft.Fabric/capacities" ? 1 : target.Type == "Microsoft.Network/virtualNetworks" ? 2 : 3).ToArray();
    }

    private async Task<FabricC1PrivateCleanupProgress> FinishPrivateInventoryCleanup(CancellationToken ct)
    {
        RestorePrivateOffboardingExport();
        var acknowledged = RequirePrivateProof(PrivateOwnerKey, "PrivateOwnerAcknowledgement");
        Guard.Against(Text(acknowledged, "exportSha256") != state.Objects[PrivateExportKey].RequestHash ||
            Text(Owned(HubReceiptKey, "SharedHubConsumerReceipt"), "state") != "offboarded",
            "c1_private_cleanup_ack", "Final own inventory cleanup needs the original acknowledged retirement.", 409);
        var inventory = await ReadPrivateGroupInventory(ct);
        var targets = PrivateArmCleanupTargets();
        bool Administrative(JsonObject item) => Text(item, "type") == "Microsoft.Resources/deployments" &&
            state.Objects.Values.Any(value => value.Kind == "ArmDeployment" && SameArmId(value.Id, Text(item, "id")));
        var unbound = inventory.Where(item => !Administrative(item) &&
            !targets.Any(target => SameArmId(target.Id, Text(item, "id"))) &&
            !state.Objects.Values.Any(value => value.Kind == "ArmGeneratedChild" && SameArmId(value.Id, Text(item, "id"))))
            .Select(item => Text(item, "id")).ToArray();
        var blockers = new List<string>(unbound.Select(id => "retained-unbound-or-unsupported-resource:" + id));
        foreach (var workspace in state.Objects.Where(pair => pair.Value.Kind == "Workspace"))
            if (!state.CleanupReceipts.TryGetValue(workspace.Key, out var absent) ||
                absent.Kind != "SourceWorkspaceAbsent" || absent.Id != workspace.Value.Id)
                blockers.Add("workspace-inventory-not-proven-absent:" + workspace.Key);
        var workspaceDependents = blockers.Any(value => value.StartsWith("workspace-inventory-not-proven-absent:", StringComparison.Ordinal));
        foreach (var target in targets.Where(target => !target.Type.StartsWith("Microsoft.Network/", StringComparison.Ordinal)))
        {
            if (target.Type == "Microsoft.Fabric/capacities" && workspaceDependents)
            { blockers.Add("capacity-workspaces-not-proven-absent:" + target.Id); continue; }
            if (!await DeletePrivateArmTarget(target, ct)) blockers.Add("original-resource-deletion-unresolved:" + target.Id);
        }
        foreach (var child in state.Objects.Where(pair => pair.Value.Kind == "ArmGeneratedChild"))
        {
            var parentId = Text(child.Value.Response, "parentId");
            var parent = targets.SingleOrDefault(target => SameArmId(target.Id, parentId));
            if (parent is null || !state.CleanupReceipts.ContainsKey(parent.Key))
            { blockers.Add("generated-child-retained-with-parent:" + child.Value.Id); continue; }
            var version = child.Value.Id.Contains("/networkInterfaces/", StringComparison.Ordinal) ? "2024-05-01" : "2024-03-02";
            var current = await Send(HttpMethod.Get, new("https://management.azure.com" + child.Value.Id + "?api-version=" + version),
                ct: ct, cleanup: true);
            RequireStatus(current, 200, 404);
            if (current.Status != 404) blockers.Add("generated-child-not-absent:" + child.Value.Id);
        }
        foreach (var target in targets.Where(target => target.Type.StartsWith("Microsoft.Network/", StringComparison.Ordinal)))
        {
            if (blockers.Count != 0)
            { blockers.Add("network-dependents-not-proven-absent:" + target.Id); continue; }
            if (!await DeletePrivateArmTarget(target, ct)) blockers.Add("original-resource-deletion-unresolved:" + target.Id);
        }
        if (blockers.Count != 0) return PrivateCleanupProgress("OwnInventoryCleanupBlocked", blockers);
        var remaining = targets.Length == 0 ? inventory : await ReadPrivateGroupInventory(ct);
        if (remaining.Any(item => !Administrative(item)))
            return PrivateCleanupProgress("OwnInventoryCleanupBlocked", remaining.Where(item => !Administrative(item)).Select(item => Text(item, "id")));
        if (!state.Objects.ContainsKey(PrivateInventoryKey))
            SavePrivateProof(PrivateInventoryKey, "PrivateInventoryAbsent", new()
            {
                ["ownerAcknowledgementSha256"] = state.Objects[PrivateOwnerKey].RequestHash,
                ["remainingDeploymentMetadata"] = new JsonArray(remaining.Select(item => item.DeepClone()).ToArray()),
                ["observedAtUtc"] = SourceUtcText(time.GetUtcNow()), ["retentionPurged"] = false
            });
        var identities = await CleanupIdentityPhase(ct);
        return PrivateCleanupProgress(identities.ActiveIdentityInventoryAbsent ? "OwnActiveInventoryAbsent" : "IdentityCleanupPendingReconciliation",
            identities.UnresolvedIdentityKeys);
    }

    private async Task<bool> DeletePrivateArmTarget(PrivateArmCleanupTarget target, CancellationToken ct)
    {
        if (!PrivatePollDue(target.Key)) return false;
        var uri = new Uri("https://management.azure.com" + target.Id + "?api-version=" + target.Version);
        var current = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404) { RecordPrivateArmAbsence(target); return true; }
        Guard.Against(state.CleanupReceipts.ContainsKey(target.Key), "c1_private_cleanup_restored",
            "A previously absent own resource reappeared; never delete its replacement automatically.", 409);
        RequireArmOwnership(current.Body);
        Guard.Against(RequireTypedArmResource(current.Body, target.Type) != target.Id,
            "c1_private_cleanup_instance", "Final resource GET returned a different identity.", 409);
        if (target.Type == "Microsoft.Compute/virtualMachines")
            VerifyPrivateProbe(current.Body, target.Original);
        else if (target.Type == "Microsoft.Fabric/capacities") VerifyPrivateCapacity(current.Body, target.Original);
        else
        {
            var originalInstance = target.Original["properties"]?["resourceGuid"]?.GetValue<string>();
            if (originalInstance is null) return false;
            Guard.Against(GuidValue(originalInstance) != GuidValue(Text(HubObject(current.Body, "properties"), "resourceGuid")),
                "c1_private_cleanup_instance", "A recreated own network resource is never adopted during final cleanup.", 409);
        }
        if (state.Objects.TryGetValue(target.Key + "-intent", out var prior))
        {
            var saved = RequirePrivateProof(target.Key + "-intent", "PrivateResourceDeleteIntent");
            Guard.Against(Text(saved, "uri") != uri.AbsoluteUri ||
                Text(saved, "creationRequestSha256") != target.CreationRequestHash ||
                !JsonNode.DeepEquals(saved["original"], target.Original) ||
                state.PendingDeletes.GetValueOrDefault(target.Key) != prior.RequestHash,
                "c1_private_cleanup_unknown", "The original resource deletion is unresolved; never reissue it.", 409);
            SetPrivatePoll(target.Key, current.RetryAfterSeconds ?? 5);
            return false;
        }
        if (target.Type == "Microsoft.Compute/virtualMachines" && !await VerifyPrivateVmDeletionDependents(target, current.Body, ct))
            return false;
        if (target.Type == "Microsoft.Fabric/capacities")
            Guard.Against(current.Body["properties"]?["state"]?.GetValue<string>() is not ("Suspended" or "Paused") ||
                current.Body["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
                "c1_private_cleanup_restored", "Paid compute became active after its original stop; do not silently delete it.", 409);
        if (target.Type == "Microsoft.Network/virtualNetworks" && !await VerifyPrivateVnetDeletionDependents(target, current.Body, ct))
            return false;
        var etag = PrivateETag(current);
        SavePrivateActionIntent(target.Key, "PrivateResourceDeleteIntent", new()
        {
            ["uri"] = uri.AbsoluteUri, ["creationRequestSha256"] = target.CreationRequestHash,
            ["original"] = target.Original.DeepClone(), ["before"] = current.Body.DeepClone(), ["ifMatch"] = etag
        });
        var deleted = await Send(HttpMethod.Delete, uri, ifMatch: etag, ct: ct, cleanup: true);
        RequireStatus(deleted, 200, 202, 204, 404);
        if (deleted.RetryAfterSeconds is not null || deleted.Status == 202)
        {
            SetPrivatePoll(target.Key, deleted.RetryAfterSeconds ?? 5);
            if (!PrivatePollDue(target.Key)) return false;
        }
        var after = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(after, 200, 404);
        if (after.Status != 404)
        {
            SetPrivatePoll(target.Key, after.RetryAfterSeconds ?? 5);
            return false;
        }
        RecordPrivateArmAbsence(target);
        return true;
    }

    private async Task<bool> VerifyPrivateVmDeletionDependents(PrivateArmCleanupTarget target, JsonObject current, CancellationToken ct)
    {
        var originalProperties = HubObject(target.Original, "properties");
        var properties = HubObject(current, "properties");
        if (!JsonNode.DeepEquals(properties["storageProfile"], originalProperties["storageProfile"]) ||
            !JsonNode.DeepEquals(properties["networkProfile"], originalProperties["networkProfile"]) ||
            originalProperties["storageProfile"]?["dataDisks"] is not (null or JsonArray { Count: 0 }) ||
            originalProperties["storageProfile"]?["osDisk"]?["deleteOption"]?.GetValue<string>() != "Delete" ||
            originalProperties["storageProfile"]?["osDisk"]?["managedDisk"]?["id"]?.GetValue<string>() is not { } diskId ||
            originalProperties["networkProfile"]?["networkInterfaces"] is not JsonArray { Count: > 0 and <= 8 } nics)
            return false;
        var children = new List<(string Id, string Type, string Instance, string Version)>()
        { (diskId, "Microsoft.Compute/disks", "uniqueId", "2024-03-02") };
        foreach (var nic in nics.OfType<JsonObject>())
        {
            if (nic["properties"]?["deleteOption"]?.GetValue<string>() is not ("Delete" or "Detach")) return false;
            children.Add((Text(nic, "id"), "Microsoft.Network/networkInterfaces", "resourceGuid", "2024-05-01"));
        }
        if (children.Count != nics.Count + 1 || children.Select(child => child.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != children.Count)
            return false;
        foreach (var child in children)
        {
            var receipts = state.Objects.Values.Where(item => item.Kind == "ArmGeneratedChild" && SameArmId(item.Id, child.Id)).ToArray();
            if (receipts.Length != 1) return false;
            var binding = receipts[0];
            var expected = Canonical.Hash(new { ResourceId = child.Id.ToLowerInvariant(), ParentId = target.Id.ToLowerInvariant(),
                Stage = "foundation", RequestHash = target.CreationRequestHash });
            Guard.Against(binding.RequestHash != expected || Text(binding.Response, "parentStage") != "foundation" ||
                Text(binding.Response, "parentRequestHash") != target.CreationRequestHash ||
                !SameArmId(Text(binding.Response, "parentId"), target.Id) ||
                binding.Response["cleanupAssociation"]?.GetValue<string>() != "ThroughRecordedParentOnly" ||
                binding.Response["directDeleteAuthorized"]?.GetValue<bool>() != false,
                "c1_private_cleanup_child", "A VM attachment lacks its original exclusive creation/instance binding.", 409);
            var original = HubObject(binding.Response, "resource");
            Guard.Against(!SameArmId(RequireTypedArmResource(original, child.Type), child.Id) ||
                GuidValue(Text(HubObject(original, "properties"), child.Instance)) != GuidValue(Text(binding.Response, "instanceId")),
                "c1_private_cleanup_child", "A VM attachment receipt changed its original instance.", 409);
            var observed = await Send(HttpMethod.Get, new("https://management.azure.com" + child.Id + "?api-version=" + child.Version),
                ct: ct, cleanup: true);
            RequireStatus(observed, 200, 404);
            if (observed.Status == 404) return false;
            var actual = observed.Body;
            if (actual["tags"] is not (null or JsonObject { Count: 0 })) RequireArmOwnership(actual);
            Guard.Against(!SameArmId(RequireTypedArmResource(actual, child.Type), child.Id) ||
                GuidValue(Text(HubObject(actual, "properties"), child.Instance)) != GuidValue(Text(binding.Response, "instanceId")) ||
                (child.Type == "Microsoft.Compute/disks"
                    ? !SameArmId(actual["managedBy"]?.GetValue<string>(), target.Id) ||
                      actual["managedByExtended"] is not (null or JsonArray { Count: 0 })
                    : !SameArmId(actual["properties"]?["virtualMachine"]?["id"]?.GetValue<string>(), target.Id)),
                "c1_private_cleanup_child", "A replaced/shared VM attachment cannot be implicitly deleted or detached.", 409);
        }
        var power = await Send(HttpMethod.Get,
            new("https://management.azure.com" + target.Id + "/instanceView" + ArtifactCommandApi), ct: ct, cleanup: true);
        RequireStatus(power, 200);
        var states = HubArray(power.Body, "statuses").OfType<JsonObject>().Select(value => Text(value, "code"))
            .Where(value => value.StartsWith("PowerState/", StringComparison.Ordinal)).ToArray();
        Guard.Against(!states.SequenceEqual(["PowerState/deallocated"]), "c1_private_cleanup_restored",
            "The original VM must remain deallocated before its attachment-bound final deletion.", 409);
        return true;
    }

    private async Task<bool> VerifyPrivateVnetDeletionDependents(PrivateArmCleanupTarget target, JsonObject current, CancellationToken ct)
    {
        var properties = HubObject(current, "properties");
        var original = HubObject(target.Original, "properties");
        if (!JsonNode.DeepEquals(properties["addressSpace"], original["addressSpace"]) ||
            properties["virtualNetworkPeerings"] is not (null or JsonArray { Count: 0 }) ||
            properties["ipAllocations"] is not (null or JsonArray { Count: 0 })) return false;
        var subnets = HubArray(properties, "subnets");
        var originals = HubArray(original, "subnets");
        if (subnets.Count != originals.Count) return false;
        foreach (var subnet in subnets.OfType<JsonObject>())
        {
            var matches = originals.OfType<JsonObject>().Where(value => SameArmId(Text(value, "id"), Text(subnet, "id"))).ToArray();
            if (matches.Length != 1) return false;
            var actual = HubObject(subnet, "properties");
            var prior = HubObject(matches[0], "properties");
            if (new[] { "addressPrefix", "addressPrefixes", "networkSecurityGroup", "routeTable" }
                    .Any(field => !JsonNode.DeepEquals(actual[field], prior[field])) ||
                new[] { "ipConfigurations", "ipConfigurationProfiles", "privateEndpoints", "applicationGatewayIPConfigurations",
                    "serviceAssociationLinks", "resourceNavigationLinks", "delegations" }
                    .Any(field => actual[field] is not (null or JsonArray { Count: 0 }))) return false;
        }
        if (subnets.Any(value => value is not JsonObject)) return false;
        var peers = await ReadAll("https://management.azure.com" + target.Id +
            "/virtualNetworkPeerings?api-version=" + HubNetworkApi, false, ct, cleanup: true);
        return peers.Length == 0;
    }

    private void RecordPrivateArmAbsence(PrivateArmCleanupTarget target)
    {
        var hash = Canonical.Hash(new { target.Id, target.CreationRequestHash, Original = Canonical.Hash(target.Original) });
        if (state.CleanupReceipts.TryGetValue(target.Key, out var existing))
        {
            Guard.Against(existing.Kind != "PrivateArmResourceAbsent" || existing.Id != target.Id || existing.RequestHash != hash,
                "c1_private_cleanup_instance", "An absence receipt belongs to a different original resource.", 409);
            return;
        }
        var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        pending.Remove(target.Key);
        PlanCleanupRequests(0);
        SaveCheckpointJournal(state with
        {
            PendingDeletes = pending, CleanupReceipts = new(state.CleanupReceipts, StringComparer.Ordinal)
            {
                [target.Key] = new("PrivateArmResourceAbsent", target.Id, hash, new()
                { ["status"] = 404, ["observedAtUtc"] = SourceUtcText(time.GetUtcNow()), ["retentionPurged"] = false })
            }
        });
    }
}
