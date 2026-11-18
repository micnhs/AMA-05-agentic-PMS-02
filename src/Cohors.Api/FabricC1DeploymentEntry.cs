using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Api;

public static class FabricC1DeploymentEntry
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Guard.Against(args.Length is not (3 or 5 or 7 or 9) || args[1] != "--manifest" || args[0] is not ("plan" or "apply" or "cleanup-identities") ||
                args.Length >= 5 && args[3] != "--journal" ||
                args.Length >= 7 && (args[5] != "--through" || args[6] != "identities") ||
                args.Length == 9 && args[7] != "--admin-approval" ||
                args[0] == "apply" && args.Length == 3 || args[0] == "plan" && args.Length != 3 ||
                args[0] == "cleanup-identities" && args.Length != 5,
                "c1_deploy_usage", "Use --fabric-c1-deploy plan --manifest FILE, apply --manifest FILE --journal PRIVATE_FILE [--through identities [--admin-approval PRIVATE_FILE]], or cleanup-identities --manifest FILE --journal EXISTING_PRIVATE_FILE.", 400);
            using var file = File.OpenRead(args[2]);
            Guard.Against(file.Length is <= 0 or > 65536, "c1_deploy_manifest_size", "Manifest is empty or oversized.", 400);
            var input = JsonSerializer.Deserialize<FabricC1DeploymentInputs>(file, Canonical.Json) ??
                throw new DomainException("c1_deploy_manifest", "Deployment manifest is missing.", 400);
            var plan = FabricC1Deployment.Plan(input, DateTimeOffset.UtcNow);
            if (args[0] is "apply" or "cleanup-identities")
            {
                var cleanup = args[0] == "cleanup-identities";
                Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                    "c1_deploy_ci_prohibited", "The credential-free build runner cannot deploy.", 409);
                Guard.Against(plan.MissingInputs.Any(field => !cleanup ||
                    field != "expiresAtUtc" && !(field == "network.operatorAccess" && input.Network?.OperatorAccess is null)),
                    "c1_deploy_inputs", "Start inputs are incomplete.", 409);
                if (!cleanup && args.Length < 7) FabricC1Deployment.RequireComplete(plan);
                FabricC1Execution.RequireSourceBuild(input.SourceCommit!);
                FabricC1PrivateFiles.RequireFile(Path.GetFullPath(args[2]));
                FabricC1PrivateFiles.RequireAbsoluteNonLink(args[4]);
                FabricC1PrivateFiles.RequireDirectory(Path.GetDirectoryName(args[4])!);
                Guard.Against(cleanup && !File.Exists(args[4]), "c1_deploy_cleanup_journal", "Cleanup requires the original existing journal, never a new inventory.", 409);
                if (File.Exists(args[4])) FabricC1PrivateFiles.RequireFile(args[4]);
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
                using var credential = new BootstrapCredential(input.BootstrapIdentity!);
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

    private sealed class BootstrapCredential : IDisposable
    {
        private readonly FabricC1Credential? application;
        public TokenCredential Value { get; }
        public BootstrapCredential(FabricC1Identity identity)
        {
            if (identity.CredentialKind == "DelegatedAzureCli")
                Value = new AzureCliCredential(new AzureCliCredentialOptions
                {
                    TenantId = identity.TenantId, ProcessTimeout = TimeSpan.FromSeconds(30)
                });
            else
            {
                FabricC1PrivateFiles.RequireFile(identity.CredentialReference!);
                application = new FabricC1Credential(identity, 60);
                Value = application.Credential;
            }
        }
        public void Dispose() => application?.Dispose();
    }
}
