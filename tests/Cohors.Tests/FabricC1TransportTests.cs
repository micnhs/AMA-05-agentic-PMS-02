using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Cohors.Api;
using Cohors.Domain;
using Cohors.Infrastructure;

namespace Cohors.Tests;

public sealed class FabricC1TransportTests
{
    [Fact]
    public async Task JsonHandshakeAndQueryUseBoundSessionProtocolAndAppTokenWithoutServiceAcceptance()
    {
        using var run = new Harness();
        run.Discovery();
        run.Handler.Respond(request => Rpc(request, ToolResult()));
        Assert.Equal("Discovered", (await run.Execute("discover")).Status);
        Assert.Equal("Observed", (await run.Execute("query", "allowed")).Status);
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" },
            run.Handler.Requests.Select(r => r.RpcMethod));
        Assert.Null(run.Handler.Requests[0].Protocol);
        Assert.All(run.Handler.Requests.Skip(1), request =>
        {
            Assert.Equal("synthetic-session", request.Session);
            Assert.Equal("2025-11-25", request.Protocol);
        });
        Assert.All(run.Handler.Requests, request =>
        {
            Assert.Equal(run.Settings.Preparation.Target.PublishedMcpEndpoint, request.Url);
            Assert.StartsWith("Bearer ", request.Authorization);
            Assert.Contains("application/json", request.Accept);
            Assert.Contains("text/event-stream", request.Accept);
        });
        Assert.All(run.Credential.Scopes, scope => Assert.Equal(FabricC1ProbeClient.TokenScope, scope));
        Assert.False(FabricC1ProbeClient.Preflight(run.Settings.Preparation).CloudExecutionAllowed);
    }

    [Fact]
    public async Task SseUsesTheFrameworkParserForFragmentedMultilineDataAndProgress()
    {
        using var run = new Harness();
        run.Discovery();
        run.Handler.Respond(request =>
        {
            var id = RequestId(request);
            var data = ": keepalive\r\n\r\nid: cursor-1\r\nevent: message\r\n" +
                "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\r\n\r\n" +
                "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\r\ndata: \"id\":\"" + id +
                "\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"SYNTHETIC-ALLOW-A\"}]}}\r\n\r\n";
            return Sse(data, fragmented: true);
        });
        await run.Execute("discover");
        var observed = await run.Execute("query", "allowed");
        Assert.Equal("Observed", observed.Status);
        Assert.Equal("SYNTHETIC-ALLOW-A", observed.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ProtocolDriftStopsBeforeNotificationOrToolsAndCannotDowngrade()
    {
        using var run = new Harness();
        run.Handler.Respond(request => Rpc(request, new { protocolVersion = "2026-03-26",
            capabilities = new { tools = new { } }, serverInfo = new { name = "synthetic", version = "1" } }));
        var result = await run.Execute("discover");
        Assert.Equal("c1_protocol", result.Code);
        await run.Execute("discover");
        Assert.Single(run.Handler.Requests);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpErrorsAndRedirectsNeverRetryOrBecomePassedDenials(int status)
    {
        using var run = new Harness();
        run.Handler.Respond(_ => new((HttpStatusCode)status)
        {
            Headers = { Location = new Uri("https://unreviewed.invalid/") },
            Content = new StringContent("must not be treated as an answer")
        });
        var result = await run.Execute("discover");
        Assert.NotEqual("Observed", result.Status);
        Assert.Equal(status == 301 ? "c1_redirect" : "c1_http_" + status, result.Code);
        await run.Execute("discover");
        Assert.Single(run.Handler.Requests);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("bytes")]
    [InlineData("server-request")]
    public async Task UnboundOrOversizedResponsesStopWithoutFallback(string failure)
    {
        using var run = new Harness();
        run.Handler.Respond(request => failure switch
        {
            "id" => Json(new { jsonrpc = "2.0", id = "different-request", result = new { } }),
            "bytes" => Json(new { jsonrpc = "2.0", id = RequestId(request), result = new { value = new string('x', 40000) } }),
            _ => Sse("data: {\"jsonrpc\":\"2.0\",\"id\":\"server-id\",\"method\":\"sampling/createMessage\",\"params\":{}}\n\n")
        });
        var result = await run.Execute("discover");
        Assert.Contains(result.Code, new[] { "c1_rpc_binding", "c1_response_limit", "c1_server_request" });
        Assert.Single(run.Handler.Requests);
    }

    [Fact]
    public async Task LostQueryWithoutCursorStaysUnknownAcrossRestartAndIsNotReissued()
    {
        using var run = new Harness();
        run.Discovery();
        run.Handler.Respond(_ => Sse(": connection ended before a result\n\n"));
        await run.Execute("discover");
        Assert.Equal("Unknown", (await run.Execute("query", "allowed")).Status);
        run.Restart();
        Assert.Equal("c1_unknown_without_cursor", (await run.Execute("resume")).Code);
        await run.Execute("query", "allowed");
        Assert.Equal(4, run.Handler.Requests.Count);
        Assert.Single(run.Handler.Requests, r => r.RpcMethod == "tools/call");
    }

    [Fact]
    public async Task InterruptedSseResumesOriginalIdUsingGetNotAnotherQuery()
    {
        using var run = new Harness();
        run.Discovery();
        run.Handler.Respond(_ => Sse("id: original-cursor\nretry: 0\ndata:\n\n"));
        await run.Execute("discover");
        Assert.Equal("PendingStream", (await run.Execute("query", "allowed")).Status);
        var id = run.Store.Value!.PendingRequest!.Value.GetProperty("id").GetString();
        run.Restart();
        run.Handler.Respond(_ => Sse($"data: {{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"content\":[]}}}}\n\n"));
        Assert.Equal("Observed", (await run.Execute("resume")).Status);
        var resumed = run.Handler.Requests.Last();
        Assert.Equal("GET", resumed.Method);
        Assert.Equal("original-cursor", resumed.Cursor);
        Assert.Equal("synthetic-session", resumed.Session);
        Assert.Single(run.Handler.Requests, r => r.RpcMethod == "tools/call");
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("tid")]
    [InlineData("oid")]
    [InlineData("appid")]
    [InlineData("scp")]
    [InlineData("idtyp")]
    [InlineData("exp")]
    public async Task TokenClaimDriftIsRejectedBeforeSendingItToTheDataEndpoint(string changed)
    {
        using var run = new Harness();
        run.Credential.Change = changed;
        var result = await run.Execute("discover");
        Assert.Equal("c1_token_binding", result.Code);
        Assert.Empty(run.Handler.Requests);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("10.0.0.99")]
    public void DnsMustResolveOnlyToExactReviewedPrivateAddresses(string address)
    {
        var approved = FabricC1Execution.PrivateAddresses(["10.0.0.4"]);
        Assert.Equal("c1_private_dns", Assert.Throws<DomainException>(() =>
            FabricC1Execution.ValidateResolution([IPAddress.Parse("10.0.0.4"), IPAddress.Parse(address)], approved)).Code);
        FabricC1Execution.ValidateResolution([IPAddress.Parse("::ffff:10.0.0.4")], approved);
    }

    [Fact]
    public async Task TaskResumeKeepsOriginalCallAndUsesOnlyTheBoundLegacyTaskDialect()
    {
        using var run = new Harness(tasks: true);
        run.Discovery(tasks: true);
        run.Handler.Respond(request => Rpc(request, new { task = TaskState("working") }));
        await run.Execute("discover");
        Assert.Equal("TaskPending", (await run.Execute("task-start", "allowed")).Status);
        var call = run.Store.Value!.OriginalCall!.Value;
        run.Restart();
        run.Handler.Respond(request => Rpc(request, TaskState("completed")));
        run.Handler.Respond(request => Rpc(request, new { content = new[] { new { type = "text", text = "synthetic" } },
            _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/related-task"] = new { taskId = "synthetic-task" } } }));
        Assert.Equal("Observed", (await run.Execute("resume")).Status);
        Assert.Equal(Canonical.Hash(call), Canonical.Hash(run.Store.Value!.OriginalCall!.Value));
        Assert.Equal(new[] { "tasks/get", "tasks/result" }, run.Handler.Requests.TakeLast(2).Select(r => r.RpcMethod));
        Assert.Single(run.Handler.Requests, r => r.RpcMethod == "tools/call");
    }

    [Theory]
    [InlineData("working", "CleanupIncomplete")]
    [InlineData("cancelled", "Observed")]
    [InlineData("completed", "Observed")]
    public async Task CancellationNeedsATerminalReadbackNotAnAcknowledgement(string finalStatus, string expected)
    {
        using var run = new Harness(tasks: true);
        run.Discovery(tasks: true);
        run.Handler.Respond(request => Rpc(request, new { task = TaskState("working") }));
        await run.Execute("discover");
        await run.Execute("task-start", "allowed");
        run.Handler.Respond(request => Rpc(request, TaskState("working")));
        run.Handler.Respond(request => Rpc(request, TaskState(finalStatus)));
        var observed = await run.Execute("cancel");
        Assert.Equal(expected, observed.Status);
        Assert.Equal(new[] { "tasks/cancel", "tasks/get" }, run.Handler.Requests.TakeLast(2).Select(r => r.RpcMethod));
        Assert.NotEqual("c1_cancel_terminal_observed", finalStatus == "cancelled" ? "" : observed.Code);
    }

    [Fact]
    public async Task NewTasksExtensionShapeIsNotMistakenForAnOldProtocolResult()
    {
        using var run = new Harness();
        run.Discovery();
        run.Handler.Respond(request => Rpc(request, new { resultType = "task", taskId = "different-dialect", status = "working" }));
        await run.Execute("discover");
        Assert.Equal("c1_task_dialect", (await run.Execute("query", "allowed")).Code);
        Assert.Equal(4, run.Handler.Requests.Count);
    }

    [Fact]
    public async Task DeadlineAndRequestCountSurviveRestartAndLeaveBoundedCleanupCapacity()
    {
        using var run = new Harness(directBudget: 7);
        run.Discovery();
        run.Handler.Respond(request => Rpc(request, ToolResult()));
        await run.Execute("discover");
        await run.Execute("query", "allowed");
        Assert.Equal("c1_request_limit", (await run.Execute("query", "excluded")).Code);
        Assert.Equal(4, run.Handler.Requests.Count);
        run.Time.Now = run.Time.Now.AddSeconds(61);
        run.Restart();
        Assert.Equal("c1_deadline", (await run.Execute("resume")).Code);
        Assert.Equal(4, run.Store.Value!.Requests);
        run.Handler.Respond(_ => new(HttpStatusCode.NoContent));
        await run.Execute("close");
        Assert.Equal("DELETE", run.Handler.Requests.Last().Method);
        Assert.Equal(5, run.Store.Value.Requests);
    }

    [Fact]
    public async Task HostedCallBindsExistingSessionAndReconcilesLostResponseWithoutQueryReplay()
    {
        using var run = new Harness();
        using var handler = new QueueHandler();
        using var client = new HttpClient(handler);
        var caller = run.Settings.Hosted!.Caller;
        var credential = new SyntheticCredential(caller, run.Time);
        var http = new FabricC1AuthenticatedHttp(client, credential, caller, FabricC1Execution.HostedScope,
            new(run.Settings.Hosted.InvocationEndpoint), run.Time);
        var store = new MemoryStore();
        var hosted = new FabricC1HostedClient(run.Settings, http, store, run.Time);
        var original = run.Command("query", "allowed");
        handler.Respond(_ => throw new HttpRequestException("synthetic interruption"));
        Assert.Equal("Unknown", (await hosted.Invoke(original)).Status);
        await Assert.ThrowsAsync<DomainException>(() => hosted.Invoke(run.Command("query", "allowed")));
        var observation = new FabricC1Observation("Observed", null, original.BindingHash, new string('a', 64), 4, "hosted",
            InvocationId: original.InvocationId);
        handler.Respond(_ =>
        {
            var response = Json(observation);
            response.Headers.Add("x-agent-session-id", "synthetic-hosted-session");
            return response;
        });
        Assert.Equal("Observed", (await hosted.Invoke(run.Command("status"))).Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(run.Settings.Hosted.InvocationEndpoint, r.Url));
        Assert.All(credential.Scopes, scope => Assert.Equal(FabricC1Execution.HostedScope, scope));
    }

    [Fact]
    public void HostedStartupRejectsWrongVersionAndUploadWritableConfiguration()
    {
        using var run = new Harness();
        var env = new Dictionary<string, string>
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = run.Settings.Preparation.Runtime.HostedProjectEndpoint!,
            ["FOUNDRY_AGENT_ID"] = run.Settings.Hosted!.AgentId,
            ["FOUNDRY_AGENT_NAME"] = run.Settings.Hosted.AgentName,
            ["FOUNDRY_AGENT_VERSION"] = run.Settings.Preparation.Runtime.HostedAgentVersion!,
            ["FOUNDRY_AGENT_SESSION_ID"] = run.Settings.Hosted.SessionId,
            ["HOME"] = Path.Combine(Path.GetTempPath(), "synthetic-upload-home")
        };
        var config = Path.Combine(Path.GetTempPath(), "synthetic-trusted", "config.json");
        var settings = run.Settings with { Preparation = run.Settings.Preparation with
        { Identity = run.Settings.Preparation.Identity with { CredentialReference = config + ".credential" } } };
        FabricC1Entry.ValidateHostedEnvironment(settings, name => env.GetValueOrDefault(name), config);
        Assert.Equal("c1_hosted_trust", Assert.Throws<DomainException>(() =>
            FabricC1Entry.ValidateHostedEnvironment(settings, name => env.GetValueOrDefault(name), Path.Combine(env["HOME"], "config.json"))).Code);
        env["FOUNDRY_AGENT_VERSION"] = "unreviewed-version";
        Assert.Equal("c1_hosted_environment", Assert.Throws<DomainException>(() =>
            FabricC1Entry.ValidateHostedEnvironment(settings, name => env.GetValueOrDefault(name), config)).Code);
    }

    [Fact]
    public void PrivateCheckpointIsExclusivePersistentAndRejectsChangedExecutionBinding()
    {
        var directory = Path.Combine(Path.GetTempPath(), "c1-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "checkpoint.json");
        try
        {
            var checkpoint = new FabricC1Checkpoint("synthetic", "direct", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
            using (var file = new FabricC1FileCheckpointStore(path, 65536))
            {
                file.Save(checkpoint);
                Assert.Throws<IOException>(() => new FabricC1FileCheckpointStore(path, 65536));
            }
            using (var file = new FabricC1FileCheckpointStore(path, 65536))
                Assert.Equal(Canonical.Hash(checkpoint), Canonical.Hash(file.Load()!));
            using var run = new Harness();
            run.Store.Value = checkpoint;
            Assert.Equal("c1_checkpoint_binding", Assert.Throws<DomainException>(run.Restart).Code);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ExecutableRevisionMatchesTheHostedCommitAndRejectsAnotherSource()
    {
        using var run = new Harness();
        var source = Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? throw new InvalidOperationException("Run the C1 verification on its allocated hosted runner.");
        FabricC1Execution.RequireSourceBuild(run.Settings with { SourceCommit = source });
        Assert.Equal("c1_binary_binding", Assert.Throws<DomainException>(() =>
            FabricC1Execution.RequireSourceBuild(run.Settings with { SourceCommit = new string('0', 40) })).Code);
    }

    private static object ToolResult() => new { content = new[] { new { type = "text", text = "SYNTHETIC-ALLOW-A" } }, isError = false };
    private static object TaskState(string status) => new { taskId = "synthetic-task", status, pollInterval = 0, ttl = 60000 };
    private static string RequestId(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.GetProperty("id").GetString()!;
    }
    private static HttpResponseMessage Rpc(HttpRequestMessage request, object result) =>
        Json(new { jsonrpc = "2.0", id = RequestId(request), result });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, Canonical.Json), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string value, bool fragmented = false)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var content = new StreamContent(fragmented ? new FragmentedStream(bytes) : new MemoryStream(bytes));
        content.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Harness : IDisposable
    {
        public readonly Clock Time = new();
        public readonly QueueHandler Handler = new();
        public readonly MemoryStore Store = new();
        public readonly FabricC1ExecutionSettings Settings;
        public readonly SyntheticCredential Credential;
        private readonly HttpClient client;
        private FabricC1Transport transport;

        public Harness(bool tasks = false, int directBudget = 24)
        {
            var input = FabricC1PreparationTests.Bound();
            input = input with { Limits = new(70, 60, 32768, 30) };
            const string project = "https://hosted.invalid/api/projects/synthetic";
            Settings = new("cohors-fabric-c1-execution-1", input, "88888888-8888-8888-8888-888888888888",
                new string('a', 40), Time.Now.AddHours(1), new string('b', 64), ["10.0.0.4"],
                tasks ? FabricC1Execution.TaskDialect : "none", directBudget, 24, 20,
                new("99999999-9999-9999-9999-999999999999", "synthetic-agent", "synthetic-hosted-session", "synthetic-platform-user",
                    new string('c', 64), project + "/agents/synthetic-agent/endpoint/protocols/invocations?api-version=v1&agent_session_id=synthetic-hosted-session",
                    input.Identity with { ExpectedAudience = "https://ai.azure.com" }, ["10.0.0.5"]));
            Credential = new(input.Identity, Time);
            client = new(Handler);
            transport = NewTransport();
        }

        public void Discovery(bool tasks = false)
        {
            Handler.Respond(request =>
            {
                object capabilities = tasks
                    ? new { tools = new { }, tasks = new { cancel = new { }, requests = new { tools = new { call = new { } } } } }
                    : new { tools = new { } };
                var response = Rpc(request, new { protocolVersion = "2025-11-25", capabilities,
                    serverInfo = new { name = "synthetic-server", version = "1" } });
                response.Headers.Add("Mcp-Session-Id", "synthetic-session");
                return response;
            });
            Handler.Respond(_ => new(HttpStatusCode.Accepted) { Content = new StringContent("") });
            Handler.Respond(request => Rpc(request, tasks
                ? JsonSerializer.SerializeToElement(new { tools = new[] { new { name = "synthetic_data_agent",
                    inputSchema = FabricC1PreparationTests.Schema, execution = new { taskSupport = "optional" } } } })
                : FabricC1PreparationTests.Tools()));
        }

        private FabricC1Transport NewTransport() => new(Settings, FabricC1PreparationTests.Fixture, "direct",
            new(client, Credential, Settings.Preparation.Identity, FabricC1ProbeClient.TokenScope,
                new(Settings.Preparation.Target.PublishedMcpEndpoint!), Time), Store, Time);
        public void Restart() => transport = NewTransport();
        public FabricC1Invocation Command(string operation, string? probe = null) =>
            new(Canonical.Hash(Settings), operation, probe, Guid.NewGuid().ToString("D"));
        public Task<FabricC1Observation> Execute(string operation, string? probe = null) => transport.Execute(Command(operation, probe));
        public void Dispose() => client.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IFabricC1CheckpointStore
    {
        public FabricC1Checkpoint? Value;
        public FabricC1Checkpoint? Load() => Value;
        public void Save(FabricC1Checkpoint checkpoint) => Value = checkpoint;
    }

    private sealed class SyntheticCredential(FabricC1Identity identity, Clock time) : TokenCredential
    {
        public List<string> Scopes { get; } = [];
        public string? Change { get; set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct)
        {
            Scopes.Add(Assert.Single(context.Scopes));
            var claims = new Dictionary<string, object>
            {
                ["iss"] = identity.ExpectedIssuer!, ["aud"] = identity.ExpectedAudience!,
                ["tid"] = identity.TenantId!, ["oid"] = identity.PrincipalObjectId!, ["appid"] = identity.ClientId!,
                ["idtyp"] = "app", ["nbf"] = time.Now.AddMinutes(-1).ToUnixTimeSeconds(),
                ["exp"] = time.Now.AddMinutes(10).ToUnixTimeSeconds()
            };
            if (Change is not null) claims[Change] = Change == "exp" ? time.Now.AddMinutes(-1).ToUnixTimeSeconds() : "synthetic-wrong-value";
            static string Encode(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return new(Encode(new { alg = "RS256", typ = "JWT" }) + "." + Encode(claims) + ".c3ludGhldGlj",
                time.Now.AddMinutes(10));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct) => new(GetToken(context, ct));
    }

    private sealed record Captured(string Method, string Url, string? RpcMethod, string Authorization, string Accept,
        string? Protocol, string? Session, string? Cursor);
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> replies = new();
        public List<Captured> Requests { get; } = [];
        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> response) => replies.Enqueue(response);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? method = null;
            if (request.Content is not null)
            {
                using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("method", out var value)) method = value.GetString();
            }
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, method,
                request.Headers.Authorization?.ToString() ?? "", request.Headers.Accept.ToString(),
                Header("MCP-Protocol-Version"), Header("Mcp-Session-Id"), Header("Last-Event-ID")));
            Assert.NotEmpty(replies);
            var response = replies.Dequeue()(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
    }
}
