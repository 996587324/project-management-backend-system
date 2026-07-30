namespace Interface;

/// <summary>
/// EdgeGateway 中心数据库启动初始化服务，集中创建业务表并维护查询索引。
/// </summary>
public interface IEdgeGatewayDatabaseInitializer
{
    /// <summary>应用启动时初始化一次 EdgeGateway 中心表和索引。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}
