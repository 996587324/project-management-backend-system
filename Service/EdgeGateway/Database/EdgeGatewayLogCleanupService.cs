using Interface;
using Model.EdgeGateway;
using SqlSugar;
using System.Globalization;

namespace Service;

/// <summary>
/// 使用独立的 SqlSugar 连接清理 EdgeGateway 中心运行日志。
/// </summary>
public sealed class EdgeGatewayLogCleanupService : IEdgeGatewayLogCleanupService
{
    private const int DeleteBatchSize = 5000;
    private const string WebSocketReceiveRecordTablePrefix = "EdgeGatewayWebSocketReceiveRecord_";
    private readonly SqlSugarScope _dbScope;

    /// <summary>通过 IOC 注入项目单例 SqlSugarScope，统一复用日志数据库配置。</summary>
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

    /// <summary>按日表名删除早于保留起始日期的 WebSocket 接收记录表，无需对高频报文逐行删除。</summary>
    public Task<int> DeleteExpiredWebSocketReceiveRecordTablesAsync(
        DateTime oldestRetainedDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = _dbScope.GetConnection("edge-gateway-logs").CopyNew();
        var deletedTableCount = 0;
        var tableNames = db.DbMaintenance.GetTableInfoList(false)
            .Select(x => x.Name)
            .Where(IsWebSocketReceiveRecordTable)
            .ToList();
        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dateText = tableName[WebSocketReceiveRecordTablePrefix.Length..];
            if (!DateTime.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var tableDate) || tableDate.Date >= oldestRetainedDate.Date)
                continue;
            if (db.DbMaintenance.DropTable(tableName)) deletedTableCount++;
        }
        return Task.FromResult(deletedTableCount);
    }

    private static bool IsWebSocketReceiveRecordTable(string tableName) =>
        tableName.StartsWith(WebSocketReceiveRecordTablePrefix, StringComparison.OrdinalIgnoreCase) &&
        tableName.Length == WebSocketReceiveRecordTablePrefix.Length + 8 &&
        tableName[WebSocketReceiveRecordTablePrefix.Length..].All(char.IsAsciiDigit);
}
