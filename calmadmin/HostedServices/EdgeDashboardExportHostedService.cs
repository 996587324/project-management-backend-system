using Interface;

namespace calmadmin.HostedServices;

/// <summary>数据库驱动的看板日报定时导出服务，不依赖前端页面保持在线。</summary>
public sealed class EdgeDashboardExportHostedService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private readonly IEdgeDashboardExportService _exportService;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly ILogger<EdgeDashboardExportHostedService> _logger;

    /// <summary>通过 IOC 获取导出业务、边缘中心总开关和日志组件。</summary>
    public EdgeDashboardExportHostedService(
        IEdgeDashboardExportService exportService,
        IEdgeGatewayFeatureService featureService,
        ILogger<EdgeDashboardExportHostedService> logger)
    {
        _exportService = exportService;
        _featureService = featureService;
        _logger = logger;
    }

    /// <summary>边缘中心启用时轮询数据库配置，到点后按每条启用线体分别生成日报。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _featureService.IsEnabledAsync())
                {
                    var generatedCount = await _exportService.RunScheduledExportsAsync(
                        DateTime.Now,
                        stoppingToken);
                    if (generatedCount > 0)
                    {
                        _logger.LogInformation(
                            "Edge dashboard scheduled export generated {GeneratedCount} line reports.",
                            generatedCount);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Edge dashboard scheduled export scan failed.");
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
