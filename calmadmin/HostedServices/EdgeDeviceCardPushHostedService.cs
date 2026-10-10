using Interface;

namespace calmadmin.HostedServices;

/// <summary>后台发送字段事件，不是周期查询设备的业务计划任务。</summary>
public sealed class EdgeDeviceCardPushHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EdgeDeviceCardPushHostedService> _logger;

    public EdgeDeviceCardPushHostedService(IServiceScopeFactory scopeFactory,
        ILogger<EdgeDeviceCardPushHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IEdgeDeviceCardPushService>().DispatchAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Device card field push dispatch failed; pending tasks are retained.");
                }
                // 轻量扫描频率不等于发送频率，实际发送由数据库配置/租约节流。
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关闭。已认领任务的未知发送结果由下次启动检查。
        }
    }
}
