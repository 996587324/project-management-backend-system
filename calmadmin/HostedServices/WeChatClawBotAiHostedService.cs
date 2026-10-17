using Interface;
using Microsoft.Extensions.Options;
using Model.WeChatClawBot;

namespace calmadmin.HostedServices;

/// <summary>独立于接收长轮询的持久化 AI 任务消费者，不在启动阶段调用模型。</summary>
public sealed class WeChatClawBotAiHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<WeChatClawBotOptions> options,
    ILogger<WeChatClawBotAiHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.MonitorEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.MonitorInitialDelaySeconds, 5, 300)), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var discovery = scopeFactory.CreateScope();
                var users = await discovery.ServiceProvider.GetRequiredService<IWeChatClawBotAiService>().GetPendingUsersAsync();
                await Parallel.ForEachAsync(users, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 3, CancellationToken = stoppingToken
                }, async (userId, token) =>
                {
                    try
                    {
                        using var work = scopeFactory.CreateScope();
                        var ai = work.ServiceProvider.GetRequiredService<IWeChatClawBotAiService>();
                        await ai.ProcessUserAsync(userId, work.ServiceProvider.GetRequiredService<IAgentService>(),
                            work.ServiceProvider.GetRequiredService<IWeChatClawBotService>(), token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception)
                    {
                        // 不输出消息正文、模型异常正文或凭证。
                        logger.LogWarning("微信 AI 用户任务处理失败，请检查任务和调用审计记录。");
                    }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("微信 AI 后台队列扫描失败，将稍后重试。");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
