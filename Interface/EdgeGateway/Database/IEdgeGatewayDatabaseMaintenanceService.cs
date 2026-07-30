namespace Interface;

/// <summary>
/// EdgeGateway 中心数据库维护接口，负责创建不适合由 CodeFirst 表结构表达的查询索引。
/// </summary>
public interface IEdgeGatewayDatabaseMaintenanceService
{
    /// <summary>确保消息、上传、事件、告警和操作日志表具备高频查询所需索引。</summary>
    void EnsureLargeTableIndexes();
}
