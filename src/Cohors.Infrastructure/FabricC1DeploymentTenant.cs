using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1TenantChange(string SettingName, string BeforeHash, JsonObject Request);
public sealed record FabricC1TenantApproval(string Contract, string DeploymentBindingHash, string ChangeHash,
    string AdministrativeConcurrencyReviewSha256, DateTimeOffset ExpiresAtUtc)
{
    public void Validate(FabricC1DeploymentInputs input, DateTimeOffset now) =>
        Guard.Against(Contract != "cohors-fabric-c1-scoped-admin-approval-1" ||
            DeploymentBindingHash != Canonical.Hash(input) || !FabricC1Execution.Hex(ChangeHash, 64) ||
            !FabricC1Execution.Hex(AdministrativeConcurrencyReviewSha256, 64) ||
            ExpiresAtUtc <= now || ExpiresAtUtc > input.ExpiresAtUtc,
            "c1_deploy_admin_approval", "A current deployment-bound scoped administrative approval is required.", 409);
}
public sealed record FabricC1TenantReview(string Status, string GroupId, FabricC1TenantChange[] Changes,
    string[] Blockers, string[] ExistingTenantWideSettings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FabricC1SharedInboundReview? SharedInbound { get; init; }
    public string ChangeHash => SharedInbound is null
        ? Canonical.Hash(new { GroupId, Changes, Blockers, ExistingTenantWideSettings })
        : Canonical.Hash(new { GroupId, Changes, Blockers, ExistingTenantWideSettings, SharedInbound });
    public bool SelectedRequirementsObserved => Status == "ScopedSettingsObserved";
    public bool CanPrepareEmptyFoundation => Changes.Length == 0 &&
        Blockers.All(blocker => blocker == FabricC1Provisioner.SharedInboundApprovalBlocker);
}

public sealed partial class FabricC1Provisioner
{
    private const string TenantSettingsUri = "https://api.fabric.microsoft.com/v1/admin/tenantsettings";

    public async Task<FabricC1TenantReview> ReviewTenantSettings(CancellationToken ct)
    {
        var group = Owned("fabric-api-group", "SecurityGroup");
        Owned("fabric-api-members", "VerifiedGroupMembers");
        var groupId = Text(group, "id");
        if (input.RequiredTenantSettingNames.Length == 0)
            return new("AwaitingScopedAdminApproval", groupId, [], ["required-tenant-settings-not-selected"], []);
        var settings = await ReadAll(TenantSettingsUri, false, ct);
        var result = PlanTenantSettings(settings, input.RequiredTenantSettingNames, groupId, Text(group, "displayName"));
        SaveObject("tenant-settings-review", "TenantSettingsReview", groupId, result.ChangeHash,
            System.Text.Json.JsonSerializer.SerializeToNode(result, Canonical.Json)!.AsObject());
        return result;
    }

    public static FabricC1TenantReview PlanTenantSettings(JsonObject[] settings, string[] requiredNames,
        string groupId, string groupName)
    {
        GuidValue(groupId);
        var changes = new List<FabricC1TenantChange>();
        var blockers = new List<string>();
        var tenantWide = new List<string>();
        FabricC1SharedInboundReview? sharedInbound = null;
        var names = requiredNames.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != requiredNames.Length || names.Length == 0) blockers.Add("required-tenant-settings-not-unique");
        foreach (var setting in settings.Where(IsAdministrative))
            if (ContainsGroup(setting, "enabledSecurityGroups", groupId))
                blockers.Add("runtime-group-has-admin-api-scope:" + Text(setting, "settingName"));
        foreach (var name in names)
        {
            var matches = settings.Where(setting => setting["settingName"]?.GetValue<string>() == name).ToArray();
            if (matches.Length != 1) { blockers.Add("setting-not-uniquely-observed:" + name); continue; }
            var setting = matches[0];
            if (IsAdministrative(setting))
            { blockers.Add("runtime-group-cannot-receive-admin-api-scope:" + name); continue; }
            if (string.IsNullOrWhiteSpace(setting["tenantSettingGroup"]?.GetValue<string>()))
            { blockers.Add("setting-category-not-observed:" + name); continue; }
            if (ContainsGroup(setting, "excludedSecurityGroups", groupId))
            { blockers.Add("generated-group-explicitly-excluded:" + name); continue; }
            var enabled = setting["enabled"]?.GetValue<bool>();
            if (enabled is null) { blockers.Add("setting-enabled-state-missing:" + name); continue; }
            if (name == WorkspaceInboundSettingName)
            {
                if (!IsSharedInboundSetting(setting)) { blockers.Add("shared-inbound-shape-differs"); continue; }
                sharedInbound = DescribeSharedInbound(setting);
                if (enabled == true) tenantWide.Add(name);
                else blockers.Add(SharedInboundApprovalBlocker);
                continue;
            }
            var allowed = setting["enabledSecurityGroups"] as JsonArray ?? new JsonArray();
            if (enabled == true && (allowed.Count == 0 || ContainsGroup(setting, "enabledSecurityGroups", groupId)))
            {
                if (allowed.Count == 0) tenantWide.Add(name);
                continue;
            }
            if (setting["canSpecifySecurityGroups"]?.GetValue<bool>() != true)
            { blockers.Add("setting-cannot-be-scoped:" + name); continue; }
            if (enabled == false && allowed.Count != 0)
            { blockers.Add("enabling-would-also-reactivate-other-groups:" + name); continue; }
            var body = UpdateBody(setting);
            body["enabled"] = true;
            var groups = (JsonArray)allowed.DeepClone();
            if (!ContainsGroup(setting, "enabledSecurityGroups", groupId))
                groups.Add(new JsonObject { ["graphId"] = groupId, ["name"] = groupName });
            body["enabledSecurityGroups"] = groups;
            changes.Add(new(name, Canonical.Hash(setting), body));
        }
        var status = blockers.Count == 0 && changes.Count == 0 ? "ScopedSettingsObserved" :
            changes.Count == 0 && blockers.All(blocker => blocker == SharedInboundApprovalBlocker) ?
                "AwaitingSharedInboundApproval" : "AwaitingScopedAdminApproval";
        return new(status, groupId, changes.ToArray(), blockers.ToArray(), tenantWide.ToArray()) { SharedInbound = sharedInbound };
    }

    public async Task<FabricC1TenantReview> ApplyScopedTenantSettings(string approvedChangeHash,
        string administrativeConcurrencyReviewSha256, CancellationToken ct)
    {
        Guard.Against(!FabricC1Execution.Hex(approvedChangeHash, 64) ||
            !FabricC1Execution.Hex(administrativeConcurrencyReviewSha256, 64),
            "c1_deploy_admin_approval", "Exact change approval and exclusive administrative change-window review are required.", 409);
        await VerifyAllowlistMembers(ct);
        var approvalKey = "tenant-approved-" + approvedChangeHash;
        FabricC1TenantReview review;
        if (state.Objects.TryGetValue(approvalKey, out var saved))
        {
            Guard.Against(saved.Kind != "ApprovedTenantPlan" || saved.RequestHash != approvedChangeHash,
                "c1_deploy_admin_approval", "Recorded administrative approval differs.", 409);
            review = saved.Response["review"]?.Deserialize<FabricC1TenantReview>(Canonical.Json) ??
                throw new DomainException("c1_deploy_admin_approval", "Recorded administrative plan is missing.", 409);
            Guard.Against(saved.Response["administrativeConcurrencyReviewSha256"]?.GetValue<string>() != administrativeConcurrencyReviewSha256,
                "c1_deploy_admin_approval", "Administrative execution-window review changed.", 409);
        }
        else review = await ReviewTenantSettings(ct);
        Guard.Against(review.ChangeHash != approvedChangeHash ||
            review.Blockers.Any(blocker => blocker != SharedInboundApprovalBlocker) ||
            review.Changes.Any(change => change.SettingName == WorkspaceInboundSettingName),
            "c1_deploy_admin_approval", "The current scoped change differs from the specifically approved request.", 409);
        if (saved is null)
            SaveObject(approvalKey, "ApprovedTenantPlan", review.GroupId, approvedChangeHash, new()
            {
                ["review"] = System.Text.Json.JsonSerializer.SerializeToNode(review, Canonical.Json),
                ["administrativeConcurrencyReviewSha256"] = administrativeConcurrencyReviewSha256
            });
        foreach (var change in review.Changes)
        {
            var key = "tenant-change-" + change.SettingName;
            var hash = Canonical.Hash(change);
            var current = await ReadAll(TenantSettingsUri, false, ct);
            var exact = current.Where(setting => setting["settingName"]?.GetValue<string>() == change.SettingName).ToArray();
            Guard.Against(exact.Length != 1, "c1_deploy_tenant_drift", "Approved tenant setting is not uniquely observed.", 409);
            if (state.Objects.TryGetValue(key, out var completed) || state.PendingCreates.ContainsKey(key))
            {
                Guard.Against(completed is not null && completed.RequestHash != hash ||
                    state.PendingCreates.TryGetValue(key, out var pending) && pending != hash ||
                    !JsonNode.DeepEquals(UpdateBody(exact[0]), change.Request),
                    "c1_deploy_unknown", "Recorded tenant update cannot be reconciled; no repeated POST.", 409);
                RecordTenantChange(key, hash, change.SettingName, exact[0], approvedChangeHash, administrativeConcurrencyReviewSha256);
                continue;
            }
            Guard.Against(Canonical.Hash(exact[0]) != change.BeforeHash,
                "c1_deploy_tenant_drift", "Tenant setting changed after approval; do not overwrite.", 409);
            await VerifyAllowlistMembers(ct);
            Pending(key, hash);
            // This API has no documented CAS contract. The separately approved administrative window is not an ETag substitute.
            var response = await Send(HttpMethod.Post, new(TenantSettingsUri + "/" + Uri.EscapeDataString(change.SettingName) + "/update"),
                change.Request, ct: ct);
            RecordDeniedRequest(key, hash, response.Status);
            RequireStatus(response, 200);
            var after = await ReadAll(TenantSettingsUri, false, ct);
            var readback = after.Where(setting => setting["settingName"]?.GetValue<string>() == change.SettingName).ToArray();
            Guard.Against(readback.Length != 1 || !JsonNode.DeepEquals(UpdateBody(readback[0]), change.Request),
                "c1_deploy_tenant_readback", "Full scoped policy change was not confirmed.", 409);
            RecordTenantChange(key, hash, change.SettingName, readback[0], approvedChangeHash, administrativeConcurrencyReviewSha256);
        }

        return await ReviewTenantSettings(ct);
    }

    private void RecordTenantChange(string key, string requestHash, string name, JsonObject setting,
        string approvalHash, string concurrencyReview) =>
        SaveObject(key, "ScopedTenantSetting", name, requestHash, new()
        {
            ["setting"] = setting.DeepClone(), ["approvalHash"] = approvalHash,
            ["administrativeConcurrencyReviewSha256"] = concurrencyReview
        });

    private static bool ContainsGroup(JsonObject setting, string property, string id) =>
        setting[property] is JsonArray groups && groups.Any(group =>
            string.Equals(group?["graphId"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

    private static bool IsAdministrative(JsonObject setting) =>
        setting["tenantSettingGroup"]?.GetValue<string>() == "AdminApiSettings" ||
        setting["settingName"]?.GetValue<string>() is "AllowServicePrincipalsUseReadAdminAPIs" or "AllowServicePrincipalsUseWriteAdminAPIs";

    private static JsonObject UpdateBody(JsonObject setting)
    {
        var result = new JsonObject();
        foreach (var field in new[] { "enabled", "delegateToCapacity", "delegateToDomain", "delegateToWorkspace",
            "enabledSecurityGroups", "excludedSecurityGroups", "properties" })
            if (setting.ContainsKey(field)) result[field] = setting[field]?.DeepClone();
        return result;
    }
}
