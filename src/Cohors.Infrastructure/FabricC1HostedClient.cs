using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed class FabricC1HostedClient
{
    private readonly FabricC1ExecutionSettings settings;
    private readonly FabricC1AuthenticatedHttp http;
    private readonly IFabricC1CheckpointStore store;
    private readonly TimeProvider time;
    private FabricC1Checkpoint state;

    public FabricC1HostedClient(FabricC1ExecutionSettings settings, FabricC1AuthenticatedHttp http,
        IFabricC1CheckpointStore store, TimeProvider? time = null)
    {
        this.settings = settings;
        this.http = http;
        this.store = store;
        this.time = time ?? TimeProvider.System;
        var now = this.time.GetUtcNow();
        var hash = FabricC1Execution.Validate(settings, now, hosted: true, allowCleanup: true);
        var expiry = now.AddSeconds(settings.Preparation.Limits.MaxDurationSeconds!.Value);
        state = store.Load() ?? new(hash, "hosted-control", now, expiry < settings.ExpiresAtUtc ? expiry : settings.ExpiresAtUtc);
        Guard.Against(state.BindingHash != hash || state.Runtime != "hosted-control" ||
            state.DeadlineUtc > settings.ExpiresAtUtc || state.Requests < 0 || state.Requests > settings.HostedControlMaxRequests,
            "c1_checkpoint_binding", "Hosted control checkpoint binding changed.", 409);
        Save(state);
    }

    public async Task<FabricC1Observation> Invoke(FabricC1Invocation command, CancellationToken ct = default)
    {
        Guard.Against(command.BindingHash != state.BindingHash || !Guid.TryParse(command.InvocationId, out var invocationId) ||
            invocationId == Guid.Empty, "c1_invocation_binding", "Bound Hosted command and unique invocation ID required.", 409);
        Guard.Against(state.PendingRequest is not null && command.Operation != "status",
            "c1_hosted_unknown", "A lost Hosted response must be reconciled with status, not another invocation of the query.", 409);
        var pending = state.PendingRequest;
        var cleanup = command.Operation is "cancel" or "close" or "status";
        var remaining = state.DeadlineUtc.AddSeconds(cleanup ? settings.Preparation.Limits.CleanupTimeoutSeconds!.Value : 0) - time.GetUtcNow();
        Guard.Against(remaining <= TimeSpan.Zero || state.Requests >= settings.HostedControlMaxRequests,
            "c1_hosted_limit", "Hosted control budget or deadline exhausted.", 409);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(remaining);
        var body = JsonSerializer.SerializeToElement(command, Canonical.Json);
        Save(state with { Requests = state.Requests + 1, PendingRequest = pending ?? body,
            Status = "Unknown", Code = "c1_hosted_pending", InvocationId = command.InvocationId });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Hosted!.InvocationEndpoint)
            {
                Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.Send(request, bounded.Token);
            Guard.Against(!response.IsSuccessStatusCode, "c1_hosted_http_" + (int)response.StatusCode,
                "Hosted response was unsuccessful; no session creation or alternate endpoint.", 409);
            Guard.Against(!response.Headers.TryGetValues("x-agent-session-id", out var sessions) ||
                sessions.SingleOrDefault() != settings.Hosted.SessionId,
                "c1_hosted_session", "Response did not identify the prebound Hosted session.", 409);
            Guard.Against(response.Content.Headers.ContentType?.MediaType != "application/json",
                "c1_hosted_content", "The bounded Invocations handler must return JSON.", 409);
            await using var raw = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var limited = new FabricC1LimitedStream(raw, settings.Preparation.Limits.MaxResponseBytes!.Value);
            var observed = await JsonSerializer.DeserializeAsync<FabricC1Observation>(limited, Canonical.Json, bounded.Token)
                ?? throw new DomainException("c1_hosted_result", "Hosted observation is missing.", 409);
            Guard.Against(observed.BindingHash != state.BindingHash || observed.Runtime != "hosted" ||
                !FabricC1Execution.Hex(observed.StateHash, 64) || observed.Requests < 0 ||
                observed.Requests > settings.HostedMaxRequests,
                "c1_hosted_result", "Hosted observation is not bound to this run/runtime.", 409);
            var expectedId = pending is { } original
                ? original.GetProperty("invocationId").GetString() : command.InvocationId;
            if (command.Operation != "status" || pending is not null)
                Guard.Against(observed.InvocationId != expectedId, "c1_hosted_unreconciled",
                    "The Hosted state does not yet identify the outstanding invocation.", 409);
            Save(state with { PendingRequest = null, Status = observed.Status, Code = observed.Code,
                Result = JsonSerializer.SerializeToElement(observed, Canonical.Json),
                Exchanges = [.. state.Exchanges ?? [], new(time.GetUtcNow(), "hosted:" + command.Operation,
                    Canonical.Hash(body), (int)response.StatusCode, Canonical.Hash(observed), null)] });
            return observed;
        }
        catch (DomainException error)
        {
            Save(state with { Status = "Unknown", Code = error.Code });
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            Save(state with { Status = "Unknown", Code = error is OperationCanceledException ? "c1_hosted_interrupted" : "c1_hosted_incomplete" });
        }
        return new(state.Status, state.Code, state.BindingHash, Canonical.Hash(state), state.Requests, state.Runtime);
    }

    private void Save(FabricC1Checkpoint next)
    {
        store.Save(next);
        state = next;
    }
}
