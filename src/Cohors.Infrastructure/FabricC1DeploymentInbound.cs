using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1SharedInboundReview(bool Enabled, string BeforeHash, JsonObject Original, JsonObject EnableRequest)
{
    public string ChangeHash => Canonical.Hash(new { BeforeHash, EnableRequest });
    public string Scope => "TenantWideWorkspaceAdminConfigurationAuthority";
    public string CleanupDisposition => "RetainUntilSeparateCurrentAdministrativeDecision";
    public bool AutomaticDisableAllowed => false;
    public bool PublicDenyProven => false;
}

public sealed record FabricC1SharedInboundApproval(string Contract, string DeploymentBindingHash, string ChangeHash,
    string DirectUserApprovalSha256, string AdministrativeConcurrencyReviewSha256, DateTimeOffset ExpiresAtUtc,
    bool AcceptsTenantWideWorkspaceAdminAuthority, bool AcceptsNoAutomaticDisable)
{
    public void Validate(FabricC1DeploymentInputs input, DateTimeOffset now) =>
        Guard.Against(Contract != "cohors-fabric-c1-shared-inbound-approval-1" ||
            DeploymentBindingHash != Canonical.Hash(input) || !FabricC1Execution.Hex(ChangeHash, 64) ||
            !FabricC1Execution.Hex(DirectUserApprovalSha256, 64) ||
            !FabricC1Execution.Hex(AdministrativeConcurrencyReviewSha256, 64) ||
            !AcceptsTenantWideWorkspaceAdminAuthority || !AcceptsNoAutomaticDisable ||
            ExpiresAtUtc <= now || input.ExpiresAtUtc is null || ExpiresAtUtc > input.ExpiresAtUtc,
            "c1_deploy_inbound_approval", "A current explicit approval of tenant-wide workspace-admin authority and no automatic disable is required.", 409);
}

public sealed partial class FabricC1Provisioner
{
    public const string WorkspaceInboundSettingName = "WorkspaceBlockInboundAccess";
    public const string SharedInboundApprovalBlocker = "shared-inbound-direct-user-approval-required";

    public async Task<FabricC1SharedInboundReview> ReviewWorkspaceInbound(CancellationToken ct)
    {
        var settings = await ReadAll(TenantSettingsUri, false, ct);
        var matches = settings.Where(setting => setting["settingName"]?.GetValue<string>() == WorkspaceInboundSettingName).ToArray();
        Guard.Against(matches.Length != 1 || !IsSharedInboundSetting(matches[0]),
            "c1_deploy_inbound_shape", "The exact unscopable workspace inbound setting was not uniquely observed.", 409);
        return DescribeSharedInbound(matches[0]);
    }

    public async Task<FabricC1SharedInboundReview> ApplyWorkspaceInboundEnable(FabricC1SharedInboundApproval approval, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        approval.Validate(input, now);
        using var approvalDeadline = new CancellationTokenSource(approval.ExpiresAtUtc - now, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, approvalDeadline.Token);
        ct = linked.Token;
        Owned("arm-private-endpoints", "ArmDeployment");
        const string originalKey = "tenant-shared-inbound-original";
        const string effectKey = "tenant-shared-inbound-enable";
        var approvalHash = Canonical.Hash(approval);
        var current = await ReviewWorkspaceInbound(ct);
        FabricC1SharedInboundReview original;
        if (state.Objects.TryGetValue(originalKey, out var saved))
        {
            Guard.Against(saved.Kind != "SharedTenantEnableIntent" || saved.RequestHash != approvalHash,
                "c1_deploy_inbound_approval", "The recorded shared-setting approval differs; do not overwrite its original diff.", 409);
            original = saved.Response["originalReview"]?.Deserialize<FabricC1SharedInboundReview>(Canonical.Json) ??
                throw new DomainException("c1_deploy_inbound_original", "Original shared-setting diff is missing.", 409);
        }
        else
        {
            Guard.Against(current.Enabled || current.ChangeHash != approval.ChangeHash,
                "c1_deploy_inbound_drift", "The shared setting no longer matches the specifically approved disabled-to-enabled change.", 409);
            original = current;
            SaveObject(originalKey, "SharedTenantEnableIntent", WorkspaceInboundSettingName, approvalHash, new()
            {
                ["originalReview"] = JsonSerializer.SerializeToNode(original, Canonical.Json),
                ["approval"] = JsonSerializer.SerializeToNode(approval, Canonical.Json),
                ["cleanupDisposition"] = original.CleanupDisposition
            });
        }
        Guard.Against(original.Enabled || original.ChangeHash != approval.ChangeHash ||
            original.BeforeHash != Canonical.Hash(original.Original) || !IsSharedInboundSetting(original.Original) ||
            !JsonNode.DeepEquals(original.EnableRequest, DescribeSharedInbound(original.Original).EnableRequest),
            "c1_deploy_inbound_original", "Recorded original state/request cannot be reconciled.", 409);
        var hash = original.ChangeHash;
        if (state.Objects.TryGetValue(effectKey, out var completed) || state.PendingCreates.ContainsKey(effectKey))
        {
            Guard.Against(completed is not null && (completed.Kind != "SharedTenantSettingEffect" || completed.RequestHash != hash) ||
                state.PendingCreates.TryGetValue(effectKey, out var pending) && pending != hash ||
                !current.Enabled || !JsonNode.DeepEquals(UpdateBody(current.Original), original.EnableRequest),
                "c1_deploy_inbound_unknown", "A submitted or completed shared change differs from readback; never replay POST or disable it.", 409);
        }
        else
        {
            Guard.Against(current.BeforeHash != original.BeforeHash, "c1_deploy_inbound_drift", "Shared setting changed after approval.", 409);
            approval.Validate(input, time.GetUtcNow());
            Pending(effectKey, hash);
            var response = await Send(HttpMethod.Post, new(TenantSettingsUri + "/" + WorkspaceInboundSettingName + "/update"),
                original.EnableRequest, ct: ct);
            RecordDeniedRequest(effectKey, hash, response.Status);
            RequireStatus(response, 200);
            current = await ReviewWorkspaceInbound(ct);
            Guard.Against(!current.Enabled || !JsonNode.DeepEquals(UpdateBody(current.Original), original.EnableRequest),
                "c1_deploy_inbound_readback", "Enable is not confirmed; preserve the intent and observe without another POST.", 409);
        }
        SaveObject(effectKey, "SharedTenantSettingEffect", WorkspaceInboundSettingName, hash, new()
        {
            ["original"] = original.Original.DeepClone(), ["observed"] = current.Original.DeepClone(),
            ["approvalSha256"] = approvalHash, ["cleanupDisposition"] = current.CleanupDisposition,
            ["automaticDisableAllowed"] = false, ["publicDenyProven"] = false
        });
        return current;
    }

    private async Task RequireWorkspaceInboundEnabled(CancellationToken ct) =>
        Guard.Against(!(await ReviewWorkspaceInbound(ct)).Enabled, "c1_deploy_inbound_approval_required",
            "Empty private-link preparation is permitted, but workspace Public-Deny and data setup require the separate shared-setting approval/enable stage.", 409);

    private static bool IsSharedInboundSetting(JsonObject setting)
    {
        static bool EmptyGroups(JsonNode? value) => value is null || value is JsonArray { Count: 0 };
        return setting["settingName"]?.GetValue<string>() == WorkspaceInboundSettingName &&
            setting["canSpecifySecurityGroups"] is JsonValue scope && scope.TryGetValue<bool>(out var scopable) && !scopable &&
            setting["enabled"] is JsonValue state && state.TryGetValue<bool>(out _) &&
            EmptyGroups(setting["enabledSecurityGroups"]) && EmptyGroups(setting["excludedSecurityGroups"]);
    }

    private static FabricC1SharedInboundReview DescribeSharedInbound(JsonObject setting)
    {
        var enable = UpdateBody(setting);
        enable["enabled"] = true;
        return new(setting["enabled"]!.GetValue<bool>(), Canonical.Hash(setting), (JsonObject)setting.DeepClone(), enable);
    }
}
