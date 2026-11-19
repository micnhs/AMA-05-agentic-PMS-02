using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public record FabricC1StorageAccessResource(string Key, string Id, string Kind, JsonObject Request);
public sealed record FabricC1CheckpointAccessResource(string Key, string Id, string Kind, JsonObject Request)
    : FabricC1StorageAccessResource(Key, Id, Kind, Request);

public sealed record FabricC1CheckpointAccessCleanupResult(string Status, string[] AbsentKeys,
    string[] UnresolvedKeys, string[] NotManagedKeys)
{
    public bool CompleteC1Cleanup => false;
    public string Scope => "checkpoint-access-inventory-only-not-worker-stop-or-effective-revocation";
}

public static partial class FabricC1Deployment
{
    internal static string CheckpointStorageRoleId(FabricC1DeploymentInputs input) => "/subscriptions/" + input.SubscriptionId +
        "/providers/Microsoft.Authorization/roleDefinitions/" + Guid.Parse(input.DeploymentId!).ToString("D");

    internal static string ArtifactStorageRoleId(FabricC1DeploymentInputs input, string purpose)
    {
        var id = Guid.ParseExact(Canonical.Hash(new { BindingHash = Canonical.Hash(input), role = "artifact-" + purpose })[..32], "N").ToString("D");
        return "/subscriptions/" + input.SubscriptionId + "/providers/Microsoft.Authorization/roleDefinitions/" + id;
    }
}

public sealed partial class FabricC1Provisioner
{
    private const string BlobDataAction = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/";
    private static readonly string[] CheckpointAccessKeys =
        ["checkpoint-storage-runtime-grant", "checkpoint-storage-control-grant", "checkpoint-storage-role"];
    private string CheckpointRoleId => FabricC1Deployment.CheckpointStorageRoleId(input);

    public FabricC1CheckpointAccessResource[] PlanBlobCheckpointAccess(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding direct, FabricC1BlobCheckpointBinding hosted, FabricC1BlobCheckpointBinding control)
    {
        PlanNormalRequests(0);
        var directEndpoint = ValidateCheckpointDeployment(execution, direct);
        var hostedEndpoint = ValidateCheckpointDeployment(execution, hosted);
        var controlEndpoint = ValidateCheckpointDeployment(execution, control);
        RequireCheckpointContainer(direct.StorageAccountResourceId);
        Guard.Against(direct.Runtime != "direct" || hosted.Runtime != "hosted" || control.Runtime != "hosted-control" ||
            !SameArmId(direct.StorageAccountResourceId, hosted.StorageAccountResourceId) ||
            !SameArmId(direct.StorageAccountResourceId, control.StorageAccountResourceId) ||
            Canonical.Hash(direct.Identity) != Canonical.Hash(hosted.Identity) ||
            GuidValue(direct.Identity.PrincipalObjectId!) == GuidValue(control.Identity.PrincipalObjectId!) ||
            GuidValue(direct.Identity.ClientId!) == GuidValue(control.Identity.ClientId!),
            "c1_checkpoint_access_binding", "Separate own runtime/control callers and one original checkpoint account are required.", 409);

        var group = FabricC1Deployment.GroupId(input);
        var roleId = CheckpointRoleId;
        var container = direct.StorageAccountResourceId + "/blobServices/default/containers/c1-checkpoints";
        var definition = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleName"] = "cohors-fabric-c1-checkpoint-" + GuidValue(input.DeploymentId!).ToString("D"),
                ["type"] = "CustomRole", ["description"] = Marker, ["assignableScopes"] = new JsonArray(group),
                ["permissions"] = new JsonArray(new JsonObject
                {
                    ["actions"] = new JsonArray(), ["notActions"] = new JsonArray(),
                    ["dataActions"] = new JsonArray(BlobDataAction + "read", BlobDataAction + "write"),
                    ["notDataActions"] = new JsonArray()
                })
            }
        };
        FabricC1CheckpointAccessResource Grant(string role, string principal, params Uri[] endpoints)
        {
            var paths = endpoints.Select(endpoint => endpoint.AbsolutePath["/c1-checkpoints/".Length..]).ToArray();
            var condition = BlobPathCondition(["read", "write"], paths);
            var name = Guid.ParseExact(Canonical.Hash(new { state.BindingHash, role, principal, container })[..32], "N").ToString("D");
            return new("checkpoint-storage-" + role + "-grant",
                container + "/providers/Microsoft.Authorization/roleAssignments/" + name, "CheckpointStorageGrant", new()
                {
                    ["properties"] = new JsonObject
                    {
                        ["roleDefinitionId"] = roleId, ["principalId"] = principal, ["principalType"] = "ServicePrincipal",
                        ["description"] = Marker, ["conditionVersion"] = "2.0", ["condition"] = condition
                    }
                });
        }
        return
        [
            new("checkpoint-storage-role", roleId, "CheckpointStorageRole", definition),
            Grant("runtime", direct.Identity.PrincipalObjectId!, directEndpoint, hostedEndpoint),
            Grant("control", control.Identity.PrincipalObjectId!, controlEndpoint)
        ];
    }

    public async Task<FabricC1ProvisionedObject[]> EnsureBlobCheckpointAccess(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding direct, FabricC1BlobCheckpointBinding hosted, FabricC1BlobCheckpointBinding control,
        CancellationToken ct)
    {
        var resources = PlanBlobCheckpointAccess(execution, direct, hosted, control);
        RecordStorageAccessIntents(resources);
        foreach (var resource in resources)
            await EnsureStorageAccessResource(resource, ct);
        return resources.Select(resource => state.Objects[resource.Key]).ToArray();
    }

    private static string BlobPathCondition(string[] actions, params string[] paths) =>
        "((" + string.Join(" OR ", actions.Select(action => "ActionMatches{'" + BlobDataAction + action + "'}")) +
        ") AND NOT SubOperationMatches{'Blob.List'} AND (" +
        string.Join(" OR ", paths.Select(path =>
            "@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringEquals '" + path + "'")) + "))";

    private static string StorageAccessFamily(FabricC1StorageAccessResource resource) => resource.Kind switch
    {
        "CheckpointStorageRole" or "CheckpointStorageGrant" => "Checkpoint",
        "ArtifactStorageRole" or "ArtifactStorageGrant" => "Artifact",
        _ => throw new DomainException("c1_storage_access_kind", "Unknown own storage access resource kind.", 409)
    };

    private void RecordStorageAccessIntents(FabricC1StorageAccessResource[] resources)
    {
        var objects = new Dictionary<string, FabricC1ProvisionedObject>(state.Objects, StringComparer.Ordinal);
        var changed = false;
        foreach (var resource in resources)
        {
            var family = StorageAccessFamily(resource);
            var key = resource.Key + "-intent";
            var hash = Canonical.Hash(resource.Request);
            var response = JsonSerializer.SerializeToNode(resource, Canonical.Json)!.AsObject();
            if (objects.TryGetValue(key, out var original))
            {
                Guard.Against(original.Kind != family + "StorageIntent" || original.Id != resource.Id ||
                    original.RequestHash != hash || Canonical.Hash(original.Response) != Canonical.Hash(response),
                    "c1_deploy_drift", "The complete original storage role/grant request cannot change.", 409);
            }
            else
            {
                Guard.Against(state.Objects.ContainsKey(resource.Key) || state.PendingCreates.ContainsKey(resource.Key) ||
                    state.DeniedRequests.ContainsKey(resource.Key), "c1_" + family.ToLowerInvariant() + "_access_intent",
                    "A missing original role/grant intent cannot be reconstructed after I/O.", 409);
                objects[key] = new(family + "StorageIntent", resource.Id, hash, response);
                changed = true;
            }
        }
        if (changed) SaveCheckpointJournal(state with { Objects = objects });
    }

    private async Task EnsureStorageAccessResource(FabricC1StorageAccessResource resource, CancellationToken ct)
    {
        var hash = Canonical.Hash(resource.Request);
        state.Objects.TryGetValue(resource.Key, out var known);
        state.PendingCreates.TryGetValue(resource.Key, out var pending);
        Guard.Against(known is not null && (known.Kind != resource.Kind ||
                !SameArmId(known.Id, resource.Id) || known.RequestHash != hash || pending is not null) ||
            pending is not null && pending != hash ||
            state.DeniedRequests.TryGetValue(resource.Key, out var deniedRequest) &&
                (deniedRequest.RequestHash != hash || deniedRequest.Status is not (401 or 403)),
            "c1_deploy_drift", "Original storage access intent changed.", 409);
        if (known is not null) VerifyStorageAccess(resource, known.Response);
        var observed = known?.Response;
        var uri = new Uri("https://management.azure.com" + resource.Id + "?api-version=2022-04-01");
        var current = await Send(HttpMethod.Get, uri, ct: ct);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            Guard.Against(known is not null || pending is not null, "c1_deploy_unknown",
                "A previous role/grant is absent or uncertain; no replacement PUT.", 409);
            SaveCheckpointJournal(state with
            {
                PendingCreates = new(state.PendingCreates, StringComparer.Ordinal) { [resource.Key] = hash }
            });
            var created = await Send(HttpMethod.Put, uri, resource.Request, ct: ct);
            if (created.Status is 401 or 403)
            {
                var denied = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
                denied.Remove(resource.Key);
                SaveCheckpointJournal(state with
                {
                    PendingCreates = denied,
                    DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                    { [resource.Key] = new(hash, created.Status) }
                });
            }
            RequireStatus(created, 201);
            VerifyStorageAccess(resource, created.Body);
            observed = created.Body;
            current = await Send(HttpMethod.Get, uri, ct: ct);
        }
        else Guard.Against(known is null && pending is null, "c1_deploy_foreign",
            "Existing unjournaled storage roles/grants are not adopted or edited.", 409);
        RequireStatus(current, 200);
        VerifyStorageAccess(resource, current.Body);
        if (observed is not null)
            foreach (var field in new[] { "createdOn", "createdBy" })
                Guard.Against(observed["properties"]?[field] is { } original &&
                    Canonical.Hash(original) != Canonical.Hash(current.Body["properties"]?[field]),
                    "c1_deploy_drift", "The observed storage access generation changed; do not adopt it.", 409);
        PlanNormalRequests(0);
        var remaining = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        remaining.Remove(resource.Key);
        SaveCheckpointJournal(state with
        {
            Objects = new(state.Objects, StringComparer.Ordinal)
            {
                [resource.Key] = new(resource.Kind, resource.Id, hash, (JsonObject)current.Body.DeepClone())
            },
            PendingCreates = remaining
        });
    }

    private static void VerifyStorageAccess(FabricC1StorageAccessResource expected, JsonObject current)
    {
        static string? Value(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var family = StorageAccessFamily(expected);
        var error = "c1_" + family.ToLowerInvariant() + "_access_readback";
        var definition = expected.Kind == family + "StorageRole";
        var request = expected.Request["properties"]!.AsObject();
        var actual = current["properties"] as JsonObject ??
            throw new DomainException(error, "Provider role/grant properties are missing.", 409);
        Guard.Against(!SameArmId(Value(current["id"]), expected.Id) ||
            !string.Equals(Value(current["type"]), "Microsoft.Authorization/" + (definition ? "roleDefinitions" : "roleAssignments"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Value(current["name"]), expected.Id.Split('/')[^1], StringComparison.OrdinalIgnoreCase) ||
            Value(actual["description"]) != Value(request["description"]),
            error, "Storage access ownership or resource identity differs.", 409);
        if (definition)
        {
            var permitted = family == "Checkpoint" || expected.Key == "artifact-storage-publisher-role"
                ? new[] { BlobDataAction + "read", BlobDataAction + "write" } : [BlobDataAction + "read"];
            Guard.Against(Value(actual["roleName"]) != Value(request["roleName"]) || Value(actual["type"]) != "CustomRole" ||
                actual["assignableScopes"] is not JsonArray { Count: 1 } scopes ||
                !SameArmId(Value(scopes[0]), Value(request["assignableScopes"]![0])) ||
                actual["permissions"] is not JsonArray { Count: 1 } permissions || permissions[0] is not JsonObject permission ||
                permission["actions"] is not JsonArray { Count: 0 } ||
                permission["notActions"] is not JsonArray { Count: 0 } ||
                permission["notDataActions"] is not JsonArray { Count: 0 } ||
                permission["dataActions"] is not JsonArray actions || actions.Count != permitted.Length ||
                !actions.Select(Value).Order(StringComparer.Ordinal).SequenceEqual(permitted),
                error, "Storage role must contain only the intended data actions in its own group.", 409);
        }
        else
        {
            Guard.Against(!SameArmId(Value(actual["scope"]), expected.Id[..expected.Id.LastIndexOf("/providers/", StringComparison.Ordinal)]) ||
                !SameArmId(Value(actual["roleDefinitionId"]), Value(request["roleDefinitionId"])) ||
                !string.Equals(Value(actual["principalId"]), Value(request["principalId"]), StringComparison.OrdinalIgnoreCase) ||
                Value(request["principalType"]) is not ("User" or "ServicePrincipal") ||
                Value(actual["principalType"]) != Value(request["principalType"]) ||
                Value(actual["conditionVersion"]) != "2.0" || Value(actual["condition"]) != Value(request["condition"]) ||
                actual["delegatedManagedIdentityResourceId"] is not null ||
                actual["canDelegate"] is not null &&
                    (actual["canDelegate"] is not JsonValue delegation || !delegation.TryGetValue<bool>(out var canDelegate) || canDelegate),
                error, "Storage grant caller, exact path condition, container scope or delegation differs.", 409);
        }
    }

    public async Task<FabricC1CheckpointAccessCleanupResult> CleanupBlobCheckpointAccess(CancellationToken ct)
    {
        RequireCertainCheckpointJournal();
        var resources = ReadCheckpointAccessIntents();
        if (resources.Length == 0) return CheckpointAccessCleanupResult();
        var (fenced, _) = PlanCleanupRequests(0);
        if (state.CleanupStartedAtUtc is null) SaveCheckpointJournal(fenced);
        foreach (var resource in resources)
            if (!await DeleteStorageAccess(resource, ct)) break;
        return CheckpointAccessCleanupResult();
    }

    private FabricC1CheckpointAccessResource[] ReadCheckpointAccessIntents()
    {
        Guard.Against(state.Objects.Keys.Any(key => key.StartsWith("checkpoint-storage-", StringComparison.Ordinal) &&
                !CheckpointAccessKeys.Any(expected => key == expected || key == expected + "-intent")) ||
            state.PendingCreates.Keys.Concat(state.PendingDeletes.Keys).Concat(state.CleanupReceipts.Keys)
                .Any(key => key.StartsWith("checkpoint-storage-", StringComparison.Ordinal) && !CheckpointAccessKeys.Contains(key)),
            "c1_checkpoint_access_cleanup_journal", "Unknown checkpoint access inventory cannot be omitted from cleanup.", 409);
        if (CheckpointAccessKeys.All(key => !state.Objects.ContainsKey(key + "-intent")))
        {
            Guard.Against(CheckpointAccessKeys.Any(key => state.Objects.ContainsKey(key) ||
                state.PendingCreates.ContainsKey(key) || state.PendingDeletes.ContainsKey(key) || state.CleanupReceipts.ContainsKey(key)),
                "c1_checkpoint_access_cleanup_journal", "The original complete access intents are missing.", 409);
            return [];
        }
        var foundation = Owned("arm-foundation", "ArmDeployment");
        var storage = foundation["properties"]?["outputs"]?["storage"]?["value"] as JsonObject ??
            throw new DomainException("c1_checkpoint_access_cleanup_journal", "Original storage ownership is missing.", 409);
        RequireArmOwnership(storage);
        var storageId = RequireTypedArmResource(storage, "Microsoft.Storage/storageAccounts");
        RequireCheckpointContainer(storageId);
        var resources = new List<FabricC1CheckpointAccessResource>();
        foreach (var key in CheckpointAccessKeys)
        {
            if (!state.Objects.TryGetValue(key + "-intent", out var intent) || intent.Kind != "CheckpointStorageIntent")
                throw new DomainException("c1_checkpoint_access_cleanup_journal", "The complete original access plan is required.", 409);
            var resource = intent.Response.Deserialize<FabricC1CheckpointAccessResource>(Canonical.Json) ??
                throw new DomainException("c1_checkpoint_access_cleanup_journal", "The original access request is missing.", 409);
            var properties = resource.Request?["properties"] as JsonObject ??
                throw new DomainException("c1_checkpoint_access_cleanup_journal", "The original access properties are missing.", 409);
            var role = key == "checkpoint-storage-role";
            Guard.Against(resource.Key != key || string.IsNullOrWhiteSpace(resource.Id) ||
                resource.Kind != (role ? "CheckpointStorageRole" : "CheckpointStorageGrant") ||
                !SameArmId(intent.Id, resource.Id) || intent.RequestHash != Canonical.Hash(resource.Request) ||
                properties["description"]?.GetValue<string>() != Marker,
                "c1_checkpoint_access_cleanup_journal", "Original access identity, marker or request hash differs.", 409);
            if (role)
            {
                Guard.Against(!SameArmId(resource.Id, CheckpointRoleId) ||
                    properties["roleName"]?.GetValue<string>() != "cohors-fabric-c1-checkpoint-" + GuidValue(input.DeploymentId!).ToString("D") ||
                    properties["assignableScopes"] is not JsonArray { Count: 1 } scopes ||
                    !SameArmId(scopes[0]?.GetValue<string>(), FabricC1Deployment.GroupId(input)),
                    "c1_checkpoint_access_cleanup_journal", "Only the original own role definition is a cleanup target.", 409);
            }
            else
            {
                var name = key == "checkpoint-storage-runtime-grant" ? "runtime" : "control";
                var principal = properties["principalId"]?.GetValue<string>() ??
                    throw new DomainException("c1_checkpoint_access_cleanup_journal", "Original access principal is missing.", 409);
                var identityRole = name == "control" ? "hosted-control" : "runtime";
                var original = Owned(identityRole + "-sp", "ServicePrincipal");
                RequireMarker(original, true);
                SameId(principal, Text(original, "id"));
                Guard.Against(GuidValue(principal) == GuidValue(input.BootstrapIdentity!.PrincipalObjectId!),
                    "c1_deploy_cleanup_actor", "The initial deployer is never a checkpoint access target.", 409);
                var separator = resource.Id.LastIndexOf("/providers/", StringComparison.Ordinal);
                var container = separator < 0 ? "" : resource.Id[..separator];
                var assignment = Guid.ParseExact(Canonical.Hash(new { state.BindingHash, role = name, principal, container })[..32], "N").ToString("D");
                Guard.Against(!SameArmId(container, storageId + "/blobServices/default/containers/c1-checkpoints") ||
                    !SameArmId(resource.Id, container + "/providers/Microsoft.Authorization/roleAssignments/" + assignment) ||
                    !SameArmId(properties["roleDefinitionId"]?.GetValue<string>(), CheckpointRoleId) ||
                    properties["principalType"]?.GetValue<string>() != "ServicePrincipal",
                    "c1_checkpoint_access_cleanup_journal", "Only the original container and own runtime/control assignments can be removed.", 409);
            }
            state.Objects.TryGetValue(key, out var known);
            state.PendingCreates.TryGetValue(key, out var pending);
            Guard.Against(known is not null && (pending is not null || known.Kind != resource.Kind ||
                    !SameArmId(known.Id, resource.Id) || known.RequestHash != intent.RequestHash) ||
                pending is not null && pending != intent.RequestHash ||
                known is null && (state.PendingDeletes.ContainsKey(key) || state.CleanupReceipts.ContainsKey(key)),
                "c1_checkpoint_access_cleanup_journal", "The original create/delete inventory is inconsistent.", 409);
            if (known is not null) VerifyStorageAccess(resource, known.Response);
            resources.Add(resource);
        }
        return resources.ToArray();
    }

    private bool IsCheckpointAccessCleanupUri(Uri uri) =>
        uri.Scheme == "https" && uri.IdnHost == "management.azure.com" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        ReadCheckpointAccessIntents().Any(resource =>
            uri.AbsoluteUri == "https://management.azure.com" + resource.Id + "?api-version=2022-04-01" &&
            (state.Objects.ContainsKey(resource.Key) || state.PendingCreates.ContainsKey(resource.Key)));

    private async Task<bool> DeleteStorageAccess(FabricC1StorageAccessResource resource, CancellationToken ct)
    {
        var family = StorageAccessFamily(resource);
        state.Objects.TryGetValue(resource.Key, out var known);
        var pendingCreate = state.PendingCreates.ContainsKey(resource.Key);
        if (known is null && !pendingCreate) return true;
        var uri = new Uri("https://management.azure.com" + resource.Id + "?api-version=2022-04-01");
        var requestHash = Canonical.Hash(resource.Request);
        var hash = Canonical.Hash(new { Method = "DELETE", Uri = uri.AbsoluteUri, RequestHash = requestHash, ObjectId = resource.Id });
        state.PendingDeletes.TryGetValue(resource.Key, out var pending);
        state.CleanupReceipts.TryGetValue(resource.Key, out var receipt);
        Guard.Against(pending is not null && (pending != hash || receipt is not null) ||
            receipt is not null && (receipt.Kind != family + "AccessAbsent" || !SameArmId(receipt.Id, resource.Id) || receipt.RequestHash != hash) ||
            state.DeniedRequests.TryGetValue("cleanup-" + resource.Key, out var rejected) &&
                (rejected.RequestHash != hash || rejected.Status is not (401 or 403)),
            "c1_" + family.ToLowerInvariant() + "_access_cleanup_journal", "The original access deletion binding differs.", 409);
        var current = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            if (known is null) return false;
            RecordStorageAccessAbsence(resource, hash);
            return true;
        }
        VerifyStorageAccess(resource, current.Body);
        Guard.Against(receipt is not null, "c1_deploy_cleanup_restored",
            "Previously absent storage access reappeared; do not delete it automatically.", 409);
        if (known is not null)
            foreach (var field in new[] { "createdOn", "createdBy" })
                Guard.Against(known.Response["properties"]?[field] is { } original &&
                    Canonical.Hash(original) != Canonical.Hash(current.Body["properties"]?[field]),
                    "c1_deploy_cleanup_restored", "Storage access creation metadata changed.", 409);
        if (pending is not null) return false;
        PlanCleanupRequests(0);
        if (known is null)
        {
            var creates = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
            creates.Remove(resource.Key);
            SaveCheckpointJournal(state with
            {
                PendingCreates = creates,
                Objects = new(state.Objects, StringComparer.Ordinal)
                { [resource.Key] = new(resource.Kind, resource.Id, requestHash, (JsonObject)current.Body.DeepClone()) }
            });
        }
        SaveCheckpointJournal(state with
        { PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [resource.Key] = hash } });
        var deleted = await Send(HttpMethod.Delete, uri, ct: ct, cleanup: true);
        if (deleted.Status is 401 or 403)
        {
            var deletes = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            deletes.Remove(resource.Key);
            SaveCheckpointJournal(state with
            {
                PendingDeletes = deletes,
                DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                { ["cleanup-" + resource.Key] = new(hash, deleted.Status) }
            });
        }
        RequireStatus(deleted, 200, 204, 404);
        if (deleted.Status == 200) VerifyStorageAccess(resource, deleted.Body);
        var readback = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        RequireStatus(readback, 200, 404);
        if (readback.Status == 200)
        {
            VerifyStorageAccess(resource, readback.Body);
            return false;
        }
        RecordStorageAccessAbsence(resource, hash);
        return true;
    }

    private void RecordStorageAccessAbsence(FabricC1StorageAccessResource resource, string hash)
    {
        var family = StorageAccessFamily(resource);
        PlanCleanupRequests(0);
        var deletes = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        deletes.Remove(resource.Key);
        SaveCheckpointJournal(state with
        {
            PendingDeletes = deletes,
            CleanupReceipts = new(state.CleanupReceipts, StringComparer.Ordinal)
            {
                [resource.Key] = new(family + "AccessAbsent", resource.Id, hash, new()
                {
                    ["status"] = "ActiveArmEndpointNotFound", ["observedAtUtc"] = time.GetUtcNow().ToString("O"),
                    ["effectiveAccessRevoked"] = false, [family.ToLowerInvariant() + "DataDeleted"] = false, ["workersStopped"] = false
                })
            }
        });
    }

    private FabricC1CheckpointAccessCleanupResult CheckpointAccessCleanupResult()
    {
        var absent = CheckpointAccessKeys.Where(key => state.CleanupReceipts.ContainsKey(key)).ToArray();
        var unresolved = CheckpointAccessKeys.Where(key => state.PendingCreates.ContainsKey(key) ||
            state.PendingDeletes.ContainsKey(key) || state.Objects.ContainsKey(key) && !state.CleanupReceipts.ContainsKey(key)).ToArray();
        var notManaged = CheckpointAccessKeys.Where(key => !state.Objects.ContainsKey(key) && !state.PendingCreates.ContainsKey(key)).ToArray();
        var status = unresolved.Length != 0 ? "CheckpointAccessCleanupPendingReconciliation" :
            notManaged.Length == CheckpointAccessKeys.Length ? "CheckpointAccessNotManaged" : "CheckpointAccessInventoryAbsent";
        return new(status,
            absent, unresolved, notManaged);
    }
}
