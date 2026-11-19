using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Api;

public static partial class FabricC1DeploymentEntry
{
    private static async Task<int> RunPrivateLifecycle(string[] args)
    {
        Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "c1_deploy_ci_prohibited", "The source runner cannot read private lifecycle inputs, acquire credentials or start a watcher.", 409);
        var command = args[0];
        Guard.Against(args.Length < 5 || args.Length % 2 != 1, "c1_deploy_usage", "The private lifecycle needs explicit named file arguments.", 400);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
            Guard.Against(args[index] is not ("--manifest" or "--journal" or "--out" or "--dns-selections" or "--hub-receipt") ||
                string.IsNullOrWhiteSpace(args[index + 1]) || !options.TryAdd(args[index], args[index + 1]),
                "c1_deploy_usage", "Unknown or duplicate private lifecycle argument.", 400);
        Guard.Against(!options.ContainsKey("--manifest") || !options.ContainsKey("--journal") ||
            command == "stop-private" && options.Count != 2 ||
            command is not ("stop-private" or "apply") && !options.ContainsKey("--out") ||
            options.ContainsKey("--dns-selections") && command != "apply" ||
            options.ContainsKey("--hub-receipt") && command != "cleanup-private",
            "c1_deploy_usage", "Use an original manifest/journal, a private --out directory, and only the command's explicit DNS or terminal-receipt option.", 400);
        var input = ReadPrivateLifecycleFile<FabricC1DeploymentInputs>(options["--manifest"], 65536);
        var cleanup = command is "stop-private" or "watch-private" or "cleanup-private";
        var plan = FabricC1Deployment.Plan(input, DateTimeOffset.UtcNow);
        Guard.Against(plan.MissingInputs.Any(field => !cleanup || field != "expiresAtUtc") || input.Network?.SharedHub is null,
            "c1_deploy_inputs", "The original shared-hub deployment inputs must remain fully bound; cleanup renews no expiry.", 409);
        if (command == "apply")
        {
            FabricC1Deployment.RequireComplete(plan);
            Guard.Against(!options.ContainsKey("--out"), "c1_deploy_usage", "Full apply also needs an explicit private owner-handoff directory.", 400);
        }
        FabricC1Execution.RequireSourceBuild(input.SourceCommit!);
        var journalPath = options["--journal"];
        FabricC1PrivateFiles.RequireAbsoluteNonLink(journalPath);
        FabricC1PrivateFiles.RequireFile(journalPath);
        var output = options.GetValueOrDefault("--out");
        using var journal = new FabricC1DeploymentFileJournal(journalPath);
        var noProvider = new LifecycleFenceOnlyApi();
        var fence = new FabricC1Provisioner(input, noProvider, journal);
        if (command == "stop-private")
        {
            Console.WriteLine(JsonSerializer.Serialize(fence.FencePrivateDeployment("operator-stop"), Canonical.Json));
            return 2;
        }
        if (command == "watch-private")
            await FabricC1Deployment.FenceAtOriginalDeadline(() => new(input, noProvider, journal), TimeProvider.System, CancellationToken.None);
        else if (cleanup) fence.FencePrivateDeployment("operator-stop");

        if (output is not null)
        {
            FabricC1PrivateFiles.RequireAbsoluteNonLink(output);
            FabricC1PrivateFiles.RequireDirectory(output);
        }
        if (command == "export-hub")
        {
            var export = fence.State.CleanupStartedAtUtc is not null ? fence.RestorePrivateOffboardingExport() :
                fence.State.Objects.ContainsKey("shared-hub-data-evidence") ? fence.RestoreSharedHubBindingsExport() :
                fence.RestoreSharedHubHandshakeExport();
            FabricC1Deployment.WriteHubConsumerHandoff(export, output!);
            Console.WriteLine(JsonSerializer.Serialize(new
            { status = "OriginalOwnerHandoffWritten", completeC1Deployment = false, ownerRevisionCreated = false }, Canonical.Json));
            return 0;
        }
        fence = new(input, noProvider, journal);
        var originalDeadline = cleanup
            ? fence.State.CleanupStartedAtUtc!.Value.AddSeconds(input.Cleanup!.MaxDurationSeconds!.Value)
            : fence.OriginalPrivateDeploymentDeadline();
        if (cleanup && input.Cleanup!.VerificationDeadlineUtc!.Value < originalDeadline)
            originalDeadline = input.Cleanup.VerificationDeadlineUtc.Value;
        var remaining = originalDeadline - DateTimeOffset.UtcNow;
        Guard.Against(remaining <= TimeSpan.Zero, cleanup ? "c1_deploy_cleanup_budget" : "c1_deploy_budget",
            "The original control/cleanup window has elapsed; never restart its clock.", 409);
        using var deadline = new CancellationTokenSource(remaining);

        // The stop/watch/cleanup paths fence before constructing credentials or private clients.
        using var credential = new FabricC1DeploymentCredential(input.BootstrapIdentity!);
        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["graph.microsoft.com"] = FabricC1DeploymentHttp.ControlClient(60),
            ["management.azure.com"] = FabricC1DeploymentHttp.ControlClient(60),
            ["api.fabric.microsoft.com"] = FabricC1DeploymentHttp.ControlClient(60)
        };
        try
        {
            FabricC1Provisioner? provisioner = null;
            var routeBindings = new Dictionary<string, string[]>(StringComparer.Ordinal);
            HttpClient BindPrivateClient(string host, string[] addresses)
            {
                if (routeBindings.TryGetValue(host, out var original))
                {
                    Guard.Against(!original.SequenceEqual(addresses), "c1_deploy_private_route", "An original private route cannot change on resume.", 409);
                    return clients[host];
                }
                var client = FabricC1Execution.PrivateHttpClient(new("https://" + host), addresses, 60);
                routeBindings.Add(host, addresses.ToArray());
                clients.Add(host, client);
                return client;
            }
            HttpClient? ResolvePrivateClient(string host)
            {
                if (provisioner is null) throw new DomainException("c1_deploy_private_route", "The original controller is not bound.", 409);
                var routes = provisioner.OriginalPrivateWorkspaceRoutes(cleanup);
                return routes.TryGetValue(host, out var addresses) ? BindPrivateClient(host, addresses) : null;
            }
            var api = new FabricC1DeploymentHttp(input, credential.Value, clients, privateClientFactory: ResolvePrivateClient);
            provisioner = new(input, api, journal);
            if (cleanup)
            {
                var reason = provisioner.State.Objects["private-lifecycle-stop"].Response["reason"]!.GetValue<string>();
                var progress = await provisioner.AdvancePrivateCleanup(reason, deadline.Token);
                if (progress.OwnerHandoff is not null) FabricC1Deployment.WriteHubConsumerHandoff(progress.OwnerHandoff, output!);
                if (options.TryGetValue("--hub-receipt", out var receiptPath))
                {
                    Guard.Against(progress.OwnerHandoff is null, "c1_hub_offboarding_pending",
                        "Ordered own cleanup must produce its original offboarding handoff before accepting an owner acknowledgement.", 409);
                    await provisioner.ImportSharedHubReceipt(ReadPrivateLifecycleFile<JsonObject>(receiptPath, 262144), deadline.Token);
                    progress = await provisioner.AdvancePrivateCleanup(reason, deadline.Token);
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    progress.Status, progress.CompletedStages, progress.Blockers, progress.NextPollAtUtc, progress.CompleteC1Cleanup, progress.EvidenceLevel,
                    ownerHandoffWritten = progress.OwnerHandoff is not null, nativeServiceCases = "NotRun"
                }, Canonical.Json));
                return progress.CompleteC1Cleanup ? 0 : 2;
            }
            await provisioner.Preflight(deadline.Token);
            var identities = await provisioner.EnsureIdentityPhase(await api.GraphRights(deadline.Token), deadline.Token);
            if (identities.Status != "IdentitiesProvisioned")
            {
                Console.WriteLine(JsonSerializer.Serialize(identities, Canonical.Json));
                return 2;
            }
            var settings = await provisioner.ReviewTenantSettings(deadline.Token);
            if (!settings.CanPrepareEmptyFoundation)
            {
                Console.WriteLine(JsonSerializer.Serialize(settings, Canonical.Json));
                return 2;
            }
            var selections = options.TryGetValue("--dns-selections", out var selectionPath)
                ? ReadPrivateLifecycleFile<FabricC1HubDnsSelection[]>(selectionPath, 65536) : null;
            var advance = await provisioner.AdvancePrivateDeployment(selections, new FabricC1HubDnsTransport(),
                routes => { foreach (var (host, addresses) in routes) BindPrivateClient(host, addresses); }, deadline.Token);
            if (advance.OwnerConsumerSlot is not null)
            {
                var export = advance.OwnerConsumerSlot["state"]!.GetValue<string>() == "data-bound"
                    ? provisioner.RestoreSharedHubBindingsExport() : provisioner.RestoreSharedHubHandshakeExport();
                FabricC1Deployment.WriteHubConsumerHandoff(export, output!);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                advance.Status, advance.CompletedStages, advance.NextPollAtUtc, advance.CompleteC1Deployment, advance.EvidenceLevel,
                ownerHandoffWritten = advance.OwnerConsumerSlot is not null,
                remainingImplementation = plan.ImplementationBlockers, nativeServiceCases = "NotRun"
            }, Canonical.Json));
            return advance.CompleteC1Deployment ? 0 : 2;
        }
        finally
        {
            foreach (var client in clients.Values) client.Dispose();
        }
    }

    private static T ReadPrivateLifecycleFile<T>(string path, int maximum) where T : class
    {
        FabricC1PrivateFiles.RequireAbsoluteNonLink(path);
        FabricC1PrivateFiles.RequireFile(path);
        using var file = File.OpenRead(path);
        Guard.Against(file.Length is <= 0 || file.Length > maximum, "c1_deploy_input_size", "The private lifecycle input is empty or oversized.", 409);
        return JsonSerializer.Deserialize<T>(file, Canonical.Json) ??
            throw new DomainException("c1_deploy_inputs", "A complete private lifecycle input is required.", 409);
    }

    private sealed class LifecycleFenceOnlyApi : IFabricC1DeploymentApi
    {
        public Task<FabricC1DeploymentReply> Send(HttpMethod method, Uri uri, JsonObject? body = null,
            string? ifMatch = null, CancellationToken ct = default) =>
            throw new DomainException("c1_lifecycle_fence_provider", "The independent fence and original-file export cannot acquire credentials or call a provider.", 409);
    }
}
