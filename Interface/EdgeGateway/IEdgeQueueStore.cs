using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 边缘节点本地 SQLite 可靠队列和配置快照存储。
/// </summary>
public interface IEdgeQueueStore
{
    /// <summary>初始化 SQLite 表、索引和本地启动配置。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
    /// <summary>将标准消息写入本地待上传队列，重复消息不会再次插入。</summary>
    Task<bool> EnqueueAsync(StandardMessage message, CancellationToken cancellationToken);
    /// <summary>读取到期且允许重试的待上传消息。</summary>
    Task<List<QueueMessage>> GetDueMessagesAsync(int limit, CancellationToken cancellationToken);
    /// <summary>标记消息上传成功并记录上传结果。</summary>
    Task MarkUploadedAsync(QueueMessage message, int statusCode, CancellationToken cancellationToken);
    /// <summary>标记消息上传失败，计算下次指数退避时间并记录结果。</summary>
    Task MarkFailedAsync(QueueMessage message, string error, int? statusCode,
        CancellationToken cancellationToken);
    /// <summary>统计本地队列的待处理、失败和已上传数量。</summary>
    Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
    /// <summary>写入边缘节点本地运行事件。</summary>
    Task RecordEventAsync(string level, string eventType, string message, object? detail,
        CancellationToken cancellationToken);
    /// <summary>事务保存中心端下发的连接、字段映射和规则快照。</summary>
    Task SaveEffectiveConfigurationAsync(EffectiveEdgeConfiguration configuration,
        CancellationToken cancellationToken);
}
