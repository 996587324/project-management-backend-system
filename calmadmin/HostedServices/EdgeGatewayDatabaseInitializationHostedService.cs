using Interface;

namespace calmadmin.HostedServices;

/// <summary>
/// 在 ASP.NET Core 开始处理请求前初始化一次 EdgeGateway 中心库及独立日志库结构。
/// </summary>
public sealed class EdgeGatewayDatabaseInitializationHostedService : IHostedService
{
    private readonly IEdgeGatewayDatabaseInitializer _databaseInitializer;

    /// <summary>通过 IOC 获取 EdgeGateway 中心数据库初始化服务。</summary>
    public EdgeGatewayDatabaseInitializationHostedService(
        IEdgeGatewayDatabaseInitializer databaseInitializer)
    {
        _databaseInitializer = databaseInitializer;
    }

    /// <summary>应用启动阶段创建 EdgeGateway 表、日志数据库和索引，初始化失败时阻止服务带病启动。</summary>
    public Task StartAsync(CancellationToken cancellationToken) =>
        _databaseInitializer.InitializeAsync(cancellationToken);

    /// <summary>数据库初始化服务不持有后台循环，停止阶段无需额外处理。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
