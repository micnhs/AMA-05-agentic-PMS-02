using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1LifecycleProgress(string Status, string[] CompletedStages, JsonObject? OwnerConsumerSlot = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextPollAtUtc { get; init; }
    public bool CompleteC1Deployment => false;
    public bool NativeCasesAccepted => false;
    public string EvidenceLevel => "composed-control-observations-not-b5-c1-or-product-acceptance";
}

public static partial class FabricC1Deployment
{
    public static async Task<FabricC1LifecycleProgress> FenceAtOriginalDeadline(
        Func<FabricC1Provisioner> reopenOriginal, TimeProvider clock, CancellationToken ct)
    {
        string? binding = null;
        DateTimeOffset? originalStart = null;
        DateTimeOffset? originalDeadline = null;
        var contention = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan remaining;
            try
            {
                var deployment = reopenOriginal();
                var deadline = deployment.OriginalPrivateDeploymentDeadline();
                var state = deployment.State;
                Guard.Against(binding is not null && (binding != state.BindingHash || originalStart != state.StartedAtUtc || originalDeadline != deadline),
                    "c1_lifecycle_watch_binding", "An independent watcher cannot adopt another journal, start or deadline.", 409);
                binding = state.BindingHash;
                originalStart = state.StartedAtUtc;
                originalDeadline = deadline;
                remaining = deadline - clock.GetUtcNow();
                if (state.CleanupStartedAtUtc is not null || remaining <= TimeSpan.Zero)
                    return deployment.FencePrivateDeployment(state.CleanupStartedAtUtc is not null ? "operator-stop" : "original-deadline");
                contention = 0;
            }
            catch (DomainException error) when ((error.Code is "c1_deploy_journal_conflict" or "c1_deploy_journal_busy") && contention++ < 100)
            {
                // Retry only short local lease/CAS contention, never a provider request or a renewed budget.
                await Task.Delay(TimeSpan.FromMilliseconds(50), clock, ct);
                continue;
            }
            await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), clock, ct);
        }
    }
}

public sealed partial class FabricC1Provisioner
{
    private const string LifecycleStopKey = "private-lifecycle-stop";

    public DateTimeOffset OriginalPrivateDeploymentDeadline()
    {
        RequireCertainCheckpointJournal();
        Guard.Against(input.Network?.SharedHub is null || state.StartedAtUtc is null ||
            input.ExpiresAtUtc is null || input.MaxControlDurationSeconds is not (>= 1 and <= 7200) ||
            time.GetUtcNow() < state.StartedAtUtc, "c1_lifecycle_original",
            "The original started private deployment and unchanged bounded clock are required.", 409);
        var deadline = state.StartedAtUtc!.Value.AddSeconds(input.MaxControlDurationSeconds!.Value);
        return input.ExpiresAtUtc!.Value < deadline ? input.ExpiresAtUtc.Value : deadline;
    }

    public async Task<FabricC1LifecycleProgress> AdvanceSharedHubNetwork(
        IReadOnlyCollection<FabricC1HubDnsSelection>? dnsSelections, IFabricC1HubDnsTransport dns, CancellationToken ct)
    {
        PlanNormalRequests(0);
        var receipt = RequireStoredHubReceipt();
        var stage = Text(receipt, "state");
        if (stage == "reserved")
        {
            await EnsureResourceGroup(ct);
            await AdvanceHubArm("hub-spoke", ct);
            await AdvanceHubArm("hub-peer", ct);
            return HubProgress("AwaitingOwnerPeeredReceipt", HubConsumerSlot(receipt, "peered"));
        }
        if (stage == "peered")
        {
            await AdvanceHubArm("hub-transit", ct);
            return HubProgress("AwaitingOwnerConnectedReceipt", HubConsumerSlot(receipt, "connected"));
        }
        if (stage == "connected")
        {
            await AdvanceHubArm("foundation", ct);
            await ResolveCapacity(ct);
            foreach (var scope in new[] { "agent", "released", "denied" })
            {
                await EnsureWorkspace(scope, ct);
                await DenyWorkspacePublicAccess(scope, ct);
            }
            var endpoints = PrepareSharedHubPrivateEndpoints();
            await AdvanceHubArm("private-endpoints", endpoints, ct);
            if (dnsSelections is null)
                return HubProgress("AwaitingExactProviderDnsRegistration");
            await PrepareSharedHubBindings(dnsSelections, ct);
            var files = await CollectSharedHubDnsEvidence(dns, ct);
            var handoff = ExportSharedHubBindings(files);
            return HubProgress("AwaitingOwnerDataBoundReceipt", handoff.Consumer);
        }
        Guard.Against(stage != "data-bound", "c1_lifecycle_hub_state", "This source-owned C1 flow cannot skip, adopt or reactivate hub states.", 409);
        RequireHubDataAcknowledgement(receipt);
        await AdvanceHubArm("hub-operator-access", ct);
        await ReadHubObservations(receipt, ct, hubOnly: true);
        await ReadOwnHubRegistrationNetwork(receipt, ct);
        return HubProgress("NetworkBoundAwaitingPrivateBootstrap");
    }

    public async Task<FabricC1LifecycleProgress> AdvancePrivateDeployment(
        IReadOnlyCollection<FabricC1HubDnsSelection>? dnsSelections, IFabricC1HubDnsTransport dns,
        Action<IReadOnlyDictionary<string, string[]>> bindPrivateClients, CancellationToken ct)
    {
        var network = await AdvanceSharedHubNetwork(dnsSelections, dns, ct);
        if (network.Status != "NetworkBoundAwaitingPrivateBootstrap") return network;
        bindPrivateClients(OriginalPrivateWorkspaceRoutes());
        foreach (var role in new[] { "released", "denied" })
        {
            await EnsureLakehouse(role, ct);
            var materialization = await AdvanceSourceMaterialization(role, ct);
            if (materialization.Status == "Pending")
                return HubProgress("AwaitingOriginalSourceJob-" + role) with { NextPollAtUtc = materialization.NextPollAtUtc };
            Guard.Against(materialization.Status != "SourceReadbackObserved", "c1_lifecycle_source",
                "Only the original completed source readback advances the composed bootstrap.", 409);
        }
        await EnsureReleasedSourceAccess(ct);
        return HubProgress("AwaitingEffectiveSourceAndHostedRuntimeBindings");
    }

    public IReadOnlyDictionary<string, string[]> OriginalPrivateWorkspaceRoutes(bool cleanup = false)
    {
        if (cleanup)
        {
            Guard.Against(state.CleanupStartedAtUtc is null, "c1_lifecycle_original",
                "Cleanup routing requires the original durable stop fence.", 409);
            PlanCleanupRequests(0);
        }
        else PlanNormalRequests(0);
        var export = Owned(HubBindingsExportKey, "SharedHubBindingsExport");
        Guard.Against(state.Objects[HubBindingsExportKey].RequestHash != FabricC1Deployment.HubWireHash(export),
            "c1_hub_registration_drift", "The original exported private bindings changed.", 409);
        var candidate = Owned(HubBindingsCandidateKey, "SharedHubBindingsCandidate");
        var observation = HubObject(candidate, "observations");
        var selections = JsonSerializer.Deserialize<FabricC1HubDnsSelection[]>(observation["selections"], Canonical.Json) ??
            throw new DomainException("c1_hub_dns_selection", "The original DNS selection journal is missing.", 409);
        var files = ReadHubEvidenceBytes().ToDictionary(
            bytes => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            bytes => JsonNode.Parse(bytes)!.AsObject(), StringComparer.Ordinal);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var role in new[] { "agent", "released", "denied" })
        {
            var host = WorkspaceHost(Text(Owned("workspace-" + role, "Workspace"), "id"));
            var id = FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Network/privateEndpoints/" +
                FabricC1Deployment.Name(input) + "-" + role + "-pe";
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in HubArray(export, "dnsRecords").OfType<JsonObject>()
                .Where(record => Text(record, "sourceResourceId") == id))
            {
                var digest = Text(record, "dnsEvidenceSha256");
                Guard.Against(!files.TryGetValue(digest, out var file), "c1_hub_evidence", "An original route evidence file is missing.", 409);
                var proof = HubObject(file!, "dns");
                var selection = selections.Single(item => item.PrivateEndpointId == id &&
                    item.Zone == Text(record, "zone") && item.Name == Text(record, "name"));
                VerifyHubDnsProof(proof, selection, observation);
                if (!HubArray(proof, "cnameChain").Any(name => name!.GetValue<string>() == host)) continue;
                foreach (var address in HubArray(record, "addresses")) addresses.Add(address!.GetValue<string>());
            }
            Guard.Against(addresses.Count == 0, "c1_deploy_private_route",
                "A calculated workspace host alone is not a private route: original PE/NIC and exact observed name/CNAME evidence are required.", 409);
            result.Add(host, addresses.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }

    private async Task AdvanceHubArm(string stage, CancellationToken ct)
    {
        var request = PrepareSharedHubStage(stage);
        await AdvanceHubArm(stage, request, ct);
    }

    private async Task AdvanceHubArm(string stage, FabricC1HubArmRequest request, CancellationToken ct)
    {
        if (HasOriginalCompletedHubStage(stage, request)) return;
        await DeployArm(stage, request.Template, request.Parameters, ct);
    }

    private bool HasOriginalCompletedHubStage(string stage, FabricC1HubArmRequest request)
    {
        if (state.Objects.TryGetValue("arm-" + stage, out var completed))
        {
            var hash = Canonical.Hash(new JsonObject { ["properties"] = new JsonObject
            { ["mode"] = "Incremental", ["template"] = request.Template.DeepClone(), ["parameters"] = request.Parameters.DeepClone() } });
            Guard.Against(completed.Kind != "ArmDeployment" || completed.RequestHash != hash ||
                state.PendingCreates.ContainsKey("arm-" + stage) ||
                completed.Response["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
                "c1_lifecycle_stage_binding", "Resume requires the original completed stage, not a supplied success marker.", 409);
            VerifyArmDeploymentReadback(completed.Response, FabricC1Deployment.GroupId(input) +
                "/providers/Microsoft.Resources/deployments/c1-" + stage, request.Parameters, completed);
            return true;
        }
        return false;
    }

    public FabricC1HubBindingsExport RestoreSharedHubHandshakeExport()
    {
        PlanNormalRequests(0);
        var receipt = RequireStoredHubReceipt();
        var stage = Text(receipt, "state") switch
        {
            "reserved" => "hub-peer", "peered" => "hub-transit",
            _ => throw new DomainException("c1_hub_registration_state", "Only an original completed handshake can be re-exported without provider access.", 409)
        };
        Guard.Against(!HasOriginalCompletedHubStage(stage, PrepareSharedHubStage(stage)),
            "c1_lifecycle_stage_binding", "The original own peer stage has not completed.", 409);
        return new(HubConsumerSlot(receipt, stage == "hub-peer" ? "peered" : "connected"), new(StringComparer.Ordinal));
    }

    private FabricC1LifecycleProgress HubProgress(string status, JsonObject? consumer = null) =>
        new(status, new[] { "hub-spoke", "hub-peer", "hub-transit", "foundation", "private-endpoints", "hub-operator-access" }
            .Where(stage => state.Objects.TryGetValue("arm-" + stage, out var item) &&
                item.Kind == "ArmDeployment" && item.Response["properties"]?["provisioningState"]?.GetValue<string>() == "Succeeded")
            .Concat(new[] { "source-materialization-released", "source-materialization-denied", "materialization" }
                .Where(key => state.Objects.TryGetValue(key, out var item) &&
                    item.Kind is "SourceMaterializationReadback" or "SourceMaterialization" &&
                    item.RequestHash == Canonical.Hash(item.Response)))
            .ToArray(), consumer is null ? null : (JsonObject)consumer.DeepClone());

    private void RequireHubDataAcknowledgement(JsonObject receipt)
    {
        var exported = Owned(HubBindingsExportKey, "SharedHubBindingsExport");
        var records = HubArray(exported, "dnsRecords").OfType<JsonObject>().Select(record =>
            input.Network!.SharedHub!.ResourceGroupId + "/providers/Microsoft.Network/privateDnsZones/" +
            Text(record, "zone") + "/A/" + Text(record, "name")).Order(StringComparer.Ordinal).ToArray();
        Guard.Against(Text(receipt, "state") != "data-bound" ||
            !JsonNode.DeepEquals(receipt["operatorEndpoints"], exported["operatorEndpoints"]) ||
            !JsonNode.DeepEquals(receipt["spoke"], exported["spoke"]) ||
            !HubArray(HubObject(receipt, "dns"), "recordSets").Select(record => record!.GetValue<string>())
                .Order(StringComparer.Ordinal).SequenceEqual(records),
            "c1_hub_registration_ack", "Only the owner acknowledgement of the exact exported own slot admits private bootstrap.", 409);
    }

    public FabricC1LifecycleProgress FencePrivateDeployment(string reason)
    {
        RequireCertainCheckpointJournal();
        Guard.Against(reason is not ("operator-stop" or "original-deadline"), "c1_lifecycle_stop_reason", "An explicit stop reason is required.", 409);
        if (input.Network?.SharedHub is null || state.StartedAtUtc is not { } originalStartedAt)
            throw new DomainException("c1_lifecycle_original", "A stop needs the original started private deployment, not a fresh journal or timebox.", 409);
        var (next, deadline) = PlanCleanupRequests(0);
        var originalDeadline = OriginalPrivateDeploymentDeadline();
        Guard.Against(reason == "original-deadline" && time.GetUtcNow() < originalDeadline,
            "c1_lifecycle_not_due", "The independent deadline path cannot invent an earlier or renewed original clock.", 409);
        if (state.Objects.TryGetValue(LifecycleStopKey, out var prior))
        {
            Guard.Against(prior.Kind != "PrivateLifecycleStop" || prior.RequestHash != Canonical.Hash(prior.Response) ||
                prior.Id != state.BindingHash || SourceUtc(prior.Response, "cleanupStartedAtUtc") != state.CleanupStartedAtUtc ||
                SourceUtc(prior.Response, "originalDeadlineUtc") != originalDeadline ||
                SourceUtc(prior.Response, "cleanupDeadlineUtc") != deadline,
                "c1_lifecycle_stop_binding", "The original stop/cleanup fence cannot be rebound or renewed.", 409);
            return HubProgress("StopFencedAwaitingOrderedCleanup");
        }
        var stop = new JsonObject
        {
            ["reason"] = reason, ["bindingHash"] = state.BindingHash, ["sourceCommit"] = input.SourceCommit,
            ["originalStartedAtUtc"] = originalStartedAt.ToString("O"), ["originalDeadlineUtc"] = originalDeadline.ToString("O"),
            ["cleanupStartedAtUtc"] = next.CleanupStartedAtUtc!.Value.ToString("O"), ["cleanupDeadlineUtc"] = deadline.ToString("O"),
            ["workStopped"] = false, ["grantsRevoked"] = false, ["peerRemoved"] = false, ["ownerOffboarded"] = false
        };
        next = next with { Objects = new(next.Objects, StringComparer.Ordinal)
        { [LifecycleStopKey] = new("PrivateLifecycleStop", state.BindingHash, Canonical.Hash(stop), stop) } };
        SaveJournal(next);
        state = next;
        return HubProgress("StopFencedAwaitingOrderedCleanup");
    }
}
