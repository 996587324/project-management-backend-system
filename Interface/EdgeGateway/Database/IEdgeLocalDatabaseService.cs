using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// EdgeGateway 本地 SQLite 单例访问服务，统一串行执行查询、写入和事务。
/// </summary>
public interface IEdgeLocalDatabaseService
{
    /// <summary>打开唯一的 SQLite 连接，并初始化 PRAGMA、表和索引。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>按 MessageId 去重后插入一条本地待上传消息。</summary>
    Task<bool> InsertMessageIfAbsentAsync(
        EdgeLocalMessageQueueEntity entity,
        CancellationToken cancellationToken);

    /// <summary>查询已经到达重试时间的本地消息实体。</summary>
    Task<List<EdgeLocalMessageQueueEntity>> GetDueMessagesAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>统计本地消息队列的三个状态数量。</summary>
    Task<QueueStatistics> GetQueueStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>插入一条本地运行事件。</summary>
    Task InsertSystemEventAsync(
        EdgeLocalSystemEventEntity entity,
        CancellationToken cancellationToken);

    /// <summary>在单一连接事务中替换中心端下发的生效配置快照。</summary>
    Task SaveEffectiveConfigurationAsync(
        EdgeLocalConfigurationWriteModel model,
        CancellationToken cancellationToken);

    /// <summary>在单一连接事务中更新队列状态并插入上传尝试记录。</summary>
    Task CompleteUploadAttemptAsync(
        EdgeLocalUploadAttemptWriteModel model,
        CancellationToken cancellationToken);
}
