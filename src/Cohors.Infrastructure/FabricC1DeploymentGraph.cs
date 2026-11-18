using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public enum FabricC1GraphOperation { OwnApplications, ApplicationCreate, UserRead, GroupRead, GroupCreate, GroupMembers }

public sealed record FabricC1GraphRights(bool Delegated, string[] Permissions)
{
    public string[] DirectoryRoleIds { get; init; } = [];
    public bool Allows(FabricC1GraphOperation operation)
    {
        bool Has(params string[] values) => values.Any(value => Permissions.Contains(value, StringComparer.Ordinal));
        var applicationAll = Has("Application.ReadWrite.All", "Directory.ReadWrite.All");
        return operation switch
        {
            FabricC1GraphOperation.OwnApplications => applicationAll || !Delegated && Has("Application.ReadWrite.OwnedBy"),
            FabricC1GraphOperation.ApplicationCreate => applicationAll || Has("AppRegistration.Create") ||
                !Delegated && Has("Application.ReadWrite.OwnedBy"),
            FabricC1GraphOperation.UserRead => Delegated && Has("User.Read", "User.ReadWrite", "User.Read.All",
                "User.ReadWrite.All", "Directory.Read.All", "Directory.ReadWrite.All"),
            FabricC1GraphOperation.GroupRead => Has("GroupMember.Read.All", "GroupMember.ReadWrite.All", "Group.Read.All",
                "Group.ReadWrite.All", "Directory.Read.All", "Directory.ReadWrite.All"),
            FabricC1GraphOperation.GroupCreate => Has("Group.ReadWrite.All", "Directory.ReadWrite.All") ||
                !Delegated && Has("Group.Create"),
            FabricC1GraphOperation.GroupMembers => applicationAll &&
                Has("GroupMember.ReadWrite.All", "Group.ReadWrite.All", "Directory.ReadWrite.All"),
            _ => false
        };
    }

    public void Require(FabricC1GraphOperation operation) =>
        Guard.Against(!Allows(operation), operation is FabricC1GraphOperation.GroupCreate or FabricC1GraphOperation.GroupRead or
            FabricC1GraphOperation.GroupMembers ? "c1_deploy_graph_group_rights" : "c1_deploy_graph_rights",
            $"The selected OAuth-permission path for {operation} is not bound by the token; no directory-role fallback, consent or role change is attempted.", 403);
}

public sealed record FabricC1IdentityPhaseResult(string Status, Dictionary<string, string> ApplicationIds,
    Dictionary<string, string> PrincipalIds, string? AllowlistGroupId, string[] MissingPermissions)
{
    public bool PaidResourcesCreated => false;
    public bool IsC1ServicePass => false;
}

public sealed partial class FabricC1Provisioner
{
    public static string[] IdentityRoles => ["runtime", "hosted-control", "bootstrap", "negative"];

    public async Task<FabricC1IdentityPhaseResult> EnsureIdentityPhase(FabricC1GraphRights rights, CancellationToken ct)
    {
        rights.Require(FabricC1GraphOperation.OwnApplications);
        var applications = new Dictionary<string, string>(StringComparer.Ordinal);
        var principals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in IdentityRoles)
        {
            var application = await EnsureApplicationForRole(role, ct);
            var principal = await EnsurePrincipalForRole(role, ct);
            applications.Add(role, Text(application, "appId"));
            principals.Add(role, Text(principal, "id"));
        }
        Guard.Against(applications.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != IdentityRoles.Length ||
            principals.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != IdentityRoles.Length ||
            applications.Values.Contains(input.BootstrapIdentity!.ClientId, StringComparer.OrdinalIgnoreCase) ||
            principals.Values.Contains(input.BootstrapIdentity.PrincipalObjectId, StringComparer.OrdinalIgnoreCase),
            "c1_deploy_identity_alias", "Generated roles must not alias each other or the existing deployer.", 409);

        var missing = new[] { FabricC1GraphOperation.GroupRead, FabricC1GraphOperation.GroupCreate, FabricC1GraphOperation.GroupMembers }
            .Where(operation => !rights.Allows(operation)).Select(operation => operation.ToString()).ToArray();
        if (missing.Length != 0)
            return new("AwaitingGroupPermission", applications, principals, null, missing);

        var group = await EnsureAllowlistGroup(ct, rights.Delegated && rights.DirectoryRoleIds.Length != 0);
        foreach (var role in new[] { "runtime", "bootstrap", "negative" })
            await EnsureAllowlistMember(role, ct);
        await VerifyAllowlistMembers(ct);
        return new("IdentitiesProvisioned", applications, principals, Text(group, "id"), []);
    }

    public Task<JsonObject> EnsureApplicationForRole(string role, CancellationToken ct)
    {
        RequireIdentityRole(role);
        var name = FabricC1Deployment.Name(input) + "-" + role;
        return EnsureCreated(role + "-app", "Application", "https://graph.microsoft.com/v1.0/applications", name,
            new JsonObject
            {
                ["displayName"] = name, ["signInAudience"] = "AzureADMyOrg", ["tags"] = new JsonArray(Marker),
                ["owners@odata.bind"] = new JsonArray($"https://graph.microsoft.com/v1.0/directoryObjects/{input.BootstrapIdentity!.PrincipalObjectId}")
            }, true, ct);
    }

    public Task<JsonObject> EnsurePrincipalForRole(string role, CancellationToken ct)
    {
        RequireIdentityRole(role);
        var appId = Text(Owned(role + "-app", "Application"), "appId");
        return EnsureCreated(role + "-sp", "ServicePrincipal", "https://graph.microsoft.com/v1.0/servicePrincipals", appId,
            new JsonObject { ["appId"] = appId, ["tags"] = new JsonArray(Marker) }, true, ct, "appId");
    }

    public Task<JsonObject> EnsureAllowlistGroup(CancellationToken ct, bool delegatedAdministrator = false)
    {
        var name = FabricC1Deployment.Name(input) + "-fabric-api";
        var body = new JsonObject
        {
            ["displayName"] = name, ["description"] = Marker, ["mailEnabled"] = false,
            ["mailNickname"] = name, ["securityEnabled"] = true, ["groupTypes"] = new JsonArray()
        };
        // Graph auto-owns a security group only for non-admin delegated users; apps/admins must bind the owner.
        if (input.BootstrapIdentity!.CredentialKind != "DelegatedAzureCli" || delegatedAdministrator)
            body["owners@odata.bind"] = new JsonArray($"https://graph.microsoft.com/v1.0/directoryObjects/{input.BootstrapIdentity.PrincipalObjectId}");
        return EnsureCreated("fabric-api-group", "SecurityGroup", "https://graph.microsoft.com/v1.0/groups", name, body, true, ct);
    }

    public async Task EnsureAllowlistMember(string role, CancellationToken ct)
    {
        Guard.Against(role is not ("runtime" or "bootstrap" or "negative"), "c1_deploy_group_scope",
            "The Hosted control caller is not a Fabric runtime/admin-API group member.", 409);
        var group = Text(Owned("fabric-api-group", "SecurityGroup"), "id");
        var principal = Text(Owned(role + "-sp", "ServicePrincipal"), "id");
        var key = "fabric-api-member-" + role;
        var uri = $"https://graph.microsoft.com/v1.0/groups/{group}/members/$ref";
        var body = new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{principal}" };
        var hash = Canonical.Hash(body);
        var members = await ReadAllowlistMembers(group, ct);
        var allowed = new[] { "runtime", "bootstrap", "negative" }.Select(memberRole => Text(Owned(memberRole + "-sp", "ServicePrincipal"), "id")).ToArray();
        Guard.Against(members.Any(id => !allowed.Contains(id, StringComparer.OrdinalIgnoreCase)),
            "c1_deploy_group_drift", "Foreign group members prevent additional grants.", 409);
        if (state.Objects.TryGetValue(key, out var known))
        {
            Guard.Against(known.Kind != "GroupMembership" || known.Id != principal || known.RequestHash != hash ||
                !members.Contains(principal, StringComparer.OrdinalIgnoreCase), "c1_deploy_group_drift",
                "A previously verified group membership changed; no regrant.", 409);
            return;
        }
        if (state.PendingCreates.TryGetValue(key, out var pending))
            Guard.Against(pending != hash, "c1_deploy_drift", "Pending group membership changed.", 409);
        if (members.Contains(principal, StringComparer.OrdinalIgnoreCase))
            Guard.Against(pending is null, "c1_deploy_foreign", "An unjournaled membership is not adopted for cleanup.", 409);
        else
        {
            Guard.Against(pending is not null, "c1_deploy_unknown", "Membership outcome is unknown; reconcile without repeating POST.", 409);
            Pending(key, hash);
            var response = await Send(HttpMethod.Post, new(uri), body, ct: ct);
            RecordDeniedRequest(key, hash, response.Status);
            RequireStatus(response, 204);
            members = await ReadAllowlistMembers(group, ct);
            Guard.Against(!members.Contains(principal, StringComparer.OrdinalIgnoreCase),
                "c1_deploy_group_readback", "Membership has not propagated; retain intent and resume read-only.", 409);
        }
        SaveObject(key, "GroupMembership", principal, hash, new() { ["id"] = principal, ["groupId"] = group });
    }

    public async Task VerifyAllowlistMembers(CancellationToken ct)
    {
        var group = Text(Owned("fabric-api-group", "SecurityGroup"), "id");
        var actual = await ReadAllowlistMembers(group, ct);
        var expected = new[] { "runtime", "bootstrap", "negative" }.Select(role => Text(Owned(role + "-sp", "ServicePrincipal"), "id")).ToArray();
        Guard.Against(actual.Length != expected.Length || !expected.All(id => actual.Contains(id, StringComparer.OrdinalIgnoreCase)),
            "c1_deploy_group_drift", "Allowlist has missing or additional members; no tenant policy change.", 409);
        SaveObject("fabric-api-members", "VerifiedGroupMembers", group, Canonical.Hash(expected),
            new() { ["id"] = group, ["members"] = new JsonArray(expected.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
    }

    private async Task<string[]> ReadAllowlistMembers(string group, CancellationToken ct)
    {
        // v1.0 list-members omits service principals; the documented group $expand workaround includes them.
        var response = await Read($"https://graph.microsoft.com/v1.0/groups/{group}?$select=id&$expand=members($select=id)", ct);
        SameId(Text(response, "id"), group);
        Guard.Against(response["members@odata.nextLink"] is not null || response["members"] is not JsonArray,
            "c1_deploy_group_readback", "Complete expanded group membership is required.", 409);
        return response["members"]!.AsArray().Select(member =>
        {
            if (member is not JsonObject item) throw new DomainException("c1_deploy_group_readback", "Invalid member.", 409);
            return GuidValue(Text(item, "id")).ToString("D");
        }).ToArray();
    }

    private static void RequireIdentityRole(string role) =>
        Guard.Against(!IdentityRoles.Contains(role, StringComparer.Ordinal), "c1_deploy_identity_role", "Unknown identity role.", 409);
}
