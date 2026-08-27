using Interface;

namespace calmadmin.HostedServices;

/// <summary>周期推进机械手离线状态，使每日统计不依赖前端轮询或下一条设备报文。</summary>
public sealed class EdgeDashboardStatisticsHostedService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private readonly IEdgeDashboardTelemetryProcessor _telemetryProcessor;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly ILogger<EdgeDashboardStatisticsHostedService> _logger;

    /// <summary>通过 IOC 获取统计处理器、边缘中心功能开关和日志组件。</summary>
    public EdgeDashboardStatisticsHostedService(
        IEdgeDashboardTelemetryProcessor telemetryProcessor,
        IEdgeGatewayFeatureService featureService,
        ILogger<EdgeDashboardStatisticsHostedService> logger)
    {
        _telemetryProcessor = telemetryProcessor;
        _featureService = featureService;
        _logger = logger;
    }

    /// <summary>边缘中心启用时每十秒扫描一次；关闭时只等待，不产生新的统计数据。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _featureService.IsEnabledAsync())
                    await _telemetryProcessor.SweepOfflineAsync(DateTime.Now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edge dashboard statistics sweep failed.");
            }

            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
