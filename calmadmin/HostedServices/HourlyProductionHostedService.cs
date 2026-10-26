using Interface;

namespace calmadmin.HostedServices;

/// <summary>初始化及统计完全后台执行；AI另一个循环，模型慢不会阻塞入账或小时推送。</summary>
public sealed class HourlyProductionHostedService(
    IServiceScopeFactory scopes,
    IHourlyProductionInitializer initializer,
    ILogger<HourlyProductionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await initializer.InitializeAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IHourlyProductionService>().TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "小时产量后台统计暂不可用，将保留事件重试。"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public sealed class HourlyProductionAiHostedService(
    IServiceScopeFactory scopes,
    IHourlyProductionInitializer initializer,
    ILogger<HourlyProductionAiHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (initializer.IsReady)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IHourlyProductionService>().RefreshAdviceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "小时看板AI分析失败，不影响统计和页面。"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
