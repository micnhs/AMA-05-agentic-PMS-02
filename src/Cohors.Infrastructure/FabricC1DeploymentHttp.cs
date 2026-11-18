using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Cohors.Domain;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Cohors.Infrastructure;

public sealed class FabricC1DeploymentHttp(FabricC1DeploymentInputs input, TokenCredential credential,
    IReadOnlyDictionary<string, HttpClient> clients, TimeProvider? time = null) : IFabricC1DeploymentApi
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public async Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null,
        string? ifMatch = null, CancellationToken ct = default)
    {
        var (scope, audience, issuer) = Binding(method, uri);
        if (uri.IdnHost == "api.fabric.microsoft.com" &&
            uri.AbsolutePath == "/v1/admin/tenantsettings/" + FabricC1Provisioner.WorkspaceInboundSettingName + "/update")
            Guard.Against(method != HttpMethod.Post || body?["enabled"] is not JsonValue enabled ||
                !enabled.TryGetValue<bool>(out var value) || !value ||
                body!["enabledSecurityGroups"] is not (null or JsonArray { Count: 0 }) ||
                body["excludedSecurityGroups"] is not (null or JsonArray { Count: 0 }),
                "c1_deploy_shared_setting_scope", "C1 cannot disable shared inbound configuration or pretend it is group-scoped.", 409);
        if (!clients.TryGetValue(uri.IdnHost, out var client))
            throw new DomainException("c1_deploy_private_route", "No approved private/control client for this exact host; no public fallback.", 409);
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
        ValidateBootstrapToken(token, input.BootstrapIdentity!, audience, clock.GetUtcNow(),
            uri.IdnHost == "graph.microsoft.com", GraphOperation(method, uri), issuer);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new("application/json"));
        if (ifMatch is not null) request.Headers.IfMatch.ParseAdd(ifMatch);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(Canonical.Json), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Guard.Against(response.RequestMessage?.RequestUri != uri || (int)response.StatusCode is >= 300 and < 400,
            "c1_deploy_redirect", "Control/private endpoint changed; no redirect is followed.", 409);
        Guard.Against(response.Content.Headers.ContentLength > 2 * 1024 * 1024, "c1_deploy_response_size", "Provider response exceeds limit.", 409);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var bounded = new FabricC1LimitedStream(stream, 2 * 1024 * 1024);
        using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = await reader.ReadToEndAsync(ct);
        var data = string.IsNullOrWhiteSpace(text) ? new JsonObject() :
            JsonNode.Parse(text) as JsonObject ?? throw new DomainException("c1_deploy_response_shape", "Provider returned non-object data.", 409);
        var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        if (retry is null && response.Headers.RetryAfter?.Date is { } date) retry = (date - clock.GetUtcNow()).TotalSeconds;
        return new((int)response.StatusCode, data, response.Headers.ETag?.ToString(),
            response.Headers.Location?.ToString()) { RetryAfterSeconds = retry is null ? null : (int)Math.Clamp(retry.Value, 0, 30) };
    }

    public static HttpClient ControlClient(int timeoutSeconds) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false
    }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private (string Scope, string Audience, string? Issuer) Binding(HttpMethod method, Uri uri)
    {
        Guard.Against(uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0,
            "c1_deploy_endpoint", "Only exact HTTPS API bindings are permitted.", 409);
        switch (uri.IdnHost)
        {
            case "management.azure.com":
                var prefix = "/subscriptions/" + input.SubscriptionId;
                Guard.Against(!uri.AbsolutePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase),
                    "c1_deploy_context", "ARM request left the selected subscription.", 409);
                RequireControlTokenBindings();
                return ("https://management.azure.com/.default", input.ControlTokenBindings!.ArmAudience!, input.ControlTokenBindings.ArmIssuer);
            case "graph.microsoft.com":
                Guard.Against(!GraphPathAllowed(method, uri.AbsolutePath),
                    "c1_deploy_graph_scope", "Graph bootstrap does not mutate consent, tenant policy or roles.", 409);
                RequireControlTokenBindings();
                return ("https://graph.microsoft.com/.default", input.ControlTokenBindings!.GraphAudience!, input.ControlTokenBindings.GraphIssuer);
            case "api.fabric.microsoft.com":
                Guard.Against(!(uri.AbsolutePath is "/v1/workspaces" or "/v1/capacities" or "/v1/admin/tenantsettings" ||
                    System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                        "^/v1/admin/tenantsettings/[a-zA-Z0-9_-]+/update$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                    System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                        "^/v1/(workspaces/[0-9a-fA-F-]{36}(/networking/communicationPolicy)?|operations/[0-9a-fA-F-]{36}(/result)?)$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
                    "c1_deploy_public_data_path", "Only documented Fabric control-plane operations may use the public control host.", 409);
                return (FabricC1ProbeClient.TokenScope, input.BootstrapIdentity!.ExpectedAudience ??
                    throw new DomainException("c1_deploy_audience", "Explicit Fabric bootstrap token audience is missing.", 409),
                    input.BootstrapIdentity.ExpectedIssuer);
            default:
                Guard.Against(!System.Text.RegularExpressions.Regex.IsMatch(uri.IdnHost,
                    "^[0-9a-f]{32}\\.z[0-9a-f]{2}\\.w\\.api\\.fabric\\.microsoft\\.com$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                    "c1_deploy_host", "Unexpected bootstrap endpoint.", 409);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var operationPath = System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                    "^/v1/operations/[0-9a-fA-F-]{36}(/result)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                Guard.Against(!operationPath && (segments.Length < 3 || segments[0] != "v1" || segments[1] != "workspaces" ||
                    !Guid.TryParse(segments[2], out var id) || uri.IdnHost != FabricC1Provisioner.WorkspaceHost(id.ToString("D"))),
                    "c1_deploy_workspace_binding", "Workspace host and item path differ.", 409);
                return (FabricC1ProbeClient.TokenScope, input.BootstrapIdentity!.ExpectedAudience ??
                    throw new DomainException("c1_deploy_audience", "Explicit Fabric bootstrap token audience is missing.", 409),
                    input.BootstrapIdentity.ExpectedIssuer);
        }
    }

    // Explicit bootstrap user credentials are not used by the app-only Fabric probe or Hosted runtime.
    public static void ValidateBootstrapToken(AccessToken token, FabricC1Identity identity, string audience, DateTimeOffset now,
        bool graph, FabricC1GraphOperation operation = FabricC1GraphOperation.OwnApplications, string? expectedIssuer = null)
    {
        if (token.Token is not { Length: > 0 and <= 65536 } || token.ExpiresOn <= now)
            throw new DomainException("c1_deploy_token", "Missing or expired deployment token.", 401);
        JsonWebToken jwt;
        try { jwt = new(token.Token); }
        catch (ArgumentException) { throw new DomainException("c1_deploy_token", "Unreadable deployment token.", 401); }
        string? Claim(string name) => jwt.TryGetClaim(name, out var claim) ? claim.Value : null;
        bool Same(string? value, string? expected) => Guid.TryParse(value, out var left) &&
            Guid.TryParse(expected, out var right) && left == right && left != Guid.Empty;
        var delegated = identity.CredentialKind == "DelegatedAzureCli";
        Guard.Against(!Same(Claim("tid"), identity.TenantId) || !Same(Claim("oid"), identity.PrincipalObjectId) ||
            !Same(Claim("azp") ?? Claim("appid"), identity.ClientId) ||
            jwt.Audiences.Count() != 1 || jwt.Audiences.Single() != audience ||
            jwt.Issuer != $"https://sts.windows.net/{identity.TenantId}/" &&
            jwt.Issuer != $"https://login.microsoftonline.com/{identity.TenantId}/v2.0" ||
            expectedIssuer is not null && jwt.Issuer != expectedIssuer ||
            audience == identity.ExpectedAudience && identity.ExpectedIssuer is not null && jwt.Issuer != identity.ExpectedIssuer ||
            jwt.ValidTo <= now.UtcDateTime || jwt.ValidFrom > now.UtcDateTime ||
            delegated != (Claim("scp") is not null),
            "c1_deploy_token_binding", "Deployment token differs from the explicit tenant/caller/audience/mode.", 401);
        if (graph)
        {
            var permissions = delegated ? Claim("scp")!.Split(' ', StringSplitOptions.RemoveEmptyEntries) :
                jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToArray();
            new FabricC1GraphRights(delegated, permissions).Require(operation);
        }
    }

    public async Task<FabricC1GraphRights> GraphRights(CancellationToken ct)
    {
        RequireControlTokenBindings();
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
        ValidateBootstrapToken(token, input.BootstrapIdentity!, input.ControlTokenBindings!.GraphAudience!, clock.GetUtcNow(),
            graph: false, expectedIssuer: input.ControlTokenBindings.GraphIssuer);
        var jwt = new JsonWebToken(token.Token);
        var delegated = input.BootstrapIdentity!.CredentialKind == "DelegatedAzureCli";
        return new(delegated, delegated ? jwt.GetClaim("scp").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) :
            jwt.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value).ToArray());
    }

    private void RequireControlTokenBindings() =>
        Guard.Against(!FabricC1Deployment.ValidControlTokenBindings(input), "c1_deploy_control_token_binding",
            "ARM and Graph audience/issuer must be explicitly bound to observed service token metadata, not request scopes.", 409);

    private static bool GraphPathAllowed(HttpMethod method, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "v1.0") return false;
        if (parts[1] == "me") return parts.Length == 2 && method == HttpMethod.Get;
        if (parts[1] is not ("applications" or "servicePrincipals" or "groups")) return false;
        if (parts.Length == 2) return method == HttpMethod.Get || method == HttpMethod.Post;
        if (!Guid.TryParse(parts[2], out var id) || id == Guid.Empty) return false;
        if (parts.Length == 3) return method == HttpMethod.Get || method == HttpMethod.Delete;
        if (parts.Length == 4 && method == HttpMethod.Get)
            return parts[1] is "applications" or "groups" && parts[3] == "owners" ||
                parts[1] == "servicePrincipals" && parts[3] == "ownedObjects";
        if (parts[1] != "groups" || parts[3] != "members") return false;
        return parts.Length == 5 && parts[4] == "$ref" && method == HttpMethod.Post ||
            parts.Length == 6 && Guid.TryParse(parts[4], out var member) && member != Guid.Empty &&
            parts[5] == "$ref" && method == HttpMethod.Delete;
    }

    private static FabricC1GraphOperation GraphOperation(HttpMethod method, Uri uri)
    {
        if (uri.AbsolutePath == "/v1.0/me") return FabricC1GraphOperation.UserRead;
        if (uri.AbsolutePath.StartsWith("/v1.0/groups", StringComparison.Ordinal))
            return method == HttpMethod.Get ? FabricC1GraphOperation.GroupRead :
                uri.AbsolutePath == "/v1.0/groups" && method == HttpMethod.Post ? FabricC1GraphOperation.GroupCreate :
                method == HttpMethod.Delete && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 3 ?
                    FabricC1GraphOperation.GroupDelete :
                FabricC1GraphOperation.GroupMembers;
        return uri.AbsolutePath == "/v1.0/applications" && method == HttpMethod.Post ?
            FabricC1GraphOperation.ApplicationCreate : FabricC1GraphOperation.OwnApplications;
    }
}

public sealed class FabricC1DeploymentFileJournal : IFabricC1DeploymentJournal, IDisposable
{
    private readonly string path;
    private readonly FileStream lease;
    private const int MaximumBytes = 4 * 1024 * 1024;
    public FabricC1DeploymentFileJournal(string path)
    {
        Guard.Against(!Path.IsPathFullyQualified(path) || !Directory.Exists(Path.GetDirectoryName(path)) ||
            File.Exists(path) && new FileInfo(path).LinkTarget is not null ||
            File.Exists(path + ".lock") && new FileInfo(path + ".lock").LinkTarget is not null,
            "c1_deploy_journal_path", "Existing protected directory and non-link absolute journal path required.", 409);
        this.path = path;
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public FabricC1DeploymentJournal? Load()
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        Guard.Against(file.Length > MaximumBytes, "c1_deploy_journal_size", "Deployment journal exceeds limit.", 409);
        return JsonSerializer.Deserialize<FabricC1DeploymentJournal>(file, Canonical.Json) ??
            throw new DomainException("c1_deploy_journal_invalid", "Invalid journal; never start over silently.", 409);
    }
    public void Save(FabricC1DeploymentJournal state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Canonical.Json);
        Guard.Against(bytes.Length > MaximumBytes, "c1_deploy_journal_size", "Deployment journal exceeds limit.", 409);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    public void Dispose() => lease.Dispose();
}
