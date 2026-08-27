using Model.EdgeGateway;

namespace Interface;

/// <summary>把每条 HTTP 实时数据转换成机械手状态、每日秒数和故障记录。</summary>
public interface IEdgeDashboardTelemetryProcessor
{
    /// <summary>处理一条已经通过实时接收校验的新样本。</summary>
    Task ProcessAsync(EdgeDashboardTelemetrySampleDto sample, CancellationToken cancellationToken);

    /// <summary>推进无新数据设备的离线时间，并关闭通信中断前的活动故障。</summary>
    Task SweepOfflineAsync(DateTime now, CancellationToken cancellationToken);
}
