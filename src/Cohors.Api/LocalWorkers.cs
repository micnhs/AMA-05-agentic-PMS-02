using Cohors.Application;
using Cohors.Domain;

namespace Cohors.Api;

public sealed class LocalWatchdog(IServiceScopeFactory scopes, ILogger<LocalWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OperationsService>().Tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Independent watchdog failed with {ErrorType}; missing-heartbeat status remains visible.", ex.GetType().Name);
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}

public sealed class LocalOutboxWorker(IServiceScopeFactory scopes, ILogger<LocalOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
            try
            {
                foreach (var e in await store.PendingEvents(50, stoppingToken))
                {
                    try
                    {
                        var result = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().Process(e.Id, stoppingToken);
                        if (result.Error is not null)
                            logger.LogWarning("Outbox {EventId}: {State}, {Code}.", e.Id, result.State, result.Error);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError("Outbox {EventId} failed with {ErrorType}; receipt not acknowledged.", e.Id, ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Outbox polling failed with {ErrorType}.", ex.GetType().Name); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
