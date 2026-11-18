using System.Text.Json;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Api;

public static class FabricC1DeploymentEntry
{
    public static int Run(string[] args)
    {
        try
        {
            Guard.Against(args.Length != 3 || args[1] != "--manifest" || args[0] is not ("plan" or "apply"),
                "c1_deploy_usage", "Use --fabric-c1-deploy plan|apply --manifest FILE.", 400);
            using var file = File.OpenRead(args[2]);
            Guard.Against(file.Length is <= 0 or > 65536, "c1_deploy_manifest_size", "Manifest is empty or oversized.", 400);
            var input = JsonSerializer.Deserialize<FabricC1DeploymentInputs>(file, Canonical.Json) ??
                throw new DomainException("c1_deploy_manifest", "Deployment manifest is missing.", 400);
            var plan = FabricC1Deployment.Plan(input, DateTimeOffset.UtcNow);
            if (args[0] == "apply")
            {
                Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                    "c1_deploy_ci_prohibited", "The credential-free build runner cannot deploy.", 409);
                FabricC1Deployment.RequireComplete(plan);
                throw new DomainException("c1_deploy_driver_incomplete", "Complete private bootstrap driver is not connected.", 409);
            }
            Console.WriteLine(JsonSerializer.Serialize(plan, Canonical.Json));
            return plan.CanApply ? 0 : 2;
        }
        catch (DomainException error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Blocked", code = error.Code }, Canonical.Json));
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "Failed", code = "c1_deploy_entry_failed", errorType = error.GetType().Name }, Canonical.Json));
            return 3;
        }
    }
}
