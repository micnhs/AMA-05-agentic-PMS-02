using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1PrivateCleanupProgress(string Status, string[] CompletedStages, string[] Blockers,
    FabricC1HubBindingsExport? OwnerHandoff = null)
{
    public bool CompleteC1Cleanup => false;
    public string EvidenceLevel => "ordered-own-control-inventory-not-effective-runtime-revocation-or-retention-purge";
    public DateTimeOffset? NextPollAtUtc { get; init; }
}

public sealed partial class FabricC1Provisioner
{
    private const string PrivateCleanupPrefix = "private-cleanup-";
    private const string PrivateOriginalKey = PrivateCleanupPrefix + "original";
    private const string PrivateWorkKey = PrivateCleanupPrefix + "work-stopped";
    private const string PrivateAccessKey = PrivateCleanupPrefix + "credentials-revoked";
    private const string PrivatePeerKey = PrivateCleanupPrefix + "spoke-peer-absent";
    private const string PrivateExportKey = PrivateCleanupPrefix + "offboarding-export";
    private const string PrivateOwnerKey = PrivateCleanupPrefix + "owner-acknowledged";
    private const string PrivateInventoryKey = PrivateCleanupPrefix + "inventory-absent";
    private const string PrivateProbeStopKey = PrivateCleanupPrefix + "probe-stop";
    private const string PrivateCapacityStopKey = PrivateCleanupPrefix + "capacity-stop";
    private const string PrivateCapacityApi = "?api-version=2023-11-01";
    private const string PrivateArmObservationSuffix = "-observation";
    private JsonObject? privateCleanupHubReadback;

    public async Task<FabricC1PrivateCleanupProgress> AdvancePrivateCleanup(string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FencePrivateDeployment(reason);
        if (!state.Objects.ContainsKey(HubReceiptKey))
        {
            var identities = await CleanupIdentityPhase(ct);
            return PrivateCleanupProgress(identities.ActiveIdentityInventoryAbsent
                ? "OwnIdentityInventoryAbsentBeforeHubBinding" : "AwaitingOriginalIdentityReconciliation",
                identities.UnresolvedIdentityKeys.Append("no-owner-admission-no-offboarding-claim"));
        }
        EnsurePrivateCleanupOriginal();
        if (state.Objects.ContainsKey(PrivateExportKey))
        {
            var exported = RestorePrivateOffboardingExport();
            if (!state.Objects.ContainsKey(PrivateOwnerKey))
                return PrivateCleanupProgress("AwaitingOwnerOffboardedReceipt", [], exported);
            return await FinishPrivateInventoryCleanup(ct);
        }

        if (!state.Objects.ContainsKey(PrivateWorkKey))
        {
            var blockers = new List<string>();
            async Task StopIndependently(string key, Func<Task> stop)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await stop();
                    ct.ThrowIfCancellationRequested();
                }
                catch (Exception error) when (PrivateProviderResponseFailure(error, ct))
                {
                    RecordPrivateProviderFailure(key, error, blockers, ct);
                }
            }
            await StopIndependently(PrivateProbeStopKey, () => StopPrivateProbe(blockers, ct));
            foreach (var role in new[] { "released", "denied" })
                await StopIndependently(PrivateCleanupPrefix + "notebook-stop-" + role, () => StopPrivateNotebook(role, blockers, ct));
            await StopIndependently(PrivateCapacityStopKey, () => StopPrivateCapacity(blockers, ct));
            blockers.AddRange(PrivateUnsupportedWork());
            if (blockers.Count != 0) return PrivateCleanupProgress("AwaitingVerifiedWorkStop", blockers);
            var inventory = await ReadPrivateGroupInventory(ct);
            var unexpected = inventory.Where(resource => !PrivateNonExecutingResource(resource)).Select(resource => Text(resource, "id")).ToArray();
            if (unexpected.Length != 0)
                return PrivateCleanupProgress("UnresolvedOwnWorkInventory", unexpected);
            SavePrivateProof(PrivateWorkKey, "PrivateWorkStopped", new()
            {
                ["originalInventory"] = RequirePrivateCleanupOriginal().DeepClone(),
                ["observations"] = PrivateStopObservations(),
                ["armInventory"] = new JsonArray(inventory.Select(resource => resource.DeepClone()).ToArray()),
                ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
            });
        }
        RequirePrivateProof(PrivateWorkKey, "PrivateWorkStopped");
        if (!state.Objects.ContainsKey(PrivateAccessKey))
        {
            var blockers = await CleanupPrivateAccessInventory(ct);
            if (blockers.Length != 0)
                return PrivateCleanupProgress("AwaitingEffectiveCredentialAndGrantRevocation", blockers);
            SavePrivateProof(PrivateAccessKey, "PrivateCredentialsRevoked", new()
            {
                ["originalInventory"] = RequirePrivateCleanupOriginal().DeepClone(),
                ["workStoppedSha256"] = state.Objects[PrivateWorkKey].RequestHash,
                ["basis"] = "no-issued-runtime-credentials-or-data-grants-in-the-closed-original-history",
                ["identityAbsences"] = new JsonObject(state.CleanupReceipts
                    .Where(pair => pair.Value.Kind == "ActiveDirectoryObjectAbsent")
                    .Select(pair => KeyValuePair.Create(pair.Key, JsonSerializer.SerializeToNode(pair.Value, Canonical.Json)))),
                ["credentialObservations"] = new JsonObject(state.Objects
                    .Where(pair => pair.Value.Kind == "PrivateUnusedIdentityCredentials")
                    .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value.Response.DeepClone()))),
                ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
            });
        }
        RequirePrivateProof(PrivateAccessKey, "PrivateCredentialsRevoked");
        var peerBlockers = new List<string>();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!await RemovePrivateSpokePeering(ct)) peerBlockers.Add("original-spoke-peer-delete-outcome");
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (PrivateProviderResponseFailure(error, ct))
        {
            RecordPrivateProviderFailure(PrivatePeerKey, error, peerBlockers, ct);
            peerBlockers.Add("original-spoke-peer-delete-outcome");
        }
        if (peerBlockers.Count != 0)
            return PrivateCleanupProgress("AwaitingOriginalSpokePeeringDeletion", peerBlockers);
        return PrivateCleanupProgress("AwaitingOwnerOffboardedReceipt", [], ExportPrivateOffboarding());
    }

    private bool PrivateProviderResponseFailure(Exception error, CancellationToken ct) =>
        // SaveJournal fences every local write failure before it reaches this filter.
        !checkpointJournalUncertain && !ct.IsCancellationRequested &&
        (error is HttpRequestException or IOException or OperationCanceledException ||
         error is DomainException domain && (domain.Code.StartsWith("c1_deploy_http_", StringComparison.Ordinal) ||
             domain.Code is "c1_deploy_response_shape" or "c1_deploy_response_size" or "c1_response_limit"));

    private void RecordPrivateProviderFailure(string key, Exception error, List<string> blockers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        blockers.Add(error is DomainException domain && domain.Code.StartsWith("c1_deploy_http_", StringComparison.Ordinal)
            ? "provider-response-rejected:" + key + ":" + domain.Code
            : "provider-response-unresolved:" + key);
        var (_, deadline) = PlanCleanupRequests(0);
        if (time.GetUtcNow().AddSeconds(5) < deadline) SetPrivatePoll(key, 5);
    }

    private FabricC1PrivateCleanupProgress PrivateCleanupProgress(string status, IEnumerable<string> blockers,
        FabricC1HubBindingsExport? export = null)
    {
        if (export is null && state.Objects.ContainsKey(PrivateExportKey)) export = RestorePrivateOffboardingExport();
        var polls = state.Objects.Where(pair => pair.Key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) &&
                pair.Value.Kind == "PrivateCleanupPoll")
            .Select(pair => SourceUtc(RequirePrivateProof(pair.Key, "PrivateCleanupPoll"), "notBeforeUtc"))
            .Concat(state.Objects.Where(pair => pair.Value.Kind == "SourceNotebookJob" && pair.Value.Response["notBeforeUtc"] is not null)
                .Select(pair => SourceUtc(pair.Value.Response, "notBeforeUtc")))
            .Where(value => value > time.GetUtcNow()).Select(value => (DateTimeOffset?)value);
        return new(status,
            new[] { LifecycleStopKey, PrivateWorkKey, PrivateAccessKey, PrivatePeerKey, PrivateExportKey, PrivateOwnerKey, PrivateInventoryKey }
                .Where(state.Objects.ContainsKey).ToArray(),
            blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), export)
        { NextPollAtUtc = polls.Min() };
    }

    private void EnsurePrivateCleanupOriginal()
    {
        if (state.Objects.ContainsKey(PrivateOriginalKey)) { RequirePrivateCleanupOriginal(); return; }
        Guard.Against(state.Objects.Keys.Concat(state.PendingCreates.Keys).Concat(state.PendingOperations.Keys)
            .Concat(state.PendingDeletes.Keys).Any(key => key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal)),
            "c1_private_cleanup_original", "Cleanup history cannot be adopted without its original inventory snapshot.", 409);
        var receipt = Owned(HubReceiptKey, "SharedHubConsumerReceipt");
        Guard.Against(Text(receipt, "state") is "offboarded" or "web-bound",
            "c1_private_cleanup_original", "Cleanup needs the original admitted consumer, not a retired or unsupported web generation.", 409);
        var unsigned = (JsonObject)receipt.DeepClone();
        unsigned.Remove("receiptSha256");
        Guard.Against(Text(receipt, "receiptSha256") != FabricC1Deployment.HubWireHash(unsigned) ||
            Text(receipt, "consumerBindingHash") != state.BindingHash ||
            Text(receipt, "generation") != input.Network!.SharedHub!.Generation,
            "c1_private_cleanup_original", "The original admitted hub binding changed.", 409);
        var snapshot = new JsonObject
        {
            ["bindingHash"] = state.BindingHash, ["sourceCommit"] = input.SourceCommit,
            ["startedAtUtc"] = SourceUtcText(state.StartedAtUtc!.Value),
            ["cleanupStartedAtUtc"] = SourceUtcText(state.CleanupStartedAtUtc!.Value),
            ["stopSha256"] = state.Objects[LifecycleStopKey].RequestHash,
            ["requests"] = state.Requests, ["cleanupRequests"] = state.CleanupRequests,
            ["hubReceipt"] = receipt.DeepClone(),
            ["objects"] = new JsonObject(state.Objects.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)PrivateOriginalDescriptor(pair.Value)))),
            ["pendingCreates"] = JsonSerializer.SerializeToNode(state.PendingCreates, Canonical.Json),
            ["pendingOperations"] = JsonSerializer.SerializeToNode(state.PendingOperations, Canonical.Json),
            ["pendingDeletes"] = JsonSerializer.SerializeToNode(state.PendingDeletes, Canonical.Json)
        };
        SavePrivateProof(PrivateOriginalKey, "PrivateCleanupOriginal", snapshot);
    }

    private static JsonObject PrivateOriginalDescriptor(FabricC1ProvisionedObject value) => new()
    {
        ["kind"] = value.Kind, ["id"] = value.Id, ["requestHash"] = value.RequestHash,
        ["responseSha256"] = Canonical.Hash(value.Response)
    };

    private JsonObject RequirePrivateCleanupOriginal()
    {
        PlanCleanupRequests(0);
        var original = RequirePrivateProof(PrivateOriginalKey, "PrivateCleanupOriginal");
        Guard.Against(Text(original, "sourceCommit") != input.SourceCommit ||
            Text(original, "bindingHash") != state.BindingHash ||
            SourceUtc(original, "startedAtUtc") != state.StartedAtUtc ||
            SourceUtc(original, "cleanupStartedAtUtc") != state.CleanupStartedAtUtc ||
            Text(original, "stopSha256") != state.Objects.GetValueOrDefault(LifecycleStopKey)?.RequestHash ||
            original["requests"]!.GetValue<int>() > state.Requests ||
            original["cleanupRequests"]!.GetValue<int>() > state.CleanupRequests,
            "c1_private_cleanup_original", "The original cleanup fence, history or counters cannot be renewed.", 409);
        var objects = HubObject(original, "objects");
        foreach (var pair in objects)
        {
            var key = pair.Key;
            if (key == HubReceiptKey)
            {
                var previous = HubObject(original, "hubReceipt");
                key += "-" + previous["revision"]!.GetValue<int>() + "-" + Text(previous, "receiptSha256");
            }
            Guard.Against(!state.Objects.TryGetValue(key, out var saved) ||
                !JsonNode.DeepEquals(pair.Value, PrivateOriginalDescriptor(saved!)),
                "c1_private_cleanup_history", "Original creation/instance evidence must be retained unchanged throughout cleanup.", 409);
        }
        var pending = HubObject(original, "pendingCreates");
        foreach (var pair in pending)
        {
            var hash = pair.Value!.GetValue<string>();
            var reconciled = state.Objects.TryGetValue(pair.Key, out var created) && created.RequestHash == hash;
            var removedMembership = pair.Key.StartsWith("fabric-api-member-", StringComparison.Ordinal) &&
                state.CleanupReceipts.GetValueOrDefault("fabric-api-group")?.Kind == "ActiveDirectoryObjectAbsent";
            Guard.Against(state.PendingCreates.GetValueOrDefault(pair.Key) != hash && !reconciled && !removedMembership,
                "c1_private_cleanup_history", "An original unresolved create cannot be pruned to claim a closed cleanup history.", 409);
        }
        foreach (var pair in HubObject(original, "pendingOperations"))
            Guard.Against(state.PendingOperations.GetValueOrDefault(pair.Key) != pair.Value!.GetValue<string>(),
                "c1_private_cleanup_history", "An unsupported original operation cannot be removed to manufacture cleanup prerequisites.", 409);
        foreach (var pair in HubObject(original, "pendingDeletes"))
        {
            var hash = pair.Value!.GetValue<string>();
            var denied = state.DeniedRequests.GetValueOrDefault("cleanup-" + pair.Key);
            Guard.Against(state.PendingDeletes.GetValueOrDefault(pair.Key) != hash &&
                state.CleanupReceipts.GetValueOrDefault(pair.Key)?.RequestHash != hash &&
                !(denied is { Status: 401 or 403 } && denied.RequestHash == hash),
                "c1_private_cleanup_history", "An unknown original deletion requires its bound absence or denied-outcome receipt.", 409);
        }
        Guard.Against(state.Objects.Any(pair => !objects.ContainsKey(pair.Key) &&
            !pair.Key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) &&
            !pair.Key.StartsWith(HubReceiptKey + "-", StringComparison.Ordinal) &&
            !(pending[pair.Key]?.GetValue<string>() == pair.Value.RequestHash)),
            "c1_private_cleanup_history", "New provisioning history appeared after the durable stop fence.", 409);
        return original;
    }

    private void SavePrivateProof(string key, string kind, JsonObject proof)
    {
        PlanCleanupRequests(0);
        if (state.Objects.TryGetValue(key, out var existing))
        {
            Guard.Against(existing.Kind != kind || existing.Id != state.BindingHash ||
                existing.RequestHash != Canonical.Hash(proof) || !JsonNode.DeepEquals(existing.Response, proof),
                "c1_private_cleanup_proof", "An original cleanup proof cannot be replaced on resume.", 409);
            return;
        }
        SaveObject(key, kind, state.BindingHash, Canonical.Hash(proof), proof);
    }

    private JsonObject RequirePrivateProof(string key, string kind)
    {
        var value = state.Objects.GetValueOrDefault(key);
        Guard.Against(value is null || value.Kind != kind || value.Id != state.BindingHash ||
            value.RequestHash != Canonical.Hash(value.Response),
            "c1_private_cleanup_proof", "The original durable cleanup proof is missing or changed.", 409);
        return value!.Response;
    }

    private void SavePrivateActionIntent(string key, string kind, JsonObject proof)
    {
        PlanCleanupRequests(0);
        Guard.Against(state.Objects.ContainsKey(key + "-intent") || state.PendingDeletes.ContainsKey(key),
            "c1_private_cleanup_intent", "An original action intent cannot be replaced.", 409);
        var intent = new FabricC1ProvisionedObject(kind, state.BindingHash, Canonical.Hash(proof), proof);
        SaveCheckpointJournal(state with
        {
            Objects = new(state.Objects, StringComparer.Ordinal) { [key + "-intent"] = intent },
            PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [key] = intent.RequestHash }
        });
    }

    private JsonObject? PrivateFoundationOutput(string field, string type)
    {
        if (!state.Objects.TryGetValue("arm-foundation", out var original)) return null;
        Guard.Against(original.Kind != "ArmDeployment" || !FabricC1Execution.Hex(original.RequestHash, 64) ||
            original.Id != FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Resources/deployments/c1-foundation" ||
            original.Response["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_private_cleanup_instance", "Only the completed original foundation can authorize stopping its own compute.", 409);
        var value = original.Response["properties"]?["outputs"]?[field]?["value"] as JsonObject ??
            throw new DomainException("c1_private_cleanup_instance", "The original compute output is missing; do not adopt by name.", 409);
        RequireArmOwnership(value);
        RequireTypedArmResource(value, type);
        return value;
    }

    private void VerifyPrivateProbe(JsonObject actual, JsonObject original)
    {
        RequireArmOwnership(actual);
        Guard.Against(RequireTypedArmResource(actual, "Microsoft.Compute/virtualMachines") != Text(original, "id") ||
            GuidValue(Text(HubObject(actual, "properties"), "vmId")) != GuidValue(Text(HubObject(original, "properties"), "vmId")) ||
            !JsonNode.DeepEquals(actual["identity"], original["identity"]),
            "c1_private_cleanup_instance", "The probe VM or its identity was replaced; no stop or deletion by name.", 409);
    }

    private async Task StopPrivateProbe(List<string> blockers, CancellationToken ct)
    {
        var original = PrivateFoundationOutput("probe", "Microsoft.Compute/virtualMachines");
        if (original is null) return;
        if (!PrivatePollDue(PrivateProbeStopKey))
        { blockers.Add("probe-stop-poll-not-due"); return; }
        var id = Text(original, "id");
        var vm = await Send(HttpMethod.Get, new("https://management.azure.com" + id + ArtifactCommandApi), ct: ct, cleanup: true);
        RequireStatus(vm, 200, 404);
        if (vm.Status == 404)
        {
            RecordPrivateStopped(PrivateProbeStopKey, new() { ["original"] = original.DeepClone(), ["status"] = 404 });
            return;
        }
        RequirePrivatePresenceNotRestored(PrivateProbeStopKey);
        VerifyPrivateProbe(vm.Body, original);
        var view = await Send(HttpMethod.Get, new("https://management.azure.com" + id + "/instanceView" + ArtifactCommandApi),
            ct: ct, cleanup: true);
        RequireStatus(view, 200);
        var power = HubArray(view.Body, "statuses").OfType<JsonObject>()
            .Select(status => Text(status, "code")).Where(code => code.StartsWith("PowerState/", StringComparison.Ordinal)).ToArray();
        Guard.Against(power.Length != 1, "c1_private_cleanup_vm_state", "A unique actual VM power state is required.", 409);
        if (power[0] == "PowerState/deallocated")
        {
            RecordPrivateStopped(PrivateProbeStopKey, new()
            { ["original"] = original.DeepClone(), ["vm"] = vm.Body.DeepClone(), ["instanceView"] = view.Body.DeepClone(), ["status"] = 200 });
            return;
        }
        RequireNotPrivateRestored(PrivateProbeStopKey);
        if (power[0] is not ("PowerState/running" or "PowerState/stopped" or "PowerState/deallocating"))
        { blockers.Add("unsupported-probe-power-state:" + power[0]); return; }
        if ((view.RetryAfterSeconds ?? vm.RetryAfterSeconds) is { } retry) SetPrivatePoll(PrivateProbeStopKey, retry);
        if (power[0] != "PowerState/deallocating")
            await SendPrivateStop(PrivateProbeStopKey, new("https://management.azure.com" + id + "/deallocate" + ArtifactCommandApi),
                original, vm.Body, PrivateETag(vm), ct);
        else SetPrivatePoll(PrivateProbeStopKey, view.RetryAfterSeconds ?? 5);
        blockers.Add("probe-deallocation-not-observed");
    }

    private void VerifyPrivateCapacity(JsonObject actual, JsonObject original)
    {
        RequireArmOwnership(actual);
        Guard.Against(RequireTypedArmResource(actual, "Microsoft.Fabric/capacities") != Text(original, "id") ||
            original["sku"]?["name"]?.GetValue<string>() != "F2" || actual["sku"]?["name"]?.GetValue<string>() != "F2" ||
            actual["sku"]?["tier"]?.GetValue<string>() != "Fabric" ||
            !JsonNode.DeepEquals(actual["properties"]?["administration"], original["properties"]?["administration"]) ||
            original["systemData"]?["createdAt"] is not JsonValue ||
            !JsonNode.DeepEquals(actual["systemData"]?["createdAt"], original["systemData"]?["createdAt"]) ||
            !JsonNode.DeepEquals(actual["systemData"]?["createdBy"], original["systemData"]?["createdBy"]),
            "c1_private_cleanup_instance", "Suspending F2 requires its original instance/administration, never an adopted capacity.", 409);
        var creation = HubObject(original, "systemData");
        Guard.Against(string.IsNullOrWhiteSpace(creation["createdBy"]?.GetValue<string>()) ||
            SourceUtc(creation, "createdAt") < state.StartedAtUtc || SourceUtc(creation, "createdAt") > state.CleanupStartedAtUtc,
            "c1_private_cleanup_instance", "The F2 creation must belong to this original started deployment, not a pre-existing capacity.", 409);
    }

    private async Task StopPrivateCapacity(List<string> blockers, CancellationToken ct)
    {
        var original = PrivateFoundationOutput("capacity", "Microsoft.Fabric/capacities");
        if (original is null)
        {
            if (state.Objects.Values.Any(item => item.Kind is "FabricCapacity" or "Workspace"))
                blockers.Add("capacity-original-instance-missing");
            return;
        }
        if (!PrivatePollDue(PrivateCapacityStopKey))
        { blockers.Add("capacity-stop-poll-not-due"); return; }
        var id = Text(original, "id");
        var current = await Send(HttpMethod.Get, new("https://management.azure.com" + id + PrivateCapacityApi), ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            RecordPrivateStopped(PrivateCapacityStopKey, new() { ["original"] = original.DeepClone(), ["status"] = 404 });
            return;
        }
        RequirePrivatePresenceNotRestored(PrivateCapacityStopKey);
        VerifyPrivateCapacity(current.Body, original);
        var properties = HubObject(current.Body, "properties");
        var status = Text(properties, "state");
        if ((status is "Suspended" or "Paused") && Text(properties, "provisioningState") == "Succeeded")
        {
            RecordPrivateStopped(PrivateCapacityStopKey, new()
            { ["original"] = original.DeepClone(), ["capacity"] = current.Body.DeepClone(), ["status"] = 200 });
            return;
        }
        RequireNotPrivateRestored(PrivateCapacityStopKey);
        if (current.RetryAfterSeconds is { } retry) SetPrivatePoll(PrivateCapacityStopKey, retry);
        if (status == "Active" && Text(properties, "provisioningState") == "Succeeded")
            await SendPrivateStop(PrivateCapacityStopKey, new("https://management.azure.com" + id + "/suspend" + PrivateCapacityApi),
                original, current.Body, current.ETag ?? current.Body["etag"]?.GetValue<string>(), ct);
        else if (status is "Suspending" or "Pausing") SetPrivatePoll(PrivateCapacityStopKey, current.RetryAfterSeconds ?? 5);
        else { blockers.Add("unsupported-capacity-state:" + status); return; }
        blockers.Add("capacity-suspension-not-observed");
    }

    private (FabricC1ProvisionedObject Job, FabricC1ProvisionedObject Intent, Uri Read, Uri Cancel)? PrivateNotebook(string role)
    {
        if (!state.Objects.TryGetValue("source-notebook-job-" + role, out var job)) return null;
        var plan = RestoreSourceNotebook(role);
        var intent = RequireSourceJobIntent(role, plan);
        RequireSourceJobReceipt(role, plan, intent, job);
        var workspace = Text(plan.Binding, "workspaceId");
        var root = $"https://{WorkspaceHost(workspace)}/v1/workspaces/{workspace}";
        return (job, intent,
            new(root + $"/notebooks/{intent.Id}/jobs/execute/instances/{job.Id}?beta=true"),
            new(root + $"/items/{intent.Id}/jobs/instances/{job.Id}/cancel"));
    }

    private async Task StopPrivateNotebook(string role, List<string> blockers, CancellationToken ct)
    {
        var target = PrivateNotebook(role);
        if (target is null)
        {
            var intentKey = "source-notebook-job-intent-" + role;
            var jobKey = "source-notebook-job-" + role;
            if (state.Objects.TryGetValue(intentKey, out var intent) &&
                (!state.DeniedRequests.TryGetValue(jobKey, out var denied) || denied.RequestHash != intent.RequestHash ||
                 denied.Status is not (401 or 403) || state.PendingCreates.ContainsKey(jobKey)))
                blockers.Add("unknown-original-notebook-submission:" + role);
            return;
        }
        var (job, originalIntent, read, cancel) = target.Value;
        var key = PrivateCleanupPrefix + "notebook-stop-" + role;
        var originalPoll = SourceUtc(job.Response, "notBeforeUtc");
        if (!PrivatePollDue(key, originalPoll))
        { blockers.Add("notebook-stop-poll-not-due:" + role); return; }
        var current = await Send(HttpMethod.Get, read, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        { blockers.Add("original-notebook-status-unresolved:" + role); return; }
        SameId(Text(current.Body, "id"), job.Id);
        SameId(Text(current.Body, "itemId"), originalIntent.Id);
        Guard.Against(Text(current.Body, "jobType") != "RunNotebook" || Text(current.Body, "invokeType") != "OnDemand",
            "c1_private_cleanup_job", "Only the original on-demand notebook workload may be stopped.", 409);
        var status = Text(current.Body, "status");
        if (status is "Completed" or "Failed" or "Cancelled")
        {
            var ended = SourceJobUtc(current.Body, "endTimeUtc");
            Guard.Against(ended < SourceUtc(originalIntent.Response, "submittedAtUtc") || ended > time.GetUtcNow(),
                "c1_private_cleanup_job", "A terminal workload requires a bound actual end time.", 409);
            RecordPrivateStopped(key, new()
            {
                ["originalJob"] = JsonSerializer.SerializeToNode(job, Canonical.Json),
                ["originalIntent"] = JsonSerializer.SerializeToNode(originalIntent, Canonical.Json),
                ["job"] = current.Body.DeepClone(), ["status"] = 200
            });
            return;
        }
        RequireNotPrivateRestored(key);
        if (status is not ("NotStarted" or "InProgress"))
        { blockers.Add("unsupported-notebook-state:" + role + ":" + status); return; }
        if (current.RetryAfterSeconds is { } retry) SetPrivatePoll(key, retry);
        await SendPrivateStop(key, cancel, job.Response, current.Body, null, ct);
        blockers.Add("notebook-cancellation-not-observed:" + role);
    }

    private async Task SendPrivateStop(string key, Uri uri, JsonObject original, JsonObject current,
        string? etag, CancellationToken ct)
    {
        if (state.Objects.TryGetValue(key + "-intent", out var previous))
        {
            var saved = RequirePrivateProof(key + "-intent", "PrivateStopIntent");
            Guard.Against(Text(saved, "uri") != uri.AbsoluteUri ||
                !JsonNode.DeepEquals(saved["original"], original),
                "c1_private_cleanup_intent", "The original stop action cannot target another resource or job.", 409);
            if (state.PendingDeletes.TryGetValue(key, out var pending))
            {
                Guard.Against(pending != previous.RequestHash, "c1_private_cleanup_intent", "The original stop intent changed.", 409);
                return;
            }
            Guard.Against(!state.DeniedRequests.TryGetValue(key, out var denied) ||
                denied.RequestHash != previous.RequestHash || denied.Status is not (401 or 403),
                "c1_private_cleanup_unknown", "A previously issued stop is read-only reconciled, never replayed.", 409);
        }
        else SavePrivateActionIntent(key, "PrivateStopIntent", new()
        {
            ["method"] = "POST", ["uri"] = uri.AbsoluteUri, ["original"] = original.DeepClone(),
            ["before"] = current.DeepClone(), ["ifMatch"] = etag, ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
        });
        var intent = state.Objects[key + "-intent"];
        if (!state.PendingDeletes.ContainsKey(key))
            SaveCheckpointJournal(state with
            { PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [key] = intent.RequestHash } });
        var reply = await Send(HttpMethod.Post, uri, ifMatch: intent.Response["ifMatch"]?.GetValue<string>(), ct: ct, cleanup: true);
        if (reply.Status is 401 or 403)
        {
            var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            pending.Remove(key);
            SaveCheckpointJournal(state with
            {
                PendingDeletes = pending,
                DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal) { [key] = new(intent.RequestHash, reply.Status) }
            });
        }
        if (reply.RetryAfterSeconds is { } retry) SetPrivatePoll(key, retry);
        RequireStatus(reply, 200, 202, 204);
        if (reply.RetryAfterSeconds is null) SetPrivatePoll(key, 5);
    }

    private void RecordPrivateStopped(string key, JsonObject observation)
    {
        var receiptKey = key + "-observed";
        if (!state.Objects.ContainsKey(receiptKey))
        {
            observation["observedAtUtc"] = SourceUtcText(time.GetUtcNow());
            SavePrivateProof(receiptKey, "PrivateStoppedObservation", observation);
        }
        else RequirePrivateProof(receiptKey, "PrivateStoppedObservation");
        if (state.PendingDeletes.ContainsKey(key))
        {
            var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            pending.Remove(key);
            SaveCheckpointJournal(state with { PendingDeletes = pending });
        }
    }

    private void RequireNotPrivateRestored(string key) =>
        Guard.Against(state.Objects.ContainsKey(key + "-observed"), "c1_private_cleanup_restored",
            "Previously stopped own work became active again; do not stop a replacement or restart automatically.", 409);

    private void RequirePrivatePresenceNotRestored(string key) =>
        Guard.Against(state.Objects.TryGetValue(key + "-observed", out var previous) &&
            previous.Response["status"]?.GetValue<int>() == 404, "c1_private_cleanup_restored",
            "Previously absent own compute reappeared; retain the original absence and do not adopt the restored resource.", 409);

    private bool PrivatePollDue(string key, DateTimeOffset? original = null)
    {
        var notBefore = original ?? state.CleanupStartedAtUtc!.Value;
        if (state.Objects.ContainsKey(key + "-poll"))
        {
            var saved = RequirePrivateProof(key + "-poll", "PrivateCleanupPoll");
            var next = SourceUtc(saved, "notBeforeUtc");
            if (next > notBefore) notBefore = next;
        }
        var (_, deadline) = PlanCleanupRequests(0);
        Guard.Against(notBefore < state.StartedAtUtc || notBefore >= deadline,
            "c1_deploy_cleanup_budget", "The original provider delay cannot fit the unchanged cleanup tail.", 409);
        return time.GetUtcNow() >= notBefore;
    }

    private void SetPrivatePoll(string key, int seconds)
    {
        Guard.Against(seconds < 0, "c1_private_cleanup_poll", "A provider polling minimum cannot be negative.", 409);
        var next = time.GetUtcNow().AddSeconds(seconds);
        if (state.Objects.ContainsKey(key + "-poll"))
        {
            var previous = SourceUtc(RequirePrivateProof(key + "-poll", "PrivateCleanupPoll"), "notBeforeUtc");
            if (previous > next) next = previous;
        }
        var body = new JsonObject { ["notBeforeUtc"] = SourceUtcText(next) };
        PlanCleanupRequests(0);
        SaveObject(key + "-poll", "PrivateCleanupPoll", state.BindingHash, Canonical.Hash(body), body);
        PrivatePollDue(key);
    }

    private JsonObject PrivateStopObservations() => new(state.Objects
        .Where(pair => pair.Key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) && pair.Value.Kind == "PrivateStoppedObservation")
        .Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)RequirePrivateProof(pair.Key, pair.Value.Kind).DeepClone())));

    private IEnumerable<string> PrivateUnsupportedWork()
    {
        foreach (var pair in state.PendingCreates)
            if (pair.Key != ArtifactCommandKey && !IsPrivateIdentityKey(pair.Key))
                yield return "unresolved-original-create:" + pair.Key;
        foreach (var key in state.PendingOperations.Keys) yield return "unresolved-original-operation:" + key;
        foreach (var key in state.PendingDeletes.Keys)
            if (!key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) && !IsPrivateIdentityKey(key) &&
                key != ArtifactCommandKey && !ArtifactAccessKeys.Contains(key, StringComparer.Ordinal) &&
                !CheckpointAccessKeys.Contains(key, StringComparer.Ordinal) && key is not ("workspace-released" or "workspace-denied"))
                yield return "unresolved-original-delete:" + key;
        foreach (var pair in state.Objects)
        {
            if (pair.Key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) || pair.Key == LifecycleStopKey) continue;
            if (!PrivateKnownHistory(pair.Key, pair.Value.Kind)) yield return "unsupported-runtime-or-history:" + pair.Key;
            if (pair.Value.Kind == "ArmDeploymentObservation" && !PrivateCompletedArmObservation(pair.Key, pair.Value))
                yield return "unresolved-original-arm-observation:" + pair.Key;
            if (pair.Value.Kind is "CheckpointIoReservation" or "CheckpointIoAllocation" ||
                pair.Key is "hosted-session" or "hosted-version" or "runtime-credential")
                yield return "independent-runtime-stop-not-implemented:" + pair.Key;
        }
        if ((state.PendingCreates.ContainsKey(ArtifactCommandKey) || state.Objects.ContainsKey(ArtifactCommandKey)) &&
            !state.Objects.ContainsKey("arm-foundation"))
            yield return "unknown-run-command-without-original-vm";
    }

    private static bool IsPrivateIdentityKey(string key) =>
        IdentityRoles.Any(role => key == role + "-app" || key == role + "-sp" || key == "fabric-api-member-" + role) ||
        key is "fabric-api-group" or "fabric-api-members";

    private bool PrivateCompletedArmObservation(string key, FabricC1ProvisionedObject observation) =>
        key.EndsWith(PrivateArmObservationSuffix, StringComparison.Ordinal) &&
        state.Objects.TryGetValue(key[..^PrivateArmObservationSuffix.Length], out var completed) &&
        completed.Kind == "ArmDeployment" && completed.Id == observation.Id && completed.RequestHash == observation.RequestHash &&
        completed.Response["properties"]?["provisioningState"]?.GetValue<string>() == "Succeeded";

    private static bool PrivateKnownHistory(string key, string kind)
    {
        if (IsPrivateIdentityKey(key))
            return kind is "Application" or "ServicePrincipal" or "SecurityGroup" or "VerifiedGroupMembers" or "GroupMembership";
        if (key == HubReceiptKey || key.StartsWith(HubReceiptKey + "-", StringComparison.Ordinal)) return kind == "SharedHubConsumerReceipt";
        if (key.StartsWith("tenant-", StringComparison.Ordinal))
            return kind is "TenantSettingsReview" or "ApprovedTenantPlan" or "ScopedTenantSetting" or "SharedTenantEnableIntent" or "SharedTenantSettingEffect";
        return kind switch
        {
            "ArmDeployment" => key is "arm-hub-spoke" or "arm-hub-peer" or "arm-hub-transit" or "arm-foundation" or "arm-private-endpoints" or "arm-hub-operator-access",
            "ArmDeploymentObservation" => key.EndsWith(PrivateArmObservationSuffix, StringComparison.Ordinal) &&
                PrivateKnownHistory(key[..^PrivateArmObservationSuffix.Length], "ArmDeployment"),
            "ArmGeneratedChild" => key.StartsWith("arm-child-", StringComparison.Ordinal),
            "ResourceGroup" => key == "resource-group",
            "FabricCapacity" => key == "fabric-capacity",
            "Workspace" or "CommunicationPolicy" or "CommunicationPolicyIntent" or "Lakehouse" or "Notebook" or
                "SourceNotebookPlan" or "SourceNotebookDefinition" or "SourceNotebookJobIntent" or "SourceNotebookJob" or
                "SourceMaterializationReadback" or "SourceMaterialization" or "SourceAccessDefinition" or "SourceAccessMetadata" or
                "OneLakeSourceRole" or "WorkspaceGrant" or "ArtifactStorageIntent" or "ArtifactStorageRole" or "ArtifactStorageGrant" or
                "ArtifactAccessTarget" or "CheckpointStorageIntent" or "CheckpointStorageRole" or "CheckpointStorageGrant" or
                "PrivateArtifactPlan" or "PrivateArtifactReadback" or "PrivateArtifactStagePlan" or "ArtifactStageCommandIntent" or
                "ArtifactStageRunCommand" or "PrivateArtifactStageReadback" or "SharedHubBindingsCandidate" or
                "SharedHubBindingsExport" or "SharedHubBindingsEvidence" or "SharedHubDnsEvidence" => true,
            _ => false
        };
    }

    private bool PrivateNonExecutingResource(JsonObject resource)
    {
        var type = Text(resource, "type");
        var id = Text(resource, "id");
        if (type == "Microsoft.Resources/deployments")
            return state.Objects.Values.Any(item => item.Kind == "ArmDeployment" && SameArmId(item.Id, id));
        if (type is "Microsoft.Authorization/roleAssignments" or "Microsoft.Authorization/roleDefinitions")
            return state.Objects.Values.Any(item => (item.Kind is "ArtifactStorageGrant" or "ArtifactStorageRole" or
                "CheckpointStorageGrant" or "CheckpointStorageRole") && SameArmId(item.Id, id));
        if (type == "Microsoft.Compute/virtualMachines")
            return state.Objects.ContainsKey(PrivateProbeStopKey + "-observed") &&
                id == PrivateFoundationOutput("probe", type)?["id"]?.GetValue<string>();
        if (type == "Microsoft.Fabric/capacities")
            return state.Objects.ContainsKey(PrivateCapacityStopKey + "-observed") &&
                id == PrivateFoundationOutput("capacity", type)?["id"]?.GetValue<string>();
        if (type is "Microsoft.Network/virtualNetworks" or "Microsoft.Network/virtualNetworks/virtualNetworkPeerings" or
                "Microsoft.Network/networkInterfaces" or "Microsoft.Network/networkSecurityGroups" or
                "Microsoft.Network/routeTables" or "Microsoft.Network/privateEndpoints" ||
            type is "Microsoft.Compute/disks" or "Microsoft.Resources/deployments" or "Microsoft.KeyVault/vaults" or
                "Microsoft.Storage/storageAccounts" or "Microsoft.Storage/storageAccounts/blobServices/containers" or
                "Microsoft.ContainerRegistry/registries" or "Microsoft.CognitiveServices/accounts" or
                "Microsoft.CognitiveServices/accounts/projects" or "Microsoft.Fabric/privateLinkServicesForFabric" or
                "Microsoft.Authorization/roleAssignments" or "Microsoft.Authorization/roleDefinitions")
        {
            if ((type is "Microsoft.Network/networkInterfaces" or "Microsoft.Compute/disks") &&
                state.Objects.Values.Any(item => item.Kind == "ArmGeneratedChild" && SameArmId(item.Id, id))) return true;
            if (type == "Microsoft.Network/virtualNetworks/virtualNetworkPeerings") return id == PrivateSpokePeerId();
            return resource["tags"]?["manifestSha256"]?.GetValue<string>() == state.BindingHash &&
                resource["tags"]?["deploymentId"]?.GetValue<string>() == input.DeploymentId;
        }
        return false;
    }

    private async Task<JsonObject[]> ReadPrivateGroupInventory(CancellationToken ct)
    {
        var root = new Uri("https://management.azure.com" + FabricC1Deployment.GroupId(input) + "/resources?api-version=2021-04-01");
        var uri = root;
        var resources = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < 8; page++)
        {
            var reply = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
            if (page == 0 && reply.Status == 404) return [];
            RequireStatus(reply, 200);
            foreach (var value in HubArray(reply.Body, "value"))
            {
                var resource = value as JsonObject ?? throw new DomainException("c1_private_cleanup_inventory", "Incomplete ARM inventory.", 409);
                var id = Text(resource, "id");
                Guard.Against(!id.StartsWith(FabricC1Deployment.GroupId(input) + "/providers/", StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(id) || resources.Count >= 512,
                    "c1_private_cleanup_inventory", "The complete own-group inventory left its scope or became ambiguous.", 409);
                resources.Add((JsonObject)resource.DeepClone());
            }
            if (reply.Body["nextLink"] is null) return resources.ToArray();
            var next = new Uri(Text(reply.Body, "nextLink"));
            Guard.Against(next.Scheme != root.Scheme || next.Authority != root.Authority ||
                next.AbsolutePath != root.AbsolutePath || next.UserInfo.Length != 0 || next.Fragment.Length != 0,
                "c1_private_cleanup_inventory", "Inventory continuation left the exact own collection.", 409);
            uri = next;
        }
        throw new DomainException("c1_private_cleanup_inventory", "The bounded own inventory is incomplete.", 409);
    }

    private async Task<string[]> CleanupPrivateAccessInventory(CancellationToken ct)
    {
        var blockers = new List<string>();
        var artifact = await CleanupPrivateArtifactAccess(ct);
        blockers.AddRange(artifact.UnresolvedKeys.Select(key => "unresolved-artifact-access:" + key));
        var checkpoint = await CleanupBlobCheckpointAccess(ct);
        blockers.AddRange(checkpoint.UnresolvedKeys.Select(key => "unresolved-checkpoint-access:" + key));
        foreach (var role in new[] { "released", "denied" })
            if (state.Objects.ContainsKey("source-materialization-" + role) &&
                state.Objects.ContainsKey("runtime-sp"))
            {
                var result = await CleanupMaterializedSourceWorkspace(role, ct);
                if (result.Status != "SourceWorkspaceInventoryAbsent") blockers.Add("unresolved-source-workspace:" + role);
            }
        foreach (var pair in state.Objects)
            if (pair.Value.Kind is "WorkspaceGrant" or "OneLakeSourceRole" or "SourceAccessDefinition" or "SourceAccessMetadata" or
                "ArtifactStorageGrant" or "CheckpointStorageGrant" ||
                pair.Key is "runtime-credential" or "source-grants" || pair.Value.Kind is "CheckpointIoAllocation" or "CheckpointIoReservation")
                blockers.Add("effective-revocation-not-proven:" + pair.Key);
        foreach (var key in state.PendingCreates.Keys.Where(key => !IsPrivateIdentityKey(key)))
            blockers.Add("unresolved-original-create:" + key);
        if (blockers.Count != 0) return blockers.ToArray();
        if (state.Objects.Any(pair => IsPrivateIdentityKey(pair.Key)) || state.PendingCreates.Keys.Any(IsPrivateIdentityKey))
        {
            if (!PrivateUnusedIdentityDependenciesAbsent())
                return ["credentialed-or-dependent-identities-require-qualified-revocation"];
            var identities = await CleanupIdentityPhase(ct);
            blockers.AddRange(identities.UnresolvedIdentityKeys.Select(key => "unresolved-identity:" + key));
        }
        return blockers.ToArray();
    }

    private bool PrivateUnusedIdentityDependenciesAbsent()
    {
        if (!state.Objects.ContainsKey(PrivateWorkKey) || state.PendingOperations.Count != 0) return false;
        RequirePrivateCleanupOriginal();
        RequirePrivateProof(PrivateWorkKey, "PrivateWorkStopped");
        foreach (var pair in state.Objects)
        {
            if (pair.Key.StartsWith(PrivateCleanupPrefix, StringComparison.Ordinal) || pair.Key == LifecycleStopKey ||
                pair.Key == HubReceiptKey || pair.Key.StartsWith(HubReceiptKey + "-", StringComparison.Ordinal) ||
                pair.Key.StartsWith("tenant-", StringComparison.Ordinal) || pair.Key == "resource-group") continue;
            if (pair.Key is "arm-hub-spoke" or "arm-hub-peer" or "arm-hub-transit") continue;
            if (!IsPrivateIdentityKey(pair.Key)) return false;
            if (pair.Value.Kind == "Application")
            {
                var role = pair.Key[..^4];
                if (pair.Value.RequestHash != Canonical.Hash(ApplicationBody(role)) ||
                    pair.Value.Response["keyCredentials"] is not (null or JsonArray { Count: 0 }) ||
                    pair.Value.Response["passwordCredentials"] is not (null or JsonArray { Count: 0 })) return false;
            }
        }
        return state.PendingCreates.Keys.All(IsPrivateIdentityKey);
    }

    private HashSet<string> PrivateIdentityCleanupScopeKeys()
    {
        if (HasIdentityOnlyPrivateFence())
        {
            Guard.Against(state.PendingDeletes.Keys.Any(key => !IsPrivateIdentityKey(key)),
                "c1_deploy_cleanup_dependents", "An unknown original deletion is not an identity-only cleanup.", 409);
            var keys = new HashSet<string>(StringComparer.Ordinal) { LifecycleStopKey };
            foreach (var role in IdentityRoles)
                foreach (var kind in new[] { "-app", "-sp" })
                {
                    var key = PrivateCleanupPrefix + "unused-credentials-" + role + kind;
                    if (!state.Objects.ContainsKey(key)) continue;
                    RequirePrivateProof(key, "PrivateUnusedIdentityCredentials");
                    keys.Add(key);
                }
            return keys;
        }
        if (!state.Objects.ContainsKey(PrivateOriginalKey)) return new(StringComparer.Ordinal);
        if (PrivateUnusedIdentityDependenciesAbsent())
            return state.Objects.Keys.Where(key => !IsPrivateIdentityKey(key)).ToHashSet(StringComparer.Ordinal);
        if (!state.Objects.ContainsKey(PrivateOwnerKey) || !state.Objects.ContainsKey(PrivateInventoryKey)) return new(StringComparer.Ordinal);
        RequirePrivateCleanupOriginal();
        RequirePrivateProof(PrivateWorkKey, "PrivateWorkStopped");
        RequirePrivateProof(PrivateAccessKey, "PrivateCredentialsRevoked");
        RequirePrivateProof(PrivateInventoryKey, "PrivateInventoryAbsent");
        RequirePrivateProof(PrivateOwnerKey, "PrivateOwnerAcknowledgement");
        return state.Objects.Keys.Where(key => !IsPrivateIdentityKey(key)).ToHashSet(StringComparer.Ordinal);
    }

    private bool HasIdentityOnlyPrivateFence()
    {
        if (input.Network?.SharedHub is null || state.CleanupStartedAtUtc is null ||
            state.Objects.ContainsKey(HubReceiptKey) || state.Objects.ContainsKey(PrivateOriginalKey) ||
            !state.Objects.ContainsKey(LifecycleStopKey)) return false;
        RequirePrivateProof(LifecycleStopKey, "PrivateLifecycleStop");
        return true;
    }

    private async Task RequirePrivateUnusedIdentityCredentials(IdentityCleanupTarget target, JsonObject current, CancellationToken ct)
    {
        if (target.Kind is not ("Application" or "ServicePrincipal")) return;
        var uri = new Uri(target.Collection + "/" + GuidValue(Text(current, "id")).ToString("D") +
            "?$select=id,appId,keyCredentials,passwordCredentials");
        var credentials = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(credentials, 200);
        SameId(Text(credentials.Body, "id"), Text(current, "id"));
        SameId(Text(credentials.Body, "appId"), Text(current, "appId"));
        Guard.Against(credentials.Body["keyCredentials"] is not JsonArray { Count: 0 } ||
            credentials.Body["passwordCredentials"] is not JsonArray { Count: 0 },
            "c1_private_cleanup_credentials", "Deleting inventory cannot prove revocation of a previously usable credential.", 409);
        JsonObject[] federated = target.Kind == "Application"
            ? await ReadAll(target.Collection + "/" + Text(current, "id") + "/federatedIdentityCredentials", true, ct, cleanup: true)
            : [];
        Guard.Against(federated.Length != 0, "c1_private_cleanup_credentials",
            "An unexpected federated credential requires a qualified revocation path, not an unused-identity proof.", 409);
        var key = PrivateCleanupPrefix + "unused-credentials-" + target.Key;
        if (!state.Objects.ContainsKey(key))
            SavePrivateProof(key, "PrivateUnusedIdentityCredentials", new()
            {
                ["identity"] = current.DeepClone(), ["credentials"] = credentials.Body.DeepClone(),
                ["federatedIdentityCredentials"] = new JsonArray(),
                ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
            });
        else RequirePrivateProof(key, "PrivateUnusedIdentityCredentials");
    }

    private static string PrivateETag(FabricC1DeploymentReply reply)
    {
        var etag = reply.ETag ?? reply.Body["etag"]?.GetValue<string>();
        Guard.Against(string.IsNullOrWhiteSpace(etag) || etag.Length > 256 || etag.Contains('*') ||
            etag.Contains('\r') || etag.Contains('\n') ||
            reply.ETag is not null && reply.Body["etag"] is not null && reply.ETag != reply.Body["etag"]!.GetValue<string>(),
            "c1_private_cleanup_etag", "An exact consistent ETag is required; no unconditional destructive request.", 409);
        return PrivateQuotedETag(etag!);
    }

    private static string PrivateQuotedETag(string etag) =>
        etag.StartsWith("W/\"", StringComparison.Ordinal) && etag.EndsWith('"') ? etag : QuoteETag(etag);
}
