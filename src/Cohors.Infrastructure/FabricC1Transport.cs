using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Cohors.Domain;

namespace Cohors.Infrastructure;

public sealed record FabricC1Exchange(DateTimeOffset AtUtc, string Method, string RequestHash, int HttpStatus,
    string? ResponseHash, string? TaskId);
public sealed record FabricC1Checkpoint(string BindingHash, string Runtime, DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc, int Requests = 0, string Status = "New", string? Code = null,
    string? SessionId = null, bool Initialized = false, JsonElement? Capabilities = null,
    JsonElement? Tools = null, JsonElement? PendingRequest = null, JsonElement? OriginalCall = null,
    string? LastEventId = null, DateTimeOffset? ResumeAfterUtc = null, string? TaskId = null,
    string? TaskStatus = null, JsonElement? Result = null, FabricC1Exchange[]? Exchanges = null,
    string? InvocationId = null);

public interface IFabricC1CheckpointStore
{
    FabricC1Checkpoint? Load();
    void Save(FabricC1Checkpoint checkpoint);
}

public sealed class FabricC1FileCheckpointStore : IFabricC1CheckpointStore, IDisposable
{
    private readonly string path;
    private readonly FileStream lease;
    private readonly long maximumBytes;

    public FabricC1FileCheckpointStore(string path, long maximumBytes)
    {
        Guard.Against(!Path.IsPathFullyQualified(path) || !Directory.Exists(Path.GetDirectoryName(path)),
            "c1_state_location", "An existing private evidence directory and absolute checkpoint path are required.", 409);
        this.path = path;
        this.maximumBytes = maximumBytes;
        Guard.Against(File.Exists(path) && new FileInfo(path).LinkTarget is not null,
            "c1_state_location", "Checkpoint files must not be symbolic links.", 409);
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public FabricC1Checkpoint? Load()
    {
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        Guard.Against(file.Length > maximumBytes, "c1_state_limit", "Checkpoint exceeds its bounded evidence size.", 409);
        return JsonSerializer.Deserialize<FabricC1Checkpoint>(file, Canonical.Json)
            ?? throw new DomainException("c1_state_missing", "Checkpoint is empty or invalid; never start a replacement query.", 409);
    }

    public void Save(FabricC1Checkpoint checkpoint)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, Canonical.Json);
        Guard.Against(bytes.LongLength > maximumBytes, "c1_state_limit", "Checkpoint exceeds its bounded evidence size.", 409);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose() => lease.Dispose();
}

public sealed class FabricC1Transport
{
    private readonly FabricC1ExecutionSettings settings;
    private readonly string fixture;
    private readonly FabricC1AuthenticatedHttp http;
    private readonly IFabricC1CheckpointStore store;
    private readonly TimeProvider time;
    private readonly Uri endpoint;
    private readonly int requestBudget;
    private FabricC1Checkpoint state;

    public FabricC1Transport(FabricC1ExecutionSettings settings, string fixture, string runtime,
        FabricC1AuthenticatedHttp http, IFabricC1CheckpointStore store, TimeProvider? time = null)
    {
        this.settings = settings;
        this.fixture = fixture;
        this.http = http;
        this.store = store;
        this.time = time ?? TimeProvider.System;
        Guard.Against(runtime is not ("direct" or "hosted"), "c1_runtime", "Explicit direct or Hosted execution required.", 409);
        var now = this.time.GetUtcNow();
        var binding = FabricC1Execution.Validate(settings, now, hosted: runtime == "hosted", allowCleanup: true);
        Guard.Against(Canonical.HashText(fixture) != settings.Preparation.Source.FixtureSha256,
            "c1_fixture", "Synthetic source bytes differ from the bound release.", 409);
        endpoint = new(settings.Preparation.Target.PublishedMcpEndpoint!);
        requestBudget = runtime == "direct" ? settings.DirectMaxRequests : settings.HostedMaxRequests;
        state = store.Load() ?? new(binding, runtime, now,
            Earlier(settings.ExpiresAtUtc, now.AddSeconds(settings.Preparation.Limits.MaxDurationSeconds!.Value)));
        Guard.Against(state.BindingHash != binding || state.Runtime != runtime ||
            state.DeadlineUtc > settings.ExpiresAtUtc || state.Requests < 0 || state.Requests > requestBudget,
            "c1_checkpoint_binding", "Checkpoint/configuration/runtime mismatch; no replacement session or query.", 409);
        Save(state);
    }

    public FabricC1Checkpoint Checkpoint => state;

    public async Task<FabricC1Observation> Execute(FabricC1Invocation invocation, CancellationToken ct = default)
    {
        Guard.Against(invocation.BindingHash != state.BindingHash, "c1_invocation_binding", "Invocation binding changed.", 409);
        if (invocation.Operation == "status") return Observation();
        if (invocation.InvocationId is not null && invocation.InvocationId == state.InvocationId) return Observation();
        Save(state with { InvocationId = invocation.InvocationId });
        var cleanup = invocation.Operation is "cancel" or "close";
        var deadline = state.DeadlineUtc.AddSeconds(cleanup ? settings.Preparation.Limits.CleanupTimeoutSeconds!.Value : 0);
        var remaining = deadline - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            Save(state with { Status = cleanup ? "CleanupIncomplete" : "Blocked", Code = "c1_deadline" });
            return Observation();
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(remaining);
        try
        {
            switch (invocation.Operation)
            {
                case "discover": await Discover(bounded.Token); break;
                case "query": await Query(invocation.ProbeId, task: false, bounded.Token); break;
                case "task-start": await Query(invocation.ProbeId, task: true, bounded.Token); break;
                case "resume": await Resume(bounded.Token); break;
                case "cancel": await Cancel(bounded.Token); break;
                case "close": await Close(bounded.Token); break;
                default: throw new DomainException("c1_operation", "Unsupported C1 operation.", 400);
            }
        }
        catch (DomainException error)
        {
            Save(state with { Status = cleanup ? "CleanupIncomplete" :
                state.PendingRequest is null ? "Blocked" : state.LastEventId is null ? "Unknown" : "PendingStream", Code = error.Code });
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or Azure.Identity.AuthenticationFailedException)
        {
            Save(state with { Status = cleanup ? "CleanupIncomplete" :
                state.PendingRequest is null && state.TaskId is null ? "Blocked" :
                state.LastEventId is not null ? "PendingStream" : "Unknown",
                Code = error is OperationCanceledException ? "c1_interrupted" : "c1_transport_incomplete" });
        }
        return Observation();
    }

    private FabricC1Observation Observation() =>
        new(state.Status, state.Code, state.BindingHash, Canonical.Hash(state), state.Requests, state.Runtime, state.Result, state.InvocationId);

    private void Save(FabricC1Checkpoint next)
    {
        store.Save(next);
        state = next;
    }

    private void RequireIdle()
    {
        Guard.Against(state.PendingRequest is not null || state.Status is "Blocked" or "Failed" or "Unknown" or "CleanupIncomplete" or "SessionClosed" ||
            state.TaskId is not null && state.TaskStatus is not ("completed" or "failed" or "cancelled"),
            "c1_unresolved", "Reconcile the existing request/task; never replay tools/call or open another session.", 409);
    }

    private async Task Discover(CancellationToken ct)
    {
        RequireIdle();
        if (state.Tools is not null) return;
        if (!state.Initialized)
        {
            object capabilities = settings.TaskDialect == FabricC1Execution.TaskDialect
                ? new { tasks = new { } } : new { };
            await Rpc("initialize", new { protocolVersion = FabricC1ProbeClient.ProtocolVersion, capabilities,
                clientInfo = new { name = "cohors-fabric-c1", version = settings.SourceCommit } }, ct);
        }
        if (state.Initialized)
        {
            await Notify("notifications/initialized", ct);
            await Rpc("tools/list", new { }, ct);
        }
    }

    private async Task Query(string? probeId, bool task, CancellationToken ct)
    {
        RequireIdle();
        Guard.Against(state.Tools is null || !state.Initialized, "c1_discovery_required", "Run and inspect bound discovery first.", 409);
        var tools = state.Tools ?? throw new DomainException("c1_discovery_required", "Bound tools are required.", 409);
        var plan = FabricC1ProbeClient.Call(settings.Preparation, fixture, probeId ?? "", tools);
        var tool = tools.GetProperty("tools")[0];
        var support = tool.TryGetProperty("execution", out var execution) &&
            execution.TryGetProperty("taskSupport", out var supported) ? supported.GetString() : null;
        Guard.Against(!task && support == "required", "c1_task_required", "This tool requires a reviewed task protocol.", 409);
        var parameters = plan.Messages[0].GetProperty("params");
        object wireParameters = parameters;
        if (task)
        {
            RequireTaskProtocol();
            Guard.Against(support is not ("optional" or "required"), "c1_task_support", "The discovered tool does not support task augmentation.", 409);
            wireParameters = new { name = parameters.GetProperty("name").GetString(),
                arguments = parameters.GetProperty("arguments"), task = new
                { ttl = (long)Math.Max(1, (state.DeadlineUtc - time.GetUtcNow()).TotalMilliseconds) } };
        }
        Save(state with { TaskId = null, TaskStatus = null, Result = null });
        await Rpc("tools/call", wireParameters, ct, isQuery: true);
    }

    private void RequireTaskProtocol()
    {
        Guard.Against(settings.TaskDialect != FabricC1Execution.TaskDialect ||
            state.Capabilities is not { } caps || !caps.TryGetProperty("tasks", out var tasks) ||
            !tasks.TryGetProperty("requests", out var requests) || !requests.TryGetProperty("tools", out var tools) ||
            !tools.TryGetProperty("call", out _),
            "c1_task_dialect", "The bound 2025-11-25 task capability is absent; do not mix the later tasks extension.", 409);
    }

    private async Task Resume(CancellationToken ct)
    {
        if (state.PendingRequest is { } pending)
        {
            if (state.LastEventId is not null)
            {
                await WaitForPoll(ct);
                using var request = Request(HttpMethod.Get);
                request.Headers.Add("Last-Event-ID", state.LastEventId);
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                await Send(request, pending, ct);
                return;
            }
            Guard.Against(state.TaskId is null ||
                pending.GetProperty("method").GetString() is not ("tasks/get" or "tasks/result" or "tasks/cancel"),
                "c1_unknown_without_cursor", "No resumable event or known task; no replacement query.", 409);
            Save(state with { PendingRequest = null });
        }
        RequireTaskProtocol();
        Guard.Against(state.TaskId is null || state.OriginalCall is null, "c1_task_missing", "A durable original task is required.", 409);
        Guard.Against(state.TaskStatus == "input_required", "c1_task_input", "Unattended execution stops on input_required; no user/OBO fallback.", 409);
        await WaitForPoll(ct);
        await Rpc("tasks/get", new { taskId = state.TaskId }, ct);
        if (state.TaskStatus == "completed") await Rpc("tasks/result", new { taskId = state.TaskId }, ct);
    }

    private async Task Cancel(CancellationToken ct)
    {
        RequireTaskProtocol();
        Guard.Against(state.TaskId is null || state.OriginalCall is null ||
            state.Capabilities is not { } caps || !caps.GetProperty("tasks").TryGetProperty("cancel", out _),
            "c1_cancel_unavailable", "A known task and advertised cancellation capability are required.", 409);
        Guard.Against(state.PendingRequest is not null, "c1_unresolved", "Resolve the outstanding task request before cancellation.", 409);
        await Rpc("tasks/cancel", new { taskId = state.TaskId }, ct, cleanup: true);
        // A cancellation response is not evidence that the remote work stopped.
        await WaitForPoll(ct);
        await Rpc("tasks/get", new { taskId = state.TaskId }, ct, cleanup: true);
        Save(state with { Status = state.TaskStatus is "cancelled" or "completed" or "failed" ? "Observed" : "CleanupIncomplete",
            Code = state.TaskStatus == "cancelled" ? "c1_cancel_terminal_observed" :
                state.TaskStatus is "completed" or "failed" ? "c1_task_finished_before_cancel" : "c1_cancel_not_terminal" });
    }

    private async Task Close(CancellationToken ct)
    {
        if (state.SessionId is null)
        {
            Save(state with { Status = state.PendingRequest is not null || UnfinishedTask() ? "CleanupIncomplete" : "SessionClosed",
                Code = "c1_no_session_to_delete" });
            return;
        }
        using var request = Request(HttpMethod.Delete);
        Reserve(cleanup: true);
        using var response = await http.Send(request, ct);
        AddExchange("DELETE", Canonical.HashText("DELETE"), (int)response.StatusCode, null);
        Guard.Against(response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NoContent or HttpStatusCode.NotFound),
            "c1_session_cleanup_unconfirmed", "Session termination was not confirmed; no full cleanup claim.", 409);
        Save(state with { SessionId = null, Status = state.PendingRequest is not null || UnfinishedTask() ? "CleanupIncomplete" : "SessionClosed",
            Code = "c1_mcp_session_only" });
    }

    private bool UnfinishedTask() => state.TaskId is not null && state.TaskStatus is not ("completed" or "failed" or "cancelled");

    private async Task WaitForPoll(CancellationToken ct)
    {
        if (state.ResumeAfterUtc is { } next && next > time.GetUtcNow())
            await Task.Delay(next - time.GetUtcNow(), time, ct);
    }

    private async Task Rpc(string method, object parameters, CancellationToken ct, bool isQuery = false, bool cleanup = false)
    {
        var message = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0",
            id = $"c1-{state.Requests + 1}", method, @params = parameters }, Canonical.Json);
        Save(state with { PendingRequest = message, OriginalCall = isQuery ? message : state.OriginalCall,
            LastEventId = null, ResumeAfterUtc = null, Code = null });
        using var request = Request(HttpMethod.Post);
        request.Content = new StringContent(message.GetRawText(), Encoding.UTF8, "application/json");
        await Send(request, message, ct, cleanup);
    }

    private async Task Notify(string method, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post);
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", method });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        Reserve(cleanup: false);
        using var response = await http.Send(request, ct);
        AddExchange(method, Canonical.HashText(json), (int)response.StatusCode, null);
        Guard.Against(response.StatusCode != HttpStatusCode.Accepted ||
            response.Content.Headers.ContentLength is > 0, "c1_notification", "Initialized notification was not accepted.", 409);
    }

    private HttpRequestMessage Request(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (state.SessionId is not null) request.Headers.Add("Mcp-Session-Id", state.SessionId);
        if (state.Initialized) request.Headers.Add("MCP-Protocol-Version", FabricC1ProbeClient.ProtocolVersion);
        return request;
    }

    private void Reserve(bool cleanup)
    {
        Guard.Against(state.Requests >= requestBudget - (cleanup ? 0 : 3),
            "c1_request_limit", "Request budget exhausted; reserved cleanup requests are not query retries.", 409);
        Save(state with { Requests = state.Requests + 1 });
    }

    private async Task Send(HttpRequestMessage request, JsonElement message, CancellationToken ct, bool cleanup = false)
    {
        Reserve(cleanup);
        using var response = await http.Send(request, ct);
        var method = message.GetProperty("method").GetString()!;
        AddExchange(request.Method == HttpMethod.Get ? "GET-resume:" + method : method,
            Canonical.Hash(message), (int)response.StatusCode, null);
        Guard.Against(!response.IsSuccessStatusCode, "c1_http_" + (int)response.StatusCode,
            "HTTP failure observed; without a healthy positive control it is not a passed denial.", 409);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
        {
            var values = ids.ToArray();
            Guard.Against(values.Length != 1 || !FabricC1Execution.HeaderValue(values[0]) ||
                state.SessionId is not null && state.SessionId != values[0] ||
                state.SessionId is null && method != "initialize", "c1_session_drift", "MCP session binding changed.", 409);
            Save(state with { SessionId = values[0] });
        }
        await using var raw = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new FabricC1LimitedStream(raw, settings.Preparation.Limits.MaxResponseBytes!.Value);
        JsonElement? body = null;
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media == "application/json")
        {
            using var document = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
            body = document.RootElement.Clone();
        }
        else if (media == "text/event-stream")
        {
            await foreach (var item in SseParser.Create(limited).EnumerateAsync(ct))
            {
                Guard.Against(item.EventType is not ("" or "message"), "c1_sse_event", "Unexpected SSE dialect or endpoint event.", 409);
                if (!string.IsNullOrEmpty(item.EventId))
                {
                    Guard.Against(!FabricC1Execution.HeaderValue(item.EventId), "c1_sse_cursor", "Invalid resumable event ID.", 409);
                    Save(state with { LastEventId = item.EventId });
                }
                if (item.ReconnectionInterval is { } interval)
                    Save(state with { ResumeAfterUtc = time.GetUtcNow().Add(interval) });
                if (string.IsNullOrWhiteSpace(item.Data)) continue;
                using var document = JsonDocument.Parse(item.Data);
                var candidate = document.RootElement;
                Guard.Against(candidate.ValueKind != JsonValueKind.Object, "c1_rpc_shape", "Expected one JSON-RPC object, not a batch.", 409);
                if (candidate.TryGetProperty("method", out _))
                {
                    Guard.Against(candidate.TryGetProperty("id", out _) ||
                        candidate.GetProperty("jsonrpc").GetString() != "2.0",
                        "c1_server_request", "Sampling, elicitation and other server requests are not enabled.", 409);
                    continue;
                }
                body = candidate.Clone();
                break;
            }
        }
        else throw new DomainException("c1_content_type", "Only JSON or Streamable HTTP SSE is supported.", 409);
        if (body is null)
        {
            Save(state with { Status = state.LastEventId is null ? "Unknown" : "PendingStream", Code = "c1_stream_incomplete" });
            return;
        }
        Accept(message, body.Value);
    }

    private void Accept(JsonElement request, JsonElement response)
    {
        Guard.Against(response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
            !response.TryGetProperty("id", out var id) || id.GetRawText() != request.GetProperty("id").GetRawText() ||
            response.TryGetProperty("result", out _) == response.TryGetProperty("error", out _),
            "c1_rpc_binding", "JSON-RPC response does not match the outstanding request.", 409);
        if (response.TryGetProperty("error", out var error))
        {
            Save(state with { PendingRequest = null, Result = error.Clone(), Status = "Failed", Code = "c1_rpc_error" });
            throw new DomainException("c1_rpc_error", "The service returned a JSON-RPC error; no alternative request was sent.", 409);
        }
        var result = response.GetProperty("result");
        var method = request.GetProperty("method").GetString();
        Guard.Against(result.ValueKind != JsonValueKind.Object, "c1_rpc_result", "Expected an object result.", 409);
        AddExchange("result:" + method, Canonical.Hash(request), 0, Canonical.Hash(response));
        Save(state with { Result = result.Clone(), PendingRequest = null, LastEventId = null, Status = "Observed", Code = null });
        switch (method)
        {
            case "initialize":
                Guard.Against(!result.TryGetProperty("protocolVersion", out var protocol) ||
                    protocol.GetString() != FabricC1ProbeClient.ProtocolVersion ||
                    !result.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                    !capabilities.TryGetProperty("tools", out _) ||
                    !result.TryGetProperty("serverInfo", out var server) || server.ValueKind != JsonValueKind.Object,
                    "c1_protocol", "Negotiated protocol/capabilities differ; no downgrade or extension guessing.", 409);
                Save(state with { Initialized = true, Capabilities = result.GetProperty("capabilities").Clone() });
                break;
            case "tools/list":
                Guard.Against(!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
                    tools.GetArrayLength() != 1 || result.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null,
                    "c1_tools", "Exactly one complete tool description is required; no pagination fallback.", 409);
                Save(state with { Tools = result.Clone(), Status = "Discovered" });
                break;
            case "tools/call":
                if (result.TryGetProperty("task", out var task))
                {
                    RequireTaskProtocol();
                    Guard.Against(!request.GetProperty("params").TryGetProperty("task", out _),
                        "c1_unrequested_task", "Unrequested task dialect; do not infer extension compatibility.", 409);
                    ReadTask(task, creating: true);
                }
                else
                {
                    Guard.Against(request.GetProperty("params").TryGetProperty("task", out _) ||
                        result.TryGetProperty("resultType", out _), "c1_task_dialect", "Unexpected task result dialect.", 409);
                    ReadToolResult(result);
                }
                break;
            case "tasks/get":
            case "tasks/cancel":
                ReadTask(result, creating: false);
                break;
            case "tasks/result":
                Guard.Against(!result.TryGetProperty("_meta", out var meta) ||
                    !meta.TryGetProperty("io.modelcontextprotocol/related-task", out var related) ||
                    related.GetProperty("taskId").GetString() != state.TaskId,
                    "c1_task_result_binding", "Result is not bound to the original task.", 409);
                ReadToolResult(result);
                break;
        }
    }

    private void ReadToolResult(JsonElement result)
    {
        Guard.Against(!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array,
            "c1_tool_result", "A tool content result is required; no invented empty answer.", 409);
        var error = result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
        Save(state with { Status = error ? "Failed" : "Observed", Code = error ? "c1_tool_error" : null });
    }

    private void ReadTask(JsonElement task, bool creating)
    {
        Guard.Against(task.ValueKind != JsonValueKind.Object ||
            !task.TryGetProperty("taskId", out var taskId) || taskId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(taskId.GetString()) ||
            !creating && taskId.GetString() != state.TaskId ||
            !task.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() is not ("working" or "input_required" or "completed" or "failed" or "cancelled"),
            "c1_task_binding", "Invalid or changed task identity/status.", 409);
        var observed = task.GetProperty("status").GetString()!;
        Guard.Against(!creating && (state.TaskStatus is "completed" or "failed" or "cancelled") && state.TaskStatus != observed,
            "c1_task_terminal_drift", "A terminal task changed state.", 409);
        var interval = task.TryGetProperty("pollInterval", out var poll) && poll.TryGetInt64(out var milliseconds)
            ? milliseconds : 1000;
        Guard.Against(interval < 0 || interval > int.MaxValue, "c1_task_poll", "Task poll interval is invalid.", 409);
        Save(state with { TaskId = task.GetProperty("taskId").GetString(), TaskStatus = observed,
            ResumeAfterUtc = time.GetUtcNow().AddMilliseconds(interval),
            Status = observed == "input_required" ? "Blocked" : observed is "working" ? "TaskPending" : "Observed",
            Code = observed == "input_required" ? "c1_task_input" : null });
    }

    private void AddExchange(string method, string requestHash, int status, string? responseHash) =>
        Save(state with { Exchanges = [.. state.Exchanges ?? [], new(time.GetUtcNow(), method, requestHash, status, responseHash, state.TaskId)] });

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
