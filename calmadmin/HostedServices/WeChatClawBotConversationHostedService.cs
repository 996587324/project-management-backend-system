using Interface;
using Model.WeChatClawBot;
using Microsoft.Extensions.Options;

namespace calmadmin.HostedServices;

/// <summary>
/// 在应用启动完成后再维护微信会话游标，避免 iLink 长轮询阻塞 Web 服务初始化。
/// 所有网络等待均位于后台任务，不影响登录、看板或 MCP 请求。
/// </summary>
public sealed class WeChatClawBotConversationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WeChatClawBotConversationHostedService> _logger;
    private readonly WeChatClawBotOptions _options;

    public WeChatClawBotConversationHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<WeChatClawBotOptions> options,
        ILogger<WeChatClawBotConversationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.MonitorEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.MonitorInitialDelaySeconds, 5, 300)), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IWeChatClawBotService>();
                await service.MaintainConversationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "WeChat ClawBot conversation maintenance failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.MonitorIdleDelaySeconds, 1, 60)), stoppingToken);
        }
    }
}
