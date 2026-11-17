using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1Identity(string? TenantId, string? ClientId, string? PrincipalObjectId,
    string? CredentialKind, string? CredentialReference, string? ExpectedIssuer, string? ExpectedAudience);
public sealed record FabricC1Target(string? CapacityResourceId, string? Region, string? WorkspaceId,
    string? DataAgentId, string? SourceItemId, string? OntologyId, string? PublishedMcpEndpoint,
    string? ProtocolVersion, string? ToolName, string? QuestionArgument, string? InputSchemaSha256, string? TaskProtocolReview);
public sealed record FabricC1Source(string? ProductScope, string? ReleaseId, string? MaterializationSha256,
    string? OntologyBindingsSha256, string? SourceGrantReviewSha256, string? FixtureSha256);
public sealed record FabricC1Runtime(string? PrivateRunnerResourceId, string? HostedProjectEndpoint,
    string? HostedAgentVersion, string? HostedImageDigest, string? ModelDeployment, string? ModelVersion);
public sealed record FabricC1Controls(string? ResourcePlanSha256, string? PrivatePathReviewSha256,
    string? DnsAndEgressPlanSha256, string? PublicDenyPlanSha256, string? DataHandlingAndRetentionSha256,
    string? BudgetPlanSha256, string? CleanupPlanSha256, string? RunApprovalReference);
public sealed record FabricC1Limits(int? MaxRequests, int? MaxDurationSeconds, int? MaxResponseBytes, int? CleanupTimeoutSeconds);
public sealed record FabricC1Inputs(string Contract, string DeliveryId, string Mode, bool GeneralCostApprovalReceived,
    string TokenScope, FabricC1Identity Identity, FabricC1Target Target, FabricC1Source Source,
    FabricC1Runtime Runtime, FabricC1Controls Controls, FabricC1Limits Limits);
public sealed record FabricC1Preflight(string Status, string BindingHash, string[] MissingOrInvalid)
{
    public bool CloudExecutionAllowed => false;
}
public sealed record FabricC1RequestPlan(string BindingHash, string ProbeId, JsonElement[] Messages)
{
    public string EvidenceLevel => "offline-request-preparation";
}
public sealed record FabricC1TaskCheckpoint(string BindingHash, string CallHash, string TaskId, string TaskProtocolReview);
public sealed record FabricC1TaskAction(string Method, string TaskId, string BindingHash, string CallHash)
{
    public string Boundary => "Task intent only; pinned SDK/dialect, private transport and native evidence are not supplied.";
}

// Offline request preparation only: no HTTP, credentials, DNS, SDK hosting or runtime registration.
public static class FabricC1ProbeClient
{
    public const string TokenScope = "https://api.fabric.microsoft.com/.default";
    public const string ProtocolVersion = "2025-11-25";

    public static FabricC1Preflight Preflight(FabricC1Inputs input, bool discoveryOnly = false)
    {
        var missing = new List<string>();
        void Text(string path, string? value) { if (string.IsNullOrWhiteSpace(value)) missing.Add(path); }
        void Id(string path, string? value) { if (!Guid.TryParse(value, out var id) || id == Guid.Empty) missing.Add(path); }
        void Hash(string path, string? value)
        {
            if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c))) missing.Add(path);
        }
        void Https(string path, string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0) missing.Add(path);
        }
        void Limit(string path, int? value) { if (value is null or <= 0) missing.Add(path); }
        if (input.Contract != "cohors-fabric-c1-preparation-1") missing.Add("contract");
        if (input.DeliveryId != "cohors-fabric") missing.Add("deliveryId");
        if (input.Mode != "PreparationOnly") missing.Add("mode");
        if (!input.GeneralCostApprovalReceived) missing.Add("generalCostApprovalReceived");
        if (input.TokenScope != TokenScope) missing.Add("tokenScope");
        var identity = input.Identity;
        if (identity is null) missing.Add("identity");
        else
        {
            Id("identity.tenantId", identity.TenantId);
            Id("identity.clientId", identity.ClientId);
            Id("identity.principalObjectId", identity.PrincipalObjectId);
            if (identity.CredentialKind is not ("ServicePrincipalCertificate" or "ServicePrincipalFederation" or "ServicePrincipalSecret"))
                missing.Add("identity.credentialKind");
            Text("identity.credentialReference", identity.CredentialReference);
            Https("identity.expectedIssuer", identity.ExpectedIssuer);
            if (string.IsNullOrWhiteSpace(identity.ExpectedAudience) ||
                !Guid.TryParse(identity.ExpectedAudience, out _) &&
                (!Uri.TryCreate(identity.ExpectedAudience, UriKind.Absolute, out var audience) ||
                    audience.Scheme != "https" || audience.UserInfo.Length != 0 || audience.Query.Length != 0 || audience.Fragment.Length != 0) ||
                identity.ExpectedAudience?.EndsWith("/.default", StringComparison.Ordinal) == true)
                missing.Add("identity.expectedAudience");
        }
        var target = input.Target;
        if (target is null) missing.Add("target");
        else
        {
            Text("target.capacityResourceId", target.CapacityResourceId);
            Text("target.region", target.Region);
            Id("target.workspaceId", target.WorkspaceId);
            Id("target.dataAgentId", target.DataAgentId);
            Id("target.sourceItemId", target.SourceItemId);
            Id("target.ontologyId", target.OntologyId);
            Https("target.publishedMcpEndpoint", target.PublishedMcpEndpoint);
            if (target.ProtocolVersion != ProtocolVersion) missing.Add("target.protocolVersion");
            if (Uri.TryCreate(target.PublishedMcpEndpoint, UriKind.Absolute, out var endpoint) &&
                endpoint.AbsolutePath != $"/v1/mcp/workspaces/{target.WorkspaceId}/dataagents/{target.DataAgentId}/agent")
                missing.Add("target.publishedMcpEndpoint.itemBinding");
            if (!discoveryOnly)
            {
                Text("target.toolName", target.ToolName);
                Text("target.questionArgument", target.QuestionArgument);
                Hash("target.inputSchemaSha256", target.InputSchemaSha256);
                Text("target.taskProtocolReview", target.TaskProtocolReview);
            }
        }
        var source = input.Source;
        if (source is null) missing.Add("source");
        else
        {
            if (source.ProductScope != "ortho") missing.Add("source.productScope");
            Text("source.releaseId", source.ReleaseId);
            Hash("source.materializationSha256", source.MaterializationSha256);
            Hash("source.ontologyBindingsSha256", source.OntologyBindingsSha256);
            Hash("source.sourceGrantReviewSha256", source.SourceGrantReviewSha256);
            Hash("source.fixtureSha256", source.FixtureSha256);
        }
        var runtime = input.Runtime;
        if (runtime is null) missing.Add("runtime");
        else
        {
            Text("runtime.privateRunnerResourceId", runtime.PrivateRunnerResourceId);
            Https("runtime.hostedProjectEndpoint", runtime.HostedProjectEndpoint);
            Text("runtime.hostedAgentVersion", runtime.HostedAgentVersion);
            Hash("runtime.hostedImageDigest", runtime.HostedImageDigest);
            Text("runtime.modelDeployment", runtime.ModelDeployment);
            Text("runtime.modelVersion", runtime.ModelVersion);
        }
        var controls = input.Controls;
        if (controls is null) missing.Add("controls");
        else
        {
            Hash("controls.resourcePlanSha256", controls.ResourcePlanSha256);
            Hash("controls.privatePathReviewSha256", controls.PrivatePathReviewSha256);
            Hash("controls.dnsAndEgressPlanSha256", controls.DnsAndEgressPlanSha256);
            Hash("controls.publicDenyPlanSha256", controls.PublicDenyPlanSha256);
            Hash("controls.dataHandlingAndRetentionSha256", controls.DataHandlingAndRetentionSha256);
            Hash("controls.budgetPlanSha256", controls.BudgetPlanSha256);
            Hash("controls.cleanupPlanSha256", controls.CleanupPlanSha256);
            Text("controls.runApprovalReference", controls.RunApprovalReference);
        }
        if (input.Limits is not { } limits) missing.Add("limits");
        else
        {
            Limit("limits.maxRequests", limits.MaxRequests);
            Limit("limits.maxDurationSeconds", limits.MaxDurationSeconds);
            Limit("limits.maxResponseBytes", limits.MaxResponseBytes);
            Limit("limits.cleanupTimeoutSeconds", limits.CleanupTimeoutSeconds);
        }
        return new(missing.Count == 0 ? "PreparedOffline" : "Blocked", Canonical.Hash(input),
            missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static FabricC1RequestPlan Discovery(FabricC1Inputs input)
    {
        var check = RequirePrepared(input, discoveryOnly: true);
        return new(check.BindingHash, "discovery", [
            Rpc("c1-initialize", "initialize", new { protocolVersion = ProtocolVersion, capabilities = new { },
                clientInfo = new { name = "cohors-fabric-c1", version = "1" } }),
            JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", method = "notifications/initialized" }),
            Rpc("c1-tools", "tools/list", new { })]);
    }

    public static FabricC1RequestPlan Call(FabricC1Inputs input, string fixtureJson, string probeId, JsonElement toolsResult)
    {
        var check = RequirePrepared(input);
        Guard.Against(Canonical.HashText(fixtureJson) != input.Source.FixtureSha256,
            "c1_fixture", "Synthetic fixture bytes differ from the bound input.", 409);
        using var fixture = JsonDocument.Parse(fixtureJson);
        Guard.Against(fixture.RootElement.GetProperty("contract").GetString() != "cohors-fabric-c1-synthetic-1" ||
            !fixture.RootElement.GetProperty("synthetic").GetBoolean() ||
            fixture.RootElement.GetProperty("release").GetString() != input.Source.ReleaseId,
            "c1_fixture", "Only the bound synthetic release may prepare a query.", 409);
        var probes = fixture.RootElement.GetProperty("probes").EnumerateArray().Where(p => p.GetProperty("id").GetString() == probeId).ToArray();
        Guard.Against(probes.Length != 1, "c1_probe", "Exactly one approved synthetic question is required.", 409);
        if (toolsResult.ValueKind != JsonValueKind.Object ||
            !toolsResult.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
            tools.GetArrayLength() != 1 || toolsResult.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
            throw new DomainException("c1_tools", "Unexpected discovery shape or pagination; do not silently select the first tool.", 409);
        var tool = tools[0];
        if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || name.GetString() != input.Target.ToolName ||
            !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object ||
            Canonical.Hash(schema) != input.Target.InputSchemaSha256)
            throw new DomainException("c1_tool_binding", "Tool name or input schema differs from the reviewed discovery.", 409);
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object" ||
            !schema.TryGetProperty("properties", out var propertiesObject) || propertiesObject.ValueKind != JsonValueKind.Object)
            throw new DomainException("c1_tool_binding", "The reviewed question schema must be an object.", 409);
        var properties = propertiesObject.EnumerateObject().ToArray();
        Guard.Against(properties.Length != 1 ||
            properties[0].Name != input.Target.QuestionArgument || properties[0].Value.GetProperty("type").GetString() != "string",
            "c1_tool_binding", "This preparation supports only the reviewed single-question data-agent tool.", 409);
        return new(check.BindingHash, probeId, [Rpc("c1-query", "tools/call",
            new { name = input.Target.ToolName, arguments = new Dictionary<string, string>
                { [input.Target.QuestionArgument!] = probes[0].GetProperty("question").GetString()! } })]);
    }

    public static FabricC1TaskAction TaskAction(FabricC1Inputs input, FabricC1RequestPlan originalCall,
        FabricC1TaskCheckpoint checkpoint, bool cancel)
    {
        var check = RequirePrepared(input);
        Guard.Against(originalCall.BindingHash != check.BindingHash || checkpoint.BindingHash != check.BindingHash ||
            originalCall.Messages.Length != 1 || originalCall.Messages[0].GetProperty("method").GetString() != "tools/call" ||
            checkpoint.CallHash != Canonical.Hash(originalCall.Messages[0]) ||
            string.IsNullOrWhiteSpace(checkpoint.TaskId) || checkpoint.TaskProtocolReview != input.Target.TaskProtocolReview,
            "c1_task_binding", "Original task, request, identity, release and reviewed dialect must remain bound; no new query.", 409);
        return new(cancel ? "tasks/cancel" : "tasks/get", checkpoint.TaskId, check.BindingHash, checkpoint.CallHash);
    }

    private static FabricC1Preflight RequirePrepared(FabricC1Inputs input, bool discoveryOnly = false)
    {
        var check = Preflight(input, discoveryOnly);
        Guard.Against(check.Status != "PreparedOffline", "c1_inputs_missing",
            "C1 preparation is blocked: " + string.Join(", ", check.MissingOrInvalid), 409);
        return check;
    }

    private static JsonElement Rpc(string id, string method, object parameters) =>
        JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id, method, @params = parameters }, Canonical.Json);
}
