using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SourceAccessResult(JsonObject Readback)
{
    public string EvidenceLevel => "source-role-metadata-not-effective-access-revocation-or-c1-acceptance";
}

public sealed partial class FabricC1Provisioner
{
    private const string SourceAccessPlanKey = "source-access-definition";
    private const string SourceAccessRoleKey = "source-access-role";
    private const string SourceViewerKey = "runtime-source-grant";

    public async Task<FabricC1SourceAccessResult> EnsureReleasedSourceAccess(CancellationToken ct)
    {
        RequireSourceNotebookScope("released");
        PlanNormalRequests(0);
        RecordSourceMaterialization();
        Owned("materialization", "SourceMaterialization");
        var plan = await PrepareSourceNotebook("released", ct);
        var principal = Owned("runtime-sp", "ServicePrincipal");
        var application = Owned("runtime-app", "Application");
        RequireMarker(principal, true);
        RequireMarker(application, true);
        SameId(Text(principal, "appId"), Text(application, "appId"));
        var principalId = GuidValue(Text(principal, "id")).ToString("D");
        var workspace = Text(plan.Binding, "workspaceId");
        var lakehouse = Text(plan.Binding, "lakehouseId");
        var endpoint = $"https://{WorkspaceHost(workspace)}/v1/workspaces/{workspace}";
        var sqlEndpointId = await RequireClosedSourceItems(plan, ct);
        await RequireDeniedSourceMembership(principalId, ct);
        var assignments = await ReadAll(endpoint + "/roleAssignments", false, ct);
        RequireClosedSourceAssignments("released", principalId, assignments);
        RequireSourceViewerOwnership(principalId, assignments);
        var definition = SourceReadRole(plan, principalId);
        var roleName = Text(definition, "name");
        var binding = new JsonObject
        {
            ["contract"] = "cohors-fabric-c1-source-access-1", ["deploymentBindingHash"] = state.BindingHash,
            ["workspaceId"] = workspace, ["lakehouseId"] = lakehouse,
            ["notebookId"] = Text(Owned("source-notebook-released", "Notebook"), "id"),
            ["sqlEndpointId"] = sqlEndpointId, ["principalId"] = principalId,
            ["clientId"] = Text(application, "appId"), ["tenantId"] = input.TenantId,
            ["materializationSha256"] = state.Objects["materialization"].RequestHash, ["role"] = definition.DeepClone()
        };
        var bindingHash = Canonical.Hash(binding);
        if (state.Objects.TryGetValue(SourceAccessPlanKey, out var savedPlan))
            Guard.Against(savedPlan.Kind != "SourceAccessDefinition" || savedPlan.Id != lakehouse ||
                savedPlan.RequestHash != Canonical.Hash(savedPlan.Response) || savedPlan.RequestHash != bindingHash,
                "c1_source_access_binding", "The original source access target, identity and materialization cannot change.", 409);
        else SaveObject(SourceAccessPlanKey, "SourceAccessDefinition", lakehouse, bindingHash, binding);

        var collection = endpoint + "/items/" + lakehouse + "/dataAccessRoles";
        var roles = await ReadAll(collection, false, ct);
        RequireClosedSourceRoles(roles, definition, workspace, lakehouse);
        var existing = roles.SingleOrDefault(role => string.Equals(Text(role, "name"), roleName, StringComparison.OrdinalIgnoreCase));
        var requestHash = Canonical.Hash(definition);
        if (state.PendingCreates.TryGetValue(SourceAccessRoleKey, out var intent))
            Guard.Against(intent != requestHash, "c1_source_access_binding", "Original source role intent changed.", 409);
        if (state.Objects.TryGetValue(SourceAccessRoleKey, out var known))
        {
            RequireSourceAccessRoleReceipt(known, binding, requestHash);
            Guard.Against(existing is not null &&
                existing["eTag"]?.GetValue<string>() != Text(known.Response["role"]!.AsObject(), "eTag"),
                "c1_source_role_readback", "The previously recorded source role version changed.", 409);
        }
        if (existing is null)
        {
            Guard.Against(intent is not null || known is not null, "c1_deploy_unknown",
                "An unresolved or previously observed source role must not be recreated.", 409);
            Pending(SourceAccessRoleKey, requestHash);
            var created = await Send(HttpMethod.Post, new(collection + "?preview=true&dataAccessRoleConflictPolicy=Abort"), definition, ct: ct);
            RecordDeniedRequest(SourceAccessRoleKey, requestHash, created.Status);
            RequireStatus(created, 200, 201);
        }
        else Guard.Against(intent is null && known is null, "c1_deploy_foreign", "An unjournaled source role is not adopted or overwritten.", 409);
        var readback = await ReadAll(collection, false, ct);
        RequireClosedSourceRoles(readback, definition, workspace, lakehouse);
        var observed = readback.SingleOrDefault(role => string.Equals(Text(role, "name"), roleName, StringComparison.OrdinalIgnoreCase)) ??
            throw new DomainException("c1_source_role_readback", "The original exact source role is not yet observed.", 409);
        Guard.Against(string.IsNullOrWhiteSpace(observed["eTag"]?.GetValue<string>()), "c1_source_role_readback",
            "The original source role version is required to detect changed or replaced metadata.", 409);
        Guard.Against(known is not null &&
            Text(observed, "eTag") != Text(known.Response["role"]!.AsObject(), "eTag"),
            "c1_source_role_readback", "A replaced source role version is not adopted.", 409);
        SaveObject(SourceAccessRoleKey, "OneLakeSourceRole", roleName, requestHash, new()
        {
            ["bindingSha256"] = bindingHash, ["workspaceId"] = workspace, ["lakehouseId"] = lakehouse,
            ["role"] = observed.DeepClone()
        });
        await AssignRuntimeWorkspace("released", SourceViewerKey, "Viewer", ct,
            current => RequireClosedSourceAssignments("released", principalId, current));
        await RequireDeniedSourceMembership(principalId, ct);
        var finalRoles = await ReadAll(collection, false, ct);
        RequireClosedSourceRoles(finalRoles, definition, workspace, lakehouse);
        var finalRole = finalRoles.SingleOrDefault(role => string.Equals(Text(role, "name"), roleName, StringComparison.OrdinalIgnoreCase));
        Guard.Against(finalRole is null || finalRole["eTag"]?.GetValue<string>() != Text(observed, "eTag"),
            "c1_source_role_readback", "Source role changed while the Viewer prerequisite was being established.", 409);
        var result = new JsonObject
        {
            ["contract"] = "cohors-fabric-c1-source-access-readback-1", ["bindingSha256"] = bindingHash,
            ["role"] = observed.DeepClone(), ["workspaceAssignment"] = state.Objects[SourceViewerKey].Response.DeepClone(),
            ["observedAtUtc"] = SourceUtcText(time.GetUtcNow())
        };
        SaveObject("source-access-metadata", "SourceAccessMetadata", lakehouse, Canonical.Hash(result), result);
        return new((JsonObject)result.DeepClone());
    }

    private async Task<string> RequireClosedSourceItems(FabricC1SourceNotebookPlan plan, CancellationToken ct, bool cleanup = false)
    {
        var scope = Text(plan.Binding, "scope");
        var workspace = Text(plan.Binding, "workspaceId");
        var lakehouse = Text(plan.Binding, "lakehouseId");
        var endpoint = $"https://{WorkspaceHost(workspace)}/v1/workspaces/{workspace}";
        var current = await Read(endpoint + "/lakehouses/" + lakehouse, ct, cleanup);
        RequireMarker(current, false);
        SameId(Text(current, "id"), lakehouse);
        SameId(Text(current, "workspaceId"), workspace);
        var schema = plan.Binding["schemaName"]?.GetValue<string>();
        Guard.Against(Text(current, "type") != "Lakehouse" || Text(current, "displayName") != "C1_" + scope ||
            current["properties"]?["defaultSchema"]?.GetValue<string>() != schema ||
            current["properties"]?["sqlEndpointProperties"]?["provisioningStatus"]?.GetValue<string>() != "Success",
            "c1_source_access_inventory", "The source layout and its associated SQL endpoint must be ready and unchanged.", 409);
        var sqlEndpoint = GuidValue(Text(current["properties"]!["sqlEndpointProperties"]!.AsObject(), "id")).ToString("D");
        var notebook = GuidValue(Text(Owned("source-notebook-" + scope, "Notebook"), "id")).ToString("D");
        Guard.Against(new[] { lakehouse, sqlEndpoint, notebook }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3,
            "c1_source_access_inventory", "Source Lakehouse, notebook and associated SQL endpoint must be distinct.", 409);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [lakehouse] = "Lakehouse", [notebook] = "Notebook", [sqlEndpoint] = "SQLEndpoint" };
        var items = await ReadAll(endpoint + "/items?recursive=true", false, ct, cleanup);
        Guard.Against(items.Length != 3 || items.Select(item => GuidValue(Text(item, "id"))).Distinct().Count() != 3,
            "c1_source_access_inventory", "The dedicated synthetic source workspace must not contain mixed inventory.", 409);
        foreach (var item in items)
        {
            SameId(Text(item, "workspaceId"), workspace);
            var id = GuidValue(Text(item, "id")).ToString("D");
            Guard.Against(!expected.TryGetValue(id, out var kind) || Text(item, "type") != kind,
                "c1_source_access_inventory", "Unexpected or substituted item in the source workspace.", 409);
            if (id != sqlEndpoint) RequireMarker(item, false);
        }
        var tables = await ReadAll(endpoint + "/lakehouses/" + lakehouse + "/tables?maxResults=100", false, ct, cleanup, entryField: "data");
        Guard.Against(tables.Length != 1, "c1_source_access_inventory", "Only the original synthetic source table may be present.", 409);
        var table = tables[0];
        var tablePath = "Tables/" + (schema is null ? "" : schema + "/") + SourceTableName;
        var name = Text(table, "name");
        Guard.Against(name != SourceTableName && name != (schema is null ? SourceTableName : schema + "." + SourceTableName) ||
            Text(table, "type") != "Managed" || !string.Equals(Text(table, "format"), "Delta", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(Text(table, "location"), UriKind.Absolute, out var location) || location.Scheme != "abfss" ||
            location.Host != "onelake.dfs.fabric.microsoft.com" || !location.IsDefaultPort ||
            location.Query.Length != 0 || location.Fragment.Length != 0 ||
            !Guid.TryParse(location.UserInfo, out var locationWorkspace) || locationWorkspace != GuidValue(workspace) ||
            location.AbsolutePath != "/" + lakehouse + "/" + tablePath,
            "c1_source_access_inventory", "Table metadata must point to the exact original table; no shortcut or foreign storage.", 409);
        return sqlEndpoint;
    }

    private async Task RequireDeniedSourceMembership(string principalId, CancellationToken ct)
    {
        var denied = Text(Owned("workspace-denied", "Workspace"), "id");
        var current = await ReadAll($"https://{WorkspaceHost(denied)}/v1/workspaces/{denied}/roleAssignments", false, ct);
        RequireClosedSourceAssignments("denied", principalId, current);
    }

    private void RequireClosedSourceAssignments(string scope, string principalId, JsonObject[] assignments)
    {
        var originalUser = GuidValue(input.BootstrapIdentity!.PrincipalObjectId!);
        Guard.Against(assignments.Length is < 1 or > 2 ||
            assignments.Select(assignment => GuidValue(Text(assignment, "id"))).Distinct().Count() != assignments.Length,
            "c1_source_workspace_roles", "Source membership must remain closed; no unknown or duplicate assignments.", 409);
        var originalAdmins = 0;
        var runtimes = 0;
        foreach (var assignment in assignments)
        {
            if (assignment["principal"] is not JsonObject principal)
                throw new DomainException("c1_source_workspace_roles", "Assignment principal is missing.", 409);
            var id = GuidValue(Text(principal, "id"));
            if (id == originalUser && Text(principal, "type") == "User" && Text(assignment, "role") == "Admin") originalAdmins++;
            else if (scope == "released" && id == GuidValue(principalId) &&
                Text(principal, "type") == "ServicePrincipal" && Text(assignment, "role") == "Viewer") runtimes++;
            else throw new DomainException("c1_source_workspace_roles",
                "No source Contributor, shared group, foreign principal or runtime access to the deny workspace is accepted.", 409);
        }
        Guard.Against(originalAdmins != 1 || runtimes > 1, "c1_source_workspace_roles", "The original source administrator must remain present.", 409);
    }

    private void RequireSourceViewerOwnership(string principalId, JsonObject[] assignments, bool allowMissing = false)
    {
        var hash = Canonical.Hash(RuntimeWorkspaceGrantBody(Text(Owned("runtime-sp", "ServicePrincipal"), "id"), "Viewer"));
        if (state.PendingCreates.TryGetValue(SourceViewerKey, out var pending))
            Guard.Against(pending != hash, "c1_source_access_binding", "Original source Viewer intent changed.", 409);
        var actual = assignments.SingleOrDefault(assignment =>
            GuidValue(Text(assignment["principal"]!.AsObject(), "id")) == GuidValue(principalId));
        if (state.Objects.TryGetValue(SourceViewerKey, out var original))
            Guard.Against(original.Kind != "WorkspaceGrant" || original.RequestHash != hash ||
                actual is null && !allowMissing || actual is not null && GuidValue(Text(actual, "id")) != GuidValue(original.Id),
                "c1_source_access_binding", "The original source Viewer assignment was removed or replaced.", 409);
        else if (actual is not null)
            Guard.Against(pending is null, "c1_deploy_foreign", "An unjournaled source Viewer is not adopted before granting data access.", 409);
        else Guard.Against(pending is not null && !allowMissing, "c1_deploy_unknown", "A missing unresolved Viewer assignment is not replayed.", 409);
    }

    private JsonObject SourceReadRole(FabricC1SourceNotebookPlan plan, string principalId)
    {
        var schema = plan.Binding["schemaName"]?.GetValue<string>();
        return new()
        {
            ["name"] = "CohorsC1Source" + GuidValue(input.DeploymentId!).ToString("N"), ["kind"] = "Policy",
            ["decisionRules"] = SourceDecision("Tables/" + (schema is null ? "" : schema + "/") + SourceTableName),
            ["members"] = new JsonObject { ["microsoftEntraMembers"] = new JsonArray(new JsonObject
            { ["objectId"] = principalId, ["tenantId"] = input.TenantId }) }
        };
    }

    private static JsonArray SourceDecision(string path) => new(new JsonObject
    {
        ["effect"] = "Permit", ["permission"] = new JsonArray(
            new JsonObject { ["attributeName"] = "Path", ["attributeValueIncludedIn"] = new JsonArray(path) },
            new JsonObject { ["attributeName"] = "Action", ["attributeValueIncludedIn"] = new JsonArray("Read") })
    });

    private static void RequireClosedSourceRoles(JsonObject[] roles, JsonObject definition, string workspace, string lakehouse)
    {
        Guard.Against(roles.Length > 2 || roles.Select(role => Text(role, "name")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roles.Length,
            "c1_source_role_scope", "Unexpected or duplicate data access roles; never replace the whole role collection.", 409);
        var defaultReader = new JsonObject
        {
            ["name"] = "DefaultReader", ["kind"] = "Policy", ["decisionRules"] = SourceDecision("*"),
            ["members"] = new JsonObject { ["fabricItemMembers"] = new JsonArray(new JsonObject
            { ["itemAccess"] = new JsonArray("ReadAll"), ["sourcePath"] = workspace + "/" + lakehouse }) }
        };
        foreach (var role in roles)
        {
            var expected = string.Equals(Text(role, "name"), "DefaultReader", StringComparison.OrdinalIgnoreCase) ? defaultReader : definition;
            Guard.Against(Canonical.Hash(NormalizeSourceRole(role)) != Canonical.Hash(NormalizeSourceRole(expected)),
                "c1_source_role_scope", "Only the unchanged default reader and the exact own table-read role are accepted.", 409);
        }
    }

    private static JsonObject NormalizeSourceRole(JsonObject value)
    {
        var role = (JsonObject)value.DeepClone();
        role.Remove("id");
        role.Remove("eTag");
        role["name"] = Text(role, "name").ToLowerInvariant();
        role["kind"] ??= "Policy";
        if (role["members"] is not JsonObject members)
            throw new DomainException("c1_source_role_scope", "Explicit source role membership is required.", 409);
        members["microsoftEntraMembers"] ??= new JsonArray();
        members["fabricItemMembers"] ??= new JsonArray();
        if (members["microsoftEntraMembers"] is not JsonArray entra || members["fabricItemMembers"] is not JsonArray fabric)
            throw new DomainException("c1_source_role_scope", "Source role membership must use the documented arrays.", 409);
        foreach (var member in entra)
        {
            if (member is not JsonObject identity) throw new DomainException("c1_source_role_scope", "Invalid source member.", 409);
            identity["objectId"] = GuidValue(Text(identity, "objectId")).ToString("D");
            identity["tenantId"] = GuidValue(Text(identity, "tenantId")).ToString("D");
        }
        foreach (var member in fabric)
        {
            if (member is not JsonObject identity) throw new DomainException("c1_source_role_scope", "Invalid Fabric member.", 409);
            var path = Text(identity, "sourcePath").Split('/');
            Guard.Against(path.Length != 2, "c1_source_role_scope", "Fabric membership must bind the exact workspace and item.", 409);
            identity["sourcePath"] = GuidValue(path[0]).ToString("D") + "/" + GuidValue(path[1]).ToString("D");
        }
        if (role["decisionRules"] is not JsonArray { Count: 1 } rules || rules[0] is not JsonObject rule ||
            rule["permission"] is not JsonArray { Count: 2 } permissions ||
            permissions.Any(permission => permission is not JsonObject))
            throw new DomainException("c1_source_role_scope", "Only one exact table-read rule is accepted.", 409);
        if (rule["constraints"] is null || rule["constraints"] is JsonObject { Count: 0 }) rule.Remove("constraints");
        rule["permission"] = new JsonArray(permissions.Select(permission => (JsonObject)permission!)
            .OrderBy(permission => Text(permission, "attributeName"), StringComparer.Ordinal).Select(permission => permission.DeepClone()).ToArray());
        return role;
    }

    private static void RequireSourceAccessRoleReceipt(FabricC1ProvisionedObject role, JsonObject binding, string requestHash)
    {
        var expected = binding["role"]!.AsObject();
        Guard.Against(role.Kind != "OneLakeSourceRole" || role.Id != Text(expected, "name") || role.RequestHash != requestHash ||
            Text(role.Response, "bindingSha256") != Canonical.Hash(binding) ||
            Text(role.Response, "workspaceId") != Text(binding, "workspaceId") ||
            Text(role.Response, "lakehouseId") != Text(binding, "lakehouseId") ||
            role.Response["role"] is not JsonObject observed ||
            Canonical.Hash(NormalizeSourceRole(observed)) != Canonical.Hash(NormalizeSourceRole(expected)),
            "c1_source_access_binding", "Original source role receipt belongs to another request or scope.", 409);
    }
}
