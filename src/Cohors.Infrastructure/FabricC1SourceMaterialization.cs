using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SourceMaterializationResult(string Status, string NotebookId,
    string? JobId, DateTimeOffset? NextPollAtUtc, JsonObject? Receipt)
{
    public string EvidenceLevel => "notebook-job-and-source-readback-not-source-access-or-c1-acceptance";
}

public sealed partial class FabricC1Provisioner
{
    private const string SourceJobProtocol = "notebook-workload-beta-exit-value-1";

    public async Task<FabricC1SourceMaterializationResult> AdvanceSourceMaterialization(string role, CancellationToken ct)
    {
        RequireSourceNotebookScope(role);
        PlanNormalRequests(0);
        var jobKey = "source-notebook-job-" + role;
        var intentKey = "source-notebook-job-intent-" + role;
        if (!state.Objects.TryGetValue(jobKey, out var savedJob))
        {
            if (state.Objects.TryGetValue(intentKey, out var originalIntent))
                Guard.Against(state.PendingCreates.ContainsKey(jobKey) ||
                    !state.DeniedRequests.TryGetValue(jobKey, out var denied) ||
                    denied.Status is not (401 or 403) || denied.RequestHash != originalIntent.RequestHash,
                    "c1_source_job_unknown", "The original job submission is unresolved; never submit again or adopt the latest run.", 409);
            var notebook = await EnsureSourceNotebook(role, ct);
            var plan = RestoreSourceNotebook(role);
            var notebookId = GuidValue(Text(notebook, "id")).ToString("D");
            var workspaceId = Text(plan.Binding, "workspaceId");
            var submit = $"https://{WorkspaceHost(workspaceId)}/v1/workspaces/{workspaceId}/notebooks/{notebookId}/jobs/execute/instances?jobType=RunNotebook";
            if (!state.Objects.ContainsKey(intentKey))
            {
                var body = new JsonObject
                {
                    ["protocol"] = SourceJobProtocol, ["bindingSha256"] = plan.BindingSha256,
                    ["definitionSha256"] = Canonical.Hash(plan.Definition), ["notebookId"] = notebookId,
                    ["submitUri"] = submit, ["submittedAtUtc"] = SourceUtcText(time.GetUtcNow()),
                    ["expiresAtUtc"] = plan.Binding["expiresAtUtc"]!.DeepClone()
                };
                SaveObject(intentKey, "SourceNotebookJobIntent", notebookId, Canonical.Hash(body), body);
            }
            var intent = RequireSourceJobIntent(role, plan);
            Pending(jobKey, intent.RequestHash);
            var response = await Send(HttpMethod.Post, new(submit), ct: ct);
            RecordDeniedRequest(jobKey, intent.RequestHash, response.Status);
            RequireStatus(response, 202);
            var jobId = SourceJobId(response.OperationUrl, workspaceId, notebookId);
            var nextPoll = SourceNextPoll(response.RetryAfterSeconds);
            var job = new JsonObject
            {
                ["id"] = jobId, ["notebookId"] = notebookId, ["bindingSha256"] = plan.BindingSha256,
                ["protocol"] = SourceJobProtocol, ["notBeforeUtc"] = nextPoll is { } next ? SourceUtcText(next) : null,
                ["observation"] = null
            };
            SaveObject(jobKey, "SourceNotebookJob", jobId, intent.RequestHash, job);
            RequireSourcePollWindow(job);
            return new("Pending", notebookId, jobId, nextPoll, null);
        }

        var original = RestoreSourceNotebook(role);
        var originalJobIntent = RequireSourceJobIntent(role, original);
        var originalNotebookId = originalJobIntent.Id;
        Guard.Against(savedJob.Kind != "SourceNotebookJob" || savedJob.RequestHash != originalJobIntent.RequestHash ||
            Text(savedJob.Response, "id") != savedJob.Id ||
            Text(savedJob.Response, "notebookId") != originalNotebookId ||
            Text(savedJob.Response, "bindingSha256") != original.BindingSha256 ||
            Text(savedJob.Response, "protocol") != SourceJobProtocol || state.PendingCreates.ContainsKey(jobKey),
            "c1_source_job_binding", "The original notebook job identity/protocol/intent is inconsistent.", 409);
        GuidValue(savedJob.Id);
        var notBefore = RequireSourcePollWindow(savedJob.Response);
        if (time.GetUtcNow() < notBefore) return new("Pending", originalNotebookId, savedJob.Id, notBefore, null);
        var workspace = Text(original.Binding, "workspaceId");
        var endpoint = $"https://{WorkspaceHost(workspace)}/v1/workspaces/{workspace}/notebooks/{originalNotebookId}/jobs/execute/instances/{savedJob.Id}?beta=true";
        var reply = await Send(HttpMethod.Get, new(endpoint), ct: ct);
        if (reply.Status != 200)
        {
            if (reply.RetryAfterSeconds is { } retry)
            {
                var waiting = (JsonObject)savedJob.Response.DeepClone();
                waiting["notBeforeUtc"] = SourceUtcText(SourceNextPoll(retry)!.Value);
                SaveObject(jobKey, savedJob.Kind, savedJob.Id, savedJob.RequestHash, waiting);
            }
            RequireStatus(reply, 200);
        }
        SameId(Text(reply.Body, "id"), savedJob.Id);
        SameId(Text(reply.Body, "itemId"), originalNotebookId);
        Guard.Against(Text(reply.Body, "jobType") != "RunNotebook" || Text(reply.Body, "invokeType") != "OnDemand",
            "c1_source_job_binding", "Only the original on-demand notebook workload contract is accepted; no Core/schedule/pipeline substitution.", 409);
        var status = Text(reply.Body, "status");
        var observed = (JsonObject)savedJob.Response.DeepClone();
        observed["observation"] = reply.Body.DeepClone();
        var delay = SourceNextPoll(reply.RetryAfterSeconds ?? (status == "Completed" ? 0 : 5))!.Value;
        observed["notBeforeUtc"] = SourceUtcText(delay);
        if (status != "Completed")
        {
            Guard.Against(status is not ("NotStarted" or "InProgress" or "Failed" or "Cancelled" or "Deduped"),
                "c1_source_job_status", "Unknown notebook status; do not infer completion.", 409);
            SaveObject(jobKey, savedJob.Kind, savedJob.Id, savedJob.RequestHash, observed);
            Guard.Against(status is not ("NotStarted" or "InProgress"), "c1_source_job_failed",
                "The original notebook did not complete successfully; no replacement job or existing-table adoption.", 409);
            RequireSourcePollWindow(observed);
            return new("Pending", originalNotebookId, savedJob.Id, delay, null);
        }
        var receipt = VerifySourceResult(original, originalJobIntent, reply.Body);
        var receiptKey = "source-materialization-" + role;
        if (state.Objects.TryGetValue(receiptKey, out var prior))
            Guard.Against(prior.Kind != "SourceMaterializationReadback" || prior.Id != savedJob.Id ||
                prior.RequestHash != Canonical.Hash(prior.Response) || prior.RequestHash != Canonical.Hash(receipt),
                "c1_source_result_changed", "A later job result cannot replace the original accepted readback.", 409);
        SaveObject(jobKey, savedJob.Kind, savedJob.Id, savedJob.RequestHash, observed);
        SaveObject(receiptKey, "SourceMaterializationReadback", savedJob.Id, Canonical.Hash(receipt), receipt);
        RecordSourceMaterialization();
        return new("SourceReadbackObserved", originalNotebookId, savedJob.Id, null, (JsonObject)receipt.DeepClone());
    }

    private FabricC1ProvisionedObject RequireSourceJobIntent(string role, FabricC1SourceNotebookPlan plan)
    {
        if (!state.Objects.TryGetValue("source-notebook-job-intent-" + role, out var intent))
            throw new DomainException("c1_source_job_binding", "Original source job intent is missing.", 409);
        var notebookId = GuidValue(Text(Owned("source-notebook-" + role, "Notebook"), "id")).ToString("D");
        var workspace = Text(plan.Binding, "workspaceId");
        if (!state.Objects.TryGetValue("source-notebook-definition-" + role, out var definition))
            throw new DomainException("c1_source_job_binding", "The original source definition readback is missing.", 409);
        Guard.Against(intent.Kind != "SourceNotebookJobIntent" || intent.Id != notebookId ||
            intent.RequestHash != Canonical.Hash(intent.Response) || intent.Response.Count != 7 ||
            Text(intent.Response, "protocol") != SourceJobProtocol ||
            Text(intent.Response, "bindingSha256") != plan.BindingSha256 ||
            Text(intent.Response, "definitionSha256") != Canonical.Hash(plan.Definition) ||
            Text(intent.Response, "notebookId") != notebookId ||
            Text(intent.Response, "submitUri") != $"https://{WorkspaceHost(workspace)}/v1/workspaces/{workspace}/notebooks/{notebookId}/jobs/execute/instances?jobType=RunNotebook" ||
            SourceUtc(intent.Response, "expiresAtUtc") != SourceUtc(plan.Binding, "expiresAtUtc") ||
            SourceUtc(intent.Response, "submittedAtUtc") < SourceUtc(plan.Binding, "startedAtUtc") ||
            SourceUtc(intent.Response, "submittedAtUtc") >= SourceUtc(plan.Binding, "expiresAtUtc") ||
            definition.Kind != "SourceNotebookDefinition" || GuidValue(definition.Id) != GuidValue(notebookId) ||
            definition.RequestHash != Canonical.Hash(plan.Definition),
            "c1_source_job_binding", "Original job, definition, source and timebox must stay bound.", 409);
        VerifySourceNotebookDefinition(plan, definition.Response);
        return intent;
    }

    private DateTimeOffset? SourceNextPoll(int? retryAfter)
    {
        Guard.Against(retryAfter is < 0, "c1_source_job_poll", "The provider's polling delay is invalid.", 409);
        return retryAfter is { } seconds ? time.GetUtcNow().AddSeconds(seconds) : null;
    }

    private DateTimeOffset RequireSourcePollWindow(JsonObject job)
    {
        Guard.Against(job["notBeforeUtc"] is null, "c1_source_job_poll",
            "The original job is retained but its provider polling minimum is missing; do not invent one.", 409);
        var next = SourceUtc(job, "notBeforeUtc");
        Guard.Against(state.StartedAtUtc is not { } start || next < start,
            "c1_source_job_poll", "Original job polling schedule is inconsistent.", 409);
        Guard.Against(next >= SourceControlDeadline(), "c1_deploy_budget",
            "Notebook polling cannot fit the original control window; no shortened delay or new window.", 409);
        return next;
    }

    private static string SourceJobId(string? location, string workspaceId, string notebookId)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query is not ("" or "?beta=true") ||
            uri.IdnHost != WorkspaceHost(workspaceId) && uri.IdnHost != "api.fabric.microsoft.com")
            throw new DomainException("c1_source_job_location", "Job reference is outside the original Fabric workspace/notebook.", 409);
        var prefixes = new[]
        {
            $"/v1/workspaces/{workspaceId}/notebooks/{notebookId}/jobs/execute/instances/",
            $"/v1/workspaces/{workspaceId}/items/{notebookId}/jobs/instances/"
        };
        var prefix = prefixes.SingleOrDefault(value => uri.AbsolutePath.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        if (prefix is null || !Guid.TryParseExact(uri.AbsolutePath[prefix.Length..], "D", out var id) || id == Guid.Empty)
            throw new DomainException("c1_source_job_location", "No exact job-instance ID for the original notebook was returned.", 409);
        // Location is an identifier only. All workload reads use the original workspace-specific private client.
        return id.ToString("D");
    }

    private JsonObject VerifySourceResult(FabricC1SourceNotebookPlan plan, FabricC1ProvisionedObject intent, JsonObject job)
    {
        var output = job["exitValue"]?.GetValue<string>();
        Guard.Against(job["failureReason"] is not null || string.IsNullOrEmpty(output) || Encoding.UTF8.GetByteCount(output) > 16 * 1024,
            "c1_source_result", "Completed alone is insufficient; the bounded complete notebook exit result is required.", 409);
        var result = ParseUniqueSourceJson(output!, "c1_source_result");
        var binding = plan.Binding;
        var schema = binding["schemaName"]?.GetValue<string>();
        var table = Text(binding, "lakehouseName") + "." + (schema is null ? "" : schema + ".") + SourceTableName;
        Guard.Against(result.Count != 16 || Text(result, "contract") != SourceResultContract ||
            Text(result, "bindingSha256") != plan.BindingSha256 ||
            Text(result, "invocationId") != Text(binding, "invocationId") ||
            Text(result, "fixtureSha256") != Text(binding, "fixtureSha256") ||
            Text(result, "scope") != Text(binding, "scope") ||
            Text(result, "tableName") != table || Canonical.Hash(result["rows"]) != Canonical.Hash(binding["rows"]) ||
            Text(result, "sparkVersion").Length is < 1 or > 80,
            "c1_source_result", "Notebook result differs from the original synthetic source, scope or actual rowset.", 409);
        foreach (var field in new[] { "workspaceId", "lakehouseId", "actorObjectId" }) SameId(Text(result, field), Text(binding, field));
        SameId(Text(result, "lakehouseWorkspaceId"), Text(binding, "workspaceId"));
        SameId(Text(result, "notebookId"), intent.Id);
        GuidValue(Text(result, "deltaTableId"));
        var started = SourceJobUtc(job, "startTimeUtc");
        var ended = SourceJobUtc(job, "endTimeUtc");
        var scriptStarted = SourceUtc(result, "startedAtUtc");
        var scriptEnded = SourceUtc(result, "completedAtUtc");
        Guard.Against(started < SourceUtc(intent.Response, "submittedAtUtc") || ended < started ||
            ended > SourceUtc(binding, "expiresAtUtc") || ended > time.GetUtcNow() ||
            scriptStarted < started || scriptEnded < scriptStarted || scriptEnded > ended,
            "c1_source_time", "Actual job and source readback must fit the original execution window.", 409);
        return new()
        {
            ["contract"] = "cohors-fabric-c1-source-readback-1", ["protocol"] = SourceJobProtocol,
            ["bindingSha256"] = plan.BindingSha256, ["intentSha256"] = intent.RequestHash,
            ["jobId"] = Text(job, "id"), ["notebookId"] = intent.Id,
            ["providerStartedAtUtc"] = SourceUtcText(started), ["providerEndedAtUtc"] = SourceUtcText(ended),
            ["result"] = result
        };
    }

    private static DateTimeOffset SourceJobUtc(JsonObject job, string field)
    {
        var value = job[field]?.GetValue<string>();
        if (value is null || !Regex.IsMatch(value,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)?$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new DomainException("c1_source_time", "Notebook UTC fields must use the documented UTC timestamp shape.", 409);
        return parsed;
    }

    private void RecordSourceMaterialization()
    {
        if (!state.Objects.TryGetValue("source-materialization-released", out var released) ||
            !state.Objects.TryGetValue("source-materialization-denied", out var denied)) return;
        Guard.Against(released.Id == denied.Id ||
            released.Response["notebookId"]?.GetValue<string>() == denied.Response["notebookId"]?.GetValue<string>() ||
            released.Response["result"]?["deltaTableId"]?.GetValue<string>() == denied.Response["result"]?["deltaTableId"]?.GetValue<string>(),
            "c1_deploy_source_scope", "Release and deny sources require distinct notebooks, jobs and Delta tables.", 409);
        foreach (var role in new[] { "released", "denied" })
        {
            var plan = RestoreSourceNotebook(role);
            var receipt = state.Objects["source-materialization-" + role];
            var job = state.Objects["source-notebook-job-" + role];
            Guard.Against(receipt.Kind != "SourceMaterializationReadback" ||
                receipt.RequestHash != Canonical.Hash(receipt.Response) || receipt.Id != job.Id ||
                job.Response["observation"] is not JsonObject observation ||
                Text(observation, "status") != "Completed" ||
                receipt.RequestHash != Canonical.Hash(VerifySourceResult(plan, RequireSourceJobIntent(role, plan), observation)),
                "c1_source_result", "Both source scopes require their own original completed job and complete readback.", 409);
        }
        var body = new JsonObject
        {
            ["contract"] = "cohors-fabric-c1-materialization-1", ["sourceCommit"] = input.SourceCommit,
            ["fixtureSha256"] = Canonical.HashText(SourceFixture()), ["releaseId"] = "C1-RELEASE-1",
            ["releasedReceiptSha256"] = released.RequestHash, ["deniedReceiptSha256"] = denied.RequestHash
        };
        if (state.Objects.TryGetValue("materialization", out var previous))
            Guard.Against(previous.Kind != "SourceMaterialization" || previous.RequestHash != Canonical.Hash(previous.Response) ||
                previous.RequestHash != Canonical.Hash(body), "c1_source_result_changed",
                "The original two-scope materialization receipt cannot be replaced.", 409);
        SaveObject("materialization", "SourceMaterialization", input.DeploymentId!, Canonical.Hash(body), body);
    }
}
