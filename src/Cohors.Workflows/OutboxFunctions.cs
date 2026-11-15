using System.Net;
using Cohors.Domain;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace Cohors.Workflows;

public sealed class OutboxFunctions(WorkflowApi api, ILogger<OutboxFunctions> logger)
{
    [Function(nameof(DispatchOutbox))]
    public async Task DispatchOutbox([TimerTrigger("*/15 * * * * *", UseMonitor = true)] TimerInfo timer,
        [DurableClient] DurableTaskClient client, CancellationToken ct)
    {
        foreach (var e in await api.Pending(ct))
        {
            var instance = $"outbox-{e.Id}-r{e.RecoveryVersion}-a{e.Attempts}";
            if (await client.GetInstanceAsync(instance, getInputsAndOutputs: false, cancellation: ct) is not null) continue;
            await client.ScheduleNewOrchestrationInstanceAsync(nameof(CohorsOutboxV1), e.Id,
                new StartOrchestrationOptions { InstanceId = instance }, ct);
        }
    }

    [Function(nameof(CohorsOutboxV1))]
    public static async Task<OutboxDispatchResult> CohorsOutboxV1([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var id = context.GetInput<string>() ?? throw new InvalidDataException("Event ID missing.");
        return await context.CallActivityAsync<OutboxDispatchResult>(nameof(ProcessOutbox),
            id, TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(10))));
    }

    [Function(nameof(ProcessOutbox))]
    public Task<OutboxDispatchResult> ProcessOutbox([ActivityTrigger] string id, CancellationToken ct) => api.Process(id, ct);

    [Function(nameof(WorkflowLive))]
    public static async Task<HttpResponseData> WorkflowLive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/live")] HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "alive", role = "cohors-workflows" });
        return response;
    }

    [Function(nameof(WorkflowReady))]
    public async Task<HttpResponseData> WorkflowReady(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequestData request,
        [DurableClient] DurableTaskClient client, CancellationToken ct)
    {
        try
        {
            await api.Ready(ct);
            await client.GetInstanceAsync("cohors-readiness", getInputsAndOutputs: false, cancellation: ct);
            var ready = request.CreateResponse(HttpStatusCode.OK);
            await ready.WriteAsJsonAsync(new { status = "ready", backend = "AzureStorage", input = "event-identifiers-only" });
            return ready;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError("Workflow readiness failed: {Type}.", error.GetType().Name);
            var response = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await response.WriteAsJsonAsync(new { status = "not-ready" });
            return response;
        }
    }
}
