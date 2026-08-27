namespace Interface;

/// <summary>
/// EdgeGateway 中心数据库维护接口，负责创建不适合由 CodeFirst 表结构表达的查询索引。
/// </summary>
public interface IEdgeGatewayDatabaseMaintenanceService
{
    /// <summary>在 CodeFirst 更新实时暂存表前，移除依赖旧 WebSocket 路由字段的索引。</summary>
    void PrepareRealtimeSnapshotSchema();

    /// <summary>修正早期看板表中被创建成非空的可选字段，兼容已经初始化过的数据库。</summary>
    void PrepareDashboardSchema();

    /// <summary>确保消息、上传、事件、告警和操作日志表具备高频查询所需索引。</summary>
    void EnsureLargeTableIndexes();
}
