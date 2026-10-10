using Interface;

namespace calmadmin.HostedServices;

/// <summary>每五分钟清理超期追溯点，并快速串行处理大数据导出任务。</summary>
public sealed class EdgeDataTraceHostedService : BackgroundService
{
    private static readonly TimeSpan ExportPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private readonly IEdgeDataTraceService _service;
    private readonly IEdgeGatewayService _edgeGatewayService;
    private readonly ILogger<EdgeDataTraceHostedService> _logger;

    public EdgeDataTraceHostedService(
        IEdgeDataTraceService service,
        IEdgeGatewayService edgeGatewayService,
        ILogger<EdgeDataTraceHostedService> logger)
    {
        _service = service;
        _edgeGatewayService = edgeGatewayService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanupTime = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recovered = await _edgeGatewayService.RecoverMessageProjectionsAsync(200, stoppingToken);
                if (recovered > 0)
                    _logger.LogInformation("Recovered {Count} EdgeGateway receive and data trace projections.", recovered);
                await _service.ProcessPendingExportsAsync(stoppingToken);
                if (DateTime.Now >= nextCleanupTime)
                {
                    var deleted = await _service.CleanupAsync(stoppingToken);
                    if (deleted > 0) _logger.LogInformation("Deleted {Count} expired data trace points.", deleted);
                    nextCleanupTime = DateTime.Now.Add(CleanupInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Data trace background processing failed.");
            }

            try { await Task.Delay(ExportPollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
