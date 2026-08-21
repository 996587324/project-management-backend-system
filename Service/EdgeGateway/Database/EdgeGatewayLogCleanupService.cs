using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// 使用项目现有 SqlSugar 连接清理 EdgeGateway 中心运行日志。
/// </summary>
public sealed class EdgeGatewayLogCleanupService : IEdgeGatewayLogCleanupService
{
    private const int DeleteBatchSize = 5000;
    private readonly SqlSugarScope _dbScope;

    /// <summary>通过 IOC 注入项目单例 SqlSugarScope，统一复用项目数据库配置。</summary>
    public EdgeGatewayLogCleanupService(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
    }

    /// <summary>分批删除中心日志表中创建时间早于指定截止时间的记录，避免大事务长时间锁表。</summary>
    public async Task<int> DeleteExpiredSystemEventsAsync(
        DateTime cutoffTime,
        CancellationToken cancellationToken)
    {
        // 清理任务可能与采集、转发服务同时访问中心库。CopyNew 复用原连接配置，
        // 但使用独立客户端，避免共享连接上尚未关闭的 DataReader 影响删除命令。
        using var db = _dbScope.GetConnection("calmadmin").CopyNew();
        var deletedCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiredIds = await db.Queryable<EdgeSystemEventEntity>()
                .Where(x => x.CreateTime < cutoffTime)
                .OrderBy(x => x.CreateTime)
                .Select(x => x.Id)
                .Take(DeleteBatchSize)
                .ToListAsync();
            if (expiredIds.Count == 0) return deletedCount;

            var batchDeletedCount = await db.Deleteable<EdgeSystemEventEntity>()
                .In(expiredIds)
                .ExecuteCommandAsync();
            deletedCount += batchDeletedCount;
            if (batchDeletedCount == 0) return deletedCount;
        }
    }
}
