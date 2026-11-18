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
        var (scope, audience) = Binding(uri);
        if (!clients.TryGetValue(uri.IdnHost, out var client))
            throw new DomainException("c1_deploy_private_route", "No approved private/control client for this exact host; no public fallback.", 409);
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
        ValidateBootstrapToken(token, input.BootstrapIdentity!, audience, clock.GetUtcNow(), uri.IdnHost == "graph.microsoft.com");
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

    private (string Scope, string Audience) Binding(Uri uri)
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
                return ("https://management.azure.com/.default", "https://management.core.windows.net/");
            case "graph.microsoft.com":
                Guard.Against(!(uri.AbsolutePath == "/v1.0/me" || uri.AbsolutePath == "/v1.0/applications" ||
                    uri.AbsolutePath.StartsWith("/v1.0/applications/", StringComparison.Ordinal) ||
                    uri.AbsolutePath == "/v1.0/servicePrincipals" ||
                    uri.AbsolutePath.StartsWith("/v1.0/servicePrincipals/", StringComparison.Ordinal)),
                    "c1_deploy_graph_scope", "Graph bootstrap does not mutate consent, tenant policy or roles.", 409);
                return ("https://graph.microsoft.com/.default", "00000003-0000-0000-c000-000000000000");
            case "api.fabric.microsoft.com":
                Guard.Against(!(uri.AbsolutePath is "/v1/workspaces" or "/v1/capacities" ||
                    System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                        "^/v1/(workspaces/[0-9a-fA-F-]{36}(/networking/communicationPolicy)?|operations/[0-9a-fA-F-]{36}(/result)?)$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)),
                    "c1_deploy_public_data_path", "Only documented Fabric control-plane operations may use the public control host.", 409);
                return (FabricC1ProbeClient.TokenScope, input.BootstrapIdentity!.ExpectedAudience ??
                    throw new DomainException("c1_deploy_audience", "Explicit Fabric bootstrap token audience is missing.", 409));
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
                    throw new DomainException("c1_deploy_audience", "Explicit Fabric bootstrap token audience is missing.", 409));
        }
    }

    // Explicit bootstrap user credentials are not used by the app-only Fabric probe or Hosted runtime.
    public static void ValidateBootstrapToken(AccessToken token, FabricC1Identity identity, string audience, DateTimeOffset now, bool graph)
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
            jwt.ValidTo <= now.UtcDateTime || jwt.ValidFrom > now.UtcDateTime ||
            delegated != (Claim("scp") is not null),
            "c1_deploy_token_binding", "Deployment token differs from the explicit tenant/caller/audience/mode.", 401);
        if (graph)
        {
            var permissions = delegated ? Claim("scp")!.Split(' ', StringSplitOptions.RemoveEmptyEntries) :
                jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToArray();
            Guard.Against(!permissions.Contains("Application.ReadWrite.All", StringComparer.Ordinal),
                "c1_deploy_graph_rights", "AppRegistration.Create alone does not cover this app/SP/certificate bootstrap.", 403);
        }
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
