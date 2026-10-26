using Interface;

namespace calmadmin.HostedServices;

/// <summary>只处理已提交邮件，不创建定时业务任务，不在启动阶段等待 SMTP。</summary>
public sealed class EmailPushHostedService(
    IServiceScopeFactory scopeFactory,
    IEmailPushDatabaseInitializer initializer,
    ILogger<EmailPushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await initializer.InitializeAsync(stoppingToken);
                    using var work = scopeFactory.CreateScope();
                    await work.ServiceProvider.GetRequiredService<IEmailPushService>().DispatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    logger.LogWarning("邮件队列处理失败，任务保留，将稍后重试；请检查数据库及邮件服务状态。");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. Unfinished SMTP sends remain Unknown rather than being blindly repeated.
        }
    }
}
