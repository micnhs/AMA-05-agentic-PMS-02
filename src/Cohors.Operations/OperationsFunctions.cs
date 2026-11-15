using System.Net;
using Cohors.Application;
using Cohors.Domain;
using Cohors.Infrastructure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Cohors.Operations;

public sealed class OperationsFunctions(OperationsService operations, SqlStateStore store,
    TelemetryClient telemetry, ILogger<OperationsFunctions> logger)
{
    [Function(nameof(DeadlineWatchdog))]
    public async Task DeadlineWatchdog([TimerTrigger("*/30 * * * * *", UseMonitor = true)] TimerInfo timer, CancellationToken ct)
    {
        await operations.Tick(ct);
        var success = new EventTelemetry("cohors.watchdog.completed");
        success.Context.Cloud.RoleName = "cohors-operations";
        telemetry.TrackEvent(success);
        await telemetry.FlushAsync(ct);
        logger.LogInformation("SQL watchdog completed. No clinical data included in telemetry.");
    }

    [Function(nameof(OperationsLive))]
    public static async Task<HttpResponseData> OperationsLive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/live")] HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "alive", role = "cohors-operations" });
        return response;
    }

    [Function(nameof(OperationsReady))]
    public async Task<HttpResponseData> OperationsReady(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequestData request, CancellationToken ct)
    {
        try
        {
            await store.RequireReady(ct);
            var heartbeat = await store.Get<OperationsHeartbeat>("operations", "heartbeat", ct);
            if (OperationsService.HeartbeatMissing(heartbeat, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Watchdog heartbeat is absent or stale.");
            var ready = request.CreateResponse(HttpStatusCode.OK);
            await ready.WriteAsJsonAsync(new { status = "ready", independentOf = new[] { "API", "Foundry", "Search" } });
            return ready;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError("Operations readiness failed: {Type}.", error.GetType().Name);
            var response = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await response.WriteAsJsonAsync(new { status = "not-ready" });
            return response;
        }
    }
}
