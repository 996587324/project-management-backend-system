using Interface;

namespace calmadmin.HostedServices;

/// <summary>
/// 将 IOC 中的 EdgeGateway Worker 接入 ASP.NET Core 后台服务生命周期。
/// </summary>
public class EdgeGatewayHostedService : BackgroundService
{
    private readonly IEdgeGatewayWorker _worker;

    public EdgeGatewayHostedService(IEdgeGatewayWorker worker)
    {
        _worker = worker;
    }

    /// <summary>应用启动后运行 Worker，应用关闭时通过 stoppingToken 通知其退出。</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _worker.RunAsync(stoppingToken);
}
