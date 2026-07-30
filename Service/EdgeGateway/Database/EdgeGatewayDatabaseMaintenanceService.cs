using Interface;
using SqlSugar;

namespace Service;

/// <summary>
/// 使用 SqlSugar 执行 SQL Server 幂等索引维护，避免大数据量下队列扫描和日志分页退化为全表扫描。
/// </summary>
public sealed class EdgeGatewayDatabaseMaintenanceService : IEdgeGatewayDatabaseMaintenanceService
{
    private static readonly object IndexInitializationLock = new();
    private static bool _indexesEnsured;
    private readonly SqlSugarScope _dbScope;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>注入项目现有的 SqlSugarScope，不额外创建数据库连接。</summary>
    public EdgeGatewayDatabaseMaintenanceService(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
    }

    /// <summary>
    /// 根据 EdgeGateway 当前分页、去重、到期扫描和统计条件创建组合索引；同一进程只检查一次。
    /// </summary>
    public void EnsureLargeTableIndexes()
    {
        lock (IndexInitializationLock)
        {
            if (_indexesEnsured) return;

            Db.Ado.ExecuteCommand("""
                IF OBJECT_ID(N'EdgeGatewayMessageQueue', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayMessageQueue_Due'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_Due
                ON EdgeGatewayMessageQueue(Status, NextRetryTime, CreateTime);

                IF OBJECT_ID(N'EdgeGatewayMessageQueue', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayMessageQueue_MessageId'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_MessageId
                ON EdgeGatewayMessageQueue(MessageId);

                IF OBJECT_ID(N'EdgeGatewayMessageQueue', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayMessageQueue_Device_Status_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_Device_Status_CreateTime
                ON EdgeGatewayMessageQueue(DeviceCode, Status, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayMessageQueue', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayMessageQueue_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_CreateTime
                ON EdgeGatewayMessageQueue(CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayUploadRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayUploadRecord_Queue_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayUploadRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayUploadRecord_Queue_CreateTime
                ON EdgeGatewayUploadRecord(QueueId, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayUploadRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayUploadRecord_Device_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayUploadRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayUploadRecord_Device_CreateTime
                ON EdgeGatewayUploadRecord(DeviceCode, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayUploadRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayUploadRecord_MessageId_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayUploadRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayUploadRecord_MessageId_CreateTime
                ON EdgeGatewayUploadRecord(MessageId, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayUploadRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayUploadRecord_Success_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayUploadRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayUploadRecord_Success_CreateTime
                ON EdgeGatewayUploadRecord(Success, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewaySystemEvent', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewaySystemEvent_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewaySystemEvent')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewaySystemEvent_CreateTime
                ON EdgeGatewaySystemEvent(CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewaySystemEvent', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewaySystemEvent_Device_Level_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewaySystemEvent')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewaySystemEvent_Device_Level_CreateTime
                ON EdgeGatewaySystemEvent(DeviceCode, Level, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayAlarmRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayAlarmRecord_Handled_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayAlarmRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayAlarmRecord_Handled_CreateTime
                ON EdgeGatewayAlarmRecord(Handled, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayAlarmRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayAlarmRecord_Device_Handled_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayAlarmRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayAlarmRecord_Device_Handled_CreateTime
                ON EdgeGatewayAlarmRecord(DeviceCode, Handled, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayOperationLog', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayOperationLog_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayOperationLog')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayOperationLog_CreateTime
                ON EdgeGatewayOperationLog(CreateTime DESC);
                """);

            _indexesEnsured = true;
        }
    }
}
