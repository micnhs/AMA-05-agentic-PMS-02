using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1CheckpointAccessResource(string Key, string Id, string Kind, JsonObject Request);

public sealed partial class FabricC1Provisioner
{
    private const string BlobDataAction = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/";

    public FabricC1CheckpointAccessResource[] PlanBlobCheckpointAccess(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding direct, FabricC1BlobCheckpointBinding hosted, FabricC1BlobCheckpointBinding control)
    {
        PlanNormalRequests(0);
        var directEndpoint = ValidateCheckpointDeployment(execution, direct);
        var hostedEndpoint = ValidateCheckpointDeployment(execution, hosted);
        var controlEndpoint = ValidateCheckpointDeployment(execution, control);
        RequireCheckpointContainer(direct);
        Guard.Against(direct.Runtime != "direct" || hosted.Runtime != "hosted" || control.Runtime != "hosted-control" ||
            !SameArmId(direct.StorageAccountResourceId, hosted.StorageAccountResourceId) ||
            !SameArmId(direct.StorageAccountResourceId, control.StorageAccountResourceId) ||
            Canonical.Hash(direct.Identity) != Canonical.Hash(hosted.Identity) ||
            GuidValue(direct.Identity.PrincipalObjectId!) == GuidValue(control.Identity.PrincipalObjectId!) ||
            GuidValue(direct.Identity.ClientId!) == GuidValue(control.Identity.ClientId!),
            "c1_checkpoint_access_binding", "Separate own runtime/control callers and one original checkpoint account are required.", 409);

        var group = FabricC1Deployment.GroupId(input);
        var roleId = "/subscriptions/" + input.SubscriptionId +
            "/providers/Microsoft.Authorization/roleDefinitions/" + GuidValue(input.DeploymentId!).ToString("D");
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
            var condition = "((ActionMatches{'" + BlobDataAction + "read'} OR ActionMatches{'" + BlobDataAction +
                "write'}) AND NOT SubOperationMatches{'Blob.List'} AND (" +
                string.Join(" OR ", paths.Select(path =>
                    "@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringEquals '" + path + "'")) + "))";
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
        foreach (var resource in resources)
            await EnsureCheckpointAccessResource(resource, ct);
        return resources.Select(resource => state.Objects[resource.Key]).ToArray();
    }

    private async Task EnsureCheckpointAccessResource(FabricC1CheckpointAccessResource resource, CancellationToken ct)
    {
        var hash = Canonical.Hash(resource.Request);
        state.Objects.TryGetValue(resource.Key, out var known);
        state.PendingCreates.TryGetValue(resource.Key, out var pending);
        Guard.Against(known is not null && (known.Kind != resource.Kind ||
                !SameArmId(known.Id, resource.Id) || known.RequestHash != hash || pending is not null) ||
            pending is not null && pending != hash ||
            state.DeniedRequests.TryGetValue(resource.Key, out var deniedRequest) &&
                (deniedRequest.RequestHash != hash || deniedRequest.Status is not (401 or 403)),
            "c1_deploy_drift", "Original checkpoint access intent changed.", 409);
        if (known is not null) VerifyCheckpointAccess(resource, known.Response);
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
            VerifyCheckpointAccess(resource, created.Body);
            current = await Send(HttpMethod.Get, uri, ct: ct);
        }
        else Guard.Against(known is null && pending is null, "c1_deploy_foreign",
            "Existing unjournaled checkpoint roles/grants are not adopted or edited.", 409);
        RequireStatus(current, 200);
        VerifyCheckpointAccess(resource, current.Body);
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

    private static void VerifyCheckpointAccess(FabricC1CheckpointAccessResource expected, JsonObject current)
    {
        static string? Value(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var definition = expected.Kind == "CheckpointStorageRole";
        var request = expected.Request["properties"]!.AsObject();
        var actual = current["properties"] as JsonObject ??
            throw new DomainException("c1_checkpoint_access_readback", "Provider role/grant properties are missing.", 409);
        Guard.Against(!SameArmId(Value(current["id"]), expected.Id) ||
            !string.Equals(Value(current["type"]), "Microsoft.Authorization/" + (definition ? "roleDefinitions" : "roleAssignments"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Value(current["name"]), expected.Id.Split('/')[^1], StringComparison.OrdinalIgnoreCase) ||
            Value(actual["description"]) != Value(request["description"]),
            "c1_checkpoint_access_readback", "Checkpoint access ownership or resource identity differs.", 409);
        if (definition)
        {
            Guard.Against(Value(actual["roleName"]) != Value(request["roleName"]) || Value(actual["type"]) != "CustomRole" ||
                actual["assignableScopes"] is not JsonArray { Count: 1 } scopes ||
                !SameArmId(Value(scopes[0]), Value(request["assignableScopes"]![0])) ||
                actual["permissions"] is not JsonArray { Count: 1 } permissions || permissions[0] is not JsonObject permission ||
                permission["actions"] is not JsonArray { Count: 0 } ||
                permission["notActions"] is not JsonArray { Count: 0 } ||
                permission["notDataActions"] is not JsonArray { Count: 0 } ||
                permission["dataActions"] is not JsonArray { Count: 2 } actions ||
                !actions.Select(Value).Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { BlobDataAction + "read", BlobDataAction + "write" }),
                "c1_checkpoint_access_readback", "Checkpoint role must contain only read/write data actions in its own group.", 409);
        }
        else
        {
            Guard.Against(!SameArmId(Value(actual["scope"]), expected.Id[..expected.Id.LastIndexOf("/providers/", StringComparison.Ordinal)]) ||
                !SameArmId(Value(actual["roleDefinitionId"]), Value(request["roleDefinitionId"])) ||
                !string.Equals(Value(actual["principalId"]), Value(request["principalId"]), StringComparison.OrdinalIgnoreCase) ||
                Value(actual["principalType"]) != "ServicePrincipal" ||
                Value(actual["conditionVersion"]) != "2.0" || Value(actual["condition"]) != Value(request["condition"]) ||
                actual["delegatedManagedIdentityResourceId"] is not null ||
                actual["canDelegate"] is not null &&
                    (actual["canDelegate"] is not JsonValue delegation || !delegation.TryGetValue<bool>(out var canDelegate) || canDelegate),
                "c1_checkpoint_access_readback", "Checkpoint grant caller, exact path condition, container scope or delegation differs.", 409);
        }
    }
}
