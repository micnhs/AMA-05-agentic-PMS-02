using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1OperatorNetwork(string Contract, string? GatewayPrefix, string? FirewallPrefix,
    string? OperatorEndpointPrefix, string? VpnClientPrefix, string? OperatorObjectId,
    string[]? ExcludedPrefixes, string[]? HttpsEgressFqdns, string? AddressPlanReviewSha256,
    string? ClientPrerequisitesReviewSha256);

public static partial class FabricC1Deployment
{
    public const string OperatorNetworkContract = "cohors-fabric-c1-operator-network-1";
    public const string VpnClientApplicationId = "c632b3df-fb67-4d84-bdcf-b95ad541b5c8";
    public const string VpnIdentityRole = "operator-vpn";

    public static string[] OperatorNetworkMissingInputs(FabricC1DeploymentInputs input)
    {
        const string prefix = "network.operatorAccess";
        if (input.Network?.OperatorAccess is not { } access) return [prefix];
        var missing = new List<string>();
        void Required(string name, bool valid) { if (!valid) missing.Add(prefix + "." + name); }
        Required("contract", access.Contract == OperatorNetworkContract);
        Required("operatorObjectId", Guid.TryParse(access.OperatorObjectId, out var user) && user != Guid.Empty &&
            (input.BootstrapIdentity?.CredentialKind != "DelegatedAzureCli" ||
             Guid.TryParse(input.BootstrapIdentity.PrincipalObjectId, out var actor) && user == actor));
        Required("addressPlanReviewSha256", FabricC1Execution.Hex(access.AddressPlanReviewSha256, 64));
        Required("clientPrerequisitesReviewSha256", FabricC1Execution.Hex(access.ClientPrerequisitesReviewSha256, 64));
        Required("prefixes", ValidOperatorPrefixes(input.Network));
        Required("httpsEgressFqdns", access.HttpsEgressFqdns is { Length: > 0 and <= 64 } hosts &&
            hosts.Distinct(StringComparer.Ordinal).Count() == hosts.Length && hosts.All(ExactDnsHost) &&
            hosts.Contains("login.microsoftonline.com", StringComparer.Ordinal) &&
            hosts.Contains("management.azure.com", StringComparer.Ordinal));
        return missing.ToArray();
    }

    private static bool ExactDnsHost(string? value) => value is { Length: > 0 and <= 253 } &&
        value == value.ToLowerInvariant() && !IPAddress.TryParse(value, out _) &&
        Regex.IsMatch(value, @"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?\z",
            RegexOptions.CultureInvariant);

    private static bool ValidOperatorPrefixes(FabricC1DeploymentNetwork network)
    {
        if (network.OperatorAccess is not { } access || !ValidPrefixes(network) ||
            !TryPrivateNetwork(network.VnetPrefix, out var vnet) || !TryPrivateNetwork(access.VpnClientPrefix, out var pool) ||
            pool.PrefixLength is < 24 or > 27 || Overlaps(vnet, pool)) return false;
        var subnets = new List<IPNetwork>();
        foreach (var (value, maximumLength) in new[]
        {
            (network.ClientPrefix, 27), (network.AgentPrefix, 27), (network.EndpointPrefix, 27),
            (access.GatewayPrefix, 27), (access.FirewallPrefix, 26), (access.OperatorEndpointPrefix, 27)
        })
        {
            if (!TryPrivateNetwork(value, out var subnet) || subnet.PrefixLength <= vnet.PrefixLength ||
                subnet.PrefixLength > maximumLength || !vnet.Contains(subnet.BaseAddress) ||
                subnets.Any(other => Overlaps(other, subnet))) return false;
            subnets.Add(subnet);
        }
        var reserved = IPNetwork.Parse("192.168.0.0/24");
        if (Overlaps(vnet, reserved) || Overlaps(pool, reserved) ||
            access.ExcludedPrefixes is not { Length: > 0 and <= 256 } excluded ||
            excluded.Distinct(StringComparer.Ordinal).Count() != excluded.Length) return false;
        foreach (var value in excluded)
        {
            if (!IPNetwork.TryParse(value, out var other) || other.BaseAddress.AddressFamily != AddressFamily.InterNetwork ||
                other.PrefixLength == 0 || other.ToString() != value || Overlaps(vnet, other) || Overlaps(pool, other)) return false;
        }
        return true;
    }

    private static bool TryPrivateNetwork(string? value, out IPNetwork network)
    {
        if (!IPNetwork.TryParse(value, out network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork ||
            network.ToString() != value) return false;
        foreach (var root in new[] { IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16") })
            if (network.PrefixLength >= root.PrefixLength && root.Contains(network.BaseAddress)) return true;
        return false;
    }

    private static bool Overlaps(IPNetwork first, IPNetwork second) =>
        first.Contains(second.BaseAddress) || second.Contains(first.BaseAddress);

    public static string VpnScopeId(FabricC1DeploymentInputs input) =>
        new Guid(Convert.FromHexString(Canonical.Hash(new { input.DeploymentId, purpose = "operator-vpn-connect" }))[..16]).ToString("D");
}

public sealed partial class FabricC1Provisioner
{
    private string[] CleanupIdentityRoles => input.Network?.OperatorAccess is null
        ? IdentityRoles : [.. IdentityRoles, FabricC1Deployment.VpnIdentityRole];

    public async Task EnsureOperatorNetworkIdentity(FabricC1GraphRights rights, CancellationToken ct)
    {
        Guard.Against(FabricC1Deployment.OperatorNetworkMissingInputs(input).Length != 0 ||
            input.BootstrapIdentity?.CredentialKind != "DelegatedAzureCli",
            "c1_deploy_operator_network", "This operator connection requires the original explicitly bound delegated user.", 409);
        rights.Require(FabricC1GraphOperation.OwnApplications);
        rights.Require(FabricC1GraphOperation.AppRoleAssignmentGrant);
        var app = await EnsureApplicationForRole(FabricC1Deployment.VpnIdentityRole, ct);
        var principal = await EnsurePrincipalForRole(FabricC1Deployment.VpnIdentityRole, ct);
        Guard.Against(string.Equals(Text(app, "appId"), input.BootstrapIdentity!.ClientId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Text(principal, "id"), input.Network!.OperatorAccess!.OperatorObjectId, StringComparison.OrdinalIgnoreCase) ||
            IdentityRoles.Any(role => state.Objects.TryGetValue(role + "-app", out var other) &&
                string.Equals(other.Response["appId"]?.GetValue<string>(), Text(app, "appId"), StringComparison.OrdinalIgnoreCase)),
            "c1_deploy_identity_alias", "The network audience must not alias a Fabric caller or the original deployer.", 409);
        await EnsureOperatorVpnIdentifier(app, ct);
        await EnsureOperatorVpnAssignment(principal, ct);
    }

    private void AddOperatorVpnApplicationFields(JsonObject body)
    {
        var scopeId = FabricC1Deployment.VpnScopeId(input);
        body["api"] = new JsonObject
        {
            ["requestedAccessTokenVersion"] = 1,
            ["acceptMappedClaims"] = false,
            ["knownClientApplications"] = new JsonArray(),
            ["oauth2PermissionScopes"] = new JsonArray(new JsonObject
            {
                ["id"] = scopeId, ["value"] = "connect", ["type"] = "Admin", ["isEnabled"] = true,
                ["adminConsentDisplayName"] = "Connect to the isolated Cohors Fabric operator VPN",
                ["adminConsentDescription"] = "Network connection only; no Fabric, Azure or Cohors application role.",
                ["userConsentDisplayName"] = "Connect to the isolated Cohors Fabric operator VPN",
                ["userConsentDescription"] = "Network connection only; no Fabric, Azure or Cohors application role."
            }),
            ["preAuthorizedApplications"] = new JsonArray(new JsonObject
            {
                ["appId"] = FabricC1Deployment.VpnClientApplicationId,
                ["delegatedPermissionIds"] = new JsonArray(scopeId)
            })
        };
        body["appRoles"] = new JsonArray();
        body["requiredResourceAccess"] = new JsonArray();
        body["keyCredentials"] = new JsonArray();
        body["passwordCredentials"] = new JsonArray();
        body["isFallbackPublicClient"] = false;
    }

    private void VerifyOperatorVpnApplication(JsonObject app, JsonObject expected)
    {
        foreach (var name in new[] { "oauth2PermissionScopes", "preAuthorizedApplications", "requestedAccessTokenVersion",
            "acceptMappedClaims", "knownClientApplications" })
            Guard.Against(!JsonNode.DeepEquals(app["api"]?[name], expected["api"]?[name]),
                "c1_deploy_vpn_application", "The isolated VPN API scope or authorized client changed.", 409);
        foreach (var name in new[] { "appRoles", "requiredResourceAccess", "keyCredentials", "passwordCredentials" })
            Guard.Against(app[name] is not JsonArray { Count: 0 },
                "c1_deploy_vpn_application", "The VPN audience gained roles, API permissions or credentials.", 409);
        Guard.Against(app["isFallbackPublicClient"]?.GetValue<bool>() != false ||
            app["identifierUris"] is not (null or JsonArray { Count: 0 }) &&
                !JsonNode.DeepEquals(app["identifierUris"], new JsonArray("api://" + Text(app, "appId"))),
            "c1_deploy_vpn_application", "Unexpected VPN audience identifier or public client configuration.", 409);
    }

    private async Task EnsureOperatorVpnIdentifier(JsonObject app, CancellationToken ct)
    {
        const string key = "operator-vpn-configuration";
        var id = Text(app, "id");
        var body = new JsonObject { ["identifierUris"] = new JsonArray("api://" + Text(app, "appId")) };
        var hash = Canonical.Hash(new { id, body });
        state.Objects.TryGetValue(key, out var known);
        state.PendingCreates.TryGetValue(key, out var pending);
        Guard.Against(known is not null && (known.Kind != "OperatorVpnConfiguration" || known.Id != id || known.RequestHash != hash) ||
            pending is not null && pending != hash, "c1_deploy_drift", "The original VPN identifier operation changed.", 409);
        if (JsonNode.DeepEquals(app["identifierUris"], body["identifierUris"]))
        {
            Guard.Against(known is null && pending is null, "c1_deploy_foreign", "An unjournaled VPN identifier change is not adopted.", 409);
        }
        else
        {
            Guard.Against(known is not null || pending is not null, "c1_deploy_unknown",
                "The VPN identifier update is missing or uncertain; no repeated PATCH.", 409);
            Pending(key, hash);
            var reply = await Send(HttpMethod.Patch, new("https://graph.microsoft.com/v1.0/applications/" + id), body, ct: ct);
            RecordDeniedRequest(key, hash, reply.Status);
            RequireStatus(reply, 204);
            var current = await Read("https://graph.microsoft.com/v1.0/applications/" + id, ct);
            RequireMarker(current, true);
            SameId(Text(current, "id"), id);
            SameId(Text(current, "appId"), Text(app, "appId"));
            VerifyOperatorVpnApplication(current, ApplicationBody(FabricC1Deployment.VpnIdentityRole));
            Guard.Against(!JsonNode.DeepEquals(current["identifierUris"], body["identifierUris"]),
                "c1_deploy_vpn_application", "VPN identifier has not propagated; retain the original intent.", 409);
        }
        SaveObject(key, "OperatorVpnConfiguration", id, hash, body);
    }

    private async Task EnsureOperatorVpnAssignment(JsonObject principal, CancellationToken ct)
    {
        const string key = "operator-vpn-assignment";
        var resource = Text(principal, "id");
        var body = new JsonObject
        {
            ["principalId"] = GuidValue(input.Network!.OperatorAccess!.OperatorObjectId!).ToString("D"),
            ["resourceId"] = resource, ["appRoleId"] = Guid.Empty.ToString("D")
        };
        var hash = Canonical.Hash(body);
        state.Objects.TryGetValue(key, out var known);
        state.PendingCreates.TryGetValue(key, out var pending);
        Guard.Against(known is not null && (known.Kind != "OperatorVpnAssignment" || known.RequestHash != hash) ||
            pending is not null && pending != hash, "c1_deploy_drift", "The original VPN operator assignment changed.", 409);
        var assignments = await ReadOperatorVpnAssignments(resource, ct);
        if (assignments.Length == 0)
        {
            Guard.Against(known is not null || pending is not null, "c1_deploy_unknown",
                "The original VPN assignment is missing or uncertain; no repeated POST.", 409);
            Pending(key, hash);
            var reply = await Send(HttpMethod.Post, new($"https://graph.microsoft.com/v1.0/servicePrincipals/{resource}/appRoleAssignedTo"),
                body, ct: ct);
            RecordDeniedRequest(key, hash, reply.Status);
            RequireStatus(reply, 201);
            assignments = await ReadOperatorVpnAssignments(resource, ct);
            Guard.Against(assignments.Length != 1, "c1_deploy_vpn_assignment",
                "The VPN operator assignment is not yet confirmed; retain its original intent.", 409);
        }
        else Guard.Against(known is null && pending is null, "c1_deploy_foreign",
            "An unjournaled VPN operator assignment is not adopted.", 409);
        var assignment = assignments[0];
        var assignmentId = Text(assignment, "id");
        Guard.Against(known is not null && known.Id != assignmentId,
            "c1_deploy_vpn_assignment", "The original VPN operator assignment was replaced.", 409);
        SaveObject(key, "OperatorVpnAssignment", assignmentId, hash, assignment);
    }

    private async Task<JsonObject[]> ReadOperatorVpnAssignments(string principal, CancellationToken ct, bool cleanup = false)
    {
        var assignments = await ReadAll($"https://graph.microsoft.com/v1.0/servicePrincipals/{principal}/appRoleAssignedTo",
            true, ct, cleanup);
        Guard.Against(assignments.Length > 1 || assignments.Any(item =>
            !string.Equals(item["principalId"]?.GetValue<string>(), input.Network!.OperatorAccess!.OperatorObjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(item["resourceId"]?.GetValue<string>(), principal, StringComparison.OrdinalIgnoreCase) ||
            item["appRoleId"]?.GetValue<string>() != Guid.Empty.ToString("D") ||
            item["principalType"]?.GetValue<string>() != "User" ||
            !Regex.IsMatch(Text(item, "id"), @"\A[A-Za-z0-9_-]{1,256}\z", RegexOptions.CultureInvariant)),
            "c1_deploy_vpn_assignment", "The VPN audience contains an unexpected user, group or application assignment.", 409);
        return assignments;
    }

    public JsonObject OperatorNetworkArmParameters()
    {
        Guard.Against(FabricC1Deployment.OperatorNetworkMissingInputs(input).Length != 0,
            "c1_deploy_operator_network", "The selected isolated operator network is not completely bound.", 409);
        var app = Owned(FabricC1Deployment.VpnIdentityRole + "-app", "Application");
        var principal = Owned(FabricC1Deployment.VpnIdentityRole + "-sp", "ServicePrincipal");
        SameId(Text(principal, "appId"), Text(app, "appId"));
        var assignment = Owned("operator-vpn-assignment", "OperatorVpnAssignment");
        SameId(Text(assignment, "resourceId"), Text(principal, "id"));
        SameId(Text(assignment, "principalId"), input.Network!.OperatorAccess!.OperatorObjectId!);
        var configuration = Owned("operator-vpn-configuration", "OperatorVpnConfiguration");
        Guard.Against(!JsonNode.DeepEquals(configuration["identifierUris"], new JsonArray("api://" + Text(app, "appId"))),
            "c1_deploy_vpn_application", "The original API identifier binding is missing.", 409);
        var parameters = FabricC1Deployment.ArmParameters(input);
        parameters.Remove("capacityAdministrators");
        parameters.Remove("compute");
        parameters["vpnAudience"] = new JsonObject { ["value"] = Text(app, "appId") };
        return parameters;
    }

    public JsonObject NetworkedFoundationArmParameters()
    {
        Guard.Against(FabricC1Deployment.OperatorNetworkMissingInputs(input).Length != 0,
            "c1_deploy_operator_network", "Foundation needs the complete selected operator network binding.", 409);
        var parameters = FabricC1Deployment.ArmParameters(input);
        parameters["firewallPrivateIp"] = new JsonObject { ["value"] = BoundFirewallPrivateAddress() };
        return parameters;
    }

    private string BoundFirewallPrivateAddress()
    {
        var network = Owned("arm-network-access", "ArmDeployment");
        Guard.Against(!SameArmId(Text(network, "id"), FabricC1Deployment.GroupId(input) +
            "/providers/Microsoft.Resources/deployments/c1-network-access") ||
            network["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_deploy_firewall_output", "Only the original completed network stage can supply the firewall address.", 409);
        var firewall = network["properties"]?["outputs"]?["firewall"]?["value"] as JsonObject ??
            throw new DomainException("c1_deploy_firewall_output", "The original network deployment has no firewall output.", 409);
        RequireArmOwnership(firewall);
        RequireTypedArmResource(firewall, "Microsoft.Network/azureFirewalls");
        var id = FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Network/azureFirewalls/" + FabricC1Deployment.Name(input) + "-firewall";
        Guard.Against(!string.Equals(Text(firewall, "id"), id, StringComparison.OrdinalIgnoreCase) ||
            firewall["properties"]?["sku"]?["name"]?.GetValue<string>() != "AZFW_VNet" ||
            firewall["properties"]?["sku"]?["tier"]?.GetValue<string>() != "Standard" ||
            firewall["properties"]?["provisioningState"]?.GetValue<string>() != "Succeeded",
            "c1_deploy_firewall_output", "The original firewall identity, SKU or provisioning state differs.", 409);
        var configurations = firewall["properties"]?["ipConfigurations"] as JsonArray;
        var resources = FabricC1Deployment.GroupId(input) + "/providers/Microsoft.Network/";
        var name = FabricC1Deployment.Name(input);
        Guard.Against(!SameArmId(firewall["properties"]?["firewallPolicy"]?["id"]?.GetValue<string>(),
                resources + "firewallPolicies/" + name + "-firewall-policy") ||
            firewall["properties"]?["autoscaleConfiguration"]?["minCapacity"]?.GetValue<int>() != 2 ||
            firewall["properties"]?["autoscaleConfiguration"]?["maxCapacity"]?.GetValue<int>() != 2 ||
            configurations is not { Count: 1 } ||
            !SameArmId(configurations[0]?["properties"]?["subnet"]?["id"]?.GetValue<string>(),
                resources + "virtualNetworks/" + name + "-vnet/subnets/AzureFirewallSubnet") ||
            !SameArmId(configurations[0]?["properties"]?["publicIPAddress"]?["id"]?.GetValue<string>(),
                resources + "publicIPAddresses/" + name + "-firewall-ip"),
            "c1_deploy_firewall_output", "Firewall policy, fixed capacity, subnet or public address binding differs.", 409);
        var address = configurations is { Count: 1 }
            ? configurations[0]?["properties"]?["privateIPAddress"]?.GetValue<string>() : null;
        Guard.Against(!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork ||
            input.Network?.OperatorAccess?.FirewallPrefix is not { } prefix || !IPNetwork.Parse(prefix).Contains(parsed),
            "c1_deploy_firewall_output", "The observed DNS/egress address is not in the own firewall subnet.", 409);
        return address!;
    }

    internal static JsonNode? ArmNetworkValue(FabricC1DeploymentNetwork? network)
    {
        var value = JsonSerializer.SerializeToNode(network, Canonical.Json);
        if (value?["operatorAccess"] is JsonObject access)
            foreach (var name in new[] { "operatorObjectId", "excludedPrefixes", "addressPlanReviewSha256", "clientPrerequisitesReviewSha256" })
                access.Remove(name);
        return value;
    }
}
