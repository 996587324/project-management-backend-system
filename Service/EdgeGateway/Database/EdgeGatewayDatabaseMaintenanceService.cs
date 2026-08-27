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

    /// <summary>先删除旧路由唯一索引，避免其阻止 CodeFirst 移除已解耦的 WebSocket 字段。</summary>
    public void PrepareRealtimeSnapshotSchema()
    {
        Db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.indexes
                   WHERE name = N'UX_EdgeGatewayRealtimeDeviceSnapshot_Route'
                     AND object_id = OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot')
               )
            DROP INDEX UX_EdgeGatewayRealtimeDeviceSnapshot_Route
            ON EdgeGatewayRealtimeDeviceSnapshot;
            """);
    }

    /// <summary>
    /// 将看板可选字段统一调整为可空，兼容首次版本已经由 CodeFirst 创建成 NOT NULL 的数据库。
    /// 新数据库由实体上的 IsNullable 配置直接生成正确结构，本方法重复执行也不会改变业务数据。
    /// </summary>
    public void PrepareDashboardSchema()
    {
        Db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'EdgeDashboardRobotFieldBinding', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardRobotFieldBinding')
                     AND name = N'Decimals'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardRobotFieldBinding
            ALTER COLUMN Decimals int NULL;

            IF OBJECT_ID(N'EdgeDashboardRobotRuntimeState', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardRobotRuntimeState')
                     AND name = N'LastSourceTimestamp'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardRobotRuntimeState
            ALTER COLUMN LastSourceTimestamp bigint NULL;

            IF OBJECT_ID(N'EdgeDashboardRobotFaultRecord', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardRobotFaultRecord')
                     AND name = N'EndTime'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardRobotFaultRecord
            ALTER COLUMN EndTime datetime NULL;

            IF OBJECT_ID(N'EdgeDashboardRobotFaultRecord', N'U') IS NOT NULL
               AND NOT EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardRobotFaultRecord')
                     AND name = N'RuleScope'
               )
            ALTER TABLE EdgeDashboardRobotFaultRecord
            ADD RuleScope nvarchar(24) NOT NULL
                CONSTRAINT DF_EdgeDashboardRobotFaultRecord_RuleScope DEFAULT N'Binding';

            IF OBJECT_ID(N'EdgeDashboardWidgetField', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardWidgetField')
                     AND name = N'Decimals'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardWidgetField
            ALTER COLUMN Decimals int NULL;

            IF OBJECT_ID(N'EdgeDashboardWidgetField', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardWidgetField')
                     AND name = N'Width'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardWidgetField
            ALTER COLUMN Width int NULL;

            IF OBJECT_ID(N'EdgeDashboardExportSetting', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardExportSetting')
                     AND name = N'LastScheduledRunDate'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardExportSetting
            ALTER COLUMN LastScheduledRunDate datetime NULL;

            IF OBJECT_ID(N'EdgeDashboardExportRecord', N'U') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID(N'EdgeDashboardExportRecord')
                     AND name = N'CompleteTime'
                     AND is_nullable = 0
               )
            ALTER TABLE EdgeDashboardExportRecord
            ALTER COLUMN CompleteTime datetime NULL;
            """);
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
                       WHERE name = N'IX_EdgeGatewayMessageQueue_Device_Source_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_Device_Source_CreateTime
                ON EdgeGatewayMessageQueue(DeviceCode, SourceId, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayMessageQueue', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayMessageQueue_SourceDevice_Status_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayMessageQueue')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayMessageQueue_SourceDevice_Status_CreateTime
                ON EdgeGatewayMessageQueue(SourceDeviceCode, Status, CreateTime DESC);

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
                       WHERE name = N'IX_EdgeGatewayUploadRecord_SourceDevice_CreateTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayUploadRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayUploadRecord_SourceDevice_CreateTime
                ON EdgeGatewayUploadRecord(SourceDeviceCode, CreateTime DESC);

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

                IF OBJECT_ID(N'EdgeGatewayBMappingRule', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayBMappingRule_Route'
                         AND object_id = OBJECT_ID(N'EdgeGatewayBMappingRule')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayBMappingRule_Route
                ON EdgeGatewayBMappingRule(IsDelete, Enabled, DeviceId, SourceId, SourceDeviceCode);

                IF OBJECT_ID(N'EdgeGatewayBMappingRule', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayBMappingRule_Page'
                         AND object_id = OBJECT_ID(N'EdgeGatewayBMappingRule')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayBMappingRule_Page
                ON EdgeGatewayBMappingRule(
                    IsDelete, Protocol, Enabled, DeviceId, UpdateTime DESC, CreateTime DESC);

                IF OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot', N'U') IS NOT NULL
                   AND EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeGatewayRealtimeDeviceSnapshot_Route'
                         AND object_id = OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot')
                   )
                DROP INDEX UX_EdgeGatewayRealtimeDeviceSnapshot_Route
                ON EdgeGatewayRealtimeDeviceSnapshot;

                IF COL_LENGTH(N'EdgeGatewayRealtimeDeviceSnapshot', N'GatewayDeviceCode') IS NOT NULL
                ALTER TABLE EdgeGatewayRealtimeDeviceSnapshot DROP COLUMN GatewayDeviceCode;

                IF COL_LENGTH(N'EdgeGatewayRealtimeDeviceSnapshot', N'SourceId') IS NOT NULL
                ALTER TABLE EdgeGatewayRealtimeDeviceSnapshot DROP COLUMN SourceId;

                IF COL_LENGTH(N'EdgeGatewayRealtimeDeviceSnapshot', N'DeviceName') IS NOT NULL
                ALTER TABLE EdgeGatewayRealtimeDeviceSnapshot DROP COLUMN DeviceName;

                IF OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeGatewayRealtimeDeviceSnapshot_TenantDevice'
                         AND object_id = OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot')
                   )
                BEGIN
                    ;WITH DuplicateSnapshots AS (
                        SELECT Id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY TenantMark, DeviceCode
                                   ORDER BY ReceiveTime DESC, Id DESC
                               ) AS RowNumber
                        FROM EdgeGatewayRealtimeDeviceSnapshot
                    )
                    DELETE FROM DuplicateSnapshots WHERE RowNumber > 1;

                    CREATE UNIQUE NONCLUSTERED INDEX UX_EdgeGatewayRealtimeDeviceSnapshot_TenantDevice
                    ON EdgeGatewayRealtimeDeviceSnapshot(TenantMark, DeviceCode);
                END;

                IF OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayRealtimeDeviceSnapshot_ReceiveTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayRealtimeDeviceSnapshot_ReceiveTime
                ON EdgeGatewayRealtimeDeviceSnapshot(ReceiveTime DESC);

                IF OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeGatewayRealtimeDeviceSnapshot_ModelReceiveTime'
                         AND object_id = OBJECT_ID(N'EdgeGatewayRealtimeDeviceSnapshot')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeGatewayRealtimeDeviceSnapshot_ModelReceiveTime
                ON EdgeGatewayRealtimeDeviceSnapshot(ModelMark, ReceiveTime DESC)
                INCLUDE(DeviceCode, TenantMark);

                IF OBJECT_ID(N'EdgeDashboardRobotBinding', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotBinding_Line'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotBinding')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotBinding_Line
                ON EdgeDashboardRobotBinding(IsDelete, Enabled, DashboardCode, LineCode, RobotCode);

                IF OBJECT_ID(N'EdgeDashboardRobotBinding', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotBinding_Source'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotBinding')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotBinding_Source
                ON EdgeDashboardRobotBinding(IsDelete, Enabled, TenantMark, DeviceMark);

                IF OBJECT_ID(N'EdgeDashboardRobotFieldBinding', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotFieldBinding_Binding'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotFieldBinding')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotFieldBinding_Binding
                ON EdgeDashboardRobotFieldBinding(RobotBindingId, IsDelete, Enabled, Sort);

                IF OBJECT_ID(N'EdgeDashboardRobotStateRule', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotStateRule_Binding'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotStateRule')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotStateRule_Binding
                ON EdgeDashboardRobotStateRule(RobotBindingId, IsDelete, Enabled, Priority);

                IF OBJECT_ID(N'EdgeDashboardModelAlarmRule', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardModelAlarmRule_Model'
                         AND object_id = OBJECT_ID(N'EdgeDashboardModelAlarmRule')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardModelAlarmRule_Model
                ON EdgeDashboardModelAlarmRule(DashboardCode, ModelMark, IsDelete, Enabled, Priority);

                IF OBJECT_ID(N'EdgeDashboardModelFieldCatalog', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardModelFieldCatalog_ModelPath'
                         AND object_id = OBJECT_ID(N'EdgeDashboardModelFieldCatalog')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardModelFieldCatalog_ModelPath
                ON EdgeDashboardModelFieldCatalog(ModelMark, SourcePath);

                IF OBJECT_ID(N'EdgeDashboardModelFieldCatalog', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardModelFieldCatalog_ModelSeen'
                         AND object_id = OBJECT_ID(N'EdgeDashboardModelFieldCatalog')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardModelFieldCatalog_ModelSeen
                ON EdgeDashboardModelFieldCatalog(ModelMark, IsDelete, LastSeenTime DESC);

                IF OBJECT_ID(N'EdgeDashboardRobotRuntimeState', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeDashboardRobotRuntimeState_Binding'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotRuntimeState')
                   )
                CREATE UNIQUE NONCLUSTERED INDEX UX_EdgeDashboardRobotRuntimeState_Binding
                ON EdgeDashboardRobotRuntimeState(RobotBindingId);

                IF OBJECT_ID(N'EdgeDashboardRobotDailyStatistics', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeDashboardRobotDailyStatistics_BindingDate'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotDailyStatistics')
                   )
                CREATE UNIQUE NONCLUSTERED INDEX UX_EdgeDashboardRobotDailyStatistics_BindingDate
                ON EdgeDashboardRobotDailyStatistics(RobotBindingId, StatDate);

                IF OBJECT_ID(N'EdgeDashboardRobotDailyStatistics', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotDailyStatistics_LineDate'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotDailyStatistics')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotDailyStatistics_LineDate
                ON EdgeDashboardRobotDailyStatistics(LineCode, StatDate DESC, RobotCode);

                IF OBJECT_ID(N'EdgeDashboardRobotFaultRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotFaultRecord_BindingStatusStart'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotFaultRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotFaultRecord_BindingStatusStart
                ON EdgeDashboardRobotFaultRecord(RobotBindingId, Status, StartTime DESC);

                IF OBJECT_ID(N'EdgeDashboardRobotFaultRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotFaultRecord_LineStart'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotFaultRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotFaultRecord_LineStart
                ON EdgeDashboardRobotFaultRecord(LineCode, StartTime DESC, RobotCode);

                IF OBJECT_ID(N'EdgeDashboardRobotFaultRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardRobotFaultRecord_ReasonStart'
                         AND object_id = OBJECT_ID(N'EdgeDashboardRobotFaultRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardRobotFaultRecord_ReasonStart
                ON EdgeDashboardRobotFaultRecord(FaultReason, StartTime DESC);

                IF OBJECT_ID(N'EdgeDashboardWidget', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardWidget_DashboardLine'
                         AND object_id = OBJECT_ID(N'EdgeDashboardWidget')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardWidget_DashboardLine
                ON EdgeDashboardWidget(DashboardCode, LineCode, IsDelete, Enabled, Sort);

                IF OBJECT_ID(N'EdgeDashboardWidgetField', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardWidgetField_Widget'
                         AND object_id = OBJECT_ID(N'EdgeDashboardWidgetField')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardWidgetField_Widget
                ON EdgeDashboardWidgetField(WidgetId, IsDelete, Visible, Sort);

                IF OBJECT_ID(N'EdgeDashboardLineConfig', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeDashboardLineConfig_DashboardLine'
                         AND object_id = OBJECT_ID(N'EdgeDashboardLineConfig')
                   )
                CREATE UNIQUE NONCLUSTERED INDEX UX_EdgeDashboardLineConfig_DashboardLine
                ON EdgeDashboardLineConfig(DashboardCode, LineCode)
                WHERE IsDelete = 0;

                IF OBJECT_ID(N'EdgeDashboardSceneLayout', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardSceneLayout_DashboardLine'
                         AND object_id = OBJECT_ID(N'EdgeDashboardSceneLayout')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardSceneLayout_DashboardLine
                ON EdgeDashboardSceneLayout(DashboardCode, LineCode, IsDelete);

                IF OBJECT_ID(N'EdgeDashboardExportSetting', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'UX_EdgeDashboardExportSetting_Dashboard'
                         AND object_id = OBJECT_ID(N'EdgeDashboardExportSetting')
                   )
                CREATE UNIQUE NONCLUSTERED INDEX UX_EdgeDashboardExportSetting_Dashboard
                ON EdgeDashboardExportSetting(DashboardCode);

                IF OBJECT_ID(N'EdgeDashboardExportRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardExportRecord_DashboardCreated'
                         AND object_id = OBJECT_ID(N'EdgeDashboardExportRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardExportRecord_DashboardCreated
                ON EdgeDashboardExportRecord(DashboardCode, CreateTime DESC)
                INCLUDE(LineCode, ReportDate, Status);

                IF OBJECT_ID(N'EdgeDashboardExportRecord', N'U') IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1 FROM sys.indexes
                       WHERE name = N'IX_EdgeDashboardExportRecord_Schedule'
                         AND object_id = OBJECT_ID(N'EdgeDashboardExportRecord')
                   )
                CREATE NONCLUSTERED INDEX IX_EdgeDashboardExportRecord_Schedule
                ON EdgeDashboardExportRecord(DashboardCode, ReportDate, LineCode, TriggerType, Status);
                """);

            _indexesEnsured = true;
        }
    }
}
