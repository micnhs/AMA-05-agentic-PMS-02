using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1IdentityCleanupResult(string Status, string[] AbsentIdentityKeys,
    string[] UnresolvedIdentityKeys, string[] RetainedAdministrativeEffects)
{
    public bool ActiveIdentityInventoryAbsent => UnresolvedIdentityKeys.Length == 0;
    public bool CompleteC1Cleanup => false;
    public string Scope => "identity-inventory-only-not-paid-resource-or-retention-purge";
}

public sealed partial class FabricC1Provisioner
{
    private sealed record IdentityCleanupTarget(string Key, string Kind, string Collection, string Name,
        JsonObject CreationBody, string FilterProperty = "displayName");

    private (FabricC1DeploymentJournal Next, DateTimeOffset Deadline) PlanCleanupRequests(int requests)
    {
        RequireCertainCheckpointJournal();
        Guard.Against(!FabricC1Deployment.ValidCleanupLimits(input), "c1_deploy_cleanup_limits", "Cleanup reserve is not bound.", 409);
        var now = time.GetUtcNow();
        var started = state.CleanupStartedAtUtc ?? now;
        var deadline = started.AddSeconds(input.Cleanup!.MaxDurationSeconds!.Value);
        if (input.Cleanup.VerificationDeadlineUtc!.Value < deadline) deadline = input.Cleanup.VerificationDeadlineUtc.Value;
        Guard.Against(requests < 0 || (long)state.Requests + PrepaidCheckpointRequests() + requests > input.MaxControlRequests ||
            (long)state.CleanupRequests + requests > input.Cleanup.ReservedRequests || now < started || now >= deadline,
            "c1_deploy_cleanup_budget", "Persisted cleanup reserve/deadline exhausted; no reset or request.", 409);
        return (state with
        {
            Requests = state.Requests + requests, CleanupRequests = state.CleanupRequests + requests, CleanupStartedAtUtc = started
        }, deadline);
    }

    public async Task<FabricC1IdentityCleanupResult> CleanupIdentityPhase(CancellationToken ct)
    {
        Guard.Against(!FabricC1Deployment.ValidCleanupLimits(input), "c1_deploy_cleanup_limits", "Cleanup reserve is not bound.", 409);
        RequireIdentityCleanupScope();
        if (state.CleanupStartedAtUtc is null)
        {
            var fenced = state with { CleanupStartedAtUtc = time.GetUtcNow() };
            SaveJournal(fenced);
            state = fenced;
        }

        foreach (var role in CleanupIdentityRoles)
        {
            var application = ApplicationCleanupTarget(role);
            await ReconcileIdentityForCleanup(application, ct);
            if (state.PendingCreates.ContainsKey(role + "-sp") && !state.Objects.ContainsKey(role + "-app"))
                throw new DomainException("c1_deploy_cleanup_journal", "Principal creation has no bound application receipt.", 409);
            if (state.Objects.ContainsKey(role + "-app"))
                await ReconcileIdentityForCleanup(PrincipalCleanupTarget(role), ct);
        }
        var group = GroupCleanupTarget();
        await ReconcileIdentityForCleanup(group, ct);
        if (state.PendingCreates.ContainsKey(group.Key) || !await DeleteKnownIdentity(group, ct)) return IdentityCleanupResult();
        foreach (var role in CleanupIdentityRoles)
            if (state.Objects.ContainsKey(role + "-sp") && !await DeleteKnownIdentity(PrincipalCleanupTarget(role), ct))
                return IdentityCleanupResult();
        foreach (var role in CleanupIdentityRoles)
            if (state.Objects.ContainsKey(role + "-app") && !await DeleteKnownIdentity(ApplicationCleanupTarget(role), ct))
                return IdentityCleanupResult();
        return IdentityCleanupResult();
    }

    private void RequireIdentityCleanupScope()
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["fabric-api-group"] = "SecurityGroup", ["fabric-api-members"] = "VerifiedGroupMembers" };
        foreach (var role in CleanupIdentityRoles)
        {
            kinds.Add(role + "-app", "Application");
            kinds.Add(role + "-sp", "ServicePrincipal");
        }
        foreach (var role in new[] { "runtime", "bootstrap", "negative" }) kinds.Add("fabric-api-member-" + role, "GroupMembership");
        if (input.Network?.OperatorAccess is not null)
        {
            kinds.Add("operator-vpn-configuration", "OperatorVpnConfiguration");
            foreach (var key in OperatorVpnAssignmentKeys) kinds.Add(key, "OperatorVpnAssignment");
        }
        var administration = new HashSet<string>(StringComparer.Ordinal)
        {
            "TenantSettingsReview", "ApprovedTenantPlan", "ScopedTenantSetting",
            "SharedTenantEnableIntent", "SharedTenantSettingEffect"
        };
        Guard.Against(state.PendingOperations.Count != 0 ||
            state.Objects.Any(pair => !kinds.ContainsKey(pair.Key) &&
                !(pair.Key.StartsWith("tenant-", StringComparison.Ordinal) && administration.Contains(pair.Value.Kind))) ||
            state.PendingCreates.Keys.Any(key => !(kinds.ContainsKey(key) && key != "fabric-api-members") &&
                !key.StartsWith("tenant-change-", StringComparison.Ordinal) && key != "tenant-shared-inbound-enable"),
            "c1_deploy_cleanup_dependents", "Paid/workspace/Hosted or unknown consumers require their ordered cleanup before identity deletion.", 409);
        Guard.Against(state.Objects.Any(pair => kinds.TryGetValue(pair.Key, out var kind) && pair.Value.Kind != kind),
            "c1_deploy_cleanup_journal", "Identity inventory kind differs.", 409);
        Guard.Against((state.Objects.ContainsKey("fabric-api-members") ||
            new[] { "runtime", "bootstrap", "negative" }.Any(role =>
                state.PendingCreates.ContainsKey("fabric-api-member-" + role) || state.Objects.ContainsKey("fabric-api-member-" + role))) &&
            !state.Objects.ContainsKey("fabric-api-group"),
            "c1_deploy_cleanup_journal", "Pending membership has no bound group receipt.", 409);
        Guard.Against(CleanupIdentityRoles.Any(role => state.Objects.ContainsKey(role + "-sp") && !state.Objects.ContainsKey(role + "-app")),
            "c1_deploy_cleanup_journal", "Principal inventory has no parent application receipt.", 409);
        Guard.Against(OperatorVpnAssignmentKeys.Any(key => state.Objects.ContainsKey(key) || state.PendingCreates.ContainsKey(key)) &&
            !state.Objects.ContainsKey(FabricC1Deployment.VpnIdentityRole + "-sp") ||
            (state.Objects.ContainsKey("operator-vpn-configuration") || state.PendingCreates.ContainsKey("operator-vpn-configuration")) &&
            !state.Objects.ContainsKey(FabricC1Deployment.VpnIdentityRole + "-app"),
            "c1_deploy_cleanup_journal", "VPN configuration or assignment has no original parent receipt.", 409);
    }

    private IdentityCleanupTarget ApplicationCleanupTarget(string role) => new(role + "-app", "Application",
        "https://graph.microsoft.com/v1.0/applications", FabricC1Deployment.Name(input) + "-" + role, ApplicationBody(role));

    private IdentityCleanupTarget PrincipalCleanupTarget(string role)
    {
        var appId = Text(Owned(role + "-app", "Application"), "appId");
        return new(role + "-sp", "ServicePrincipal", "https://graph.microsoft.com/v1.0/servicePrincipals", appId,
            PrincipalBody(role, appId), "appId");
    }

    private IdentityCleanupTarget GroupCleanupTarget() => new("fabric-api-group", "SecurityGroup",
        "https://graph.microsoft.com/v1.0/groups", FabricC1Deployment.Name(input) + "-fabric-api", AllowlistGroupBody());

    private async Task ReconcileIdentityForCleanup(IdentityCleanupTarget target, CancellationToken ct)
    {
        if (!state.PendingCreates.TryGetValue(target.Key, out var intent)) return;
        Guard.Against(state.Objects.ContainsKey(target.Key) || intent != Canonical.Hash(target.CreationBody),
            "c1_deploy_cleanup_journal", "Pending identity create differs from its original bound request.", 409);
        try
        {
            await EnsureCreated(target.Key, target.Kind, target.Collection, target.Name, target.CreationBody, true, ct,
                target.FilterProperty, reconcileForCleanup: true);
        }
        catch (DomainException error) when (error.Code == "c1_deploy_unknown")
        {
            // An empty eventual-consistency read does not prove a lost create never happened.
            // Keep the intent visible in the result; no replacement create and no claimed absence.
        }
    }

    private async Task<bool> DeleteKnownIdentity(IdentityCleanupTarget target, CancellationToken ct)
    {
        if (!state.Objects.TryGetValue(target.Key, out var known)) return true;
        Guard.Against(known.Kind != target.Kind, "c1_deploy_cleanup_journal", "Cleanup target kind differs.", 409);
        var id = GuidValue(known.Id).ToString("D");
        Guard.Against(GuidValue(id) == GuidValue(input.BootstrapIdentity!.PrincipalObjectId!) ||
            Guid.TryParse(known.Response["appId"]?.GetValue<string>(), out var appId) && appId == GuidValue(input.BootstrapIdentity.ClientId!),
            "c1_deploy_cleanup_actor", "The initial deployer is never a cleanup target.", 409);
        var uri = new Uri(target.Collection + "/" + id);
        var hash = Canonical.Hash(new { Method = "DELETE", Uri = uri.AbsoluteUri, known.RequestHash, ObjectId = id });
        if (state.PendingDeletes.TryGetValue(target.Key, out var pending))
            Guard.Against(pending != hash, "c1_deploy_cleanup_journal", "Pending deletion binding differs.", 409);
        if (state.CleanupReceipts.TryGetValue(target.Key, out var receipt))
            Guard.Against(receipt.Kind != "ActiveDirectoryObjectAbsent" || GuidValue(receipt.Id) != GuidValue(id) || receipt.RequestHash != hash,
                "c1_deploy_cleanup_journal", "Absence receipt belongs to a different object/request.", 409);
        var current = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        if (current.Status == 404)
        {
            RecordIdentityAbsence(target, known, hash);
            return true;
        }
        RequireStatus(current, 200);
        SameId(Text(current.Body, "id"), id);
        RequireMarker(current.Body, target.Kind != "SecurityGroup");
        Guard.Against(receipt is not null, "c1_deploy_cleanup_restored", "A previously absent object reappeared; do not delete a restored object automatically.", 409);
        if (target.Kind is "Application" or "ServicePrincipal")
            SameId(Text(current.Body, "appId"), Text(known.Response, "appId"));
        if (pending is not null) return false;
        await VerifyGraphBinding(target.Kind, target.Collection, current.Body, target.CreationBody, ct, cleanup: true);
        if (target.Kind == "ServicePrincipal")
        {
            var role = target.Key[..^3];
            var application = state.Objects[role + "-app"];
            var parent = await Send(HttpMethod.Get, new("https://graph.microsoft.com/v1.0/applications/" + GuidValue(application.Id)),
                ct: ct, cleanup: true);
            if (parent.Status == 200)
            {
                SameId(Text(parent.Body, "id"), application.Id);
                SameId(Text(parent.Body, "appId"), Text(current.Body, "appId"));
                RequireMarker(parent.Body, true);
                await VerifyGraphBinding("Application", "https://graph.microsoft.com/v1.0/applications", parent.Body, ApplicationBody(role), ct, cleanup: true);
            }
            else RequireStatus(parent, 404);
        }
        if (target.Kind == "SecurityGroup")
        {
            var members = await ReadAllowlistMembers(id, ct, cleanup: true);
            var allowed = new[] { "runtime", "bootstrap", "negative" }
                .Where(role => state.Objects.ContainsKey(role + "-sp"))
                .Select(role => state.Objects[role + "-sp"].Id).ToArray();
            Guard.Against(members.Any(member => !allowed.Contains(member, StringComparer.OrdinalIgnoreCase)),
                "c1_deploy_cleanup_members", "The owned group contains another principal; do not delete it.", 409);
        }
        var intended = state with { PendingDeletes = new(state.PendingDeletes, StringComparer.Ordinal) { [target.Key] = hash } };
        SaveJournal(intended);
        state = intended;
        var deleted = await Send(HttpMethod.Delete, uri, ct: ct, cleanup: true);
        if (deleted.Status is 401 or 403)
        {
            var deletes = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
            deletes.Remove(target.Key);
            var denied = state with
            {
                PendingDeletes = deletes,
                DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal) { ["cleanup-" + target.Key] = new(hash, deleted.Status) }
            };
            SaveJournal(denied);
            state = denied;
        }
        RequireStatus(deleted, 204, 404);
        var readback = await Send(HttpMethod.Get, uri, ct: ct, cleanup: true);
        if (readback.Status == 200)
        {
            SameId(Text(readback.Body, "id"), id);
            RequireMarker(readback.Body, target.Kind != "SecurityGroup");
            return false;
        }
        RequireStatus(readback, 404);
        RecordIdentityAbsence(target, known, hash);
        return true;
    }

    private void RecordIdentityAbsence(IdentityCleanupTarget target, FabricC1ProvisionedObject known, string hash)
    {
        var deletes = new Dictionary<string, string>(state.PendingDeletes, StringComparer.Ordinal);
        deletes.Remove(target.Key);
        var creates = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        if (target.Kind == "SecurityGroup")
            foreach (var role in new[] { "runtime", "bootstrap", "negative" }) creates.Remove("fabric-api-member-" + role);
        if (target.Key == FabricC1Deployment.VpnIdentityRole + "-sp")
            foreach (var key in OperatorVpnAssignmentKeys) creates.Remove(key);
        if (target.Key == FabricC1Deployment.VpnIdentityRole + "-app") creates.Remove("operator-vpn-configuration");
        var next = state with
        {
            PendingDeletes = deletes, PendingCreates = creates,
            CleanupReceipts = new(state.CleanupReceipts, StringComparer.Ordinal)
            {
                [target.Key] = new("ActiveDirectoryObjectAbsent", known.Id, hash, new()
                {
                    ["status"] = "ActiveEndpointNotFound", ["observedAtUtc"] = time.GetUtcNow().ToString("O"),
                    ["providerRetentionPurged"] = false
                })
            }
        };
        SaveJournal(next);
        state = next;
    }

    private FabricC1IdentityCleanupResult IdentityCleanupResult()
    {
        var keys = CleanupIdentityRoles.SelectMany(role => new[] { role + "-app", role + "-sp" }).Append("fabric-api-group").ToArray();
        var absent = keys.Where(key => state.CleanupReceipts.ContainsKey(key)).ToArray();
        var unresolved = keys.Where(key => state.PendingCreates.ContainsKey(key) ||
            state.Objects.ContainsKey(key) && !state.CleanupReceipts.ContainsKey(key)).ToList();
        unresolved.AddRange(state.PendingCreates.Keys.Where(key => key.StartsWith("fabric-api-member-", StringComparison.Ordinal)));
        unresolved.AddRange(state.PendingCreates.Keys.Where(key => key == "operator-vpn-configuration" ||
            OperatorVpnAssignmentKeys.Contains(key, StringComparer.Ordinal)));
        var retained = state.Objects.Keys.Concat(state.PendingCreates.Keys)
            .Where(key => key.StartsWith("tenant-", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(unresolved.Count == 0 ? "IdentityInventoryAbsent" : "IdentityCleanupPendingReconciliation",
            absent, unresolved.ToArray(), retained);
    }
}
