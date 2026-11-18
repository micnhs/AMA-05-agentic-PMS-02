using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1DeploymentNetwork(string? VnetPrefix, string? ClientPrefix, string? AgentPrefix,
    string? EndpointPrefix, string? EgressReviewSha256, string? OperatorReviewSha256);
public sealed record FabricC1DeploymentCompute(string? Size, string? ImagePublisher, string? ImageOffer,
    string? ImageSku, string? ImageVersion, string? SshPublicKey);
public sealed record FabricC1DeploymentReviews(string? StartRightsSha256, string? TenantSettingsSha256,
    string? ResourcePlanSha256, string? BudgetPlanSha256, string? DataHandlingAndRetentionSha256, string? CleanupPlanSha256);
public sealed record FabricC1ControlTokenBindings(string? ArmAudience, string? ArmIssuer, string? GraphAudience, string? GraphIssuer);
public sealed record FabricC1CleanupLimits(int? ReservedRequests, int? MaxDurationSeconds, DateTimeOffset? VerificationDeadlineUtc);
public sealed record FabricC1DeploymentInputs(string Contract, string? DeploymentId, string? SourceCommit,
    string? TenantId, string? SubscriptionId, string? Location, string? NamePrefix, DateTimeOffset? ExpiresAtUtc,
    FabricC1Identity? BootstrapIdentity, string[]? CapacityAdministrators, FabricC1DeploymentNetwork? Network,
    FabricC1DeploymentCompute? Compute, FabricC1DeploymentReviews? Reviews, int? MaxControlRequests, int? MaxControlDurationSeconds)
{
    public string[] RequiredTenantSettingNames { get; init; } = [];
    public FabricC1ControlTokenBindings? ControlTokenBindings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DelegatedGroupOwnerMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FabricC1CleanupLimits? Cleanup { get; init; }
}
public sealed record FabricC1DeploymentPlan(string Status, string BindingHash, string[] MissingInputs,
    string[] ImplementationBlockers, string[] Resources, string[] NonArmOperations)
{
    public bool CanApply => MissingInputs.Length == 0 && ImplementationBlockers.Length == 0;
    public string EvidenceLevel => "offline-deployment-contract-not-service-evidence";
}

public static class FabricC1Deployment
{
    public const string Contract = "cohors-fabric-c1-deployment-1";
    public static string[] ImplementationBlockers =>
    [
        "private-operator-artifact-delivery-and-fqdn-egress-not-integrated",
        "private-source-materialization-and-source-grant-readback-not-integrated",
        "hosted-certificate-delivery-and-durable-checkpoint-not-integrated",
        "hosted-image-session-and-final-c1-binding-not-integrated",
        "independent-deadline-stop-and-inventory-cleanup-not-integrated"
    ];

    public static FabricC1DeploymentPlan Plan(FabricC1DeploymentInputs input, DateTimeOffset now)
    {
        var missing = new List<string>();
        void Required(string name, bool valid) { if (!valid) missing.Add(name); }
        bool Id(string? value) => Guid.TryParse(value, out var guid) && guid != Guid.Empty;
        bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
        Required("contract", input.Contract == Contract);
        Required("deploymentId", Id(input.DeploymentId));
        Required("sourceCommit", FabricC1Execution.Hex(input.SourceCommit, 40));
        Required("tenantId", Id(input.TenantId));
        Required("subscriptionId", Id(input.SubscriptionId));
        Required("location", input.Location == "francecentral");
        Required("namePrefix", input.NamePrefix is not null && Regex.IsMatch(input.NamePrefix, "^[a-z][a-z0-9]{2,5}$", RegexOptions.CultureInvariant));
        Required("expiresAtUtc", input.ExpiresAtUtc > now && input.ExpiresAtUtc <= now.AddHours(48));
        Required("capacityAdministrators", input.CapacityAdministrators is { Length: > 0 and <= 3 } &&
            input.CapacityAdministrators.All(Text));
        Required("maxControlRequests", input.MaxControlRequests is >= 1 and <= 1000);
        Required("maxControlDurationSeconds", input.MaxControlDurationSeconds is >= 1 and <= 7200);
        Required("cleanup", ValidCleanupLimits(input));
        Required("requiredTenantSettingNames", input.RequiredTenantSettingNames is { Length: <= 32 } &&
            input.RequiredTenantSettingNames.All(name => name is not null &&
                Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,128}$", RegexOptions.CultureInvariant)));
        Required("bootstrapIdentity", input.BootstrapIdentity is { } identity &&
            Guid.TryParse(identity.TenantId, out var tenant) && Guid.TryParse(input.TenantId, out var targetTenant) && tenant == targetTenant &&
            Id(identity.ClientId) && Id(identity.PrincipalObjectId) &&
            (identity.CredentialKind == "DelegatedAzureCli" && identity.CredentialReference is null ||
             identity.CredentialKind is "ServicePrincipalCertificate" or "ServicePrincipalFederation" or "ServicePrincipalSecret" &&
             identity.CredentialReference is not null && Path.IsPathFullyQualified(identity.CredentialReference)));
        Required("controlTokenBindings", ValidControlTokenBindings(input));
        Required("delegatedGroupOwnerMode", input.BootstrapIdentity?.CredentialKind == "DelegatedAzureCli"
            ? input.DelegatedGroupOwnerMode is "ExplicitReviewedAdministrator" or "AutomaticReviewedNonAdministrator"
            : input.DelegatedGroupOwnerMode is null);
        Required("network", input.Network is not null);
        if (input.Network is { } network)
        {
            Required("network.egressReviewSha256", FabricC1Execution.Hex(network.EgressReviewSha256, 64));
            Required("network.operatorReviewSha256", FabricC1Execution.Hex(network.OperatorReviewSha256, 64));
            Required("network.prefixes", ValidPrefixes(network));
        }
        Required("compute", input.Compute is not null);
        if (input.Compute is { } compute)
        {
            Required("compute.size", Text(compute.Size));
            Required("compute.image", Text(compute.ImagePublisher) && Text(compute.ImageOffer) && Text(compute.ImageSku) &&
                Text(compute.ImageVersion) && compute.ImageVersion != "latest");
            Required("compute.sshPublicKey", compute.SshPublicKey?.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) == true ||
                compute.SshPublicKey?.StartsWith("ssh-rsa ", StringComparison.Ordinal) == true);
        }
        Required("reviews", input.Reviews is not null);
        if (input.Reviews is { } reviews)
        {
            foreach (var (name, value) in new Dictionary<string, string?>
            {
                ["startRightsSha256"] = reviews.StartRightsSha256, ["tenantSettingsSha256"] = reviews.TenantSettingsSha256,
                ["resourcePlanSha256"] = reviews.ResourcePlanSha256, ["budgetPlanSha256"] = reviews.BudgetPlanSha256,
                ["dataHandlingAndRetentionSha256"] = reviews.DataHandlingAndRetentionSha256, ["cleanupPlanSha256"] = reviews.CleanupPlanSha256
            }) Required("reviews." + name, FabricC1Execution.Hex(value, 64));
        }
        return new("Blocked", Canonical.Hash(input), missing.ToArray(), ImplementationBlockers.ToArray(),
        [
            "Own resource group; one F2, overage disabled",
            "Own VNet, client/agent/private-endpoint subnets; no public VM IP",
            "Client-subnet Standard NAT gateway and dedicated static Standard IPv4; connectivity only, not a FQDN filter",
            "Private DNS, Fabric workspace PLS/PE, Foundry/ACR/Vault/Blob PE",
            "Foundry account with network injection from creation and separate project identity",
            "Premium ACR, RBAC Key Vault, private Blob evidence storage, private probe VM",
            "No Firewall, VPN, Bastion, C2 archive, SQL journal or production resource"
        ],
        [
            "Graph application with public certificate, service principal, exact ownership",
            "Fabric capacity ID readback; owned workspaces; full Deny/Deny communication policies with ETags",
            "Owned Lakehouse/DataAgent item APIs, published definition readback, scoped workspace assignments",
            "Private source/credential/Hosted bootstrap and complete output binding: BLOCKED, see implementationBlockers"
        ]);
    }

    public static void RequireComplete(FabricC1DeploymentPlan plan)
    {
        Guard.Against(plan.MissingInputs.Length != 0, "c1_deploy_inputs", "Deployment start parameters are incomplete.", 409);
        Guard.Against(!plan.CanApply, "c1_deploy_incomplete",
            "Full C1 automation is incomplete. No identity acquisition, cloud reads or paid partial deployment is allowed.", 409);
    }

    public static string Name(FabricC1DeploymentInputs input) =>
        input.NamePrefix + Guid.Parse(input.DeploymentId!).ToString("N")[..10];
    public static string GroupId(FabricC1DeploymentInputs input) =>
        $"/subscriptions/{Guid.Parse(input.SubscriptionId!):D}/resourceGroups/{Name(input)}-c1";
    public static string Marker(FabricC1DeploymentInputs input) => "cohors-fabric-c1:" + input.DeploymentId + ":" + Canonical.Hash(input);
    public static JsonObject Tags(FabricC1DeploymentInputs input) => new()
    {
        ["delivery"] = "cohors-fabric", ["profile"] = "C1", ["deploymentId"] = input.DeploymentId,
        ["manifestSha256"] = Canonical.Hash(input), ["expiresAtUtc"] = input.ExpiresAtUtc?.ToString("O")
    };

    public static JsonObject ArmParameters(FabricC1DeploymentInputs input) => new()
    {
        ["name"] = new JsonObject { ["value"] = Name(input) },
        ["location"] = new JsonObject { ["value"] = input.Location },
        ["tenantId"] = new JsonObject { ["value"] = input.TenantId },
        ["tags"] = new JsonObject { ["value"] = Tags(input) },
        ["capacityAdministrators"] = new JsonObject { ["value"] = JsonSerializer.SerializeToNode(input.CapacityAdministrators, Canonical.Json) },
        ["network"] = new JsonObject { ["value"] = JsonSerializer.SerializeToNode(input.Network, Canonical.Json) },
        ["compute"] = new JsonObject { ["value"] = JsonSerializer.SerializeToNode(input.Compute, Canonical.Json) }
    };

    public static JsonObject LockCommunicationPolicy(JsonObject previous)
    {
        var policy = (JsonObject)previous.DeepClone();
        foreach (var direction in new[] { "inbound", "outbound" })
        {
            if (policy[direction] is null) policy[direction] = new JsonObject();
            if (policy[direction] is not JsonObject rules)
                throw new DomainException("c1_policy_shape", "Unexpected networking policy; do not overwrite.", 409);
            if (rules["publicAccessRules"] is null) rules["publicAccessRules"] = new JsonObject();
            if (rules["publicAccessRules"] is not JsonObject access)
                throw new DomainException("c1_policy_shape", "Unexpected access rules; do not overwrite.", 409);
            access["defaultAction"] = "Deny";
        }
        return policy;
    }

    public static bool AllowsAction(JsonArray permissions, string action)
    {
        bool Match(string pattern) => Regex.IsMatch(action, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return permissions.OfType<JsonObject>().Any(permission =>
            permission["actions"] is JsonArray actions && actions.Any(a => a is not null && Match(a.GetValue<string>())) &&
            !(permission["notActions"] is JsonArray denied && denied.Any(a => a is not null && Match(a.GetValue<string>()))));
    }

    public static bool ValidControlTokenBindings(FabricC1DeploymentInputs input)
    {
        bool Issuer(string? value) => Guid.TryParse(input.TenantId, out var tenant) &&
            (value == $"https://sts.windows.net/{tenant:D}/" || value == $"https://login.microsoftonline.com/{tenant:D}/v2.0");
        return input.ControlTokenBindings is { } tokens &&
            tokens.ArmAudience is "https://management.azure.com/" or "https://management.core.windows.net/" or "797f4846-ba00-4fd7-ba43-dac1f8f63013" &&
            tokens.GraphAudience is "https://graph.microsoft.com/" or "00000003-0000-0000-c000-000000000000" &&
            Issuer(tokens.ArmIssuer) && Issuer(tokens.GraphIssuer);
    }

    public static bool ValidCleanupLimits(FabricC1DeploymentInputs input) =>
        input.Cleanup is { } cleanup && cleanup.ReservedRequests is >= 1 &&
        cleanup.ReservedRequests < input.MaxControlRequests && cleanup.MaxDurationSeconds is >= 1 and <= 7200 &&
        cleanup.MaxDurationSeconds <= input.MaxControlDurationSeconds &&
        input.ExpiresAtUtc is { } expires && input.MaxControlDurationSeconds is >= 1 and <= 7200 &&
        cleanup.VerificationDeadlineUtc > expires &&
        cleanup.VerificationDeadlineUtc <= expires.AddSeconds(input.MaxControlDurationSeconds.Value);

    private static bool ValidPrefixes(FabricC1DeploymentNetwork network)
    {
        if (!IPNetwork.TryParse(network.VnetPrefix, out var parent) || parent.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var privateRoots = new[] { IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16") };
        if (!privateRoots.Any(root => parent.PrefixLength >= root.PrefixLength && root.Contains(parent.BaseAddress))) return false;
        var children = new List<IPNetwork>();
        foreach (var prefix in new[] { network.ClientPrefix, network.AgentPrefix, network.EndpointPrefix })
        {
            if (!IPNetwork.TryParse(prefix, out var child) || child.PrefixLength <= parent.PrefixLength ||
                child.PrefixLength > 27 || !parent.Contains(child.BaseAddress) ||
                children.Any(other => other.Contains(child.BaseAddress) || child.Contains(other.BaseAddress))) return false;
            children.Add(child);
        }
        var hostedReserved = IPNetwork.Parse("192.168.0.0/24");
        var endpoints = children[2];
        if (endpoints.Contains(hostedReserved.BaseAddress) || hostedReserved.Contains(endpoints.BaseAddress)) return false;
        return true;
    }
}

public sealed record FabricC1DeploymentReply(int Status, JsonObject Body, string? ETag = null, string? OperationUrl = null)
{
    public int? RetryAfterSeconds { get; init; }
}
public interface IFabricC1DeploymentApi
{
    Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null, string? ifMatch = null,
        CancellationToken ct = default);
}
public sealed record FabricC1ProvisionedObject(string Kind, string Id, string RequestHash, JsonObject Response);
public sealed record FabricC1DeploymentDraft(string Status, FabricC1Inputs Preparation, string[] MissingOrInvalid)
{
    public bool CloudExecutionAllowed => false;
    public string EvidenceLevel => "resource-receipts-only-not-c1-service-acceptance";
}
public sealed record FabricC1DeploymentJournal(string BindingHash,
    Dictionary<string, FabricC1ProvisionedObject> Objects, Dictionary<string, string> PendingCreates)
{
    public int Requests { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public Dictionary<string, string> PendingOperations { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, FabricC1DeniedRequest> DeniedRequests { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset? CleanupStartedAtUtc { get; init; }
    public int CleanupRequests { get; init; }
    public Dictionary<string, string> PendingDeletes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, FabricC1ProvisionedObject> CleanupReceipts { get; init; } = new(StringComparer.Ordinal);
}
public sealed record FabricC1DeniedRequest(string RequestHash, int Status);
public interface IFabricC1DeploymentJournal
{
    FabricC1DeploymentJournal? Load();
    void Save(FabricC1DeploymentJournal state);
}

// Provider operations are kept separate from the full-profile apply gate. Partial foundation deployment is not exposed by the CLI.
public sealed partial class FabricC1Provisioner
{
    private readonly FabricC1DeploymentInputs input;
    private readonly IFabricC1DeploymentApi api;
    private readonly IFabricC1DeploymentJournal journal;
    private readonly TimeProvider time;
    private FabricC1DeploymentJournal state;
    private string Marker => FabricC1Deployment.Marker(input);
    public FabricC1DeploymentJournal State => state;

    public FabricC1Provisioner(FabricC1DeploymentInputs input, IFabricC1DeploymentApi api, IFabricC1DeploymentJournal journal, TimeProvider? time = null)
    {
        this.input = input;
        this.api = api;
        this.journal = journal;
        this.time = time ?? TimeProvider.System;
        state = journal.Load() ?? new(Canonical.Hash(input), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        Guard.Against(state.BindingHash != Canonical.Hash(input), "c1_deploy_journal_binding", "Deployment manifest changed; no adoption or update.", 409);
        Guard.Against(state.Requests < 0 || state.CleanupRequests < 0 || state.CleanupRequests > state.Requests ||
            state.Requests > state.CleanupRequests && state.StartedAtUtc is null ||
            state.CleanupRequests > 0 && state.CleanupStartedAtUtc is null,
            "c1_deploy_journal_budget", "Persisted counters lack their original start or are inconsistent; do not reset them.", 409);
    }

    public async Task Preflight(CancellationToken ct)
    {
        var subscription = await Read($"https://management.azure.com/subscriptions/{input.SubscriptionId}?api-version=2022-12-01", ct);
        SameId(Text(subscription, "subscriptionId"), input.SubscriptionId!);
        SameId(Text(subscription, "tenantId"), input.TenantId!);
        Guard.Against(Text(subscription, "state") != "Enabled", "c1_deploy_subscription", "Subscription is not enabled.", 409);
        var caller = input.BootstrapIdentity!;
        if (caller.CredentialKind == "DelegatedAzureCli")
        {
            var user = await Read("https://graph.microsoft.com/v1.0/me?$select=id", ct);
            SameId(Text(user, "id"), caller.PrincipalObjectId!);
        }
        else
        {
            var principal = await Read($"https://graph.microsoft.com/v1.0/servicePrincipals/{caller.PrincipalObjectId}?$select=id,appId,appOwnerOrganizationId,accountEnabled", ct);
            SameId(Text(principal, "id"), caller.PrincipalObjectId!);
            SameId(Text(principal, "appId"), caller.ClientId!);
            SameId(Text(principal, "appOwnerOrganizationId"), input.TenantId!);
            Guard.Against(principal["accountEnabled"]?.GetValue<bool>() != true, "c1_deploy_identity", "Bound bootstrap principal is disabled.", 409);
        }
        var permissions = await Read($"https://management.azure.com/subscriptions/{input.SubscriptionId}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);
        var values = permissions["value"] as JsonArray ?? throw new DomainException("c1_deploy_permissions", "ARM permissions are missing.", 403);
        foreach (var action in new[]
        {
            "Microsoft.Resources/subscriptions/resourceGroups/write", "Microsoft.Resources/deployments/write",
            "Microsoft.Fabric/capacities/write", "Microsoft.Fabric/privateLinkServicesForFabric/write",
            "Microsoft.Network/virtualNetworks/write", "Microsoft.Network/privateEndpoints/write",
            "Microsoft.Network/natGateways/write", "Microsoft.Network/publicIPAddresses/write",
            "Microsoft.Network/privateDnsZones/write", "Microsoft.Compute/virtualMachines/write",
            "Microsoft.Compute/virtualMachines/runCommands/read", "Microsoft.Compute/virtualMachines/runCommands/write",
            "Microsoft.Compute/virtualMachines/runCommands/delete", "Microsoft.Authorization/roleAssignments/write",
            "Microsoft.CognitiveServices/accounts/write", "Microsoft.ContainerRegistry/registries/write",
            "Microsoft.KeyVault/vaults/write", "Microsoft.Storage/storageAccounts/write"
        })
            Guard.Against(!FabricC1Deployment.AllowsAction(values, action), "c1_deploy_rights", "Required ARM creation action is absent.", 403);
        Guard.Against(permissions["nextLink"] is not null, "c1_deploy_permissions_paging", "Incomplete permission response; no assumed rights.", 403);
        // ARM permissions are not Graph consent, Fabric policy, data-plane grants or a guarantee against Azure Policy/deny assignments.
        foreach (var provider in new[] { "Microsoft.Fabric", "Microsoft.Network", "Microsoft.Compute", "Microsoft.CognitiveServices",
            "Microsoft.ContainerRegistry", "Microsoft.KeyVault", "Microsoft.Storage", "Microsoft.App", "Microsoft.ContainerService" })
        {
            var registered = await Read($"https://management.azure.com/subscriptions/{input.SubscriptionId}/providers/{provider}?api-version=2021-04-01", ct);
            Guard.Against(Text(registered, "registrationState") != "Registered", "c1_deploy_provider", "Required provider is not registered; no automatic registration.", 409);
        }
    }

    public Task<JsonObject> EnsureApplication(string publicCertificateBase64, DateTimeOffset notBefore,
        DateTimeOffset notAfter, CancellationToken ct) =>
        EnsureCreated("runtime-app", "Application", "https://graph.microsoft.com/v1.0/applications",
            FabricC1Deployment.Name(input) + "-runtime",
            new JsonObject
            {
                ["displayName"] = FabricC1Deployment.Name(input) + "-runtime", ["signInAudience"] = "AzureADMyOrg",
                ["tags"] = new JsonArray(Marker),
                ["owners@odata.bind"] = new JsonArray($"https://graph.microsoft.com/v1.0/directoryObjects/{input.BootstrapIdentity!.PrincipalObjectId}"),
                ["keyCredentials"] = new JsonArray(new JsonObject
                {
                    ["type"] = "AsymmetricX509Cert", ["usage"] = "Verify", ["key"] = publicCertificateBase64,
                    ["startDateTime"] = notBefore.ToString("O"), ["endDateTime"] = notAfter.ToString("O"),
                    ["displayName"] = "cohors-c1"
                })
            }, isGraph: true, ct);

    public async Task<JsonObject> EnsureServicePrincipal(CancellationToken ct)
    {
        var app = Owned("runtime-app", "Application");
        var body = new JsonObject { ["appId"] = Text(app, "appId"), ["tags"] = new JsonArray(Marker) };
        return await EnsureCreated("runtime-sp", "ServicePrincipal", "https://graph.microsoft.com/v1.0/servicePrincipals",
            Text(app, "appId"), body, isGraph: true, ct, filterProperty: "appId");
    }

    public async Task<JsonObject> EnsureWorkspace(string key, CancellationToken ct)
    {
        var capacityId = Text(Owned("fabric-capacity", "FabricCapacity"), "id");
        GuidValue(capacityId);
        Guard.Against(key is not ("agent" or "released" or "denied"), "c1_deploy_workspace", "Unknown C1 workspace purpose.", 409);
        var name = FabricC1Deployment.Name(input) + "-" + key;
        return await EnsureCreated("workspace-" + key, "Workspace", "https://api.fabric.microsoft.com/v1/workspaces", name,
            new JsonObject { ["displayName"] = name, ["capacityId"] = capacityId, ["description"] = Marker }, false, ct);
    }

    public async Task DenyWorkspacePublicAccess(string key, CancellationToken ct)
    {
        var id = Text(Owned("workspace-" + key, "Workspace"), "id");
        await RequireWorkspaceInboundEnabled(ct);
        var uri = new Uri($"https://api.fabric.microsoft.com/v1/workspaces/{id}/networking/communicationPolicy");
        var before = await Send(HttpMethod.Get, uri, ct: ct);
        RequireStatus(before, 200);
        Guard.Against(string.IsNullOrWhiteSpace(before.ETag), "c1_deploy_etag", "Policy ETag is absent; do not overwrite.", 409);
        var body = FabricC1Deployment.LockCommunicationPolicy(before.Body);
        var updated = await Send(HttpMethod.Put, uri, body, QuoteETag(before.ETag!), ct);
        RequireStatus(updated, 200);
        var after = await Send(HttpMethod.Get, uri, ct: ct);
        RequireStatus(after, 200);
        Guard.Against(!JsonNode.DeepEquals(body, after.Body), "c1_deploy_policy_readback", "Full networking policy readback differs.", 409);
        SaveObject("policy-" + key, "CommunicationPolicy", id, Canonical.Hash(body), after.Body);
    }

    public async Task<JsonObject> EnsureLakehouse(string workspace, CancellationToken ct)
    {
        Guard.Against(workspace is not ("released" or "denied"), "c1_deploy_source_scope", "No source may share the Contributor agent workspace.", 409);
        var id = Text(Owned("workspace-" + workspace, "Workspace"), "id");
        Owned("policy-" + workspace, "CommunicationPolicy");
        await RequireWorkspaceInboundEnabled(ct);
        var host = WorkspaceHost(id);
        return await EnsureCreated("lakehouse-" + workspace, "Lakehouse", $"https://{host}/v1/workspaces/{id}/lakehouses",
            "C1_" + workspace, new JsonObject { ["displayName"] = "C1_" + workspace, ["description"] = Marker }, false, ct);
    }

    public async Task<JsonObject> EnsureDataAgent(JsonObject definition, CancellationToken ct)
    {
        var id = Text(Owned("workspace-agent", "Workspace"), "id");
        Owned("policy-agent", "CommunicationPolicy");
        var parts = definition["parts"] as JsonArray;
        Guard.Against(parts is null || !parts.Any(p => p?["path"]?.GetValue<string>() == "Files/Config/publish_info.json") ||
            !parts.Any(p => p?["path"]?.GetValue<string>() == "Files/Config/published/stage_config.json"),
            "c1_deploy_publication", "Published definition parts are required; create alone does not publish evidence.", 409);
        await RequireWorkspaceInboundEnabled(ct);
        var result = await EnsureCreated("data-agent", "DataAgent", $"https://{WorkspaceHost(id)}/v1/workspaces/{id}/dataAgents",
            "C1_Agent", new JsonObject { ["displayName"] = "C1_Agent", ["description"] = Marker, ["definition"] = definition.DeepClone() }, false, ct);
        var readback = await Send(HttpMethod.Post,
            new($"https://{WorkspaceHost(id)}/v1/workspaces/{id}/dataAgents/{Text(result, "id")}/getDefinition"), ct: ct);
        if (readback.Status == 202)
        {
            Guard.Against(readback.OperationUrl is null, "c1_deploy_definition_pending", "Definition LRO has no supported operation URL.", 409);
            var collection = $"https://{WorkspaceHost(id)}/v1/workspaces/{id}/dataAgents";
            ValidateOperation(readback.OperationUrl!, collection);
            if (readback.RetryAfterSeconds is > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(readback.RetryAfterSeconds.Value, 30)), time, ct);
            readback = new(200, await AwaitOperation(readback.OperationUrl!, collection, ct));
        }
        RequireStatus(readback, 200);
        Guard.Against(readback.Body["definition"] is not JsonObject actual ||
            !DefinitionsEqual(definition, actual), "c1_deploy_publication_readback", "Published definition differs; no MCP readiness claim.", 409);
        SaveObject("published-definition", "DataAgentDefinition", Text(result, "id"), Canonical.Hash(definition), readback.Body);
        return result;
    }

    public async Task AssignRuntimeToAgentWorkspace(CancellationToken ct)
    {
        var workspaceId = Text(Owned("workspace-agent", "Workspace"), "id");
        var principalId = Text(Owned("runtime-sp", "ServicePrincipal"), "id");
        var uri = $"https://{WorkspaceHost(workspaceId)}/v1/workspaces/{workspaceId}/roleAssignments";
        const string key = "runtime-agent-grant";
        var body = new JsonObject
        {
            ["principal"] = new JsonObject { ["id"] = principalId, ["type"] = "ServicePrincipal" }, ["role"] = "Contributor"
        };
        var hash = Canonical.Hash(body);
        if (state.PendingCreates.TryGetValue(key, out var intent))
            Guard.Against(intent != hash, "c1_deploy_drift", "Pending grant differs.", 409);
        var assignments = await ReadAll(uri, isGraph: false, ct);
        var matches = assignments.Where(a => a["principal"]?["id"]?.GetValue<string>() == principalId).ToArray();
        Guard.Against(matches.Length > 1 || matches.Length == 1 && matches[0]["role"]?.GetValue<string>() != "Contributor",
            "c1_deploy_grant_conflict", "Existing runtime grant differs; do not broaden or replace it.", 409);
        if (matches.Length == 0)
        {
            Guard.Against(intent is not null || state.Objects.ContainsKey(key), "c1_deploy_unknown", "Recorded grant is missing; reconcile, do not replay POST.", 409);
            Pending(key, hash);
            var response = await Send(HttpMethod.Post, new(uri), body, ct: ct);
            RequireStatus(response, 201);
        }
        else Guard.Against(intent is null && !state.Objects.ContainsKey(key), "c1_deploy_foreign", "Unjournaled grant is not adopted for cleanup.", 409);
        var readback = await ReadAll(uri, false, ct);
        var exact = readback.Where(a => a["principal"]?["id"]?.GetValue<string>() == principalId &&
            a["principal"]?["type"]?.GetValue<string>() == "ServicePrincipal" && a["role"]?.GetValue<string>() == "Contributor").ToArray();
        Guard.Against(exact.Length != 1, "c1_deploy_grant_readback", "Targeted agent-workspace grant not confirmed.", 409);
        var assignmentId = Text(exact[0], "id");
        GuidValue(assignmentId);
        SaveObject(key, "WorkspaceGrant", assignmentId, hash, exact[0]);
    }

    public async Task<JsonObject> EnsureResourceGroup(CancellationToken ct)
    {
        var id = FabricC1Deployment.GroupId(input);
        var uri = new Uri("https://management.azure.com" + id + "?api-version=2022-09-01");
        var existing = await Send(HttpMethod.Get, uri, ct: ct);
        if (existing.Status == 200)
        {
            Guard.Against(!state.PendingCreates.ContainsKey("resource-group") && !state.Objects.ContainsKey("resource-group"),
                "c1_deploy_foreign", "An existing resource group without this deployment journal is not adopted.", 409);
            RequireArmOwnership(existing.Body);
            Guard.Against(!string.Equals(Text(existing.Body, "id"), id, StringComparison.OrdinalIgnoreCase),
                "c1_deploy_context", "Resource group identity differs.", 409);
            var hash = state.Objects.TryGetValue("resource-group", out var known) ? known.RequestHash : state.PendingCreates["resource-group"];
            SaveObject("resource-group", "ResourceGroup", id, hash, existing.Body);
            return existing.Body;
        }
        RequireStatus(existing, 404);
        Guard.Against(state.Objects.ContainsKey("resource-group") || state.PendingCreates.ContainsKey("resource-group"),
            "c1_deploy_unknown", "Previously requested resource group is missing; do not recreate it.", 409);
        var body = new JsonObject { ["location"] = input.Location, ["tags"] = FabricC1Deployment.Tags(input) };
        Pending("resource-group", Canonical.Hash(body));
        var created = await Send(HttpMethod.Put, uri, body, ct: ct);
        RequireStatus(created, 200, 201);
        RequireArmOwnership(created.Body);
        Guard.Against(!string.Equals(Text(created.Body, "id"), id, StringComparison.OrdinalIgnoreCase),
            "c1_deploy_context", "Created resource group identity differs.", 409);
        SaveObject("resource-group", "ResourceGroup", id, Canonical.Hash(body), created.Body);
        return created.Body;
    }

    public async Task<JsonObject> DeployArm(string stage, JsonObject template, JsonObject parameters, CancellationToken ct)
    {
        Guard.Against(stage is not ("foundation" or "private-endpoints"), "c1_deploy_stage", "Unknown ARM stage.", 409);
        Owned("resource-group", "ResourceGroup");
        await EnsureResourceGroup(ct);
        var group = FabricC1Deployment.GroupId(input);
        var id = group + "/providers/Microsoft.Resources/deployments/c1-" + stage;
        var uri = new Uri("https://management.azure.com" + id + "?api-version=2022-09-01");
        var body = new JsonObject { ["properties"] = new JsonObject
        {
            ["mode"] = "Incremental", ["template"] = template.DeepClone(), ["parameters"] = parameters.DeepClone()
        }};
        var hash = Canonical.Hash(body);
        var key = "arm-" + stage;
        if (state.Objects.TryGetValue(key, out var known))
            Guard.Against(known.RequestHash != hash, "c1_deploy_drift", "ARM template or parameters changed.", 409);
        if (state.PendingCreates.TryGetValue(key, out var pending))
            Guard.Against(pending != hash, "c1_deploy_drift", "Pending ARM request changed.", 409);
        state.Objects.TryGetValue(key + "-observation", out var observed);
        Guard.Against(observed is not null && (observed.Kind != "ArmDeploymentObservation" ||
            observed.RequestHash != hash || known is null && pending is null),
            "c1_deploy_arm_binding", "ARM observation has no matching original request.", 409);
        var expectedReadback = known ?? observed;
        var existing = await Send(HttpMethod.Get, uri, ct: ct);
        if (existing.Status == 404)
        {
            Guard.Against(known is not null || pending is not null, "c1_deploy_unknown", "ARM deployment outcome unknown; never recreate automatically.", 409);
            SaveArmInventory(await VerifyArmInventory(ct));
            Pending(key, hash);
            var create = await Send(HttpMethod.Put, uri, body, ct: ct);
            RequireStatus(create, 200, 201, 202);
        }
        else
        {
            RequireStatus(existing, 200);
            Guard.Against(known is null && pending is null, "c1_deploy_foreign", "Unowned ARM deployment name.", 409);
            VerifyArmDeploymentReadback(existing.Body, id, parameters, expectedReadback);
            if (expectedReadback is null)
            {
                SaveObject(key + "-observation", "ArmDeploymentObservation", id, hash, existing.Body);
                expectedReadback = state.Objects[key + "-observation"];
            }
        }
        for (var poll = 0; poll < 120; poll++)
        {
            var result = await Send(HttpMethod.Get, uri, ct: ct);
            RequireStatus(result, 200);
            VerifyArmDeploymentReadback(result.Body, id, parameters, expectedReadback);
            if (expectedReadback is null)
            {
                SaveObject(key + "-observation", "ArmDeploymentObservation", id, hash, result.Body);
                expectedReadback = state.Objects[key + "-observation"];
            }
            var status = result.Body["properties"]?["provisioningState"]?.GetValue<string>();
            if (status == "Succeeded")
            {
                var children = await VerifyArmInventory(ct, stage, hash, result.Body);
                SaveArmInventory(children, stage, id, hash, result.Body);
                return result.Body;
            }
            Guard.Against(status is not ("Accepted" or "Running" or "Creating"), "c1_deploy_arm_failed", "ARM deployment did not succeed.", 409);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new DomainException("c1_deploy_arm_timeout", "ARM operation remains pending; resume the same deployment.", 409);
    }

    public async Task<string> ResolveCapacity(CancellationToken ct)
    {
        var foundation = Owned("arm-foundation", "ArmDeployment");
        var capacity = foundation["properties"]?["outputs"]?["capacity"]?["value"] as JsonObject ??
            throw new DomainException("c1_deploy_capacity", "ARM capacity output is missing.", 409);
        RequireArmOwnership(capacity);
        var expectedId = FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Fabric/capacities/" + FabricC1Deployment.Name(input) + "cap";
        Guard.Against(!string.Equals(Text(capacity, "id"), expectedId, StringComparison.OrdinalIgnoreCase) ||
            capacity["sku"]?["name"]?.GetValue<string>() != "F2" ||
            !string.Equals(Text(capacity, "location"), input.Location, StringComparison.OrdinalIgnoreCase),
            "c1_deploy_capacity", "Capacity does not match the owned fixed-F2 resource.", 409);
        var capacities = await ReadAll("https://api.fabric.microsoft.com/v1/capacities", false, ct);
        var match = capacities.Where(c => c["displayName"]?.GetValue<string>() == FabricC1Deployment.Name(input) + "cap" &&
            c["sku"]?.GetValue<string>() == "F2" &&
            string.Equals(c["region"]?.GetValue<string>()?.Replace(" ", "", StringComparison.Ordinal), input.Location, StringComparison.OrdinalIgnoreCase)).ToArray();
        Guard.Against(match.Length != 1 || match[0]["state"]?.GetValue<string>() != "Active",
            "c1_deploy_capacity", "Fabric capacity GUID is not uniquely confirmed; never derive it from ARM ID.", 409);
        var id = Text(match[0], "id");
        GuidValue(id);
        SaveObject("fabric-capacity", "FabricCapacity", id, Canonical.Hash(capacity), match[0]);
        return id;
    }

    public static string WorkspaceHost(string id)
    {
        var guid = GuidValue(id).ToString("N");
        return $"{guid}.z{guid[..2]}.w.api.fabric.microsoft.com";
    }

    public FabricC1DeploymentDraft PreparationDraft()
    {
        string? Value(string key, string kind, string field)
        {
            if (!state.Objects.TryGetValue(key, out var item)) return null;
            Guard.Against(item.Kind != kind, "c1_deploy_receipt_kind", "Output receipt kind differs.", 409);
            return item.Response[field]?.GetValue<string>();
        }
        var workspace = Value("workspace-agent", "Workspace", "id");
        var agent = Value("data-agent", "DataAgent", "id");
        var app = Value("runtime-app", "Application", "appId");
        var principal = Value("runtime-sp", "ServicePrincipal", "id");
        var source = Value("lakehouse-released", "Lakehouse", "id");
        var capacity = state.Objects.TryGetValue("arm-foundation", out var arm) ?
            arm.Response["properties"]?["outputs"]?["capacity"]?["value"]?["id"]?.GetValue<string>() : null;
        var published = Value("published-definition", "DataAgentDefinition", "id");
        // The definition response need not contain an id; the receipt itself binds the created agent.
        if (state.Objects.TryGetValue("published-definition", out var definition))
        {
            Guard.Against(definition.Kind != "DataAgentDefinition" || definition.Id != agent,
                "c1_deploy_receipt_binding", "Publication receipt belongs to a different item.", 409);
            published = definition.Id;
        }
        var preparation = new FabricC1Inputs(FabricC1ProbeClient.Contract, "cohors-fabric", "PreparationOnly", true,
            FabricC1ProbeClient.TokenScope,
            new(input.TenantId, app, principal, "ServicePrincipalCertificate", null, null, null),
            new(capacity, input.Location, workspace, agent, source, null,
                published is not null && workspace is not null ?
                    $"https://{WorkspaceHost(workspace)}/v1/mcp/workspaces/{workspace}/dataagents/{published}/agent" : null,
                FabricC1ProbeClient.ProtocolVersion, null, null, null, null),
            new("ortho", null, null, null, null, null),
            new(null, null, null, null, null, null) { AiExecution = FabricC1ProbeClient.AiExecution },
            new(input.Reviews?.ResourcePlanSha256, null, input.Network?.EgressReviewSha256, null,
                input.Reviews?.DataHandlingAndRetentionSha256, input.Reviews?.BudgetPlanSha256, input.Reviews?.CleanupPlanSha256, null),
            new(null, null, null, null));
        var check = FabricC1ProbeClient.Preflight(preparation, discoveryOnly: true);
        return new("Blocked", preparation, check.MissingOrInvalid);
    }

    public void RequireFullBindings()
    {
        foreach (var key in new[] { "runtime-app", "runtime-sp", "arm-foundation", "arm-private-endpoints",
            "workspace-agent", "workspace-released", "workspace-denied", "lakehouse-released", "lakehouse-denied",
            "published-definition", "runtime-agent-grant", "source-grants", "materialization",
            "runtime-credential", "hosted-version", "hosted-session", "private-path", "caller-access" })
            Guard.Against(!state.Objects.ContainsKey(key), "c1_deploy_binding_incomplete",
                "Deployment receipts are incomplete; no runnable C1 inputs may be emitted.", 409);
        Guard.Against(state.PendingCreates.Count != 0 || state.PendingOperations.Count != 0, "c1_deploy_unknown", "Unresolved creates prevent C1 binding.", 409);
    }

    private async Task<JsonObject> EnsureCreated(string key, string kind, string collection, string name, JsonObject body,
        bool isGraph, CancellationToken ct, string filterProperty = "displayName", bool reconcileForCleanup = false)
    {
        var hash = Canonical.Hash(body);
        if (state.Objects.TryGetValue(key, out var known))
        {
            Guard.Against(known.Kind != kind || known.RequestHash != hash, "c1_deploy_drift", "A completed create request changed.", 409);
            var current = await Read(collection + "/" + known.Id, ct, reconcileForCleanup);
            RequireMarker(current, isGraph && kind != "SecurityGroup");
            SameId(Text(current, "id"), known.Id);
            await VerifyGraphBinding(kind, collection, current, body, ct, reconcileForCleanup);
            return current;
        }
        if (state.PendingCreates.TryGetValue(key, out var pending))
            Guard.Against(pending != hash, "c1_deploy_drift", "A pending create request changed.", 409);
        if (state.PendingOperations.TryGetValue(key, out var operation))
        {
            Guard.Against(pending is null, "c1_deploy_journal_invalid", "Operation has no creation intent.", 409);
            return await ConfirmCreation(key, kind, collection, await AwaitOperation(operation, collection, ct), body, hash, isGraph, ct);
        }
        var listUri = isGraph
            ? collection + "?$filter=" + Uri.EscapeDataString(filterProperty + " eq '" + name.Replace("'", "''") + "'")
            : collection;
        var all = await ReadAll(listUri, isGraph, ct, reconcileForCleanup);
        var matches = all.Where(item => item[filterProperty]?.GetValue<string>() == name).ToArray();
        Guard.Against(matches.Length > 1, "c1_deploy_name_conflict", "Ambiguous name; no resource is adopted.", 409);
        if (matches.Length == 1)
        {
            Guard.Against(pending is null, "c1_deploy_foreign", "Name already exists without a recorded creation intent.", 409);
            RequireMarker(matches[0], isGraph && kind != "SecurityGroup");
            GuidValue(Text(matches[0], "id"));
            await VerifyGraphBinding(kind, collection, matches[0], body, ct, reconcileForCleanup);
            SaveObject(key, kind, Text(matches[0], "id"), hash, matches[0]);
            return matches[0];
        }
        Guard.Against(pending is not null, "c1_deploy_unknown",
            "No authoritative create result; absence after interruption is not permission to retry POST.", 409);
        Guard.Against(reconcileForCleanup, "c1_deploy_cleanup_create", "Cleanup never creates an identity.", 409);
        Pending(key, hash);
        var created = await Send(HttpMethod.Post, new(collection), body, ct: ct);
        RecordDeniedRequest(key, hash, created.Status);
        if (created.Status == 202)
        {
            Guard.Against(isGraph || created.OperationUrl is null, "c1_deploy_item_pending",
                "Creation remains pending without a supported operation URL; reconcile inventory without POST replay.", 409);
            ValidateOperation(created.OperationUrl!, collection);
            var next = state with { PendingOperations = new(state.PendingOperations, StringComparer.Ordinal) { [key] = created.OperationUrl! } };
            journal.Save(next);
            state = next;
            if (created.RetryAfterSeconds is > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(created.RetryAfterSeconds.Value, 30)), time, ct);
            return await ConfirmCreation(key, kind, collection, await AwaitOperation(created.OperationUrl!, collection, ct), body, hash, isGraph, ct);
        }
        RequireStatus(created, 201);
        return await ConfirmCreation(key, kind, collection, created.Body, body, hash, isGraph, ct);
    }

    private async Task<JsonObject> ConfirmCreation(string key, string kind, string collection, JsonObject response,
        JsonObject request, string hash, bool isGraph, CancellationToken ct)
    {
        RequireMarker(response, isGraph && kind != "SecurityGroup");
        var id = Text(response, "id");
        GuidValue(id);
        await VerifyGraphBinding(kind, collection, response, request, ct);
        SaveObject(key, kind, id, hash, response);
        return response;
    }

    private static void ValidateOperation(string operation, string collection)
    {
        Guard.Against(!Uri.TryCreate(operation, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Authority != new Uri(collection).Authority ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !Regex.IsMatch(uri.AbsolutePath, "^/v1/operations/[0-9a-fA-F-]{36}$", RegexOptions.CultureInvariant) ||
            !Guid.TryParse(uri.AbsolutePath.Split('/')[^1], out var id) || id == Guid.Empty,
            "c1_deploy_operation_binding", "LRO moved outside the pinned provider authority/operation path.", 409);
    }

    private async Task<JsonObject> AwaitOperation(string operation, string collection, CancellationToken ct)
    {
        ValidateOperation(operation, collection);
        for (var poll = 0; poll < 120; poll++)
        {
            var response = await Send(HttpMethod.Get, new(operation), ct: ct);
            RequireStatus(response, 200);
            var status = Text(response.Body, "status");
            if (status == "Succeeded") return await Read(operation + "/result", ct);
            Guard.Against(status is not ("Running" or "NotStarted"), "c1_deploy_operation_failed", "Item operation failed; no replacement create.", 409);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(response.RetryAfterSeconds ?? 5, 1, 30)), time, ct);
        }
        throw new DomainException("c1_deploy_operation_pending", "Bounded LRO wait exhausted; resume the same operation.", 409);
    }

    private async Task<JsonObject[]> ReadAll(string uri, bool isGraph, CancellationToken ct, bool cleanup = false)
    {
        var start = new Uri(uri);
        var result = new List<JsonObject>();
        for (var page = 0; page < 20; page++)
        {
            var data = await Read(uri, ct, cleanup);
            if (data["value"] is not JsonArray entries)
                throw new DomainException("c1_deploy_list_shape", "Missing list data.", 409);
            result.AddRange(entries.Select(entry => entry as JsonObject ??
                throw new DomainException("c1_deploy_list_shape", "Invalid list item.", 409)));
            var next = data[isGraph ? "@odata.nextLink" : "continuationUri"]?.GetValue<string>()
                ?? (!isGraph ? data["nextLink"]?.GetValue<string>() : null);
            if (next is null)
            {
                Guard.Against(data["continuationToken"] is not null, "c1_deploy_paging", "Unresolved list continuation.", 409);
                return result.ToArray();
            }
            var target = new Uri(next);
            Guard.Against(target.Scheme != "https" || target.Authority != start.Authority ||
                target.AbsolutePath != start.AbsolutePath || target.UserInfo.Length != 0 || target.Fragment.Length != 0,
                "c1_deploy_paging", "Continuation left the exact collection.", 409);
            uri = next;
        }
        throw new DomainException("c1_deploy_paging", "Bounded list incomplete; no create on incomplete inventory.", 409);
    }

    private async Task<JsonObject> Read(string uri, CancellationToken ct, bool cleanup = false)
    {
        var reply = await Send(HttpMethod.Get, new(uri), ct: ct, cleanup: cleanup);
        RequireStatus(reply, 200);
        return reply.Body;
    }
    private JsonObject Owned(string key, string kind) =>
        state.Objects.TryGetValue(key, out var value) && value.Kind == kind ? value.Response :
            throw new DomainException("c1_deploy_dependency", "A verified prerequisite creation receipt is missing.", 409);
    private void Pending(string key, string hash)
    {
        var next = state with { PendingCreates = new(state.PendingCreates, StringComparer.Ordinal) { [key] = hash } };
        journal.Save(next);
        state = next;
    }
    private void RecordDeniedRequest(string key, string hash, int status)
    {
        if (status is not (401 or 403)) return;
        var pending = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        pending.Remove(key);
        var next = state with
        {
            PendingCreates = pending,
            DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal) { [key] = new(hash, status) }
        };
        journal.Save(next);
        state = next;
    }
    private void SaveObject(string key, string kind, string id, string hash, JsonObject body)
    {
        var objects = new Dictionary<string, FabricC1ProvisionedObject>(state.Objects, StringComparer.Ordinal)
            { [key] = new(kind, id, hash, (JsonObject)body.DeepClone()) };
        var pending = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        pending.Remove(key);
        var operations = new Dictionary<string, string>(state.PendingOperations, StringComparer.Ordinal);
        operations.Remove(key);
        var next = state with { Objects = objects, PendingCreates = pending, PendingOperations = operations };
        journal.Save(next);
        state = next;
    }
    private void RequireMarker(JsonObject body, bool isGraph)
    {
        var owned = isGraph ? body["tags"] is JsonArray tags && tags.Any(tag => tag?.GetValue<string>() == Marker)
            : body["description"]?.GetValue<string>() == Marker;
        Guard.Against(!owned, "c1_deploy_foreign", "Ownership marker is absent or mismatched.", 409);
    }
    private void RequireArmOwnership(JsonObject body) =>
        Guard.Against(body["tags"]?["deploymentId"]?.GetValue<string>() != input.DeploymentId ||
            body["tags"]?["manifestSha256"]?.GetValue<string>() != Canonical.Hash(input) ||
            !(string.Equals(body["id"]?.GetValue<string>(), FabricC1Deployment.GroupId(input), StringComparison.OrdinalIgnoreCase) ||
              body["id"]?.GetValue<string>()?.StartsWith(FabricC1Deployment.GroupId(input) + "/", StringComparison.OrdinalIgnoreCase) == true),
            "c1_deploy_foreign", "ARM resource is outside the owned C1 manifest.", 409);
    private async Task VerifyGraphBinding(string kind, string collection, JsonObject item, JsonObject request, CancellationToken ct, bool cleanup = false)
    {
        if (kind == "Application")
        {
            Guard.Against(Text(item, "signInAudience") != "AzureADMyOrg", "c1_deploy_app_tenant", "Runtime application must be single-tenant.", 409);
            GuidValue(Text(item, "appId"));
            var owners = await ReadAll(collection + "/" + Text(item, "id") + "/owners?$select=id", true, ct, cleanup);
            Guard.Against(!owners.Any(owner => owner["id"]?.GetValue<string>() == input.BootstrapIdentity!.PrincipalObjectId),
                "c1_deploy_app_owner", "Bound creator is not a verified application owner.", 409);
            Guard.Against(cleanup && owners.Length != 1, "c1_deploy_cleanup_ownership", "Additional application owners require review before deletion.", 409);
        }
        if (kind == "ServicePrincipal") SameId(Text(item, "appId"), Text(request, "appId"));
        if (kind == "SecurityGroup")
        {
            var groupId = Text(item, "id");
            var details = await Read(collection + "/" + groupId +
                "?$select=id,description,securityEnabled,mailEnabled,isAssignableToRole,groupTypes", ct, cleanup);
            SameId(Text(details, "id"), groupId);
            RequireMarker(details, false);
            Guard.Against(details["securityEnabled"]?.GetValue<bool>() != true ||
                details["mailEnabled"]?.GetValue<bool>() != false || !details.ContainsKey("isAssignableToRole") ||
                details["isAssignableToRole"]?.GetValue<bool>() == true ||
                details["groupTypes"] is not JsonArray types || types.Count != 0,
                "c1_deploy_group_scope", "Allowlist group must be static, non-mail, non-role-assignable security group.", 409);
            var delegated = input.BootstrapIdentity!.CredentialKind == "DelegatedAzureCli";
            var ownership = await ReadAll(delegated ? collection + "/" + groupId + "/owners?$select=id" :
                $"https://graph.microsoft.com/v1.0/servicePrincipals/{input.BootstrapIdentity.PrincipalObjectId}/ownedObjects?$select=id", true, ct, cleanup);
            var expectedId = delegated ? input.BootstrapIdentity.PrincipalObjectId : groupId;
            Guard.Against(!ownership.Any(owner => owner["id"]?.GetValue<string>() == expectedId),
                "c1_deploy_group_owner", "Bound creator is not a verified allowlist group owner.", 409);
            if (cleanup)
            {
                var owners = delegated ? ownership : await ReadAll(collection + "/" + groupId + "/owners?$select=id", true, ct, true);
                Guard.Against(owners.Length != 1 || owners[0]["id"]?.GetValue<string>() != input.BootstrapIdentity.PrincipalObjectId,
                    "c1_deploy_cleanup_ownership", "Additional or changed group owners require review before deletion.", 409);
            }
        }
    }
    private async Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null,
        string? ifMatch = null, CancellationToken ct = default, bool cleanup = false)
    {
        Guard.Against(!FabricC1Deployment.ValidCleanupLimits(input), "c1_deploy_cleanup_limits",
            "A reviewed cleanup reserve must be bound before the first control-plane request.", 409);
        var now = time.GetUtcNow();
        DateTimeOffset deadline;
        FabricC1DeploymentJournal next;
        if (cleanup)
        {
            Guard.Against(uri.IdnHost != "graph.microsoft.com" || method != HttpMethod.Get && method != HttpMethod.Delete,
                "c1_deploy_cleanup_scope", "Identity cleanup can only read or delete its bound Graph inventory.", 409);
            var started = state.CleanupStartedAtUtc ?? now;
            deadline = started.AddSeconds(input.Cleanup!.MaxDurationSeconds!.Value);
            if (input.Cleanup.VerificationDeadlineUtc!.Value < deadline) deadline = input.Cleanup.VerificationDeadlineUtc.Value;
            Guard.Against(state.Requests >= input.MaxControlRequests || state.CleanupRequests >= input.Cleanup.ReservedRequests ||
                now >= deadline, "c1_deploy_cleanup_budget", "Persisted cleanup reserve/deadline exhausted; no reset or request.", 409);
            next = state with { Requests = state.Requests + 1, CleanupRequests = state.CleanupRequests + 1, CleanupStartedAtUtc = started };
        }
        else
        {
            Guard.Against(state.CleanupStartedAtUtc is not null, "c1_deploy_cleanup_started", "Cleanup has fenced this run; no new provisioning or grants.", 409);
            var started = state.StartedAtUtc ?? now;
            deadline = started.AddSeconds(input.MaxControlDurationSeconds!.Value);
            if (input.ExpiresAtUtc!.Value < deadline) deadline = input.ExpiresAtUtc.Value;
            Guard.Against(state.Requests >= input.MaxControlRequests - input.Cleanup!.ReservedRequests || now >= deadline,
                "c1_deploy_budget", "Persisted normal budget/deadline exhausted; the cleanup reserve is not spent on provisioning.", 409);
            next = state with { Requests = state.Requests + 1, StartedAtUtc = started };
        }
        journal.Save(next);
        state = next;
        var remaining = deadline - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero, cleanup ? "c1_deploy_cleanup_budget" : "c1_deploy_budget",
            "Deadline elapsed while persisting intent; no provider call.", 409);
        using var budget = new CancellationTokenSource(remaining, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        return await api.Send(method, uri, body, ifMatch, linked.Token);
    }
    public static void RequireStatus(FabricC1DeploymentReply reply, params int[] expected) =>
        Guard.Against(!expected.Contains(reply.Status), "c1_deploy_http_" + reply.Status, "Provider operation not confirmed; no retry or fallback.", 409);
    private static string Text(JsonObject body, string key) => body[key]?.GetValue<string>() ??
        throw new DomainException("c1_deploy_response", "Required provider response field is missing.", 409);
    private static Guid GuidValue(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id :
        throw new DomainException("c1_deploy_response_id", "Provider did not return a valid identity.", 409);
    private static void SameId(string left, string right) =>
        Guard.Against(GuidValue(left) != GuidValue(right), "c1_deploy_context", "Observed identity differs from explicit deployment context.", 409);
    private static string QuoteETag(string etag) => etag.StartsWith('"') && etag.EndsWith('"') ? etag : "\"" + etag + "\"";
    private static bool DefinitionsEqual(JsonObject expected, JsonObject actual)
    {
        if (expected["parts"] is not JsonArray a || actual["parts"] is not JsonArray b || a.Count != b.Count) return false;
        var left = a.Select(p => Canonical.Hash(p!)).Order(StringComparer.Ordinal);
        var right = b.Select(p => Canonical.Hash(p!)).Order(StringComparer.Ordinal);
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }
}
