namespace Interface;

/// <summary>
/// EdgeGateway 中心日志清理接口，负责删除超过保留期限的运行事件。
/// </summary>
public interface IEdgeGatewayLogCleanupService
{
    /// <summary>删除创建时间早于截止时间的系统事件，并返回删除数量。</summary>
    Task<int> DeleteExpiredSystemEventsAsync(
        DateTime cutoffTime,
        CancellationToken cancellationToken);
}
