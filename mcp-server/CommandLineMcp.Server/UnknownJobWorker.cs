using CommandLineMcp.Data;

namespace CommandLineMcp.Server;

public sealed class UnknownJobWorker(IServiceScopeFactory scopeFactory, ILogger<UnknownJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CommandLineMcpDbContext>();
                await ClientJobEndpoints.ExpireAllStaleRunningAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to mark stale running jobs as unknown.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
