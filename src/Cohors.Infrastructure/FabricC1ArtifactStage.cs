using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1ArtifactStageBundle(JsonObject Profile, JsonObject Allowance,
    string ReceiverSource, string VerifierSource)
{
    public string EvidenceLevel => "original-stage-allocation-not-guest-delivery-or-execution";
    public string WorkerDirectory => "/var/lib/cohors-c1/" + Profile["deploymentBindingHash"]!.GetValue<string>() + "/artifact-stage";
}

public sealed partial class FabricC1Provisioner
{
    private const string ArtifactStageKey = "private-artifact-stage-plan";
    private const string ArtifactStageContract = "cohors-fabric-c1-stage-allowance-1";

    public async Task<FabricC1ArtifactStageBundle> PreparePrivateArtifactStage(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory, int maxDurationSeconds, CancellationToken ct)
    {
        RequirePrivateArtifactAccess(binding, packageDirectory);
        var (package, target, _) = ReadArtifactAccess(binding, packageDirectory);
        state.Objects.TryGetValue(ArtifactPlanKey, out var publicationPlan);
        state.Objects.TryGetValue(ArtifactPublicationKey, out var publication);
        Guard.Against(maxDurationSeconds is < 1 or > 300 || publicationPlan is null || publication is null ||
            state.PendingCreates.ContainsKey(ArtifactPublicationKey),
            "c1_artifact_stage_publication", "Stage preparation requires the confirmed original private publication.", 409);
        var receipt = publication!.Response.Deserialize<FabricC1ArtifactPublicationReceipt>(Canonical.Json) ??
            throw new DomainException("c1_artifact_stage_publication", "Publication readback is missing.", 409);
        Guard.Against(publicationPlan!.Kind != "PrivateArtifactPlan" || publication.Kind != "PrivateArtifactReadback" ||
            publicationPlan.Id != package.Endpoint.AbsoluteUri || publication.Id != package.Endpoint.AbsoluteUri ||
            publicationPlan.RequestHash != Canonical.Hash(publicationPlan.Response) ||
            publication.RequestHash != publicationPlan.RequestHash ||
            publicationPlan.Response["bindingSha256"]?.GetValue<string>() != Canonical.Hash(binding) ||
            receipt.PublicationPlanSha256 != publicationPlan.RequestHash || receipt.Endpoint != package.Endpoint.AbsoluteUri ||
            receipt.OuterArchiveSha256 != package.ArchiveSha256 || receipt.OuterArchiveLength != package.ArchiveLength ||
            receipt.CreationId != Text(publicationPlan.Response, "creationId") ||
            !EntityTagHeaderValue.TryParse(receipt.ETag, out var etag) || etag.IsWeak || etag.Tag == "*" ||
            receipt.ObservedAtUtc < publicationPlan.Response["startedAtUtc"]!.GetValue<DateTimeOffset>() ||
            receipt.ObservedAtUtc >= publicationPlan.Response["deadlineUtc"]!.GetValue<DateTimeOffset>() ||
            receipt.ObservedAtUtc > time.GetUtcNow(),
            "c1_artifact_stage_publication", "Stage preparation cannot substitute another publication or historical target.", 409);
        var requestHash = Canonical.Hash(new
        {
            Binding = binding, Target = target, MaxDurationSeconds = maxDurationSeconds,
            receipt.PublicationPlanSha256, receipt.CreationId, receipt.ETag
        });
        if (state.Objects.TryGetValue(ArtifactStageKey, out var retained))
            return ExportArtifactStage(retained, requestHash);

        var (_, controlDeadline) = PlanNormalRequests(0);
        var remaining = controlDeadline - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero, "c1_deploy_budget", "The original stage preparation deadline elapsed.", 409);
        using var timeout = new CancellationTokenSource(remaining, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var (receiver, verifier) = await ReadArtifactStageTools(package, bounded.Token);
        var vm = await Read("https://management.azure.com" + Text(target, "vmResourceId") +
            "?api-version=2024-07-01", bounded.Token);
        VerifyArtifactAccessProbe(vm, target);
        var principal = await Read("https://graph.microsoft.com/v1.0/servicePrincipals/" + Text(target, "vmPrincipalId") +
            "?$select=id,appId,servicePrincipalType,alternativeNames,accountEnabled", bounded.Token);
        Guard.Against(!SameArmId(principal["id"]?.GetValue<string>(), Text(target, "vmPrincipalId")) ||
            principal["servicePrincipalType"]?.GetValue<string>() != "ManagedIdentity" ||
            principal["accountEnabled"]?.GetValue<bool>() != true ||
            principal["alternativeNames"] is not JsonArray names ||
            !names.Any(name => name is JsonValue value && value.TryGetValue<string>(out var text) &&
                SameArmId(text, Text(target, "vmResourceId"))),
            "c1_artifact_stage_identity", "The targeted service principal must be the original VM's managed identity.", 409);
        var clientId = GuidValue(Text(principal, "appId")).ToString("D");
        Guard.Against(SameArmId(clientId, input.BootstrapIdentity!.ClientId),
            "c1_artifact_stage_identity", "The receiver cannot use the original operator's OAuth client.", 409);
        var (next, deadline) = PlanNormalRequests(2);
        var started = time.GetUtcNow();
        if (started.AddSeconds(maxDurationSeconds) < deadline) deadline = started.AddSeconds(maxDurationSeconds);
        if (binding.ExpiresAtUtc < deadline) deadline = binding.ExpiresAtUtc;
        Guard.Against(deadline <= started, "c1_artifact_stage_binding", "The original artifact reception window elapsed.", 409);
        var profile = new JsonObject
        {
            ["contract"] = "cohors-fabric-c1-private-stage-1", ["deploymentBindingHash"] = state.BindingHash,
            ["vmResourceId"] = Text(target, "vmResourceId"), ["storageAccountResourceId"] = binding.StorageAccountResourceId,
            ["tenantId"] = input.TenantId, ["managedIdentityClientId"] = clientId,
            ["managedIdentityObjectId"] = Text(target, "vmPrincipalId"), ["expectedIssuer"] = binding.StorageIssuer,
            ["privateAddresses"] = JsonSerializer.SerializeToNode(binding.PrivateAddresses, Canonical.Json),
            ["privatePathReviewSha256"] = binding.PrivatePathReviewSha256, ["sourceCommit"] = input.SourceCommit,
            ["runId"] = Text(package.Provenance, "runId"), ["runAttempt"] = Text(package.Provenance, "runAttempt"),
            ["outerArchiveSha256"] = package.ArchiveSha256, ["outerArchiveLength"] = package.ArchiveLength,
            ["receiverSha256"] = Canonical.HashText(receiver), ["verifierSha256"] = Canonical.HashText(verifier),
            ["expiresAtUtc"] = StageUtc(deadline), ["maxDurationSeconds"] = maxDurationSeconds
        };
        var id = Guid.NewGuid().ToString("D");
        var allowance = new JsonObject
        {
            ["contract"] = ArtifactStageContract, ["deploymentBindingHash"] = state.BindingHash, ["invocationId"] = id,
            // Python's canonical wire form ends with LF; the global audit serializer must not change.
            ["profileSha256"] = Canonical.HashText(Canonical.Serialize(profile) + "\n"),
            ["startedAtUtc"] = StageUtc(started), ["expiresAtUtc"] = StageUtc(deadline), ["requests"] = 2,
            ["budgetReviewSha256"] = input.Reviews!.BudgetPlanSha256
        };
        var response = new JsonObject
        {
            ["requestSha256"] = requestHash, ["profile"] = profile, ["allowance"] = allowance,
            ["receiverSource"] = receiver, ["verifierSource"] = verifier,
            ["managedIdentityReadback"] = principal.DeepClone(), ["vmInstanceId"] = Text(target, "vmInstanceId")
        };
        var plan = new FabricC1ProvisionedObject("PrivateArtifactStagePlan", id, Canonical.Hash(response), response);
        SaveCheckpointJournal(next with
        { Objects = new(next.Objects, StringComparer.Ordinal) { [ArtifactStageKey] = plan } });
        return ExportArtifactStage(plan, requestHash);
    }

    private FabricC1ArtifactStageBundle ExportArtifactStage(FabricC1ProvisionedObject plan, string requestHash)
    {
        PlanNormalRequests(0);
        var response = plan.Response;
        var profile = response["profile"] as JsonObject ??
            throw new DomainException("c1_artifact_stage_binding", "Original receiver profile is missing.", 409);
        var allowance = response["allowance"] as JsonObject ??
            throw new DomainException("c1_artifact_stage_binding", "Original receiver allowance is missing.", 409);
        Guard.Against(plan.Kind != "PrivateArtifactStagePlan" || plan.RequestHash != Canonical.Hash(response) ||
            Text(response, "requestSha256") != requestHash || Text(allowance, "invocationId") != plan.Id ||
            Text(allowance, "contract") != ArtifactStageContract || allowance["requests"]?.GetValue<int>() != 2 ||
            Text(allowance, "deploymentBindingHash") != state.BindingHash ||
            Text(allowance, "budgetReviewSha256") != input.Reviews!.BudgetPlanSha256 ||
            Text(allowance, "expiresAtUtc") != Text(profile, "expiresAtUtc") ||
            Text(allowance, "profileSha256") != Canonical.HashText(Canonical.Serialize(profile) + "\n") ||
            Text(profile, "receiverSha256") != Canonical.HashText(Text(response, "receiverSource")) ||
            Text(profile, "verifierSha256") != Canonical.HashText(Text(response, "verifierSource")) ||
            !DateTimeOffset.TryParse(Text(allowance, "startedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ||
            !DateTimeOffset.TryParse(Text(allowance, "expiresAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ||
            started > time.GetUtcNow() || expires <= time.GetUtcNow(),
            "c1_artifact_stage_binding", "A prepared receiver cannot reset its original profile, identity, clock or two-request debit.", 409);
        var exported = (JsonObject)allowance.DeepClone();
        exported["journalReceiptSha256"] = Canonical.Hash(plan);
        return new((JsonObject)profile.DeepClone(), exported, Text(response, "receiverSource"), Text(response, "verifierSource"));
    }

    private static string StageUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static async Task<(string Receiver, string Verifier)> ReadArtifactStageTools(ArtifactPackage package, CancellationToken ct)
    {
        await using var archive = new FileStream(package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous);
        Guard.Against(archive.Length != package.ArchiveLength ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, ct)) != package.ArchiveSha256,
            "c1_artifact_digest", "Receiver tools must come from the original verified archive bytes.", 409);
        archive.Position = 0;
        using var outer = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        Guard.Against(outer.Entries.Count != 2 || outer.Entries.Count(entry => entry.FullName == "archive.json") != 1,
            "c1_artifact_stage_tools", "The retained outer archive shape differs.", 409);
        async Task<byte[]> EntryBytes(ZipArchive zip, string name, int maximum)
        {
            var matches = zip.Entries.Where(entry => entry.FullName == name).ToArray();
            Guard.Against(matches.Length != 1 || matches[0].Length is <= 0 ||
                matches[0].Length > maximum, "c1_artifact_stage_tools", "A bound bootstrap tool is absent, duplicated or oversized.", 409);
            await using var stream = matches[0].Open();
            using var limited = new FabricC1LimitedStream(stream, maximum);
            using var output = new MemoryStream();
            await limited.CopyToAsync(output, ct);
            Guard.Against(output.Length != matches[0].Length, "c1_artifact_stage_tools", "The bound archive entry length differs.", 409);
            return output.ToArray();
        }
        var inner = await EntryBytes(outer, "package.zip", ArtifactMaximumBytes);
        Guard.Against(Convert.ToHexStringLower(SHA256.HashData(inner)) != Text(package.Provenance, "innerArchiveSha256"),
            "c1_artifact_stage_tools", "The inner archive differs from the original download provenance.", 409);
        using var memory = new MemoryStream(inner, writable: false);
        using var payload = new ZipArchive(memory, ZipArchiveMode.Read);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return (utf8.GetString(await EntryBytes(payload, "assets/scripts/fabric/c1_private_stage.py", 256 * 1024)),
            utf8.GetString(await EntryBytes(payload, "assets/scripts/fabric/c1_package.py", 256 * 1024)));
    }
}
