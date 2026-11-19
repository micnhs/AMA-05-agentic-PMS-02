using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Api;

public static partial class FabricC1DeploymentEntry
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 0 && (args[0] is "stop-private" or "watch-private" or "cleanup-private" or "export-hub" ||
                args[0] == "apply" && !args.Contains("--through", StringComparer.Ordinal)))
                return await RunPrivateLifecycle(args);
            var bindHub = args.Length > 0 && args[0] == "bind-hub";
            Guard.Against(args.Length is not (3 or 5 or 7 or 9) || args[1] != "--manifest" || args[0] is not ("plan" or "apply" or "cleanup-identities" or "bind-hub") ||
                args.Length >= 5 && args[3] != "--journal" ||
                args.Length >= 7 && (bindHub ? args[5] != "--hub-receipt" : args[5] != "--through" || args[6] != "identities") ||
                args.Length == 9 && args[7] != "--admin-approval" ||
                args[0] == "apply" && args.Length == 3 || args[0] == "plan" && args.Length != 3 ||
                args[0] == "cleanup-identities" && args.Length != 5 || bindHub && args.Length != 7,
                "c1_deploy_usage", "Use --fabric-c1-deploy plan --manifest FILE, apply --manifest FILE --journal PRIVATE_FILE [--through identities [--admin-approval PRIVATE_FILE]], cleanup-identities --manifest FILE --journal EXISTING_PRIVATE_FILE, or bind-hub --manifest FILE --journal EXISTING_PRIVATE_FILE --hub-receipt PRIVATE_FILE.", 400);
            Guard.Against(args[0] != "plan" && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "c1_deploy_ci_prohibited", "The credential-free build runner cannot read deployment inputs or operate cloud resources.", 409);
            using var file = File.OpenRead(args[2]);
            Guard.Against(file.Length is <= 0 or > 65536, "c1_deploy_manifest_size", "Manifest is empty or oversized.", 400);
            var input = JsonSerializer.Deserialize<FabricC1DeploymentInputs>(file, Canonical.Json) ??
                throw new DomainException("c1_deploy_manifest", "Deployment manifest is missing.", 400);
            var plan = FabricC1Deployment.Plan(input, DateTimeOffset.UtcNow);
            if (args[0] is "apply" or "cleanup-identities" or "bind-hub")
            {
                var cleanup = args[0] == "cleanup-identities";
                Guard.Against(plan.MissingInputs.Any(field => !cleanup ||
                    field != "expiresAtUtc" && !(field == "network.operatorAccess" && input.Network?.OperatorAccess is null) &&
                    !(field == "network.sharedHub" && input.Network?.SharedHub is null) &&
                    !(field.StartsWith("network.operatorAccess.vpnUsers.", StringComparison.Ordinal) &&
                      input.Network?.OperatorAccess?.VpnUsers is null)),
                    "c1_deploy_inputs", "Start inputs are incomplete.", 409);
                if (!cleanup && !bindHub && args.Length < 7) FabricC1Deployment.RequireComplete(plan);
                FabricC1Execution.RequireSourceBuild(input.SourceCommit!);
                FabricC1PrivateFiles.RequireFile(Path.GetFullPath(args[2]));
                FabricC1PrivateFiles.RequireAbsoluteNonLink(args[4]);
                FabricC1PrivateFiles.RequireDirectory(Path.GetDirectoryName(args[4])!);
                Guard.Against((cleanup || bindHub) && !File.Exists(args[4]), "c1_deploy_cleanup_journal",
                    "Cleanup and hub binding require the original existing journal, never a new inventory or budget.", 409);
                if (File.Exists(args[4])) FabricC1PrivateFiles.RequireFile(args[4]);
                JsonObject? hubReceipt = null;
                if (bindHub)
                {
                    FabricC1PrivateFiles.RequireFile(args[6]);
                    using var receiptFile = File.OpenRead(args[6]);
                    Guard.Against(receiptFile.Length is <= 0 or > 262144, "c1_hub_receipt_size", "The private hub receipt is empty or oversized.", 409);
                    hubReceipt = JsonNode.Parse(receiptFile) as JsonObject ??
                        throw new DomainException("c1_hub_shape", "The owner-issued JSON receipt is missing.", 409);
                }
                FabricC1TenantApproval? approval = null;
                if (args.Length == 9)
                {
                    FabricC1PrivateFiles.RequireFile(args[8]);
                    using var approvalFile = File.OpenRead(args[8]);
                    Guard.Against(approvalFile.Length is <= 0 or > 8192, "c1_deploy_admin_approval", "Approval file is empty or oversized.", 409);
                    approval = JsonSerializer.Deserialize<FabricC1TenantApproval>(approvalFile, Canonical.Json) ??
                        throw new DomainException("c1_deploy_admin_approval", "Administrative approval is missing.", 409);
                    approval.Validate(input, DateTimeOffset.UtcNow);
                }
                using var journal = new FabricC1DeploymentFileJournal(args[4]);
                using var credential = new FabricC1DeploymentCredential(input.BootstrapIdentity!);
                using var graph = FabricC1DeploymentHttp.ControlClient(60);
                using var arm = FabricC1DeploymentHttp.ControlClient(60);
                using var fabric = FabricC1DeploymentHttp.ControlClient(60);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(cleanup ?
                    input.Cleanup!.MaxDurationSeconds!.Value : input.MaxControlDurationSeconds!.Value));
                var api = new FabricC1DeploymentHttp(input, credential.Value, new Dictionary<string, HttpClient>
                {
                    ["graph.microsoft.com"] = graph, ["management.azure.com"] = arm, ["api.fabric.microsoft.com"] = fabric
                });
                var provisioner = new FabricC1Provisioner(input, api, journal);
                if (bindHub)
                {
                    var result = await provisioner.ImportSharedHubReceipt(hubReceipt!, deadline.Token);
                    Console.WriteLine(JsonSerializer.Serialize(result, Canonical.Json));
                    return 0;
                }
                if (cleanup)
                {
                    var result = await provisioner.CleanupIdentityPhase(deadline.Token);
                    Console.WriteLine(JsonSerializer.Serialize(result, Canonical.Json));
                    return result.ActiveIdentityInventoryAbsent ? 0 : 2;
                }
                await provisioner.Preflight(deadline.Token);
                var identities = await provisioner.EnsureIdentityPhase(await api.GraphRights(deadline.Token), deadline.Token);
                FabricC1TenantReview? settings = null;
                string? administrativeReadBlocker = null;
                if (identities.Status == "IdentitiesProvisioned")
                {
                    try
                    {
                        approval?.Validate(input, DateTimeOffset.UtcNow);
                        settings = approval is null ? await provisioner.ReviewTenantSettings(deadline.Token) :
                            await provisioner.ApplyScopedTenantSettings(approval.ChangeHash,
                                approval.AdministrativeConcurrencyReviewSha256, deadline.Token);
                    }
                    catch (DomainException error) when (error.Code is "c1_deploy_http_401" or "c1_deploy_http_403")
                    {
                        administrativeReadBlocker = error.Code;
                    }
                }
                var status = identities.Status != "IdentitiesProvisioned" ? identities.Status :
                    administrativeReadBlocker is not null || settings?.CanPrepareEmptyFoundation != true ?
                        "AwaitingScopedAdminApproval" : "IdentityPhaseComplete";
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status, scope = "unpaid-identities-only", identities, settings, administrativeReadBlocker,
                    completeC1Deployment = false, nativeServiceCases = "NotRun",
                    remainingImplementation = plan.ImplementationBlockers
                }, Canonical.Json));
                return status == "IdentityPhaseComplete" ? 0 : 2;
            }
            Console.WriteLine(JsonSerializer.Serialize(plan, Canonical.Json));
            return plan.CanApply ? 0 : 2;
        }
        catch (DomainException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Blocked", code = error.Code }, Canonical.Json));
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or CryptographicException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Failed", code = "c1_deploy_entry_failed", errorType = error.GetType().Name }, Canonical.Json));
            return 3;
        }
        catch (Exception error) when (error is AuthenticationFailedException or HttpRequestException or OperationCanceledException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = "Blocked", code = "c1_deploy_auth_or_transport", errorType = error.GetType().Name,
                outcome = "Resume the persisted journal; no automatic retry or credential fallback."
            }, Canonical.Json));
            return 2;
        }
    }
}
