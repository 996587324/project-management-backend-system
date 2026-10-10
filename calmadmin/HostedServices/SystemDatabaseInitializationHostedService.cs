using Service;

namespace calmadmin.HostedServices;

/// <summary>在 HTTP 服务和各后台循环启动前串行准备登录、权限及动态菜单。</summary>
public sealed class SystemDatabaseInitializationHostedService : IHostedService
{
    private readonly SystemDatabaseInitializer _initializer;

    public SystemDatabaseInitializationHostedService(SystemDatabaseInitializer initializer)
    {
        _initializer = initializer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _initializer.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
