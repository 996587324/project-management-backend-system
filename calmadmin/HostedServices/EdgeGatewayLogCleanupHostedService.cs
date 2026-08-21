using Interface;

namespace calmadmin.HostedServices;

/// <summary>
/// 定期清理 EdgeGateway 日志中心历史记录，只保留最近七天的系统事件。
/// </summary>
public sealed class EdgeGatewayLogCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(10);
    private readonly IEdgeGatewayLogCleanupService _cleanupService;
    private readonly ILogger<EdgeGatewayLogCleanupHostedService> _logger;

    /// <summary>通过 IOC 获取日志清理业务服务和日志组件。</summary>
    public EdgeGatewayLogCleanupHostedService(
        IEdgeGatewayLogCleanupService cleanupService,
        ILogger<EdgeGatewayLogCleanupHostedService> logger)
    {
        _cleanupService = cleanupService;
        _logger = logger;
    }

    /// <summary>应用启动时立即清理一次，随后每二十四小时再次执行。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = CleanupInterval;
            try
            {
                var cutoffTime = DateTime.Now.Subtract(RetentionPeriod);
                var deletedCount = await _cleanupService.DeleteExpiredSystemEventsAsync(
                    cutoffTime,
                    stoppingToken);
                if (deletedCount > 0)
                {
                    _logger.LogInformation(
                        "Deleted {DeletedCount} expired EdgeGateway system events before {CutoffTime}.",
                        deletedCount,
                        cutoffTime);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EdgeGateway system event cleanup failed.");
                nextDelay = FailureRetryInterval;
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
