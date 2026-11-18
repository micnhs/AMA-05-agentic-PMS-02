using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1CheckpointSeedReceipt(string Endpoint, string CreationId, string AllocationReceiptSha256,
    string SeedSha256, string ETag, DateTimeOffset ObservedAtUtc)
{
    public string EvidenceLevel => "original-conditional-seed-readback-not-current-state-or-c1-acceptance";
}

public sealed partial class FabricC1Provisioner
{
    private const string SeedPrefix = "checkpoint-seed-";

    public async Task<FabricC1CheckpointSeedReceipt> MaterializeBlobCheckpointNative(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding, CancellationToken ct)
    {
        Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "c1_cloud_ci_prohibited", "The source build runner cannot materialize private checkpoint state.", 409);
        FabricC1Execution.RequireSourceBuild(execution);
        var endpoint = ValidateCheckpointDeployment(execution, binding);
        RequireCheckpointContainer(binding);
        FabricC1PrivateFiles.RequireFile(binding.Identity.CredentialReference!);
        using var credential = new FabricC1Credential(binding.Identity, 10);
        using var client = FabricC1Execution.PrivateHttpClient(endpoint, binding.PrivateAddresses, 10);
        return await MaterializeBlobCheckpointWithTransport(execution, binding, client, credential.Credential, ct);
    }

    public async Task<FabricC1CheckpointSeedReceipt> MaterializeBlobCheckpointWithTransport(FabricC1ExecutionSettings execution,
        FabricC1BlobCheckpointBinding binding, HttpClient client, TokenCredential credential, CancellationToken ct)
    {
        var endpoint = ValidateCheckpointDeployment(execution, binding);
        RequireCheckpointContainer(binding);
        var allocation = PrepareBlobCheckpoint(execution, binding);
        var deadline = allocation.Seed.Checkpoint.DeadlineUtc;
        Guard.Against(time.GetUtcNow() >= deadline, "c1_deploy_checkpoint_deadline",
            "An expired original run cannot acquire a new seed.", 409);
        var allocationHash = Canonical.Hash(state.Objects[AllocationPrefix + binding.Runtime]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(allocation.Seed, Canonical.Json);
        Guard.Against(bytes.Length > 65536, "c1_deploy_checkpoint_seed_size", "Initial checkpoint seed is oversized.", 409);
        var seedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = SeedPrefix + binding.Runtime;
        var intentKey = key + "-intent";
        state.Objects.TryGetValue(intentKey, out var intent);
        state.PendingCreates.TryGetValue(key, out var pending);
        if (intent is not null)
            Guard.Against(intent.Kind != "CheckpointSeedIntent" || intent.Id != endpoint.AbsoluteUri ||
                intent.RequestHash != Canonical.Hash(intent.Response) ||
                intent.Response["allocationReceiptSha256"]?.GetValue<string>() != allocationHash ||
                intent.Response["seedSha256"]?.GetValue<string>() != seedHash ||
                !Guid.TryParseExact(intent.Response["creationId"]?.GetValue<string>(), "D", out var creation) || creation == Guid.Empty,
                "c1_deploy_checkpoint_seed_binding", "The original seed intent changed.", 409);
        Guard.Against(pending is not null && (intent is null || pending != intent.RequestHash),
            "c1_deploy_checkpoint_seed_binding", "A seed write has no matching original intent.", 409);
        if (state.Objects.TryGetValue(key, out var known))
        {
            Guard.Against(intent is null || pending is not null || known.Kind != "CheckpointSeedReadback" ||
                known.Id != endpoint.AbsoluteUri || known.RequestHash != intent.RequestHash,
                "c1_deploy_checkpoint_seed_binding", "The original seed readback does not match its write.", 409);
            var original = known.Response.Deserialize<FabricC1CheckpointSeedReceipt>(Canonical.Json) ??
                throw new DomainException("c1_deploy_checkpoint_seed_binding", "Seed receipt is missing.", 409);
            Guard.Against(original.Endpoint != endpoint.AbsoluteUri || original.AllocationReceiptSha256 != allocationHash ||
                original.SeedSha256 != seedHash || original.CreationId != intent!.Response["creationId"]?.GetValue<string>() ||
                original.ObservedAtUtc < allocation.Seed.Checkpoint.StartedAtUtc || original.ObservedAtUtc >= deadline ||
                !EntityTagHeaderValue.TryParse(original.ETag, out var etag) || etag.IsWeak || etag.Tag == "*",
                "c1_deploy_checkpoint_seed_binding", "Seed receipt fields differ from the original allocation.", 409);
            return original;
        }

        var http = new FabricC1AuthenticatedHttp(client, credential, binding.Identity,
            FabricC1BlobCheckpointStore.Scope, endpoint, time);
        var current = await SendSeedRequest(HttpMethod.Get, endpoint, http, bytes, null, deadline, ct);
        if (current.Status == HttpStatusCode.NotFound)
        {
            Guard.Against(current.ErrorCode != "BlobNotFound", "c1_deploy_checkpoint_container",
                "The own container must already exist; an account/container error is not an absent seed.", 409);
            Guard.Against(pending is not null, "c1_deploy_checkpoint_seed_unknown",
                "The original conditional create remains uncertain; no replay or replacement seed.", 409);
            if (intent is null)
            {
                var body = new JsonObject
                {
                    ["creationId"] = Guid.NewGuid().ToString("D"),
                    ["allocationReceiptSha256"] = allocationHash, ["seedSha256"] = seedHash
                };
                intent = new("CheckpointSeedIntent", endpoint.AbsoluteUri, Canonical.Hash(body), body);
            }
            else Guard.Against(!state.DeniedRequests.TryGetValue(key, out var denied) ||
                denied.RequestHash != intent.RequestHash || denied.Status is not (401 or 403),
                "c1_deploy_checkpoint_seed_unknown", "A previous intent without a proven rejection cannot be issued again.", 409);
            SaveCheckpointJournal(state with
            {
                Objects = new(state.Objects, StringComparer.Ordinal) { [intentKey] = intent },
                PendingCreates = new(state.PendingCreates, StringComparer.Ordinal) { [key] = intent.RequestHash }
            });
            var created = await SendSeedRequest(HttpMethod.Put, endpoint, http, bytes, intent, deadline, ct);
            if (created.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var creates = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
                creates.Remove(key);
                SaveCheckpointJournal(state with
                {
                    PendingCreates = creates,
                    DeniedRequests = new(state.DeniedRequests, StringComparer.Ordinal)
                    { [key] = new(intent.RequestHash, (int)created.Status) }
                });
            }
            RequireSeedStatus(created.Status, HttpStatusCode.Created);
            current = await SendSeedRequest(HttpMethod.Get, endpoint, http, bytes, null, deadline, ct);
        }
        RequireSeedStatus(current.Status, HttpStatusCode.OK);
        Guard.Against(intent is null || !state.PendingCreates.ContainsKey(key),
            "c1_deploy_checkpoint_seed_foreign", "An existing unjournaled checkpoint is never adopted or overwritten.", 409);
        var expectedMetadata = SeedMetadata(intent!);
        Guard.Against(current.Metadata.Count != expectedMetadata.Count || expectedMetadata.Any(pair =>
            !current.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value),
            "c1_deploy_checkpoint_seed_foreign", "The returned blob does not carry this original creation marker.", 409);
        var receipt = new FabricC1CheckpointSeedReceipt(endpoint.AbsoluteUri, Text(intent!.Response, "creationId"),
            allocationHash, seedHash, current.ETag!, time.GetUtcNow());
        var objects = new Dictionary<string, FabricC1ProvisionedObject>(state.Objects, StringComparer.Ordinal)
        {
            [key] = new("CheckpointSeedReadback", endpoint.AbsoluteUri, intent.RequestHash,
                JsonSerializer.SerializeToNode(receipt, Canonical.Json)!.AsObject())
        };
        var remaining = new Dictionary<string, string>(state.PendingCreates, StringComparer.Ordinal);
        remaining.Remove(key);
        SaveCheckpointJournal(state with { Objects = objects, PendingCreates = remaining });
        return receipt;
    }

    private void RequireCheckpointContainer(FabricC1BlobCheckpointBinding binding)
    {
        var foundation = Owned("arm-foundation", "ArmDeployment");
        var container = foundation["properties"]?["outputs"]?["checkpointContainer"]?["value"] as JsonObject;
        Guard.Against(container is null ||
            !SameArmId(container["id"]?.GetValue<string>(), binding.StorageAccountResourceId + "/blobServices/default/containers/c1-checkpoints") ||
            !string.Equals(container["type"]?.GetValue<string>(), "Microsoft.Storage/storageAccounts/blobServices/containers",
                StringComparison.OrdinalIgnoreCase) || container["properties"]?["publicAccess"]?.GetValue<string>() != "None",
            "c1_deploy_checkpoint_container", "The own completed foundation must bind the nonpublic checkpoint container.", 409);
    }

    private sealed record SeedResponse(HttpStatusCode Status, string? ErrorCode, string? ETag,
        Dictionary<string, string> Metadata);

    private static Dictionary<string, string> SeedMetadata(FabricC1ProvisionedObject intent) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-ms-meta-cohors_seed_id"] = Text(intent.Response, "creationId"),
        ["x-ms-meta-cohors_allocation"] = Text(intent.Response, "allocationReceiptSha256"),
        ["x-ms-meta-cohors_seed_sha256"] = Text(intent.Response, "seedSha256")
    };

    private async Task<SeedResponse> SendSeedRequest(HttpMethod method, Uri endpoint, FabricC1AuthenticatedHttp http,
        byte[] bytes, FabricC1ProvisionedObject? intent, DateTimeOffset seedDeadline, CancellationToken ct)
    {
        var (next, controlDeadline) = PlanNormalRequests(1);
        var deadline = seedDeadline < controlDeadline ? seedDeadline : controlDeadline;
        Guard.Against(time.GetUtcNow() >= deadline, "c1_deploy_checkpoint_deadline", "Original seed deadline elapsed.", 409);
        SaveCheckpointJournal(next);
        var remaining = deadline - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero, "c1_deploy_checkpoint_deadline", "No request after expired journal persistence.", 409);
        using var timeout = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10), time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-date", time.GetUtcNow().UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        if (method == HttpMethod.Put)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Headers.Add("x-ms-client-request-id", Text(intent!.Response, "creationId"));
            foreach (var header in SeedMetadata(intent)) request.Headers.Add(header.Key, header.Value);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await http.Send(request, bounded.Token);
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.StatusCode == HttpStatusCode.OK && method == HttpMethod.Get)
        {
            Guard.Against(response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength != bytes.Length || response.Content.Headers.ContentEncoding.Count != 0 ||
                Header("x-ms-blob-type") != "BlockBlob" || Header("x-ms-tag-count") is not (null or "0"),
                "c1_deploy_checkpoint_seed_content", "The original bounded JSON seed is missing, changed or already in use.", 409);
            foreach (var header in response.Headers.Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)))
            {
                var value = header.Value.SingleOrDefault();
                Guard.Against(value is null || metadata.Count >= 3, "c1_deploy_checkpoint_seed_foreign",
                    "Checkpoint metadata is unexpected; do not overwrite it.", 409);
                metadata.Add(header.Key, value!);
            }
            await using var raw = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var limited = new FabricC1LimitedStream(raw, bytes.Length);
            using var body = new MemoryStream(bytes.Length);
            await limited.CopyToAsync(body, bounded.Token);
            Guard.Against(!body.ToArray().AsSpan().SequenceEqual(bytes),
                "c1_deploy_checkpoint_seed_content", "The blob is not the exact original seed; no reset, import or overwrite.", 409);
        }
        var etag = response.Headers.ETag;
        if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK && method == HttpMethod.Get)
            Guard.Against(etag is null || etag.IsWeak || etag.Tag == "*",
                "c1_deploy_checkpoint_seed_etag", "Seed creation/readback requires a strong storage ETag.", 409);
        bounded.Token.ThrowIfCancellationRequested();
        Guard.Against(time.GetUtcNow() >= deadline, "c1_deploy_checkpoint_deadline",
            "The original seed deadline elapsed before I/O completion; retain the unresolved original intent.", 409);
        return new(response.StatusCode, Header("x-ms-error-code"), etag?.ToString(), metadata);
    }

    private static void RequireSeedStatus(HttpStatusCode actual, HttpStatusCode expected) =>
        Guard.Against(actual != expected, "c1_deploy_checkpoint_seed_http_" + (int)actual,
            "Seed I/O was not confirmed; no retry, shared key, new clock, replacement blob or file fallback.", 409);
}
