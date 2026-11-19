using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SourceWorkspaceCleanupResult(string Status, string Scope, string WorkspaceId)
{
    public bool CompleteC1Cleanup => false;
    public bool EffectiveRevocationProven => false;
    public string EvidenceLevel => "active-synthetic-workspace-inventory-not-worker-network-capacity-or-retention-cleanup";
}

public sealed partial class FabricC1Provisioner
{
    private sealed record SourceWorkspaceCleanupTarget(string Scope, string Key, string Id, Uri Uri,
        string RequestHash, FabricC1SourceNotebookPlan Plan);

    public async Task<FabricC1SourceWorkspaceCleanupResult> CleanupMaterializedSourceWorkspace(string scope, CancellationToken ct)
    {
        RequireCertainCheckpointJournal();
        var target = SourceWorkspaceCleanupTargetFor(scope);
        var (fenced, _) = PlanCleanupRequests(0);
        if (state.CleanupStartedAtUtc is null) SaveCheckpointJournal(fenced);
        state.PendingDeletes.TryGetValue(target.Key, out var deleting);
        state.CleanupReceipts.TryGetValue(target.Key, out var absent);
        Guard.Against(deleting is not null && (deleting != target.RequestHash || absent is not null) ||
            absent is not null && (absent.Kind != "SourceWorkspaceAbsent" || absent.Id != target.Id ||
                absent.RequestHash != target.RequestHash) ||
            state.DeniedRequests.TryGetValue("cleanup-" + target.Key, out var denied) &&
                (denied.RequestHash != target.RequestHash || denied.Status is not (401 or 403)),
            "c1_source_cleanup_binding", "The original source workspace cleanup journal changed.", 409);

        var current = await Send(HttpMethod.Get, target.Uri, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            RecordSourceWorkspaceAbsence(target);
            return new("SourceWorkspaceInventoryAbsent", scope, target.Id);
        }
        Guard.Against(absent is not null, "c1_deploy_cleanup_restored",
            "A previously absent source workspace reappeared; never delete the restored resource.", 409);
        VerifySourceCleanupWorkspace(current.Body, target);
        if (deleting is not null) return new("SourceWorkspaceCleanupPendingReconciliation", scope, target.Id);

        await RequireClosedSourceItems(target.Plan, ct, cleanup: true);
        var principal = Owned("runtime-sp", "ServicePrincipal");
        RequireMarker(principal, true);
        var principalId = GuidValue(Text(principal, "id")).ToString("D");
        var assignments = await ReadAll(target.Uri.AbsoluteUri + "/roleAssignments", false, ct, cleanup: true);
        RequireClosedSourceAssignments(scope, principalId, assignments);
        if (scope == "released") RequireSourceViewerOwnership(principalId, assignments, allowMissing: true);
        await RequireSourceCleanupRoles(target, principalId, ct);

        PlanCleanupRequests(0);
        SaveCheckpointJournal(state with
        { PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [target.Key] = target.RequestHash } });
        var deleted = await Send(HttpMethod.Delete, target.Uri, ct: ct, cleanup: true);
        if (deleted.Status is 401 or 403)
        {
            var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            pending.Remove(target.Key);
            SaveCheckpointJournal(state with
            {
                PendingDeletes = pending,
                DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                { ["cleanup-" + target.Key] = new(target.RequestHash, deleted.Status) }
            });
        }
        RequireStatus(deleted, 200, 404);
        var readback = await Send(HttpMethod.Get, target.Uri, ct: ct, cleanup: true);
        RequireStatus(readback, 200, 404);
        if (readback.Status == 200)
        {
            VerifySourceCleanupWorkspace(readback.Body, target);
            return new("SourceWorkspaceCleanupPendingReconciliation", scope, target.Id);
        }
        RecordSourceWorkspaceAbsence(target);
        return new("SourceWorkspaceInventoryAbsent", scope, target.Id);
    }

    private SourceWorkspaceCleanupTarget SourceWorkspaceCleanupTargetFor(string scope)
    {
        RequireSourceNotebookScope(scope);
        var plan = RestoreSourceNotebook(scope);
        var materialization = RequireSourceMaterializationReceipt(plan);
        var key = "workspace-" + scope;
        var original = state.Objects[key];
        var id = GuidValue(original.Id).ToString("D");
        SameId(id, Text(original.Response, "id"));
        SameId(id, Text(plan.Binding, "workspaceId"));
        var expected = new JsonObject
        {
            ["displayName"] = FabricC1Deployment.Name(input) + "-" + scope,
            ["capacityId"] = Text(Owned("fabric-capacity", "FabricCapacity"), "id"), ["description"] = Marker
        };
        Guard.Against(original.RequestHash != Canonical.Hash(expected) ||
            state.PendingCreates.ContainsKey(key) || state.PendingOperations.ContainsKey(key),
            "c1_source_cleanup_binding", "Cleanup requires the exact original workspace creation receipt, not a supplied or unresolved workspace.", 409);
        var uri = new Uri($"https://{WorkspaceHost(id)}/v1/workspaces/{id}");
        var hash = Canonical.Hash(new
        {
            Method = "DELETE", Uri = uri.AbsoluteUri, WorkspaceCreationSha256 = original.RequestHash,
            SourceBindingSha256 = plan.BindingSha256, MaterializationSha256 = materialization.RequestHash
        });
        var target = new SourceWorkspaceCleanupTarget(scope, key, id, uri, hash, plan);
        VerifySourceCleanupWorkspace(original.Response, target);
        return target;
    }

    private void VerifySourceCleanupWorkspace(JsonObject workspace, SourceWorkspaceCleanupTarget target)
    {
        RequireMarker(workspace, false);
        SameId(Text(workspace, "id"), target.Id);
        SameId(Text(workspace, "capacityId"), Text(Owned("fabric-capacity", "FabricCapacity"), "id"));
        Guard.Against(Text(workspace, "displayName") != FabricC1Deployment.Name(input) + "-" + target.Scope,
            "c1_source_cleanup_binding", "The original dedicated synthetic workspace changed.", 409);
    }

    private async Task RequireSourceCleanupRoles(SourceWorkspaceCleanupTarget target, string principalId, CancellationToken ct)
    {
        var roles = await ReadAll(target.Uri.AbsoluteUri + "/items/" + Text(target.Plan.Binding, "lakehouseId") +
            "/dataAccessRoles", false, ct, cleanup: true);
        var definition = SourceReadRole(target.Plan, principalId);
        RequireClosedSourceRoles(roles, definition, target.Id, Text(target.Plan.Binding, "lakehouseId"));
        var ownRole = roles.SingleOrDefault(role =>
            string.Equals(Text(role, "name"), Text(definition, "name"), StringComparison.OrdinalIgnoreCase));
        if (ownRole is null) return;
        Guard.Against(target.Scope != "released", "c1_source_role_scope", "The denied source must not contain a runtime role.", 409);
        var hash = Canonical.Hash(definition);
        if (state.Objects.TryGetValue(SourceAccessRoleKey, out var known))
        {
            RequireSourceAccessRoleReceipt(known, Owned(SourceAccessPlanKey, "SourceAccessDefinition"), hash);
            Guard.Against(ownRole["eTag"]?.GetValue<string>() != known.Response["role"]?["eTag"]?.GetValue<string>(),
                "c1_source_cleanup_binding", "A replaced data role is not adopted during source cleanup.", 409);
        }
        else Guard.Against(!state.PendingCreates.TryGetValue(SourceAccessRoleKey, out var pending) || pending != hash,
            "c1_deploy_foreign", "An unjournaled source role prevents whole-workspace cleanup.", 409);
    }

    private bool IsSourceWorkspaceCleanupUri(HttpMethod method, Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        foreach (var scope in new[] { "released", "denied" })
        {
            if (!state.Objects.TryGetValue("workspace-" + scope, out var original) ||
                uri.IdnHost != WorkspaceHost(original.Id)) continue;
            var target = SourceWorkspaceCleanupTargetFor(scope);
            if (uri.AbsoluteUri == target.Uri.AbsoluteUri) return method == HttpMethod.Get || method == HttpMethod.Delete;
            if (method != HttpMethod.Get) return false;
            var root = target.Uri.AbsolutePath;
            var lakehouse = Text(target.Plan.Binding, "lakehouseId");
            return uri.AbsolutePath == root + "/lakehouses/" + lakehouse ||
                uri.AbsolutePath == root + "/items" || uri.AbsolutePath == root + "/roleAssignments" ||
                uri.AbsolutePath == root + "/lakehouses/" + lakehouse + "/tables" ||
                uri.AbsolutePath == root + "/items/" + lakehouse + "/dataAccessRoles";
        }
        return false;
    }

    private void RecordSourceWorkspaceAbsence(SourceWorkspaceCleanupTarget target)
    {
        PlanCleanupRequests(0);
        var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        pending.Remove(target.Key);
        SaveCheckpointJournal(state with
        {
            PendingDeletes = pending,
            CleanupReceipts = new(state.CleanupReceipts, StringComparer.Ordinal)
            {
                [target.Key] = new("SourceWorkspaceAbsent", target.Id, target.RequestHash, new()
                {
                    ["status"] = "ActiveFabricWorkspaceNotFound", ["scope"] = target.Scope,
                    ["observedAtUtc"] = SourceUtcText(time.GetUtcNow()), ["effectiveRevocationProven"] = false,
                    ["workersStopped"] = false, ["retentionPurged"] = false, ["completeC1Cleanup"] = false
                })
            }
        });
    }
}
