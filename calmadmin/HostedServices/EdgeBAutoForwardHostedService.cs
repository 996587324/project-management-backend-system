using Interface;

namespace calmadmin.HostedServices;

/// <summary>
/// 中心端 B 系统自动转发后台服务；运行参数每轮从 SQL Server 读取，不依赖前端在线。
/// </summary>
public class EdgeBAutoForwardHostedService : BackgroundService
{
    private readonly IEdgeBAutoForwardService _autoForwardService;
    private readonly ILogger<EdgeBAutoForwardHostedService> _logger;

    /// <summary>通过 IOC 获取自动转发业务服务和日志组件。</summary>
    public EdgeBAutoForwardHostedService(
        IEdgeBAutoForwardService autoForwardService,
        ILogger<EdgeBAutoForwardHostedService> logger)
    {
        _autoForwardService = autoForwardService;
        _logger = logger;
    }

    /// <summary>循环处理到期中心队列，并动态应用数据库中的扫描间隔和启停状态。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = 5;
            try
            {
                var setting = await _autoForwardService.RunOnceAsync(stoppingToken);
                var configuredDelay = Math.Clamp(setting.ScanIntervalSeconds, 1, 300);
                // 停用时最多 5 秒检查一次数据库，使前端重新启用后无需等待较长的旧间隔。
                delaySeconds = setting.Enabled ? configuredDelay : Math.Min(configuredDelay, 5);
                if (setting.Enabled && setting.LastRequestedCount > 0)
                {
                    _logger.LogInformation(
                        "B system auto-forward completed. Requested: {Requested}, Success: {Success}, Failed: {Failed}.",
                        setting.LastRequestedCount,
                        setting.LastSuccessCount,
                        setting.LastFailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "B system auto-forward loop failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
