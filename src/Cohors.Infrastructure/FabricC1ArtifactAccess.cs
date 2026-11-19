using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1ArtifactAccessCleanupResult(string Status, string[] AbsentKeys,
    string[] UnresolvedKeys, string[] NotManagedKeys)
{
    public bool CompleteC1Cleanup => false;
    public string Scope => "artifact-access-inventory-only-not-worker-stop-or-effective-revocation";
}

public sealed partial class FabricC1Provisioner
{
    private const string ArtifactAccessTargetKey = "artifact-storage-target";
    private static readonly string[] ArtifactAccessKeys =
    [
        "artifact-storage-publisher-grant", "artifact-storage-receiver-grant",
        "artifact-storage-publisher-role", "artifact-storage-receiver-role"
    ];

    public FabricC1StorageAccessResource[] PlanPrivateArtifactAccess(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory) =>
        ReadArtifactAccess(binding, packageDirectory).Resources;

    public async Task<FabricC1ProvisionedObject[]> EnsurePrivateArtifactAccess(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory, CancellationToken ct)
    {
        var (package, target, resources) = ReadArtifactAccess(binding, packageDirectory);
        var (next, deadline) = PlanNormalRequests(0);
        var hash = Canonical.Hash(target);
        if (state.Objects.TryGetValue(ArtifactAccessTargetKey, out var original))
            Guard.Against(original.Kind != "ArtifactAccessTarget" || original.Id != package.Endpoint.AbsoluteUri ||
                original.RequestHash != hash || Canonical.Hash(original.Response) != hash,
                "c1_artifact_access_binding", "The original artifact/VM/access binding cannot change.", 409);
        else
        {
            Guard.Against(ArtifactAccessKeys.Any(key => state.Objects.ContainsKey(key) ||
                state.Objects.ContainsKey(key + "-intent") || state.PendingCreates.ContainsKey(key) ||
                state.PendingDeletes.ContainsKey(key) || state.CleanupReceipts.ContainsKey(key) ||
                state.DeniedRequests.ContainsKey(key)), "c1_artifact_access_binding",
                "An access target cannot be reconstructed after role/grant I/O.", 409);
            SaveCheckpointJournal(next with
            {
                Objects = new(next.Objects, StringComparer.Ordinal)
                { [ArtifactAccessTargetKey] = new("ArtifactAccessTarget", package.Endpoint.AbsoluteUri, hash, target) }
            });
        }
        RecordStorageAccessIntents(resources);
        var remaining = deadline - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero, "c1_deploy_budget", "The original control deadline elapsed.", 409);
        using var timeout = new CancellationTokenSource(remaining, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var archive = new FileStream(package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Guard.Against(archive.Length != package.ArchiveLength ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, bounded.Token)) != package.ArchiveSha256,
            "c1_artifact_digest", "No access grants for an archive that differs from its verified provenance.", 409);
        await VerifyArtifactTarget(binding.StorageAccountResourceId, deadline, bounded.Token);
        var probe = await Read("https://management.azure.com" + Text(target, "vmResourceId") +
            "?api-version=2024-07-01", bounded.Token);
        VerifyArtifactAccessProbe(probe, target);
        foreach (var resource in resources) await EnsureStorageAccessResource(resource, bounded.Token);
        return resources.Select(resource => state.Objects[resource.Key]).ToArray();
    }

    private (ArtifactPackage Package, JsonObject Target, FabricC1StorageAccessResource[] Resources) ReadArtifactAccess(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory)
    {
        var package = ReadArtifactPackage(binding, packageDirectory);
        var target = ArtifactAccessTarget(package.ArchiveSha256, binding.DownloadProvenanceSha256, Canonical.Hash(binding));
        Guard.Against(!SameArmId(Text(target, "storageAccountResourceId"), binding.StorageAccountResourceId) ||
            Text(target, "endpoint") != package.Endpoint.AbsoluteUri,
            "c1_artifact_access_binding", "The access plan must bind the original publication target.", 409);
        return (package, target, ArtifactAccessResources(target));
    }

    private JsonObject ArtifactAccessTarget(string archiveHash, string provenanceHash, string publicationBindingHash)
    {
        Guard.Against(!new[] { archiveHash, provenanceHash, publicationBindingHash }.All(value => FabricC1Execution.Hex(value, 64)),
            "c1_artifact_access_binding", "Original archive, provenance and publication hashes are required.", 409);
        var foundation = Owned("arm-foundation", "ArmDeployment");
        Guard.Against(!SameArmId(foundation["id"]?.GetValue<string>(), FabricC1Deployment.GroupId(input) +
            "/providers/Microsoft.Resources/deployments/c1-foundation") ||
            foundation["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_artifact_access_binding", "Only the completed own foundation supplies the artifact receiver.", 409);
        var storage = foundation["properties"]?["outputs"]?["storage"]?["value"] as JsonObject ??
            throw new DomainException("c1_artifact_access_binding", "Original storage output is missing.", 409);
        RequireArmOwnership(storage);
        var storageId = RequireTypedArmResource(storage, "Microsoft.Storage/storageAccounts");
        var container = foundation["properties"]?["outputs"]?["artifactContainer"]?["value"] as JsonObject ??
            throw new DomainException("c1_artifact_access_binding", "Original artifact container output is missing.", 409);
        VerifyArtifactContainer(container, storageId);
        var probe = foundation["properties"]?["outputs"]?["probe"]?["value"] as JsonObject ??
            throw new DomainException("c1_artifact_access_binding", "Original probe VM output is missing.", 409);
        var identity = probe["identity"] as JsonObject ??
            throw new DomainException("c1_artifact_access_binding", "The probe's original system identity is missing.", 409);
        var target = new JsonObject
        {
            ["deploymentBindingHash"] = state.BindingHash, ["sourceCommit"] = input.SourceCommit,
            ["outerArchiveSha256"] = archiveHash, ["downloadProvenanceSha256"] = provenanceHash,
            ["publicationBindingSha256"] = publicationBindingHash, ["storageAccountResourceId"] = storageId,
            ["endpoint"] = $"https://{storageId.Split('/')[^1]}.blob.core.windows.net/c1-artifacts/{state.BindingHash}/{input.SourceCommit}/{archiveHash}.zip",
            ["vmResourceId"] = FabricC1Deployment.GroupId(input) +
                "/providers/Microsoft.Compute/virtualMachines/" + FabricC1Deployment.Name(input) + "-probe",
            ["vmInstanceId"] = GuidValue(probe["properties"]?["vmId"]?.GetValue<string>() ?? "").ToString("D"),
            ["vmPrincipalId"] = GuidValue(Text(identity, "principalId")).ToString("D")
        };
        VerifyArtifactAccessProbe(probe, target);
        return target;
    }

    private void VerifyArtifactAccessProbe(JsonObject probe, JsonObject target)
    {
        RequireArmOwnership(probe);
        Guard.Against(!SameArmId(RequireTypedArmResource(probe, "Microsoft.Compute/virtualMachines"), Text(target, "vmResourceId")) ||
            probe["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded" ||
            !SameArmId(probe["properties"]?["vmId"]?.GetValue<string>(), Text(target, "vmInstanceId")) ||
            probe["identity"]?["type"]?.GetValue<string>() != "SystemAssigned" ||
            probe["identity"]?["userAssignedIdentities"] is not (null or JsonObject { Count: 0 }) ||
            !SameArmId(probe["identity"]?["tenantId"]?.GetValue<string>(), input.TenantId) ||
            !SameArmId(probe["identity"]?["principalId"]?.GetValue<string>(), Text(target, "vmPrincipalId")) ||
            SameArmId(Text(target, "vmPrincipalId"), input.BootstrapIdentity!.PrincipalObjectId) ||
            new[] { "runtime-sp", "hosted-control-sp" }.Any(key =>
                state.Objects.TryGetValue(key, out var principal) && SameArmId(principal.Id, Text(target, "vmPrincipalId"))),
            "c1_artifact_access_receiver", "Only the original own VM generation and its distinct system identity can receive artifacts.", 409);
    }

    private FabricC1StorageAccessResource[] ArtifactAccessResources(JsonObject target)
    {
        var container = Text(target, "storageAccountResourceId") + "/blobServices/default/containers/c1-artifacts";
        var path = state.BindingHash + "/" + input.SourceCommit + "/" + Text(target, "outerArchiveSha256") + ".zip";
        FabricC1StorageAccessResource Role(string purpose, string[] actions)
        {
            return new("artifact-storage-" + purpose + "-role",
                FabricC1Deployment.ArtifactStorageRoleId(input, purpose),
                "ArtifactStorageRole", new()
                {
                    ["properties"] = new JsonObject
                    {
                        ["roleName"] = "cohors-fabric-c1-artifact-" + purpose + "-" + GuidValue(input.DeploymentId!).ToString("D"),
                        ["type"] = "CustomRole", ["description"] = Marker,
                        ["assignableScopes"] = new JsonArray(FabricC1Deployment.GroupId(input)),
                        ["permissions"] = new JsonArray(new JsonObject
                        {
                            ["actions"] = new JsonArray(), ["notActions"] = new JsonArray(), ["notDataActions"] = new JsonArray(),
                            ["dataActions"] = new JsonArray(actions.Select(action => (JsonNode)JsonValue.Create(BlobDataAction + action)!).ToArray())
                        })
                    }
                });
        }
        FabricC1StorageAccessResource Grant(string purpose, string principal, string principalType,
            FabricC1StorageAccessResource role, string[] actions)
        {
            var id = Guid.ParseExact(Canonical.Hash(new { state.BindingHash, role = "artifact-" + purpose, principal, container })[..32], "N").ToString("D");
            return new("artifact-storage-" + purpose + "-grant",
                container + "/providers/Microsoft.Authorization/roleAssignments/" + id, "ArtifactStorageGrant", new()
                {
                    ["properties"] = new JsonObject
                    {
                        ["roleDefinitionId"] = role.Id, ["principalId"] = principal, ["principalType"] = principalType,
                        ["description"] = Marker, ["conditionVersion"] = "2.0", ["condition"] = BlobPathCondition(actions, path)
                    }
                });
        }
        var publisher = Role("publisher", ["read", "write"]);
        var receiver = Role("receiver", ["read"]);
        return
        [
            publisher, receiver,
            Grant("publisher", GuidValue(input.BootstrapIdentity!.PrincipalObjectId!).ToString("D"),
                input.BootstrapIdentity.CredentialKind == "DelegatedAzureCli" ? "User" : "ServicePrincipal", publisher, ["read", "write"]),
            Grant("receiver", Text(target, "vmPrincipalId"), "ServicePrincipal", receiver, ["read"])
        ];
    }

    private void RequirePrivateArtifactAccess(FabricC1ArtifactPublicationBinding binding, string packageDirectory)
    {
        var (_, target, resources) = ReadArtifactAccess(binding, packageDirectory);
        var retained = ReadArtifactAccessIntents();
        Guard.Against(retained.Length != resources.Length ||
            !state.Objects.TryGetValue(ArtifactAccessTargetKey, out var original) ||
            original.RequestHash != Canonical.Hash(target) ||
            resources.Any(resource => !state.Objects.ContainsKey(resource.Key) || state.PendingCreates.ContainsKey(resource.Key) ||
                state.PendingDeletes.ContainsKey(resource.Key) || state.CleanupReceipts.ContainsKey(resource.Key)),
            "c1_artifact_access_binding", "Native publication requires the exact original observed publisher and receiver grants.", 409);
    }

    public async Task<FabricC1ArtifactAccessCleanupResult> CleanupPrivateArtifactAccess(CancellationToken ct)
    {
        RequireCertainCheckpointJournal();
        var resources = ReadArtifactAccessIntents();
        if (resources.Length == 0) return ArtifactAccessCleanupResult();
        var (fenced, _) = PlanCleanupRequests(0);
        if (state.CleanupStartedAtUtc is null) SaveCheckpointJournal(fenced);
        foreach (var resource in resources)
            if (!await DeleteStorageAccess(resource, ct)) break;
        return ArtifactAccessCleanupResult();
    }

    private FabricC1StorageAccessResource[] ReadArtifactAccessIntents()
    {
        Guard.Against(state.Objects.Keys.Any(key => key.StartsWith("artifact-storage-", StringComparison.Ordinal) &&
                key != ArtifactAccessTargetKey && !ArtifactAccessKeys.Any(expected => key == expected || key == expected + "-intent")) ||
            state.PendingCreates.Keys.Concat(state.PendingDeletes.Keys).Concat(state.CleanupReceipts.Keys)
                .Any(key => key.StartsWith("artifact-storage-", StringComparison.Ordinal) && !ArtifactAccessKeys.Contains(key)),
            "c1_artifact_access_cleanup_journal", "Unknown artifact access inventory cannot be omitted from cleanup.", 409);
        if (!state.Objects.TryGetValue(ArtifactAccessTargetKey, out var original))
        {
            Guard.Against(ArtifactAccessKeys.Any(key => state.Objects.ContainsKey(key) || state.Objects.ContainsKey(key + "-intent") ||
                state.PendingCreates.ContainsKey(key) || state.PendingDeletes.ContainsKey(key) ||
                state.CleanupReceipts.ContainsKey(key) || state.DeniedRequests.ContainsKey(key)),
                "c1_artifact_access_cleanup_journal", "The original artifact access target is missing.", 409);
            return [];
        }
        var target = ArtifactAccessTarget(Text(original.Response, "outerArchiveSha256"),
            Text(original.Response, "downloadProvenanceSha256"), Text(original.Response, "publicationBindingSha256"));
        Guard.Against(original.Kind != "ArtifactAccessTarget" || original.Id != Text(target, "endpoint") ||
            original.RequestHash != Canonical.Hash(target) || Canonical.Hash(original.Response) != original.RequestHash,
            "c1_artifact_access_cleanup_journal", "The retained artifact target differs from the original own foundation.", 409);
        if (ArtifactAccessKeys.All(key => !state.Objects.ContainsKey(key + "-intent")))
        {
            Guard.Against(ArtifactAccessKeys.Any(key => state.Objects.ContainsKey(key) || state.PendingCreates.ContainsKey(key) ||
                state.PendingDeletes.ContainsKey(key) || state.CleanupReceipts.ContainsKey(key) || state.DeniedRequests.ContainsKey(key)),
                "c1_artifact_access_cleanup_journal", "Artifact I/O has no complete original access intents.", 409);
            return [];
        }
        var resources = ArtifactAccessResources(target).ToDictionary(resource => resource.Key, StringComparer.Ordinal);
        foreach (var resource in resources.Values)
        {
            var hash = Canonical.Hash(resource.Request);
            Guard.Against(!state.Objects.TryGetValue(resource.Key + "-intent", out var intent) ||
                intent.Kind != "ArtifactStorageIntent" || intent.Id != resource.Id || intent.RequestHash != hash ||
                Canonical.Hash(intent.Response) != Canonical.Hash(resource),
                "c1_artifact_access_cleanup_journal", "The complete original artifact role/grant request differs.", 409);
            state.Objects.TryGetValue(resource.Key, out var known);
            state.PendingCreates.TryGetValue(resource.Key, out var pending);
            Guard.Against(known is not null && (pending is not null || known.Kind != resource.Kind ||
                    !SameArmId(known.Id, resource.Id) || known.RequestHash != hash) ||
                pending is not null && pending != hash ||
                known is null && (state.PendingDeletes.ContainsKey(resource.Key) || state.CleanupReceipts.ContainsKey(resource.Key)) ||
                state.DeniedRequests.TryGetValue(resource.Key, out var denied) && (denied.RequestHash != hash || denied.Status is not (401 or 403)),
                "c1_artifact_access_cleanup_journal", "The original artifact create/delete inventory is inconsistent.", 409);
            if (known is not null) VerifyStorageAccess(resource, known.Response);
        }
        return ArtifactAccessKeys.Select(key => resources[key]).ToArray();
    }

    private bool IsArtifactAccessCleanupUri(Uri uri) =>
        uri.Scheme == "https" && uri.IdnHost == "management.azure.com" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        ReadArtifactAccessIntents().Any(resource =>
            uri.AbsoluteUri == "https://management.azure.com" + resource.Id + "?api-version=2022-04-01" &&
            (state.Objects.ContainsKey(resource.Key) || state.PendingCreates.ContainsKey(resource.Key)));

    private FabricC1ArtifactAccessCleanupResult ArtifactAccessCleanupResult()
    {
        var absent = ArtifactAccessKeys.Where(key => state.CleanupReceipts.ContainsKey(key)).ToArray();
        var unresolved = ArtifactAccessKeys.Where(key => state.PendingCreates.ContainsKey(key) ||
            state.PendingDeletes.ContainsKey(key) || state.Objects.ContainsKey(key) && !state.CleanupReceipts.ContainsKey(key)).ToArray();
        var notManaged = ArtifactAccessKeys.Where(key => !state.Objects.ContainsKey(key) && !state.PendingCreates.ContainsKey(key)).ToArray();
        return new(unresolved.Length != 0 ? "ArtifactAccessCleanupPendingReconciliation" :
            notManaged.Length == ArtifactAccessKeys.Length ? "ArtifactAccessNotManaged" : "ArtifactAccessInventoryAbsent",
            absent, unresolved, notManaged);
    }
}
