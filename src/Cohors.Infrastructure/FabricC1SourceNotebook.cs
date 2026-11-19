using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SourceNotebookPlan(JsonObject Binding, JsonObject Definition, string Program)
{
    public string BindingSha256 => Canonical.Hash(Binding);
    public string EvidenceLevel => "prepared-synthetic-notebook-not-executed";
}

public sealed partial class FabricC1Provisioner
{
    private const string SourceNotebookContract = "cohors-fabric-c1-source-notebook-1";
    private const string SourceResultContract = "cohors-fabric-c1-source-result-1";
    private const string SourceTableName = "c1_sources";

    public async Task<FabricC1SourceNotebookPlan> PrepareSourceNotebook(string role, CancellationToken ct)
    {
        RequireSourceNotebookScope(role);
        PlanNormalRequests(0);
        var workspace = Owned("workspace-" + role, "Workspace");
        var workspaceId = GuidValue(Text(workspace, "id")).ToString("D");
        var lakehouse = Owned("lakehouse-" + role, "Lakehouse");
        var lakehouseId = GuidValue(Text(lakehouse, "id")).ToString("D");
        var policy = state.Objects["policy-" + role];
        Guard.Against(policy.Kind != "CommunicationPolicy" || GuidValue(policy.Id) != GuidValue(workspaceId) ||
            policy.RequestHash != Canonical.Hash(policy.Response) ||
            !JsonNode.DeepEquals(policy.Response, FabricC1Deployment.LockCommunicationPolicy(policy.Response)),
            "c1_source_policy", "Source preparation requires the original fully denied workspace policy.", 409);
        await RequireWorkspaceInboundEnabled(ct);
        var currentWorkspace = await Read($"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}", ct);
        RequireMarker(currentWorkspace, false);
        SameId(Text(currentWorkspace, "id"), workspaceId);
        SameId(Text(currentWorkspace, "capacityId"), Text(Owned("fabric-capacity", "FabricCapacity"), "id"));
        Guard.Against(Text(currentWorkspace, "displayName") != FabricC1Deployment.Name(input) + "-" + role,
            "c1_source_workspace", "The dedicated source workspace changed.", 409);
        var currentPolicy = await Read($"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/networking/communicationPolicy", ct);
        Guard.Against(!JsonNode.DeepEquals(policy.Response, currentPolicy), "c1_source_policy",
            "Current source privacy differs from the original readback; no notebook is created.", 409);
        var currentLakehouse = await Read($"https://{WorkspaceHost(workspaceId)}/v1/workspaces/{workspaceId}/lakehouses/{lakehouseId}", ct);
        RequireMarker(currentLakehouse, false);
        SameId(Text(currentLakehouse, "id"), lakehouseId);
        SameId(Text(currentLakehouse, "workspaceId"), workspaceId);
        Guard.Against(Text(currentLakehouse, "type") != "Lakehouse" ||
            Text(currentLakehouse, "displayName") != "C1_" + role ||
            currentLakehouse["properties"] is not JsonObject,
            "c1_source_lakehouse", "The original source lakehouse and its properties are required.", 409);
        var schema = currentLakehouse["properties"]!["defaultSchema"]?.GetValue<string>();
        Guard.Against(schema is not (null or "dbo"), "c1_source_schema",
            "Only the documented schemaless or dbo source layout is supported; no schema conversion.", 409);
        var (_, deadline) = PlanNormalRequests(0);
        var key = "source-notebook-plan-" + role;
        if (state.Objects.ContainsKey(key))
        {
            var restored = RestoreSourceNotebook(role);
            Guard.Against(restored.Binding["schemaName"]?.GetValue<string>() != schema ||
                Text(restored.Binding, "policySha256") != policy.RequestHash,
                "c1_source_binding", "The original source schema or policy changed.", 409);
            return restored;
        }
        var fixture = SourceFixture();
        var binding = new JsonObject
        {
            ["contract"] = SourceNotebookContract, ["deploymentBindingHash"] = state.BindingHash,
            ["sourceCommit"] = input.SourceCommit, ["invocationId"] = Guid.NewGuid().ToString("D"), ["scope"] = role,
            ["workspaceId"] = workspaceId, ["lakehouseId"] = lakehouseId, ["lakehouseName"] = "C1_" + role,
            ["actorObjectId"] = GuidValue(input.BootstrapIdentity!.PrincipalObjectId!).ToString("D"),
            ["schemaName"] = schema, ["policySha256"] = policy.RequestHash,
            ["fixtureSha256"] = Canonical.HashText(fixture), ["releaseId"] = "C1-RELEASE-1",
            ["rows"] = SourceRows(fixture, role), ["startedAtUtc"] = SourceUtcText(state.StartedAtUtc!.Value),
            ["expiresAtUtc"] = SourceUtcText(deadline)
        };
        var plan = BuildSourceNotebook(binding);
        var body = new JsonObject
        {
            ["binding"] = binding.DeepClone(), ["definitionSha256"] = Canonical.Hash(plan.Definition),
            ["programSha256"] = Canonical.HashText(plan.Program)
        };
        SaveObject(key, "SourceNotebookPlan", Text(binding, "invocationId"), Canonical.Hash(body), body);
        return plan;
    }

    public async Task<JsonObject> EnsureSourceNotebook(string role, CancellationToken ct)
    {
        var plan = await PrepareSourceNotebook(role, ct);
        var workspaceId = Text(plan.Binding, "workspaceId");
        var collection = $"https://{WorkspaceHost(workspaceId)}/v1/workspaces/{workspaceId}/notebooks";
        var name = "C1_Materialize_" + role;
        var notebook = await EnsureCreated("source-notebook-" + role, "Notebook", collection, name,
            new JsonObject { ["displayName"] = name, ["description"] = Marker, ["definition"] = plan.Definition.DeepClone() }, false, ct);
        RequireMarker(notebook, false);
        SameId(Text(notebook, "workspaceId"), workspaceId);
        Guard.Against(Text(notebook, "type") != "Notebook" || Text(notebook, "displayName") != name,
            "c1_source_notebook", "The returned notebook is not the original source notebook.", 409);
        var definitionKey = "source-notebook-definition-" + role;
        var readback = await ReadItemDefinition(definitionKey, collection, Text(notebook, "id"), ct, "ipynb");
        VerifySourceNotebookDefinition(plan, readback);
        SaveObject(definitionKey, "SourceNotebookDefinition", Text(notebook, "id"), Canonical.Hash(plan.Definition), readback);
        return notebook;
    }

    private void RequireSourceNotebookScope(string role)
    {
        Guard.Against(role is not ("released" or "denied"), "c1_deploy_source_scope",
            "Synthetic source notebooks cannot use the Contributor agent workspace.", 409);
        Guard.Against(input.BootstrapIdentity?.CredentialKind != "DelegatedAzureCli",
            "c1_source_actor", "This authoring path requires the unchanged delegated original user, not an app-only worker.", 409);
        var ids = new[] { "agent", "released", "denied" }
            .Select(name => GuidValue(Text(Owned("workspace-" + name, "Workspace"), "id"))).ToArray();
        Guard.Against(ids.Distinct().Count() != 3, "c1_deploy_source_scope", "Agent, release and deny workspaces must be distinct.", 409);
        Owned("policy-" + role, "CommunicationPolicy");
    }

    private FabricC1SourceNotebookPlan RestoreSourceNotebook(string role)
    {
        RequireSourceNotebookScope(role);
        var saved = state.Objects.TryGetValue("source-notebook-plan-" + role, out var value) ? value :
            throw new DomainException("c1_source_binding", "The original source notebook plan is missing.", 409);
        var binding = saved.Response["binding"] as JsonObject ??
            throw new DomainException("c1_source_binding", "The source binding is missing.", 409);
        var fixture = SourceFixture();
        var schema = binding["schemaName"]?.GetValue<string>();
        Guard.Against(saved.Kind != "SourceNotebookPlan" || saved.RequestHash != Canonical.Hash(saved.Response) ||
            saved.Id != Text(binding, "invocationId") || binding.Count != 16 ||
            Text(binding, "contract") != SourceNotebookContract || Text(binding, "deploymentBindingHash") != state.BindingHash ||
            Text(binding, "sourceCommit") != input.SourceCommit || Text(binding, "scope") != role ||
            Text(binding, "lakehouseName") != "C1_" + role || schema is not (null or "dbo") ||
            Text(binding, "fixtureSha256") != Canonical.HashText(fixture) || Text(binding, "releaseId") != "C1-RELEASE-1" ||
            Canonical.Hash(binding["rows"]) != Canonical.Hash(SourceRows(fixture, role)) ||
            Text(binding, "policySha256") != state.Objects["policy-" + role].RequestHash ||
            SourceUtc(binding, "startedAtUtc") != state.StartedAtUtc ||
            SourceUtc(binding, "expiresAtUtc") != SourceControlDeadline(),
            "c1_source_binding", "Original source, identity, scope, code and control window cannot be substituted.", 409);
        GuidValue(saved.Id);
        SameId(Text(binding, "workspaceId"), Text(Owned("workspace-" + role, "Workspace"), "id"));
        SameId(Text(binding, "lakehouseId"), Text(Owned("lakehouse-" + role, "Lakehouse"), "id"));
        SameId(Text(binding, "actorObjectId"), input.BootstrapIdentity!.PrincipalObjectId!);
        var plan = BuildSourceNotebook((JsonObject)binding.DeepClone());
        Guard.Against(Text(saved.Response, "definitionSha256") != Canonical.Hash(plan.Definition) ||
            Text(saved.Response, "programSha256") != Canonical.HashText(plan.Program),
            "c1_source_binding", "Original notebook code changed; do not rebuild or rerun it under the old plan.", 409);
        return plan;
    }

    private DateTimeOffset SourceControlDeadline()
    {
        var started = state.StartedAtUtc ??
            throw new DomainException("c1_source_binding", "The original control start is missing.", 409);
        var deadline = started.AddSeconds(input.MaxControlDurationSeconds!.Value);
        return input.ExpiresAtUtc!.Value < deadline ? input.ExpiresAtUtc.Value : deadline;
    }

    private static string SourceFixture()
    {
        using var stream = typeof(FabricC1Provisioner).Assembly.GetManifestResourceStream("Cohors.Fabric.C1.SyntheticSource.json") ??
            throw new DomainException("c1_source_fixture", "The versioned synthetic source is absent from this build.", 409);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
        return reader.ReadToEnd();
    }

    private static JsonArray SourceRows(string fixture, string role)
    {
        var data = JsonNode.Parse(fixture) as JsonObject ??
            throw new DomainException("c1_source_fixture", "The embedded synthetic source is not an object.", 409);
        Guard.Against(data["contract"]?.GetValue<string>() != "cohors-fabric-c1-synthetic-1" ||
            data["synthetic"]?.GetValue<bool>() != true || data["release"]?.GetValue<string>() != "C1-RELEASE-1" ||
            data["rows"] is not JsonArray { Count: 5 }, "c1_source_fixture", "Only the versioned five-row synthetic release is supported.", 409);
        var rows = data["rows"]!.AsArray().Select(node => node as JsonObject ??
            throw new DomainException("c1_source_fixture", "Synthetic row is not an object.", 409)).ToArray();
        Guard.Against(rows.Any(row => row.Count != 6 || row["sourceId"]?.GetValue<string>() is not ("C1-A" or "C1-B" or "C1-C" or "C1-D") ||
            row["productId"]?.GetValue<string>() is not ("ortho" or "cardio") || row["revision"]?.GetValue<int>() is not (1 or 2) ||
            row["state"]?.GetValue<string>() is not ("Activated" or "Pending") ||
            row["releaseState"]?.GetValue<string>() is not ("Released" or "Revoked") ||
            row["marker"]?.GetValue<string>() is not ("SYNTHETIC-ALLOW-A" or "SYNTHETIC-ALLOW-B" or
                "SYNTHETIC-DENY-PENDING" or "SYNTHETIC-DENY-REVOKED" or "SYNTHETIC-DENY-PRODUCT")) ||
            rows.Select(row => Text(row, "sourceId") + "@" + row["revision"]!.GetValue<int>()).Distinct(StringComparer.Ordinal).Count() != 5,
            "c1_source_fixture", "The fixed synthetic source shape or unique revisions changed.", 409);
        var selected = rows.Where(row => (Text(row, "productId") == "ortho" &&
            Text(row, "state") == "Activated" && Text(row, "releaseState") == "Released") == (role == "released"))
            .OrderBy(row => Text(row, "sourceId"), StringComparer.Ordinal).ThenBy(row => row["revision"]!.GetValue<int>())
            .Select(row => row.DeepClone()).ToArray();
        Guard.Against(selected.Length != (role == "released" ? 2 : 3), "c1_source_fixture", "The release/deny split must remain two plus three rows.", 409);
        return new(selected);
    }

    private static FabricC1SourceNotebookPlan BuildSourceNotebook(JsonObject binding)
    {
        var bytes = Encoding.UTF8.GetBytes(Canonical.Serialize(binding));
        var payload = Convert.ToBase64String(bytes);
        var bindingHash = Canonical.Hash(binding);
        var program = SourceNotebookProgram(payload, bindingHash);
        var metadata = new JsonObject
        {
            ["kernel_info"] = new JsonObject { ["name"] = "synapse_pyspark" },
            ["dependencies"] = new JsonObject { ["lakehouse"] = new JsonObject
            {
                ["default_lakehouse"] = Text(binding, "lakehouseId"), ["default_lakehouse_name"] = Text(binding, "lakehouseName"),
                ["default_lakehouse_workspace_id"] = Text(binding, "workspaceId"), ["known_lakehouses"] = new JsonArray()
            }}
        };
        var source = "# Fabric notebook source\n\n# METADATA ********************\n\n# META " + Canonical.Serialize(metadata) +
            "\n\n# CELL ********************\n\n" + program +
            "\n\n# METADATA ********************\n\n# META {\"language\":\"python\",\"language_group\":\"synapse_pyspark\"}\n";
        return new(binding, new JsonObject
        {
            ["format"] = "fabricGitSource", ["parts"] = new JsonArray(new JsonObject
            {
                ["path"] = "notebook-content.py", ["payloadType"] = "InlineBase64",
                ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(source))
            })
        }, program);
    }

    private static void VerifySourceNotebookDefinition(FabricC1SourceNotebookPlan plan, JsonObject response)
    {
        if (response["definition"]?["parts"] is not JsonArray parts || parts.Count is < 1 or > 2 ||
            parts.Any(part => part?["path"]?.GetValue<string>() is not ("artifact.content.ipynb" or ".platform") ||
                part?["payloadType"]?.GetValue<string>() != "InlineBase64") ||
            parts.Count(part => part?["path"]?.GetValue<string>() == "artifact.content.ipynb") != 1)
            throw new DomainException("c1_source_definition", "Expected one ipynb content part and optional platform metadata.", 409);
        var content = parts.Single(part => part?["path"]?.GetValue<string>() == "artifact.content.ipynb")!;
        Guard.Against(content["payloadType"]?.GetValue<string>() != "InlineBase64",
            "c1_source_definition", "Notebook definition must be inline content, not a remote source.", 409);
        var encoded = content["payload"]?.GetValue<string>();
        Guard.Against(encoded is null || encoded.Length > 256 * 1024, "c1_source_definition", "Notebook content is missing or oversized.", 409);
        JsonObject notebook;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded!));
            notebook = ParseUniqueSourceJson(text, "c1_source_definition");
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { throw new DomainException("c1_source_definition", "Notebook content encoding is invalid.", 409); }
        if (notebook["nbformat"]?.GetValue<int>() != 4 || notebook["cells"] is not JsonArray { Count: 1 } cells ||
            cells[0]?["cell_type"]?.GetValue<string>() != "code")
            throw new DomainException("c1_source_definition", "Only the original single code cell may execute.", 409);
        var cell = cells[0]!["source"];
        var actual = cell is JsonArray lines ? string.Concat(lines.Select(line => line?.GetValue<string>() ??
            throw new DomainException("c1_source_definition", "Notebook source line is missing.", 409))) : cell?.GetValue<string>();
        Guard.Against(actual?.Replace("\r\n", "\n").TrimEnd('\n') != plan.Program.Replace("\r\n", "\n").TrimEnd('\n'),
            "c1_source_definition", "Actual notebook code differs from the original scope/identity/write/readback contract.", 409);
    }

    private static JsonObject ParseUniqueSourceJson(string text, string code)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            Guard.Against(!UniqueC1JsonProperties(document.RootElement), code, "Duplicate JSON fields are not accepted.", 409);
            return JsonNode.Parse(text) as JsonObject ?? throw new DomainException(code, "A complete JSON object is required.", 409);
        }
        catch (JsonException) { throw new DomainException(code, "Complete valid JSON is required.", 409); }
    }

    private static string SourceUtcText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset SourceUtc(JsonObject value, string name)
    {
        Guard.Against(!DateTimeOffset.TryParse(value[name]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero,
            "c1_source_time", "A complete original UTC timestamp is required.", 409);
        return timestamp;
    }

    private static string SourceNotebookProgram(string payload, string bindingHash) => $$"""
import base64
import hashlib
import json
import os
import uuid
from datetime import datetime, timezone

SOURCE_BYTES = base64.b64decode("{{payload}}", validate=True)
SOURCE_HASH = "{{bindingHash}}"

def materialize_source(utilities, session, clock=None):
    if hashlib.sha256(SOURCE_BYTES).hexdigest() != SOURCE_HASH:
        raise RuntimeError("c1_source_binding")
    binding = json.loads(SOURCE_BYTES)
    clock = clock or (lambda: datetime.now(timezone.utc))
    started = datetime.fromisoformat(binding["startedAtUtc"].replace("Z", "+00:00"))
    expires = datetime.fromisoformat(binding["expiresAtUtc"].replace("Z", "+00:00"))
    def check_time():
        now = clock()
        if now < started or now >= expires:
            raise RuntimeError("c1_source_deadline")
        return now
    script_started = check_time()
    context = utilities.runtime.context
    def identity(name):
        value = context.get(name)
        if not isinstance(value, str):
            raise RuntimeError("c1_source_context")
        try:
            result = uuid.UUID(value)
        except ValueError:
            raise RuntimeError("c1_source_context") from None
        if result.int == 0:
            raise RuntimeError("c1_source_context")
        return str(result)
    observed = {
        "workspaceId": identity("currentWorkspaceId"),
        "lakehouseId": identity("defaultLakehouseId"),
        "lakehouseWorkspaceId": identity("defaultLakehouseWorkspaceId"),
        "notebookId": identity("currentNotebookId"),
        "actorObjectId": identity("userId"),
    }
    if (observed["workspaceId"] != binding["workspaceId"] or
            observed["lakehouseId"] != binding["lakehouseId"] or
            observed["lakehouseWorkspaceId"] != binding["workspaceId"] or
            observed["actorObjectId"] != binding["actorObjectId"] or
            context.get("defaultLakehouseName") != binding["lakehouseName"] or
            context.get("isForPipeline") is not False or context.get("isReferenceRun") is not False):
        raise RuntimeError("c1_source_context")
    schema = binding["schemaName"]
    table_parts = [binding["lakehouseName"]] + ([schema] if schema else []) + ["c1_sources"]
    table = ".".join(table_parts)
    relative_path = "Tables/" + (schema + "/" if schema else "") + "c1_sources"
    if session.catalog.tableExists(table) or utilities.fs.exists(relative_path):
        raise RuntimeError("c1_source_already_exists")
    types = [("sourceId", "string"), ("productId", "string"), ("revision", "int"),
             ("state", "string"), ("releaseState", "string"), ("marker", "string")]
    ddl = ", ".join(name + " " + kind for name, kind in types)
    frame = session.createDataFrame(binding["rows"], ddl)
    check_time()
    frame.write.format("delta").mode("errorifexists").saveAsTable(table)
    check_time()
    actual = session.table(table)
    if actual.dtypes != types:
        raise RuntimeError("c1_source_schema")
    rows = [row.asDict(recursive=True) for row in
            actual.orderBy("sourceId", "revision").limit(len(binding["rows"]) + 1).collect()]
    if rows != binding["rows"]:
        raise RuntimeError("c1_source_rows")
    details = session.sql("DESCRIBE DETAIL " + ".".join("`" + part + "`" for part in table_parts)).collect()
    if len(details) != 1 or details[0]["format"] != "delta":
        raise RuntimeError("c1_source_table")
    if not isinstance(details[0]["id"], str):
        raise RuntimeError("c1_source_table")
    try:
        delta_id = str(uuid.UUID(details[0]["id"]))
    except ValueError:
        raise RuntimeError("c1_source_table") from None
    if uuid.UUID(delta_id).int == 0:
        raise RuntimeError("c1_source_table")
    finished = check_time()
    return {
        "contract": "{{SourceResultContract}}", "bindingSha256": SOURCE_HASH,
        "invocationId": binding["invocationId"], "fixtureSha256": binding["fixtureSha256"],
        "scope": binding["scope"], **observed, "tableName": table, "deltaTableId": delta_id,
        "rows": rows, "sparkVersion": session.version,
        "startedAtUtc": script_started.isoformat().replace("+00:00", "Z"),
        "completedAtUtc": finished.isoformat().replace("+00:00", "Z"),
    }

def main():
    if os.environ.get("GITHUB_ACTIONS", "").lower() == "true":
        raise RuntimeError("c1_source_native_ci_forbidden")
    result = materialize_source(notebookutils, spark)
    notebookutils.notebook.exit(json.dumps(result, sort_keys=True, separators=(",", ":"), ensure_ascii=True))

if __name__ == "__main__":
    main()
""";
}
