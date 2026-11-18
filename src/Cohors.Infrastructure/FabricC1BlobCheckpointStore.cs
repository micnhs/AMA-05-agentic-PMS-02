using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Core;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1BlobCheckpointBinding(string Contract, string DeploymentBindingHash,
    string ExecutionBindingHash, string Runtime, string StorageAccountResourceId, FabricC1Identity Identity,
    string[] PrivateAddresses, string PrivatePathReviewSha256, string BudgetAllocationSha256, int AllocatedRequests);
public sealed record FabricC1CheckpointReservation(string Id, string JournalReceiptSha256, int Requests,
    DateTimeOffset ExpiresAtUtc);
public sealed record FabricC1BlobCheckpointDocument(string Contract, string StorageBindingHash,
    FabricC1Checkpoint Checkpoint, FabricC1CheckpointReservation[] Reservations);

public sealed class FabricC1BlobCheckpointStore : IFabricC1CheckpointStore, IDisposable, IAsyncDisposable
{
    public const string Contract = "cohors-fabric-c1-blob-checkpoint-1";
    public const string DocumentContract = "cohors-fabric-c1-blob-checkpoint-document-1";
    public const string Scope = "https://storage.azure.com/.default";
    private static readonly JsonSerializerOptions Json = new(Canonical.Json)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly FabricC1BlobCheckpointBinding binding;
    private readonly FabricC1CheckpointReservation reservation;
    private readonly FabricC1AuthenticatedHttp blobHttp;
    private readonly FabricC1AuthenticatedHttp leaseHttp;
    private readonly Uri blobUri;
    private readonly Uri leaseUri;
    private readonly TimeProvider time;
    private readonly int maximumBytes;
    private readonly int requestBudget;
    private readonly DateTimeOffset maximumDeadline;
    private readonly TimeSpan maximumDuration;
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource failure = new();
    private readonly string leaseId = Guid.NewGuid().ToString("D");
    private readonly ITimer leaseExpiry;
    private readonly ITimer invocationExpiry;
    private readonly IDisposable[] owned;
    private readonly Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
    private FabricC1BlobCheckpointDocument document = null!;
    private EntityTagHeaderValue etag = null!;
    private Task renewal = Task.CompletedTask;
    private Task cancellation = Task.CompletedTask;
    private DateTimeOffset leaseStartedAt;
    private long leaseTimestamp;
    private string? failureCode;
    private bool leased;
    private bool disposed;
    private int requests;

    public CancellationToken FailureToken => failure.Token;
    public int Requests => Volatile.Read(ref requests);

    public static Uri Endpoint(FabricC1ExecutionSettings settings, FabricC1BlobCheckpointBinding binding, DateTimeOffset now)
    {
        var hash = FabricC1Execution.Validate(settings, now, binding.Runtime is "hosted" or "hosted-control", allowCleanup: true);
        var resource = Regex.Match(binding.StorageAccountResourceId ?? "",
            "^/subscriptions/([0-9a-f-]{36})/resourceGroups/([A-Za-z0-9_.-]{1,90})/providers/Microsoft\\.Storage/storageAccounts/([a-z0-9]{3,24})$",
            RegexOptions.CultureInvariant);
        Guard.Against(binding.Contract != Contract || binding.ExecutionBindingHash != hash ||
            binding.Runtime is not ("direct" or "hosted" or "hosted-control") ||
            !FabricC1Execution.Hex(binding.DeploymentBindingHash, 64) ||
            !FabricC1Execution.Hex(binding.PrivatePathReviewSha256, 64) ||
            !FabricC1Execution.Hex(binding.BudgetAllocationSha256, 64) ||
            binding.AllocatedRequests is < 4 or > 1000 || !resource.Success ||
            !Guid.TryParse(resource.Groups[1].Value, out var subscription) || subscription == Guid.Empty,
            "c1_checkpoint_storage_binding", "An independent reviewed storage and preallocated I/O budget binding is required.", 409);
        var expected = binding.Runtime == "hosted-control" ? settings.Hosted!.Caller : settings.Preparation.Identity;
        var identity = binding.Identity;
        Guard.Against(identity is null || identity.TenantId != expected.TenantId || identity.ClientId != expected.ClientId ||
            identity.PrincipalObjectId != expected.PrincipalObjectId || identity.CredentialKind != expected.CredentialKind ||
            identity.CredentialReference != expected.CredentialReference ||
            identity.ExpectedAudience is not ("https://storage.azure.com" or "https://storage.azure.com/") ||
            FabricC1ProbeClient.Preflight(settings.Preparation with { Identity = identity }, discoveryOnly: true).Status != "PreparedOffline",
            "c1_checkpoint_identity", "Storage uses the same explicit app-only caller, with its separately bound storage audience/issuer.", 409);
        FabricC1Execution.PrivateAddresses(binding.PrivateAddresses);
        return new($"https://{resource.Groups[3].Value}.blob.core.windows.net/c1-checkpoints/" +
            $"{binding.DeploymentBindingHash.ToLowerInvariant()}/{hash}/{binding.Runtime}.json");
    }

    public static async Task<FabricC1BlobCheckpointStore> OpenNative(FabricC1ExecutionSettings settings,
        FabricC1BlobCheckpointBinding binding, FabricC1CheckpointReservation reservation, CancellationToken ct = default)
    {
        var endpoint = Endpoint(settings, binding, DateTimeOffset.UtcNow);
        Guard.Against(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "c1_cloud_ci_prohibited", "The credential-free build runner is not a checkpoint service runner.", 409);
        FabricC1Execution.RequireSourceBuild(settings);
        ValidateReservation(settings, binding, reservation, DateTimeOffset.UtcNow);
        var credential = new FabricC1Credential(binding.Identity, 10);
        var client = FabricC1Execution.PrivateHttpClient(endpoint, binding.PrivateAddresses, 10);
        try
        {
            return await OpenCore(settings, binding, reservation, client, credential.Credential, TimeProvider.System,
                [credential, client], ct);
        }
        catch
        {
            client.Dispose();
            credential.Dispose();
            throw;
        }
    }

    public static Task<FabricC1BlobCheckpointStore> OpenWithTransport(FabricC1ExecutionSettings settings,
        FabricC1BlobCheckpointBinding binding, FabricC1CheckpointReservation reservation,
        HttpClient client, TokenCredential credential, TimeProvider time, CancellationToken ct = default) =>
        OpenCore(settings, binding, reservation, client, credential, time, [], ct);

    private static async Task<FabricC1BlobCheckpointStore> OpenCore(FabricC1ExecutionSettings settings,
        FabricC1BlobCheckpointBinding binding, FabricC1CheckpointReservation reservation,
        HttpClient client, TokenCredential credential, TimeProvider time, IDisposable[] owned, CancellationToken ct)
    {
        var endpoint = Endpoint(settings, binding, time.GetUtcNow());
        ValidateReservation(settings, binding, reservation, time.GetUtcNow());
        var store = new FabricC1BlobCheckpointStore(settings, binding, reservation, endpoint, client, credential, time, owned);
        try
        {
            await store.io.WaitAsync(ct);
            try
            {
                await store.Lease("acquire", ct);
                store.renewal = store.RenewLoop();
                await store.ReadOriginal(ct);
                var previous = store.document.Reservations;
                var cleanup = settings.Preparation.Limits.CleanupTimeoutSeconds!.Value;
                Guard.Against(previous.Length > 250 ||
                    previous.Any(value => value is null || value.Requests is < 4 or > 1000 ||
                        !Guid.TryParseExact(value.Id, "D", out var id) || id == Guid.Empty ||
                        value.Id != id.ToString("D") || !FabricC1Execution.Hex(value.JournalReceiptSha256, 64) ||
                        value.ExpiresAtUtc <= store.document.Checkpoint.StartedAtUtc ||
                        value.ExpiresAtUtc > store.document.Checkpoint.DeadlineUtc.AddSeconds(cleanup)) ||
                    previous.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != previous.Length ||
                    previous.Any(value => string.Equals(value.Id, reservation.Id, StringComparison.OrdinalIgnoreCase)) ||
                    previous.Sum(value => (long)value.Requests) + reservation.Requests > binding.AllocatedRequests,
                    "c1_checkpoint_reservation", "The durable I/O reservation is reused, malformed or exceeds the original allocation.", 409);
                Guard.Against(reservation.ExpiresAtUtc > store.document.Checkpoint.DeadlineUtc.AddSeconds(cleanup),
                    "c1_checkpoint_deadline", "A reservation cannot extend the established checkpoint's cleanup deadline.", 409);
                await store.Write(store.document with { Reservations = [.. previous, reservation] }, ct);
            }
            finally { store.io.Release(); }
            return store;
        }
        catch (Exception original)
        {
            try { await store.DisposeAsync(); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Checkpoint opening failed and its own lease release was not confirmed.", original, cleanup);
            }
            throw;
        }
    }

    private FabricC1BlobCheckpointStore(FabricC1ExecutionSettings settings, FabricC1BlobCheckpointBinding binding,
        FabricC1CheckpointReservation reservation, Uri endpoint, HttpClient client, TokenCredential credential,
        TimeProvider time, IDisposable[] owned)
    {
        this.binding = binding;
        this.reservation = reservation;
        this.time = time;
        this.owned = owned;
        blobUri = endpoint;
        leaseUri = new(endpoint.AbsoluteUri + "?comp=lease");
        blobHttp = new(client, credential, binding.Identity, Scope, blobUri, time);
        leaseHttp = new(client, credential, binding.Identity, Scope, leaseUri, time);
        maximumBytes = checked(8 * settings.Preparation.Limits.MaxResponseBytes!.Value +
            2048 * settings.Preparation.Limits.MaxRequests!.Value + 65536);
        maximumDeadline = settings.ExpiresAtUtc;
        maximumDuration = TimeSpan.FromSeconds(settings.Preparation.Limits.MaxDurationSeconds!.Value);
        requestBudget = binding.Runtime switch
        {
            "direct" => settings.DirectMaxRequests, "hosted" => settings.HostedMaxRequests,
            _ => settings.HostedControlMaxRequests
        };
        leaseExpiry = time.CreateTimer(_ => Fence("c1_checkpoint_lease_lost"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        invocationExpiry = time.CreateTimer(_ => Fence("c1_checkpoint_deadline"), null,
            reservation.ExpiresAtUtc - time.GetUtcNow(), Timeout.InfiniteTimeSpan);
    }

    private static void ValidateReservation(FabricC1ExecutionSettings settings, FabricC1BlobCheckpointBinding binding,
        FabricC1CheckpointReservation reservation, DateTimeOffset now) =>
        Guard.Against(!Guid.TryParseExact(reservation.Id, "D", out var id) || id == Guid.Empty ||
            reservation.Id != id.ToString("D") ||
            !FabricC1Execution.Hex(reservation.JournalReceiptSha256, 64) ||
            reservation.Requests < 4 || reservation.Requests > binding.AllocatedRequests ||
            reservation.ExpiresAtUtc <= now ||
            reservation.ExpiresAtUtc > settings.ExpiresAtUtc.AddSeconds(settings.Preparation.Limits.CleanupTimeoutSeconds!.Value),
            "c1_checkpoint_reservation", "A previously debited, bounded invocation reservation is required; the consumer cannot allocate or reset it.", 409);

    public FabricC1Checkpoint Load()
    {
        io.Wait();
        try { Demand(); return document.Checkpoint; }
        finally { io.Release(); }
    }

    public void Save(FabricC1Checkpoint checkpoint)
    {
        io.Wait();
        try
        {
            Demand();
            var before = document.Checkpoint;
            Guard.Against(checkpoint.BindingHash != before.BindingHash || checkpoint.Runtime != before.Runtime ||
                checkpoint.StartedAtUtc != before.StartedAtUtc || checkpoint.DeadlineUtc != before.DeadlineUtc ||
                checkpoint.Requests < before.Requests || checkpoint.Requests > requestBudget,
                "c1_checkpoint_binding", "Checkpoint identity, original clock and consumed requests cannot be replaced or reset.", 409);
            if (Canonical.Hash(checkpoint) != Canonical.Hash(before))
                Write(document with { Checkpoint = checkpoint }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { io.Release(); }
    }

    public async Task RenewLease(CancellationToken ct = default)
    {
        await io.WaitAsync(ct);
        try { Demand(); await Lease("renew", ct); }
        finally { io.Release(); }
    }

    private async Task RenewLoop()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), time, stop.Token);
                await RenewLease(stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (ExpectedFailure(error)) { Fence("c1_checkpoint_lease_lost"); }
    }

    private async Task ReadOriginal(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, failure.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = Request(HttpMethod.Get, blobUri);
        request.Headers.Add("x-ms-lease-id", leaseId);
        using var response = await Send(request, blobHttp, release: false, bounded.Token);
        RequireStatus(response, HttpStatusCode.OK);
        Guard.Against(response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is not > 0 || response.Content.Headers.ContentLength > maximumBytes ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            !response.Headers.TryGetValues("x-ms-blob-type", out var types) || types.SingleOrDefault() != "BlockBlob",
            "c1_checkpoint_content", "The existing checkpoint is not a bounded JSON block blob.", 409);
        Guard.Against(response.Headers.TryGetValues("x-ms-tag-count", out var tags) && tags.SingleOrDefault() != "0",
            "c1_checkpoint_metadata", "Unexpected index tags require a separate preservation contract.", 409);
        foreach (var header in response.Headers.Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)))
        {
            var value = header.Value.SingleOrDefault();
            Guard.Against(value is null || metadata.Sum(pair => pair.Key.Length + pair.Value.Length) +
                header.Key.Length + value.Length > 8192, "c1_checkpoint_metadata", "Blob metadata is malformed or oversized.", 409);
            metadata.Add(header.Key, value!);
        }
        if (response.Headers.TryGetValues("x-ms-encryption-scope", out var scopes))
        {
            var scope = scopes.SingleOrDefault();
            Guard.Against(string.IsNullOrWhiteSpace(scope), "c1_checkpoint_metadata", "Encryption scope readback is ambiguous.", 409);
            metadata.Add("x-ms-encryption-scope", scope!);
        }
        await using var raw = await response.Content.ReadAsStreamAsync(bounded.Token);
        using var limited = new FabricC1LimitedStream(raw, maximumBytes);
        document = await JsonSerializer.DeserializeAsync<FabricC1BlobCheckpointDocument>(limited, Json, bounded.Token)
            ?? throw new DomainException("c1_checkpoint_missing", "The checkpoint document is missing; never create replacement state.", 409);
        var checkpoint = document.Checkpoint;
        Guard.Against(document.Contract != DocumentContract || document.StorageBindingHash != Canonical.Hash(binding) ||
            document.Reservations is null || checkpoint is null || checkpoint.BindingHash != binding.ExecutionBindingHash ||
            checkpoint.Runtime != binding.Runtime || checkpoint.StartedAtUtc > time.GetUtcNow() ||
            checkpoint.DeadlineUtc < checkpoint.StartedAtUtc || checkpoint.DeadlineUtc > maximumDeadline ||
            checkpoint.DeadlineUtc - checkpoint.StartedAtUtc > maximumDuration ||
            checkpoint.Requests < 0 || checkpoint.Requests > requestBudget,
            "c1_checkpoint_binding", "The pre-existing checkpoint belongs to another storage/run/runtime or invalid original clock.", 409);
        etag = StrongEtag(response);
    }

    private async Task Write(FabricC1BlobCheckpointDocument next, CancellationToken ct)
    {
        Demand();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        Guard.Against(bytes.Length > maximumBytes, "c1_state_limit", "Checkpoint exceeds its bounded evidence size.", 409);
        using var request = Request(HttpMethod.Put, blobUri);
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Headers.Add("x-ms-lease-id", leaseId);
        foreach (var header in metadata) request.Headers.Add(header.Key, header.Value);
        request.Headers.IfMatch.Add(etag);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json");
        try
        {
            using var response = await Send(request, blobHttp, release: false, ct);
            RequireStatus(response, HttpStatusCode.Created);
            etag = StrongEtag(response);
            document = next;
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            Fence("c1_checkpoint_write_unknown");
            throw;
        }
    }

    private async Task Lease(string action, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var timestamp = time.GetTimestamp();
        using var request = Request(HttpMethod.Put, leaseUri);
        request.Content = new ByteArrayContent([]);
        request.Headers.Add("x-ms-lease-action", action);
        if (action == "acquire")
        {
            request.Headers.Add("x-ms-lease-duration", "60");
            request.Headers.Add("x-ms-proposed-lease-id", leaseId);
        }
        else request.Headers.Add("x-ms-lease-id", leaseId);
        try
        {
            using var response = await Send(request, leaseHttp, release: action == "release", ct);
            if (action == "renew" && response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                leased = false;
            RequireStatus(response, action == "acquire" ? HttpStatusCode.Created : HttpStatusCode.OK);
            if (action == "release") { leased = false; return; }
            Guard.Against(!response.Headers.TryGetValues("x-ms-lease-id", out var ids) ||
                !Guid.TryParse(ids.SingleOrDefault(), out var actual) || actual != Guid.Parse(leaseId),
                "c1_checkpoint_lease_binding", "The service did not return this instance's proposed lease.", 409);
            leased = true;
            leaseStartedAt = started;
            leaseTimestamp = timestamp;
            var remaining = TimeSpan.FromSeconds(50) - time.GetElapsedTime(timestamp);
            Guard.Against(remaining <= TimeSpan.Zero, "c1_checkpoint_lease_lost", "The lease response arrived too late.", 409);
            leaseExpiry.Change(remaining, Timeout.InfiniteTimeSpan);
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            Fence("c1_checkpoint_lease_lost");
            throw;
        }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, FabricC1AuthenticatedHttp http,
        bool release, CancellationToken ct)
    {
        if (!release) Demand(requireLease: false);
        Guard.Against(time.GetUtcNow() >= reservation.ExpiresAtUtc || requests >= reservation.Requests - (release ? 0 : 1),
            "c1_checkpoint_io_limit", "The original I/O reservation or deadline is exhausted; no retries or new allowance.", 409);
        Interlocked.Increment(ref requests);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, release ? CancellationToken.None : failure.Token);
        var remaining = reservation.ExpiresAtUtc - time.GetUtcNow();
        bounded.CancelAfter(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10));
        return await http.Send(request, bounded.Token);
    }

    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-date", time.GetUtcNow().UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        return request;
    }

    private static EntityTagHeaderValue StrongEtag(HttpResponseMessage response)
    {
        var value = response.Headers.ETag;
        Guard.Against(value is null || value.IsWeak || value.Tag == "*", "c1_checkpoint_etag", "A strong storage ETag is required.", 409);
        return value!;
    }

    private static void RequireStatus(HttpResponseMessage response, HttpStatusCode status) =>
        Guard.Against(response.StatusCode != status, "c1_checkpoint_http_" + (int)response.StatusCode,
            "Checkpoint request failed; no retry, lease break, replacement blob or file fallback.", 409);

    private void Demand(bool requireLease = true)
    {
        Guard.Against(disposed, "c1_checkpoint_disposed", "Checkpoint owner has been disposed.", 409);
        if (time.GetUtcNow() >= reservation.ExpiresAtUtc) Fence("c1_checkpoint_deadline");
        if (leased && (time.GetUtcNow() >= leaseStartedAt.AddSeconds(50) ||
            time.GetElapsedTime(leaseTimestamp) >= TimeSpan.FromSeconds(50))) Fence("c1_checkpoint_lease_lost");
        Guard.Against(failureCode is not null, failureCode ?? "c1_checkpoint_fenced", "Checkpoint authority was lost; stop the in-flight transport.", 409);
        Guard.Against(requireLease && !leased, "c1_checkpoint_lease_lost", "A confirmed lease is required.", 409);
    }

    private void Fence(string code)
    {
        if (Interlocked.CompareExchange(ref failureCode, code, null) is null)
            cancellation = failure.CancelAsync();
    }

    private static bool ExpectedFailure(Exception error) => error is DomainException or HttpRequestException or
        IOException or OperationCanceledException or JsonException or InvalidOperationException or
        Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        stop.Cancel();
        try
        {
            await renewal;
            await io.WaitAsync();
            try { if (leased) await Lease("release", CancellationToken.None); }
            finally { io.Release(); }
        }
        finally
        {
            disposed = true;
            await leaseExpiry.DisposeAsync();
            await invocationExpiry.DisposeAsync();
            await cancellation;
            foreach (var resource in owned) resource.Dispose();
            stop.Dispose();
            failure.Dispose();
        }
    }
}
