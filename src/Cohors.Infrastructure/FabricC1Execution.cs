using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Cohors.Domain;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Cohors.Infrastructure;

public sealed record FabricC1HostedBinding(string AgentId, string AgentName, string SessionId, string PlatformUserId,
    string EndpointReviewSha256, string InvocationEndpoint, FabricC1Identity Caller, string[] PrivateAddresses);
public sealed record FabricC1ExecutionSettings(string Contract, FabricC1Inputs Preparation, string RunId,
    string SourceCommit, DateTimeOffset ExpiresAtUtc, string AccessReviewSha256, string[] FabricPrivateAddresses,
    string TaskDialect, int DirectMaxRequests, int HostedMaxRequests, int HostedControlMaxRequests,
    FabricC1HostedBinding? Hosted);
public sealed record FabricC1Invocation(string BindingHash, string Operation, string? ProbeId = null, string? InvocationId = null);
public sealed record FabricC1Observation(string Status, string? Code, string BindingHash, string StateHash,
    int Requests, string Runtime, JsonElement? Result = null, string? InvocationId = null);

public static class FabricC1Execution
{
    public const string HostedScope = "https://ai.azure.com/.default";
    public const string TaskDialect = "mcp-2025-11-25";

    public static string Validate(FabricC1ExecutionSettings settings, DateTimeOffset now, bool hosted = false, bool allowCleanup = false)
    {
        if (settings.Preparation is null)
            throw new DomainException("c1_execution_inputs", "Preparation bindings are required.", 409);
        var check = FabricC1ProbeClient.Preflight(settings.Preparation, discoveryOnly: true);
        Guard.Against(check.Status != "PreparedOffline", "c1_inputs_missing",
            "C1 execution requires the reviewed preparation bindings.", 409);
        Guard.Against(settings.Preparation.Contract != FabricC1ProbeClient.Contract,
            "c1_model_contract", "Historical model metadata is offline-only; rebind the Fabric-managed AI contract and use a new checkpoint.", 409);
        Guard.Against(settings.Contract != "cohors-fabric-c1-execution-1" ||
            !Guid.TryParse(settings.RunId, out var run) || run == Guid.Empty ||
            !Hex(settings.SourceCommit, 40) || !Hex(settings.AccessReviewSha256, 64) ||
            settings.ExpiresAtUtc.AddSeconds(allowCleanup ? settings.Preparation.Limits.CleanupTimeoutSeconds!.Value : 0) <= now ||
            settings.TaskDialect is not ("none" or TaskDialect) ||
            settings.DirectMaxRequests < 4 || settings.HostedMaxRequests < 4 || settings.HostedControlMaxRequests < 1 ||
            (long)settings.DirectMaxRequests + settings.HostedMaxRequests + settings.HostedControlMaxRequests >
                settings.Preparation.Limits.MaxRequests,
            "c1_execution_inputs", "Run, source, access review, expiry, protocol and split request budgets must be bound.", 409);
        PrivateAddresses(settings.FabricPrivateAddresses);
        var endpoint = new Uri(settings.Preparation.Target.PublishedMcpEndpoint!);
        Guard.Against(endpoint.Port != 443 || IPAddress.TryParse(endpoint.Host, out _) ||
            endpoint.Host.Equals("api.fabric.microsoft.com", StringComparison.OrdinalIgnoreCase),
            "c1_private_endpoint", "Use the reviewed private workspace FQDN, not a public endpoint or IP URL.", 409);
        if (hosted)
        {
            var binding = settings.Hosted ?? throw new DomainException("c1_hosted_binding", "Hosted bindings are missing.", 409);
            Guard.Against(!Guid.TryParse(binding.AgentId, out var agent) || agent == Guid.Empty ||
                string.IsNullOrWhiteSpace(binding.AgentName) || binding.AgentName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                !HeaderValue(binding.SessionId) || !HeaderValue(binding.PlatformUserId) ||
                !Hex(binding.EndpointReviewSha256, 64), "c1_hosted_binding", "Exact Hosted endpoint/session/identity review required.", 409);
            PrivateAddresses(binding.PrivateAddresses);
            var callerCheck = FabricC1ProbeClient.Preflight(settings.Preparation with { Identity = binding.Caller }, discoveryOnly: true);
            Guard.Against(callerCheck.Status != "PreparedOffline", "c1_hosted_identity", "Explicit Hosted caller binding required.", 409);
            var expected = settings.Preparation.Runtime.HostedProjectEndpoint!.TrimEnd('/') +
                $"/agents/{binding.AgentName}/endpoint/protocols/invocations?api-version=v1&agent_session_id={Uri.EscapeDataString(binding.SessionId)}";
            Guard.Against(binding.InvocationEndpoint != expected ||
                !Uri.TryCreate(expected, UriKind.Absolute, out var hostedEndpoint) || hostedEndpoint.Port != 443,
                "c1_hosted_endpoint", "Use the bound v1 Invocations endpoint and existing session; do not create or reroute sessions.", 409);
        }
        return Canonical.Hash(settings);
    }

    public static bool Hex(string? value, int length) =>
        value is not null && value.Length == length && value.All(char.IsAsciiHexDigit);

    public static bool HeaderValue(string? value) =>
        value is { Length: > 0 and <= 4096 } && value.All(c => c is >= '!' and <= '~');

    public static void RequireSourceBuild(FabricC1ExecutionSettings settings) => RequireSourceBuild(settings.SourceCommit);

    public static void RequireSourceBuild(string sourceCommit)
    {
        var version = typeof(FabricC1Execution).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Guard.Against(version is null || !version.EndsWith("+" + sourceCommit, StringComparison.OrdinalIgnoreCase),
            "c1_binary_binding", "The executable was not built from the bound source revision.", 409);
    }

    public static IPAddress[] PrivateAddresses(string[]? values)
    {
        Guard.Against(values is null || values.Length is < 1 or > 16, "c1_private_addresses", "A bounded private address allowlist is required.", 409);
        var addresses = new List<IPAddress>();
        foreach (var value in values!)
        {
            if (!IPAddress.TryParse(value, out var address) || !IsPrivate(address))
                throw new DomainException("c1_private_addresses", "Only explicit RFC1918 or IPv6 unique-local endpoint addresses are allowed.", 409);
            addresses.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }
        return addresses.Distinct().ToArray();
    }

    public static void ValidateResolution(IPAddress[] resolved, IPAddress[] approved)
    {
        Guard.Against(resolved.Length == 0 || resolved.Any(address =>
            !IsPrivate(address) || !approved.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)),
            "c1_private_dns", "DNS returned an unapproved or public address. No connection or public fallback.", 409);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }

    public static HttpClient PrivateHttpClient(Uri endpoint, string[] privateAddresses, int timeoutSeconds)
    {
        var approved = PrivateAddresses(privateAddresses);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, ct) =>
            {
                Guard.Against(!context.DnsEndPoint.Host.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != 443, "c1_endpoint_drift", "Connection target differs from the reviewed endpoint.", 409);
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                ValidateResolution(addresses, approved);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

public sealed class FabricC1Credential : IDisposable
{
    private readonly HttpClient authHttp;
    private readonly X509Certificate2? certificate;
    public TokenCredential Credential { get; }

    public FabricC1Credential(FabricC1Identity identity, int timeoutSeconds)
    {
        Guard.Against(identity.CredentialReference is null || !Path.IsPathFullyQualified(identity.CredentialReference),
            "c1_credential_reference", "Credential reference must be an explicit protected mounted file.", 409);
        authHttp = new(new AuthorityHandler(identity.TenantId!)
        {
            InnerHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }
        }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var transport = new HttpClientTransport(authHttp);
        switch (identity.CredentialKind)
        {
            case "ServicePrincipalCertificate":
                certificate = X509Certificate2.CreateFromPem(ReadMaterial(identity.CredentialReference!));
                Guard.Against(!certificate.HasPrivateKey, "c1_credential_certificate", "Mounted PEM must contain the reviewed certificate and private key.", 409);
                var certificateOptions = new ClientCertificateCredentialOptions { DisableInstanceDiscovery = true, Transport = transport };
                certificateOptions.Retry.MaxRetries = 0;
                Credential = new ClientCertificateCredential(identity.TenantId, identity.ClientId, certificate, certificateOptions);
                break;
            case "ServicePrincipalFederation":
                var assertionOptions = new ClientAssertionCredentialOptions { DisableInstanceDiscovery = true, Transport = transport };
                assertionOptions.Retry.MaxRetries = 0;
                Credential = new ClientAssertionCredential(identity.TenantId, identity.ClientId,
                    _ => Task.FromResult(ReadMaterial(identity.CredentialReference!)), assertionOptions);
                break;
            case "ServicePrincipalSecret":
                var secretOptions = new ClientSecretCredentialOptions { DisableInstanceDiscovery = true, Transport = transport };
                secretOptions.Retry.MaxRetries = 0;
                Credential = new ClientSecretCredential(identity.TenantId, identity.ClientId,
                    ReadMaterial(identity.CredentialReference!), secretOptions);
                break;
            default:
                throw new DomainException("c1_credential_kind", "No credential chain, user/OBO/CLI or managed-identity fallback is permitted.", 409);
        }
    }

    private static string ReadMaterial(string path)
    {
        using var stream = File.OpenRead(path);
        Guard.Against(stream.Length is <= 0 or > 65536, "c1_credential_file", "Credential file is absent, empty or oversized.", 409);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    public void Dispose()
    {
        certificate?.Dispose();
        authHttp.Dispose();
    }

    private sealed class AuthorityHandler(string tenant) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri;
            Guard.Against(uri is null || uri.Scheme != "https" || uri.Port != 443 ||
                uri.IdnHost != "login.microsoftonline.com" || !uri.AbsolutePath.StartsWith($"/{tenant}/", StringComparison.OrdinalIgnoreCase),
                "c1_authority_drift", "Only the bound tenant on the Azure public-cloud Entra authority is allowed.", 409);
            return base.SendAsync(request, ct);
        }
    }
}

public sealed class FabricC1AuthenticatedHttp(HttpClient client, TokenCredential credential,
    FabricC1Identity identity, string scope, Uri endpoint, TimeProvider time)
{
    public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        Guard.Against(request.RequestUri != endpoint, "c1_endpoint_drift", "Request target changed.", 409);
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
        ValidateToken(token, identity, time.GetUtcNow());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.RequestMessage?.RequestUri != endpoint || (int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new DomainException("c1_redirect", "Redirects and endpoint changes are prohibited.", 409);
        }
        return response;
    }

    // A consistency check on a token acquired with explicit client credentials, not signature or service-side identity proof.
    public static void ValidateToken(AccessToken token, FabricC1Identity expected, DateTimeOffset now)
    {
        if (token.Token is not { Length: > 0 and <= 65536 } || token.ExpiresOn <= now)
            throw new DomainException("c1_token", "Missing or expired app-only token.", 401);
        JsonWebToken jwt;
        try { jwt = new JsonWebToken(token.Token); }
        catch (ArgumentException) { throw new DomainException("c1_token", "The token is not a readable JWT; no endpoint call was made.", 401); }
        string? Claim(string name) => jwt.TryGetClaim(name, out var claim) ? claim.Value : null;
        bool SameId(string? value, string? bound) => Guid.TryParse(value, out var left) &&
            Guid.TryParse(bound, out var right) && left == right && left != Guid.Empty;
        Guard.Against(jwt.Issuer != expected.ExpectedIssuer ||
            jwt.Audiences.Count() != 1 || jwt.Audiences.Single() != expected.ExpectedAudience ||
            !SameId(Claim("tid"), expected.TenantId) || !SameId(Claim("oid"), expected.PrincipalObjectId) ||
            !SameId(Claim("azp") ?? Claim("appid"), expected.ClientId) ||
            Claim("scp") is not null || Claim("idtyp") is { } type && type != "app" ||
            jwt.ValidTo <= now.UtcDateTime || jwt.ValidFrom > now.UtcDateTime,
            "c1_token_binding", "Token issuer/audience/tenant/caller/lifetime differs from the explicit app-only binding.", 401);
    }
}

public sealed class FabricC1LimitedStream(Stream inner, int maximum) : Stream
{
    private int remaining = maximum;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private int Count(int read)
    {
        remaining -= read;
        Guard.Against(remaining < 0, "c1_response_limit", "Response exceeded the approved byte limit.", 409);
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, (int)Math.Min(count, (long)remaining + 1)));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Count(await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, (long)remaining + 1)], ct));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
