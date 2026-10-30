using Interface;

namespace calmadmin.HostedServices;

public sealed class WorkTaskHostedService(IServiceScopeFactory scopes, IWorkTaskInitializer initializer, ILogger<WorkTaskHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await initializer.InitializeAsync(stoppingToken);
                using var scope=scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IWorkTaskService>().TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "工作任务作业失败，持久化事件保留等待重试"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
