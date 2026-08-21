using Interface;

namespace calmadmin.HostedServices;

/// <summary>
/// 将 IOC 中的 EdgeGateway Worker 接入 ASP.NET Core 后台服务生命周期。
/// </summary>
public class EdgeGatewayHostedService : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);
    private readonly IEdgeGatewayWorker _worker;
    private readonly ILogger<EdgeGatewayHostedService> _logger;

    /// <summary>通过 IOC 获取网关 Worker 和日志组件。</summary>
    public EdgeGatewayHostedService(
        IEdgeGatewayWorker worker,
        ILogger<EdgeGatewayHostedService> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    /// <summary>
    /// 应用启动后持续监督 Worker；非主动退出时记录异常并延迟重启，避免单个后台任务终止整个 Web API。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _worker.RunAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                    _logger.LogWarning("EdgeGateway worker stopped unexpectedly and will be restarted.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "EdgeGateway worker terminated unexpectedly and will be restarted.");
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
