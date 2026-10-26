using Interface;

namespace calmadmin.HostedServices;

public sealed class WeChatPushQueueHostedService(
    IServiceScopeFactory scopes,
    IWeChatPushQueueInitializer initializer,
    ILogger<WeChatPushQueueHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await initializer.InitializeAsync(stoppingToken);
                    using var work = scopes.CreateScope();
                    await work.ServiceProvider.GetRequiredService<IWeChatPushQueueService>().DispatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    logger.LogWarning("微信发送队列暂不可用，保留任务稍后处理；请核对数据库状态。");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机；已开始的发送不盲目重放。
        }
    }
}
