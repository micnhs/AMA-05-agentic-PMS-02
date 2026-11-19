using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1ArtifactPublicationBinding(string Contract, string DeploymentBindingHash,
    string StorageAccountResourceId, string[] PrivateAddresses, string PrivatePathReviewSha256,
    string DownloadProvenanceSha256, string StorageAudience, string StorageIssuer,
    int MaxDurationSeconds, DateTimeOffset ExpiresAtUtc);

public sealed record FabricC1ArtifactPublicationReceipt(string Endpoint, string CreationId, string PublicationPlanSha256,
    string OuterArchiveSha256, long OuterArchiveLength, string ETag, DateTimeOffset ObservedAtUtc)
{
    public string EvidenceLevel => "private-artifact-readback-not-worker-execution-or-c1-acceptance";
}

public sealed partial class FabricC1Provisioner
{
    private const string ArtifactPublicationKey = "private-artifact-published";
    private const string ArtifactPlanKey = "private-artifact-publication-plan";
    private const int ArtifactMaximumBytes = 256 * 1024 * 1024;
    private sealed record ArtifactPackage(Uri Endpoint, string ArchivePath, string ArchiveSha256, long ArchiveLength,
        JsonObject Provenance);
    private sealed record ArtifactReply(HttpStatusCode Status, string? ErrorCode, string? ETag,
        Dictionary<string, string> Metadata);

    public async Task<FabricC1ArtifactPublicationReceipt> PublishPrivateArtifactNative(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory, CancellationToken ct)
    {
        Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "c1_cloud_ci_prohibited", "The source runner cannot publish private artifacts.", 409);
        FabricC1Execution.RequireSourceBuild(input.SourceCommit!);
        var package = ReadArtifactPackage(binding, packageDirectory);
        using var credential = new FabricC1DeploymentCredential(input.BootstrapIdentity!);
        using var client = FabricC1Execution.PrivateHttpClient(package.Endpoint, binding.PrivateAddresses, 300);
        return await PublishPrivateArtifactWithTransport(binding, packageDirectory, client, credential.Value, ct);
    }

    public async Task<FabricC1ArtifactPublicationReceipt> PublishPrivateArtifactWithTransport(
        FabricC1ArtifactPublicationBinding binding, string packageDirectory, HttpClient client,
        TokenCredential credential, CancellationToken ct)
    {
        var package = ReadArtifactPackage(binding, packageDirectory);
        var plan = ArtifactPublicationPlan(binding, package);
        var deadline = plan.Response["deadlineUtc"]!.GetValue<DateTimeOffset>();
        var remainingTime = deadline - time.GetUtcNow();
        Guard.Against(remainingTime <= TimeSpan.Zero, "c1_artifact_deadline", "No publication after expired journal persistence.", 409);
        using var timeout = new CancellationTokenSource(remainingTime, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var archive = new FileStream(package.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Guard.Against(archive.Length != package.ArchiveLength ||
            Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, bounded.Token)) != package.ArchiveSha256,
            "c1_artifact_digest", "The retained archive differs from its verified download provenance.", 409);
        await VerifyArtifactTarget(binding.StorageAccountResourceId, deadline, bounded.Token);
        state.PendingCreates.TryGetValue(ArtifactPublicationKey, out var pending);
        state.Objects.TryGetValue(ArtifactPublicationKey, out var known);
        Guard.Against(pending is not null && pending != plan.RequestHash ||
            known is not null && (pending is not null || known.Kind != "PrivateArtifactReadback" ||
                known.Id != package.Endpoint.AbsoluteUri || known.RequestHash != plan.RequestHash) ||
            state.DeniedRequests.TryGetValue(ArtifactPublicationKey, out var denied) &&
                (denied.RequestHash != plan.RequestHash || denied.Status is not (401 or 403)),
            "c1_artifact_binding", "The original artifact write/readback binding changed.", 409);
        var current = await SendArtifactRequest(HttpMethod.Get, binding, package, plan, archive, client, credential, bounded.Token);
        if (current.Status == HttpStatusCode.NotFound)
        {
            Guard.Against(current.ErrorCode != "BlobNotFound", "c1_artifact_container",
                "Only an absent blob in the own declared container permits initial publication.", 409);
            Guard.Against(pending is not null || known is not null, "c1_artifact_unknown",
                "An earlier artifact is absent or uncertain; no replacement or write replay.", 409);
            SaveCheckpointJournal(state with
            { PendingCreates = new(state.PendingCreates, StringComparer.Ordinal) { [ArtifactPublicationKey] = plan.RequestHash } });
            var created = await SendArtifactRequest(HttpMethod.Put, binding, package, plan, archive, client, credential, bounded.Token);
            if (created.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var creates = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
                creates.Remove(ArtifactPublicationKey);
                SaveCheckpointJournal(state with
                {
                    PendingCreates = creates,
                    DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                    { [ArtifactPublicationKey] = new(plan.RequestHash, (int)created.Status) }
                });
            }
            RequireArtifactStatus(created.Status, HttpStatusCode.Created);
            current = await SendArtifactRequest(HttpMethod.Get, binding, package, plan, archive, client, credential, bounded.Token);
        }
        RequireArtifactStatus(current.Status, HttpStatusCode.OK);
        var expected = ArtifactMetadata(plan);
        Guard.Against(known is null && !state.PendingCreates.ContainsKey(ArtifactPublicationKey) ||
            current.Metadata.Count != expected.Count || expected.Any(pair =>
                !current.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value),
            "c1_artifact_foreign", "An existing unjournaled or differently marked artifact is not adopted or overwritten.", 409);
        if (known is not null)
        {
            var original = known.Response.Deserialize<FabricC1ArtifactPublicationReceipt>(Canonical.Json) ??
                throw new DomainException("c1_artifact_binding", "The original artifact receipt is missing.", 409);
            Guard.Against(original.Endpoint != package.Endpoint.AbsoluteUri || original.PublicationPlanSha256 != plan.RequestHash ||
                original.OuterArchiveSha256 != package.ArchiveSha256 || original.OuterArchiveLength != package.ArchiveLength ||
                original.CreationId != Text(plan.Response, "creationId") || original.ETag != current.ETag ||
                original.ObservedAtUtc < plan.Response["startedAtUtc"]!.GetValue<DateTimeOffset>() || original.ObservedAtUtc >= deadline,
                "c1_artifact_changed", "The bound artifact receipt or current storage generation changed.", 409);
        }
        RequireArtifactTime(deadline);
        var receipt = new FabricC1ArtifactPublicationReceipt(package.Endpoint.AbsoluteUri, Text(plan.Response, "creationId"),
            plan.RequestHash, package.ArchiveSha256, package.ArchiveLength, current.ETag!, time.GetUtcNow());
        var remaining = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        remaining.Remove(ArtifactPublicationKey);
        SaveCheckpointJournal(state with
        {
            PendingCreates = remaining,
            Objects = new(state.Objects, StringComparer.Ordinal)
            {
                [ArtifactPublicationKey] = new("PrivateArtifactReadback", package.Endpoint.AbsoluteUri, plan.RequestHash,
                    JsonSerializer.SerializeToNode(receipt, Canonical.Json)!.AsObject())
            }
        });
        return receipt;
    }

    private ArtifactPackage ReadArtifactPackage(FabricC1ArtifactPublicationBinding binding, string directory)
    {
        PlanNormalRequests(0);
        Guard.Against(binding.Contract != "cohors-fabric-c1-artifact-publication-1" ||
            binding.DeploymentBindingHash != state.BindingHash ||
            !FabricC1Execution.Hex(binding.PrivatePathReviewSha256, 64) ||
            !FabricC1Execution.Hex(binding.DownloadProvenanceSha256, 64) ||
            binding.StorageAudience is not ("https://storage.azure.com/" or "https://storage.azure.com") ||
            binding.StorageIssuer != $"https://sts.windows.net/{input.TenantId}/" &&
                binding.StorageIssuer != $"https://login.microsoftonline.com/{input.TenantId}/v2.0" ||
            binding.MaxDurationSeconds is < 1 or > 300 || binding.ExpiresAtUtc <= time.GetUtcNow() ||
            binding.ExpiresAtUtc > input.ExpiresAtUtc,
            "c1_artifact_binding", "Original deployment, private route, token audience/issuer and bounded publication are required.", 409);
        FabricC1Execution.PrivateAddresses(binding.PrivateAddresses);
        FabricC1PrivateFiles.RequireDirectory(directory);
        var archive = Path.Combine(directory, "transport.zip");
        var provenancePath = Path.Combine(directory, "download-provenance.json");
        FabricC1PrivateFiles.RequireFile(archive);
        FabricC1PrivateFiles.RequireFile(provenancePath);
        using var provenance = File.Open(provenancePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Guard.Against(provenance.Length is <= 0 or > 16384, "c1_artifact_provenance", "Download provenance is empty or oversized.", 409);
        var bytes = new byte[(int)provenance.Length];
        provenance.ReadExactly(bytes);
        Guard.Against(provenance.ReadByte() != -1 ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != binding.DownloadProvenanceSha256,
            "c1_artifact_provenance", "The original protected download provenance changed.", 409);
        var data = JsonNode.Parse(bytes) as JsonObject ??
            throw new DomainException("c1_artifact_provenance", "Download provenance is not an object.", 409);
        var run = Text(data, "runId");
        var attempt = Text(data, "runAttempt");
        var hash = Text(data, "outerArchiveSha256");
        var length = data["outerArchiveLength"]?.GetValue<long>() ?? 0;
        Guard.Against(data.Count != 15 || Text(data, "contract") != "cohors-fabric-c1-download-1" ||
            Text(data, "status") != "VerifiedNotExecuted" || Text(data, "repository") != "micnhs/AMA-05-agentic-PMS-02" ||
            Text(data, "branch") != "micnhs-cohors-fabric-umsetzung" ||
            Text(data, "workflow") != ".github/workflows/cohors-fabric-contracts.yml" ||
            Text(data, "sourceCommit") != input.SourceCommit ||
            !new[] { run, attempt, Text(data, "artifactId") }.All(value => Regex.IsMatch(value, "^[1-9][0-9]*$", RegexOptions.CultureInvariant)) ||
            Text(data, "artifactName") != "cohors-fabric-c1-" + run + "-" + attempt ||
            Text(data, "transportArchiveName") != "transport.zip" ||
            !FabricC1Execution.Hex(hash, 64) || !FabricC1Execution.Hex(Text(data, "innerArchiveSha256"), 64) ||
            length is <= 0 or > ArtifactMaximumBytes || new FileInfo(archive).Length != length ||
            data["expiresAtUtc"]!.GetValue<DateTimeOffset>() < binding.ExpiresAtUtc,
            "c1_artifact_provenance", "An exact retained verified source/run/archive is required; no arbitrary upload.", 409);
        var storage = Owned("arm-foundation", "ArmDeployment")["properties"]?["outputs"]?["storage"]?["value"] as JsonObject ??
            throw new DomainException("c1_artifact_target", "Original storage output is missing.", 409);
        RequireArmOwnership(storage);
        Guard.Against(!SameArmId(RequireTypedArmResource(storage, "Microsoft.Storage/storageAccounts"), binding.StorageAccountResourceId),
            "c1_artifact_target", "Artifact storage differs from the original own account.", 409);
        var container = Owned("arm-foundation", "ArmDeployment")["properties"]?["outputs"]?["artifactContainer"]?["value"] as JsonObject ??
            throw new DomainException("c1_artifact_container", "The declared original artifact container is missing.", 409);
        VerifyArtifactContainer(container, binding.StorageAccountResourceId);
        var account = binding.StorageAccountResourceId.Split('/')[^1];
        return new(new($"https://{account}.blob.core.windows.net/c1-artifacts/{state.BindingHash}/{input.SourceCommit}/{hash}.zip"),
            archive, hash, length, data);
    }

    private FabricC1ProvisionedObject ArtifactPublicationPlan(FabricC1ArtifactPublicationBinding binding, ArtifactPackage package)
    {
        var (next, controlDeadline) = PlanNormalRequests(0);
        var started = time.GetUtcNow();
        if (state.Objects.TryGetValue(ArtifactPlanKey, out var original))
        {
            Guard.Against(original.Kind != "PrivateArtifactPlan" || original.Id != package.Endpoint.AbsoluteUri ||
                original.RequestHash != Canonical.Hash(original.Response) ||
                original.Response["bindingSha256"]?.GetValue<string>() != Canonical.Hash(binding) ||
                Canonical.Hash(original.Response["provenance"]) != Canonical.Hash(package.Provenance),
                "c1_artifact_binding", "The original publication plan cannot be replaced.", 409);
            started = original.Response["startedAtUtc"]!.GetValue<DateTimeOffset>();
        }
        var deadline = started.AddSeconds(binding.MaxDurationSeconds);
        if (binding.ExpiresAtUtc < deadline) deadline = binding.ExpiresAtUtc;
        if (controlDeadline < deadline) deadline = controlDeadline;
        RequireArtifactTime(deadline);
        Guard.Against(started > time.GetUtcNow() || original is not null &&
            (original.Response["deadlineUtc"]!.GetValue<DateTimeOffset>() != deadline ||
                !Guid.TryParseExact(original.Response["creationId"]?.GetValue<string>(), "D", out var id) || id == Guid.Empty),
            "c1_artifact_binding", "The original publication clock or creation marker changed.", 409);
        if (original is not null) return original;
        Guard.Against(state.Objects.ContainsKey(ArtifactPublicationKey) || state.PendingCreates.ContainsKey(ArtifactPublicationKey) ||
            state.DeniedRequests.ContainsKey(ArtifactPublicationKey), "c1_artifact_binding",
            "Existing artifact I/O has no original publication plan; do not reconstruct it.", 409);
        var response = new JsonObject
        {
            ["bindingSha256"] = Canonical.Hash(binding), ["provenance"] = package.Provenance.DeepClone(),
            ["creationId"] = Guid.NewGuid().ToString("D"), ["startedAtUtc"] = started, ["deadlineUtc"] = deadline,
            ["deploymentBindingHash"] = state.BindingHash, ["provenanceSha256"] = binding.DownloadProvenanceSha256,
            ["archiveSha256"] = package.ArchiveSha256
        };
        var plan = new FabricC1ProvisionedObject("PrivateArtifactPlan", package.Endpoint.AbsoluteUri, Canonical.Hash(response), response);
        SaveCheckpointJournal(next with { Objects = new(next.Objects, StringComparer.Ordinal) { [ArtifactPlanKey] = plan } });
        return plan;
    }

    private async Task VerifyArtifactTarget(string storageId, DateTimeOffset deadline, CancellationToken ct)
    {
        RequireArtifactTime(deadline);
        var storage = await Read("https://management.azure.com" + storageId + "?api-version=2023-05-01", ct);
        RequireArtifactTime(deadline);
        RequireArmOwnership(storage);
        Guard.Against(!SameArmId(RequireTypedArmResource(storage, "Microsoft.Storage/storageAccounts"), storageId) ||
            storage["properties"]?["publicNetworkAccess"]?.GetValue<string>() != "Disabled" ||
            storage["properties"]?["allowSharedKeyAccess"]?.GetValue<bool>() != false ||
            storage["properties"]?["allowBlobPublicAccess"]?.GetValue<bool>() != false ||
            storage["properties"]?["supportsHttpsTrafficOnly"]?.GetValue<bool>() != true ||
            storage["properties"]?["minimumTlsVersion"]?.GetValue<string>() is not ("TLS1_2" or "TLS1_3") ||
            storage["properties"]?["networkAcls"]?["bypass"]?.GetValue<string>() != "None" ||
            storage["properties"]?["networkAcls"]?["defaultAction"]?.GetValue<string>() != "Deny",
            "c1_artifact_target", "Current own storage must remain private and keyless.", 409);
        var container = await Read("https://management.azure.com" + storageId +
            "/blobServices/default/containers/c1-artifacts?api-version=2023-05-01", ct);
        RequireArtifactTime(deadline);
        VerifyArtifactContainer(container, storageId);
    }

    private static void VerifyArtifactContainer(JsonObject container, string storageId) =>
        Guard.Against(!SameArmId(container["id"]?.GetValue<string>(), storageId + "/blobServices/default/containers/c1-artifacts") ||
            !string.Equals(container["type"]?.GetValue<string>(), "Microsoft.Storage/storageAccounts/blobServices/containers",
                StringComparison.OrdinalIgnoreCase) || container["properties"]?["publicAccess"]?.GetValue<string>() != "None",
            "c1_artifact_container", "Only the original nonpublic artifact container is used.", 409);

    private void RequireArtifactTime(DateTimeOffset deadline)
    {
        PlanNormalRequests(0);
        Guard.Against(time.GetUtcNow() >= deadline, "c1_artifact_deadline",
            "The original publication deadline elapsed; no new window or retry.", 409);
    }

    private static Dictionary<string, string> ArtifactMetadata(FabricC1ProvisionedObject plan) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-ms-meta-cohors_creation"] = Text(plan.Response, "creationId"),
        ["x-ms-meta-cohors_deployment"] = Text(plan.Response, "deploymentBindingHash"),
        ["x-ms-meta-cohors_provenance"] = Text(plan.Response, "provenanceSha256"),
        ["x-ms-meta-cohors_sha256"] = Text(plan.Response, "archiveSha256")
    };

    private async Task<ArtifactReply> SendArtifactRequest(HttpMethod method, FabricC1ArtifactPublicationBinding binding,
        ArtifactPackage package, FabricC1ProvisionedObject plan, FileStream archive, HttpClient client,
        TokenCredential credential, CancellationToken ct)
    {
        var deadline = plan.Response["deadlineUtc"]!.GetValue<DateTimeOffset>();
        RequireArtifactTime(deadline);
        var (next, _) = PlanNormalRequests(1);
        SaveCheckpointJournal(next);
        RequireArtifactTime(deadline);
        var token = await credential.GetTokenAsync(new TokenRequestContext([FabricC1BlobCheckpointStore.Scope]), ct);
        FabricC1DeploymentHttp.ValidateBootstrapToken(token, input.BootstrapIdentity!, binding.StorageAudience,
            time.GetUtcNow(), graph: false, expectedIssuer: binding.StorageIssuer);
        using var request = new HttpRequestMessage(method, package.Endpoint);
        request.Headers.Authorization = new("Bearer", token.Token);
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-date", time.GetUtcNow().UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        if (method == HttpMethod.Put)
        {
            archive.Position = 0;
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Headers.Add("x-ms-client-request-id", Text(plan.Response, "creationId"));
            foreach (var header in ArtifactMetadata(plan)) request.Headers.Add(header.Key, header.Value);
            request.Content = new StreamContent(archive);
            request.Content.Headers.ContentLength = package.ArchiveLength;
            request.Content.Headers.ContentType = new("application/octet-stream");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Guard.Against(response.RequestMessage?.RequestUri != package.Endpoint || (int)response.StatusCode is >= 300 and < 400,
            "c1_artifact_redirect", "Artifact transport cannot change authority or follow redirects.", 409);
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (method == HttpMethod.Get && response.StatusCode == HttpStatusCode.OK)
        {
            Guard.Against(response.Content.Headers.ContentLength != package.ArchiveLength ||
                response.Content.Headers.ContentEncoding.Count != 0 || response.Headers.TransferEncoding.Count != 0 ||
                Header("x-ms-blob-type") != "BlockBlob" || Header("x-ms-tag-count") is not (null or "0"),
                "c1_artifact_content", "The artifact stream shape or length changed.", 409);
            foreach (var header in response.Headers.Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)))
            {
                var value = header.Value.SingleOrDefault();
                Guard.Against(value is null || metadata.Count >= 4, "c1_artifact_foreign", "Unexpected artifact metadata.", 409);
                metadata.Add(header.Key, value!);
            }
            var expected = ArtifactMetadata(plan);
            Guard.Against(!state.PendingCreates.ContainsKey(ArtifactPublicationKey) && !state.Objects.ContainsKey(ArtifactPublicationKey) ||
                metadata.Count != expected.Count || expected.Any(pair =>
                    !metadata.TryGetValue(pair.Key, out var value) || value != pair.Value),
                "c1_artifact_foreign", "Do not consume an unowned or differently marked artifact body.", 409);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new FabricC1LimitedStream(stream, package.ArchiveLength);
            Guard.Against(Convert.ToHexStringLower(await SHA256.HashDataAsync(limited, ct)) != package.ArchiveSha256,
                "c1_artifact_digest", "The private readback is not the bound source archive.", 409);
        }
        var etag = response.Headers.ETag;
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            Guard.Against(etag is null || etag.IsWeak || etag.Tag == "*", "c1_artifact_etag", "A strong storage ETag is required.", 409);
        ct.ThrowIfCancellationRequested();
        RequireArtifactTime(deadline);
        return new(response.StatusCode, Header("x-ms-error-code"), etag?.ToString(), metadata);
    }

    private static void RequireArtifactStatus(HttpStatusCode actual, HttpStatusCode expected) =>
        Guard.Against(actual != expected, "c1_artifact_http_" + (int)actual,
            "Artifact I/O was not confirmed; no retry, shared key, SAS or public fallback.", 409);
}
