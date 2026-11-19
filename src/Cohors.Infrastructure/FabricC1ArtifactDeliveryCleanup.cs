using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1ArtifactDeliveryCleanupResult(string Status, string? CommandResourceId)
{
    public bool CompleteC1Cleanup => false;
    public bool WorkersStopped => false;
    public bool ArtifactDataDeleted => false;
    public string Scope => "run-command-inventory-only-not-independent-stop-or-vm-data-cleanup";
}

public sealed partial class FabricC1Provisioner
{
    public async Task<FabricC1ArtifactDeliveryCleanupResult> CleanupPrivateArtifactDelivery(CancellationToken ct)
    {
        RequireCertainCheckpointJournal();
        var intent = ReadArtifactCommandIntent();
        if (intent is null) return new("ArtifactCommandNotManaged", null);
        var (fenced, _) = PlanCleanupRequests(0);
        if (state.CleanupStartedAtUtc is null) SaveCheckpointJournal(fenced);
        if (!state.Objects.ContainsKey(ArtifactCommandKey) && !state.PendingCreates.ContainsKey(ArtifactCommandKey))
            return new("ArtifactCommandNotManaged", intent.Id);
        var uri = new Uri("https://management.azure.com" + intent.Id + ArtifactCommandApi);
        var viewUri = new Uri("https://management.azure.com" + intent.Id + ArtifactCommandView);
        var hash = Canonical.Hash(new { Method = "DELETE", Uri = uri.AbsoluteUri, intent.RequestHash, ObjectId = intent.Id });
        state.Objects.TryGetValue(ArtifactCommandKey, out var known);
        state.PendingDeletes.TryGetValue(ArtifactCommandKey, out var deleting);
        state.CleanupReceipts.TryGetValue(ArtifactCommandKey, out var absent);
        Guard.Against(deleting is not null && (deleting != hash || absent is not null || known is null) ||
            absent is not null && (absent.Kind != "ArtifactCommandAbsent" || absent.Id != intent.Id || absent.RequestHash != hash) ||
            state.DeniedRequests.TryGetValue("cleanup-" + ArtifactCommandKey, out var denied) &&
                (denied.RequestHash != hash || denied.Status is not (401 or 403)),
            "c1_artifact_command_cleanup", "The original command deletion journal changed.", 409);
        var vm = await Send(HttpMethod.Get,
            new("https://management.azure.com" + Text(intent.Response, "vmResourceId") + ArtifactCommandApi), ct: ct, cleanup: true);
        RequireStatus(vm, 200, 404);
        if (vm.Status == 200) VerifyArtifactAccessProbe(vm.Body, state.Objects[ArtifactAccessTargetKey].Response);
        var current = await Send(HttpMethod.Get, viewUri, ct: ct, cleanup: true);
        RequireStatus(current, 200, 404);
        if (current.Status == 404)
        {
            if (known is null) return new("ArtifactCommandCleanupPendingReconciliation", intent.Id);
            RecordArtifactCommandAbsence(intent, hash);
            return new("ArtifactCommandInventoryAbsent", intent.Id);
        }
        Guard.Against(vm.Status != 200 || absent is not null, "c1_deploy_cleanup_restored",
            "The command reappeared or its original VM cannot be confirmed; do not delete it.", 409);
        VerifyArtifactCommand(current.Body, intent, known);
        if (deleting is not null) return new("ArtifactCommandCleanupPendingReconciliation", intent.Id);
        PlanCleanupRequests(0);
        if (known is null) SaveObject(ArtifactCommandKey, "ArtifactStageRunCommand", intent.Id, intent.RequestHash, current.Body);
        SaveCheckpointJournal(state with
        { PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [ArtifactCommandKey] = hash } });
        var deleted = await Send(HttpMethod.Delete, uri, ct: ct, cleanup: true);
        if (deleted.Status is 401 or 403)
        {
            var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            pending.Remove(ArtifactCommandKey);
            SaveCheckpointJournal(state with
            {
                PendingDeletes = pending,
                DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                { ["cleanup-" + ArtifactCommandKey] = new(hash, deleted.Status) }
            });
        }
        RequireStatus(deleted, 200, 202, 204, 404);
        var readback = await Send(HttpMethod.Get, viewUri, ct: ct, cleanup: true);
        RequireStatus(readback, 200, 404);
        if (readback.Status == 200)
        {
            VerifyArtifactCommand(readback.Body, intent, state.Objects[ArtifactCommandKey]);
            return new("ArtifactCommandCleanupPendingReconciliation", intent.Id);
        }
        RecordArtifactCommandAbsence(intent, hash);
        return new("ArtifactCommandInventoryAbsent", intent.Id);
    }

    private FabricC1ProvisionedObject? ReadArtifactCommandIntent()
    {
        Guard.Against(state.Objects.Keys.Any(key => key.StartsWith(ArtifactCommandKey, StringComparison.Ordinal) &&
                key != ArtifactCommandKey && key != ArtifactCommandIntentKey) ||
            state.PendingCreates.Keys.Concat(state.PendingDeletes.Keys).Concat(state.CleanupReceipts.Keys)
                .Any(key => key.StartsWith(ArtifactCommandKey, StringComparison.Ordinal) && key != ArtifactCommandKey),
            "c1_artifact_command_cleanup", "Unknown command inventory cannot be omitted from cleanup.", 409);
        if (!state.Objects.TryGetValue(ArtifactCommandIntentKey, out var intent))
        {
            Guard.Against(state.Objects.ContainsKey(ArtifactCommandKey) || state.Objects.ContainsKey(ArtifactStageReceiptKey) ||
                state.PendingCreates.ContainsKey(ArtifactCommandKey) || state.PendingDeletes.ContainsKey(ArtifactCommandKey) ||
                state.CleanupReceipts.ContainsKey(ArtifactCommandKey) || state.DeniedRequests.ContainsKey(ArtifactCommandKey) ||
                state.DeniedRequests.ContainsKey("cleanup-" + ArtifactCommandKey),
                "c1_artifact_command_cleanup", "The original command intent is missing; never reconstruct it during cleanup.", 409);
            return null;
        }
        var stage = state.Objects.GetValueOrDefault(ArtifactStageKey) ??
            throw new DomainException("c1_artifact_command_cleanup", "The original receiver allocation is missing.", 409);
        var bundle = RestoreArtifactStage(stage, Text(stage.Response, "requestSha256"));
        var access = state.Objects.GetValueOrDefault(ArtifactAccessTargetKey) ??
            throw new DomainException("c1_artifact_command_cleanup", "The original VM/archive target is missing.", 409);
        var target = ArtifactAccessTarget(Text(access.Response, "outerArchiveSha256"),
            Text(access.Response, "downloadProvenanceSha256"), Text(access.Response, "publicationBindingSha256"));
        Guard.Against(access.Kind != "ArtifactAccessTarget" || access.Id != Text(target, "endpoint") ||
            access.RequestHash != Canonical.Hash(target) ||
            Canonical.Hash(access.Response) != access.RequestHash,
            "c1_artifact_command_cleanup", "The original VM/archive target changed.", 409);
        var expected = ExpectedArtifactCommandIntent(bundle, target);
        Guard.Against(intent.Kind != expected.Kind || intent.Id != expected.Id || intent.RequestHash != expected.RequestHash ||
            Canonical.Hash(intent.Response) != expected.RequestHash,
            "c1_artifact_command_cleanup", "The complete original command intent changed.", 409);
        state.Objects.TryGetValue(ArtifactCommandKey, out var known);
        state.PendingCreates.TryGetValue(ArtifactCommandKey, out var pending);
        ValidateArtifactCommandInventory(intent, known, pending);
        return intent;
    }

    private bool IsArtifactCommandCleanupUri(HttpMethod method, Uri uri)
    {
        if (uri.Scheme != "https" || uri.IdnHost != "management.azure.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        var intent = ReadArtifactCommandIntent();
        if (intent is null || !state.Objects.ContainsKey(ArtifactCommandKey) && !state.PendingCreates.ContainsKey(ArtifactCommandKey))
            return false;
        return method == HttpMethod.Get &&
            (uri.AbsoluteUri == "https://management.azure.com" + intent.Id + ArtifactCommandView ||
             uri.AbsoluteUri == "https://management.azure.com" + Text(intent.Response, "vmResourceId") + ArtifactCommandApi) ||
            method == HttpMethod.Delete && uri.AbsoluteUri == "https://management.azure.com" + intent.Id + ArtifactCommandApi;
    }

    private void RecordArtifactCommandAbsence(FabricC1ProvisionedObject intent, string hash)
    {
        PlanCleanupRequests(0);
        var pending = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        pending.Remove(ArtifactCommandKey);
        SaveCheckpointJournal(state with
        {
            PendingDeletes = pending,
            CleanupReceipts = new(state.CleanupReceipts, StringComparer.Ordinal)
            {
                [ArtifactCommandKey] = new("ArtifactCommandAbsent", intent.Id, hash, new()
                {
                    ["status"] = "ActiveArmEndpointNotFound", ["observedAtUtc"] = StageUtc(time.GetUtcNow()),
                    ["workersStopped"] = false, ["artifactDataDeleted"] = false
                })
            }
        });
    }
}
