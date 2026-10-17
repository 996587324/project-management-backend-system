using Interface;

namespace calmadmin.HostedServices;

public sealed class ProcessBoardExportHostedService(
    IServiceScopeFactory scopes,
    ProcessBoardInitializationState state,
    ILogger<ProcessBoardExportHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (state.Ready)
                {
                    using var services = scopes.CreateScope();
                    await services.ServiceProvider.GetRequiredService<IProcessBoardService>().RunExportAsync(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Process board export failed; HTTP requests remain available.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
