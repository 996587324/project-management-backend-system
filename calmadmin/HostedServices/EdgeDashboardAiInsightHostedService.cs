using Interface;

namespace calmadmin.HostedServices;

/// <summary>后台定时生成生产看板 AI 建议，避免每个浏览器页面重复调用模型。</summary>
public sealed class EdgeDashboardAiInsightHostedService : BackgroundService
{
    // 先让 Web 服务和工作台可用，避免重启期间因多条线体的模型请求延迟启动。
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);
    private readonly IEdgeDashboardAiInsightService _insightService;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly ILogger<EdgeDashboardAiInsightHostedService> _logger;

    public EdgeDashboardAiInsightHostedService(
        IEdgeDashboardAiInsightService insightService,
        IEdgeGatewayFeatureService featureService,
        ILogger<EdgeDashboardAiInsightHostedService> logger)
    {
        _insightService = insightService;
        _featureService = featureService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _featureService.IsEnabledAsync())
                    await _insightService.RefreshInsightsAsync(DateTime.Now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Production dashboard AI insight refresh failed.");
            }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
