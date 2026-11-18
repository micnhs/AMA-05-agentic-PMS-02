using System.Text.Json;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Cohors.Api;

public static class FabricC1Entry
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "check" && args[1] == "--preparation")
            {
                var preparation = Canonical.Read<FabricC1Inputs>(ReadSmall(args[2], 65536));
                var check = FabricC1ProbeClient.Preflight(preparation, discoveryOnly: true);
                Console.WriteLine(JsonSerializer.Serialize(check, Canonical.Json));
                return check.Status == "Blocked" ? 2 : 0;
            }
            Guard.Against(args.Length < 7 || args[1] != "--inputs" || args[3] != "--fixture" || args[5] != "--state",
                "c1_usage", "Use check --preparation FILE, or OP --inputs FILE --fixture FILE --state FILE [--probe ID]; hosted uses --operation OP.", 400);
            var settings = Canonical.Read<FabricC1ExecutionSettings>(ReadSmall(args[2], 65536));
            FabricC1Execution.Validate(settings, DateTimeOffset.UtcNow, hosted: args[0] is "host" or "hosted", allowCleanup: true);
            FabricC1Execution.RequireSourceBuild(settings);
            Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "c1_cloud_ci_prohibited", "The credential-free build runner is never a C1 service runner.", 409);
            var fixture = ReadSmall(args[4], 65536);
            Guard.Against(Canonical.HashText(fixture) != settings.Preparation.Source.FixtureSha256,
                "c1_fixture", "Fixture bytes differ from the bound synthetic release.", 409);
            if (args[0] == "host")
            {
                Guard.Against(args.Length != 7, "c1_usage", "Hosted server accepts only fixed startup files.", 400);
                await Host(settings, fixture, args[6], args[2]);
                return 0;
            }
            var remote = args[0] == "hosted";
            var operation = remote
                ? args.Length >= 9 && args[7] == "--operation" ? args[8] : throw new DomainException("c1_usage", "Hosted operation is required.", 400)
                : args[0];
            var probeIndex = remote ? 9 : 7;
            string? probe = null;
            if (args.Length != probeIndex)
            {
                Guard.Against(args.Length != probeIndex + 2 || args[probeIndex] != "--probe", "c1_usage", "Unexpected C1 arguments.", 400);
                probe = args[probeIndex + 1];
            }
            var command = new FabricC1Invocation(Canonical.Hash(settings), operation, probe, Guid.NewGuid().ToString("D"));
            var result = remote
                ? await InvokeHosted(settings, command, args[6])
                : await InvokeDirect(settings, fixture, command, args[6], "direct", CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new { result.Status, result.Code, result.BindingHash,
                result.StateHash, result.Requests, result.Runtime, result.InvocationId,
                evidenceLevel = "transport-observation-not-capability-acceptance" }, Canonical.Json));
            return result.Status is "Discovered" or "Observed" or "SessionClosed" ? 0 : 2;
        }
        catch (DomainException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Blocked", code = error.Code }, Canonical.Json));
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            InvalidOperationException or Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Failed", code = "c1_entry_failed", errorType = error.GetType().Name }, Canonical.Json));
            return 3;
        }
    }

    public static async Task<FabricC1Observation> InvokeDirect(FabricC1ExecutionSettings settings, string fixture,
        FabricC1Invocation command, string statePath, string runtime, CancellationToken ct)
    {
        FabricC1Execution.Validate(settings, DateTimeOffset.UtcNow, hosted: runtime == "hosted", allowCleanup: true);
        using var store = StateStore(settings, statePath);
        using var credential = new FabricC1Credential(settings.Preparation.Identity,
            settings.Preparation.Limits.MaxDurationSeconds!.Value);
        using var client = FabricC1Execution.PrivateHttpClient(new(settings.Preparation.Target.PublishedMcpEndpoint!),
            settings.FabricPrivateAddresses, settings.Preparation.Limits.MaxDurationSeconds.Value);
        var http = new FabricC1AuthenticatedHttp(client, credential.Credential, settings.Preparation.Identity,
            FabricC1ProbeClient.TokenScope, new(settings.Preparation.Target.PublishedMcpEndpoint!), TimeProvider.System);
        var transport = new FabricC1Transport(settings, fixture, runtime, http, store);
        return await transport.Execute(command, ct);
    }

    private static async Task<FabricC1Observation> InvokeHosted(FabricC1ExecutionSettings settings,
        FabricC1Invocation command, string statePath)
    {
        using var store = StateStore(settings, statePath);
        var hosted = settings.Hosted!;
        using var credential = new FabricC1Credential(hosted.Caller, settings.Preparation.Limits.MaxDurationSeconds!.Value);
        using var client = FabricC1Execution.PrivateHttpClient(new(hosted.InvocationEndpoint), hosted.PrivateAddresses,
            settings.Preparation.Limits.MaxDurationSeconds.Value);
        var http = new FabricC1AuthenticatedHttp(client, credential.Credential, hosted.Caller, FabricC1Execution.HostedScope,
            new(hosted.InvocationEndpoint), TimeProvider.System);
        return await new FabricC1HostedClient(settings, http, store).Invoke(command);
    }

    private static FabricC1FileCheckpointStore StateStore(FabricC1ExecutionSettings settings, string path) =>
        new(path, 8L * settings.Preparation.Limits.MaxResponseBytes!.Value + 2048L * settings.Preparation.Limits.MaxRequests!.Value);

    public static void ValidateHostedEnvironment(FabricC1ExecutionSettings settings, Func<string, string?> environment,
        string configurationPath, string statePath)
    {
        var hosted = settings.Hosted ?? throw new DomainException("c1_hosted_binding", "Hosted bindings are required.", 409);
        Guard.Against(environment("FOUNDRY_PROJECT_ENDPOINT")?.TrimEnd('/') != settings.Preparation.Runtime.HostedProjectEndpoint?.TrimEnd('/') ||
            environment("FOUNDRY_AGENT_ID") != hosted.AgentId || environment("FOUNDRY_AGENT_NAME") != hosted.AgentName ||
            environment("FOUNDRY_AGENT_VERSION") != settings.Preparation.Runtime.HostedAgentVersion ||
            environment("FOUNDRY_AGENT_SESSION_ID") != hosted.SessionId,
            "c1_hosted_environment", "Platform project, agent/version and session differ from the reviewed deployment.", 409);
        var home = environment("HOME");
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            throw new DomainException("c1_hosted_trust", "An absolute session HOME is required.", 409);
        var homePath = Path.GetFullPath(home);
        var prefix = Path.EndsInDirectorySeparator(homePath) ? homePath : homePath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in new[] { configurationPath, settings.Preparation.Identity.CredentialReference, statePath })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                Path.GetFullPath(path).Equals(Path.TrimEndingDirectorySeparator(homePath), comparison) ||
                Path.GetFullPath(path).StartsWith(prefix, comparison))
                throw new DomainException("c1_hosted_trust",
                    "Trusted configuration, credential and checkpoint files must be outside upload-writable session HOME.", 409);
        }
    }

    private static async Task Host(FabricC1ExecutionSettings settings, string fixture, string statePath, string configurationPath)
    {
        ValidateHostedEnvironment(settings, Environment.GetEnvironmentVariable, configurationPath, statePath);
        using (var store = StateStore(settings, statePath)) { store.Load(); }
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 4096;
            options.ListenAnyIP(8088, listen => listen.Protocols = HttpProtocols.Http1);
        });
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(settings.Preparation.Limits.CleanupTimeoutSeconds!.Value));
        var app = builder.Build();
        app.MapGet("/readiness", () => Results.Ok(new { status = "probe-host-ready", capabilityAccepted = false }));
        app.MapPost("/invocations", async (HttpContext context) =>
        {
            if (context.Request.Headers["x-agent-user-id"].ToString() != settings.Hosted!.PlatformUserId ||
                !FabricC1Execution.HeaderValue(context.Request.Headers["x-agent-foundry-call-id"].ToString()))
                return Results.StatusCode(403);
            try
            {
                var command = await JsonSerializer.DeserializeAsync<FabricC1Invocation>(context.Request.Body, Canonical.Json, context.RequestAborted);
                Guard.Against(command is null || !Guid.TryParse(command.InvocationId, out var id) || id == Guid.Empty,
                    "c1_invocation", "A bound invocation ID is required.", 400);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                var observation = await InvokeDirect(settings, fixture, command!, statePath, "hosted", lifetime.Token);
                return Results.Json(observation, Canonical.Json);
            }
            catch (DomainException error) { return Results.Json(new { status = "Blocked", code = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is IOException or JsonException or Azure.Identity.AuthenticationFailedException or
                Azure.Identity.CredentialUnavailableException or UnauthorizedAccessException)
            {
                return Results.Json(new { status = "Failed", code = "c1_hosted_handler_failed", errorType = error.GetType().Name }, statusCode: 503);
            }
        });
        await app.RunAsync();
    }

    private static string ReadSmall(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        Guard.Against(stream.Length > maximum, "c1_input_size", "Input exceeds the bounded configuration/fixture size.", 409);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
