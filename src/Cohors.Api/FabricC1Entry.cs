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
            Guard.Against(args.Length < 7 || args[1] != "--inputs" || args[3] != "--fixture" ||
                args[5] is not ("--state" or "--checkpoint-profile"),
                "c1_usage", "Use check --preparation FILE, or OP --inputs FILE --fixture FILE with --state FILE or --checkpoint-profile FILE --allowance FILE; hosted uses --operation OP. Host startup has no allowance.", 400);
            Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "c1_cloud_ci_prohibited", "The credential-free build runner is never a C1 service runner.", 409);
            var blob = args[5] == "--checkpoint-profile";
            if (blob)
            {
                FabricC1PrivateFiles.RequireFile(args[2]);
                FabricC1PrivateFiles.RequireFile(args[4]);
            }
            var settings = Canonical.Read<FabricC1ExecutionSettings>(ReadSmall(args[2], 65536));
            FabricC1Execution.Validate(settings, DateTimeOffset.UtcNow, hosted: args[0] is "host" or "hosted", allowCleanup: true);
            FabricC1Execution.RequireSourceBuild(settings);
            var fixture = ReadSmall(args[4], 65536);
            Guard.Against(Canonical.HashText(fixture) != settings.Preparation.Source.FixtureSha256,
                "c1_fixture", "Fixture bytes differ from the bound synthetic release.", 409);
            var profile = blob ? ReadProtected<FabricC1BlobCheckpointProfile>(args[6]) : null;
            if (profile is not null)
                FabricC1CheckpointProfiles.Validate(settings, profile,
                    args[0] == "host" ? "hosted" : args[0] == "hosted" ? "hosted-control" : "direct", DateTimeOffset.UtcNow);
            if (args[0] == "host")
            {
                Guard.Against(args.Length != 7, "c1_usage", "Hosted server accepts only fixed startup files.", 400);
                await Host(settings, fixture, args[6], args[2], profile);
                return 0;
            }
            var remote = args[0] == "hosted";
            var optionIndex = blob ? 9 : 7;
            Guard.Against(blob && (args.Length < 9 || args[7] != "--allowance"), "c1_usage",
                "Blob execution requires a protected per-invocation allowance; it never falls back to a file checkpoint.", 400);
            var operation = remote
                ? args.Length >= optionIndex + 2 && args[optionIndex] == "--operation" ? args[optionIndex + 1] :
                    throw new DomainException("c1_usage", "Hosted operation is required.", 400)
                : args[0];
            var probeIndex = optionIndex + (remote ? 2 : 0);
            string? probe = null;
            if (args.Length != probeIndex)
            {
                Guard.Against(args.Length != probeIndex + 2 || args[probeIndex] != "--probe", "c1_usage", "Unexpected C1 arguments.", 400);
                probe = args[probeIndex + 1];
            }
            FabricC1Invocation command;
            FabricC1Observation result;
            if (profile is not null)
            {
                var allowance = ReadProtected<FabricC1InvocationAllowance>(args[8]);
                command = FabricC1CheckpointProfiles.ValidateAllowance(settings, profile, allowance,
                    remote ? "hosted-control" : "direct", operation, probe, DateTimeOffset.UtcNow);
                result = await InvokeBlob(settings, fixture, command, profile, allowance.Reservation, CancellationToken.None);
            }
            else
            {
                command = new(Canonical.Hash(settings), operation, probe, Guid.NewGuid().ToString("D"));
                result = remote
                    ? await InvokeHosted(settings, command, args[6])
                    : await InvokeDirect(settings, fixture, command, args[6], "direct", CancellationToken.None);
            }
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
            InvalidOperationException or HttpRequestException or OperationCanceledException or AggregateException or
            Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Failed", code = "c1_entry_failed", errorType = error.GetType().Name }, Canonical.Json));
            return 3;
        }
    }

    public static async Task<FabricC1Observation> InvokeDirect(FabricC1ExecutionSettings settings, string fixture,
        FabricC1Invocation command, string statePath, string runtime, CancellationToken ct)
    {
        FabricC1Execution.Validate(settings, DateTimeOffset.UtcNow, hosted: runtime == "hosted", allowCleanup: true);
        Guard.Against(command.HostedCheckpointReservation is not null, "c1_checkpoint_allowance",
            "An invocation carrying a Blob claim cannot run against a file checkpoint.", 409);
        using var store = StateStore(settings, statePath);
        return await InvokeDirectTransport(settings, fixture, command, runtime, store, ct);
    }

    private static async Task<FabricC1Observation> InvokeDirectTransport(FabricC1ExecutionSettings settings, string fixture,
        FabricC1Invocation command, string runtime, IFabricC1CheckpointStore store, CancellationToken ct)
    {
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
        return await InvokeHostedTransport(settings, command, store, CancellationToken.None);
    }

    private static async Task<FabricC1Observation> InvokeHostedTransport(FabricC1ExecutionSettings settings,
        FabricC1Invocation command, IFabricC1CheckpointStore store, CancellationToken ct)
    {
        var hosted = settings.Hosted!;
        using var credential = new FabricC1Credential(hosted.Caller, settings.Preparation.Limits.MaxDurationSeconds!.Value);
        using var client = FabricC1Execution.PrivateHttpClient(new(hosted.InvocationEndpoint), hosted.PrivateAddresses,
            settings.Preparation.Limits.MaxDurationSeconds.Value);
        var http = new FabricC1AuthenticatedHttp(client, credential.Credential, hosted.Caller, FabricC1Execution.HostedScope,
            new(hosted.InvocationEndpoint), TimeProvider.System);
        return await new FabricC1HostedClient(settings, http, store).Invoke(command, ct);
    }

    private static async Task<FabricC1Observation> InvokeBlob(FabricC1ExecutionSettings settings, string fixture,
        FabricC1Invocation command, FabricC1BlobCheckpointProfile profile, FabricC1CheckpointReservation reservation, CancellationToken ct)
    {
        var store = await FabricC1BlobCheckpointStore.OpenNative(settings, profile, reservation, ct);
        FabricC1Observation result;
        try
        {
            result = profile.Allocation.Binding.Runtime == "hosted-control"
                ? await InvokeHostedTransport(settings, command, store, ct)
                : await InvokeDirectTransport(settings, fixture, command, profile.Allocation.Binding.Runtime, store, ct);
        }
        catch (Exception original)
        {
            try { await store.DisposeAsync(); }
            catch (Exception cleanup)
            {
                throw new AggregateException("C1 execution failed and its original checkpoint lease release was not confirmed.", original, cleanup);
            }
            throw;
        }
        await store.DisposeAsync();
        return result;
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

    private static async Task Host(FabricC1ExecutionSettings settings, string fixture, string statePath, string configurationPath,
        FabricC1BlobCheckpointProfile? profile)
    {
        ValidateHostedEnvironment(settings, Environment.GetEnvironmentVariable, configurationPath, statePath);
        if (profile is null)
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
        app.MapGet("/readiness", () => Results.Ok(new { status = "probe-host-ready", capabilityAccepted = false,
            checkpointMode = profile is null ? "explicit-file" : "bound-blob-not-service-accepted" }));
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
                FabricC1Observation observation;
                if (profile is not null)
                {
                    var reservation = FabricC1CheckpointProfiles.HostedReservation(settings, profile, command!, DateTimeOffset.UtcNow);
                    observation = await InvokeBlob(settings, fixture, command!, profile, reservation, lifetime.Token);
                }
                else
                {
                    Guard.Against(command!.HostedCheckpointReservation is not null, "c1_checkpoint_allowance",
                        "A Blob reservation cannot silently select or fall back to a file-mode Hosted server.", 409);
                    observation = await InvokeDirect(settings, fixture, command, statePath, "hosted", lifetime.Token);
                }
                return Results.Json(observation, Canonical.Json);
            }
            catch (DomainException error) { return Results.Json(new { status = "Blocked", code = error.Code }, statusCode: error.Status); }
            catch (Exception error) when (error is IOException or JsonException or Azure.Identity.AuthenticationFailedException or
                Azure.Identity.CredentialUnavailableException or UnauthorizedAccessException or HttpRequestException or
                OperationCanceledException or AggregateException)
            {
                return Results.Json(new { status = "Failed", code = "c1_hosted_handler_failed", errorType = error.GetType().Name }, statusCode: 503);
            }
        });
        await app.RunAsync();
    }

    private static T ReadProtected<T>(string path)
    {
        FabricC1PrivateFiles.RequireFile(path);
        return Canonical.Read<T>(ReadSmall(path, 65536));
    }

    private static string ReadSmall(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        Guard.Against(stream.Length > maximum, "c1_input_size", "Input exceeds the bounded configuration/fixture size.", 409);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
