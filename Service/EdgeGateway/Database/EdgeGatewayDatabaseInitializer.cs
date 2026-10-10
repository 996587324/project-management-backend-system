using System.Diagnostics;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// EdgeGateway 中心 SQL Server 初始化实现，避免在每个业务请求中重复执行 CodeFirst。
/// </summary>
public sealed class EdgeGatewayDatabaseInitializer : IEdgeGatewayDatabaseInitializer
{
    private const string WebSocketLogDatabaseName = "EdgeGatewayLogs";
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static bool _initialized;
    private readonly ISqlSugarClient _db;
    private readonly ISqlSugarClient _webSocketLogDb;
    private readonly IEdgeGatewayDatabaseMaintenanceService _databaseMaintenanceService;
    private readonly ILogger<EdgeGatewayDatabaseInitializer> _logger;

    /// <summary>通过 IOC 获取项目 SqlSugar 连接、索引维护服务和日志组件。</summary>
    public EdgeGatewayDatabaseInitializer(
        SqlSugarScope dbScope,
        IEdgeGatewayDatabaseMaintenanceService databaseMaintenanceService,
        ILogger<EdgeGatewayDatabaseInitializer> logger)
    {
        _db = dbScope.GetConnectionScope("calmadmin");
        _webSocketLogDb = dbScope.GetConnectionScope("edge-gateway-logs");
        _databaseMaintenanceService = databaseMaintenanceService;
        _logger = logger;
    }

    /// <summary>串行完成中心端全部 EdgeGateway 表结构和索引初始化，同一进程只执行一次。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await InitializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Initializing EdgeGateway center database tables and indexes.");

            // WebSocket 接收报文单独存放，首次启动时自动创建日志数据库。
            // 数据库名是代码内固定的受控标识，不接受外部输入，避免拼接 SQL 带来注入风险。
            await EnsureWebSocketLogDatabaseAsync(cancellationToken);
            // 实体特性定义了主键、组合索引和按日分表规则；初始化时创建当天日表。
            RunInitializationStep(nameof(EdgeWebSocketReceiveRecordEntity), () => _webSocketLogDb.CodeFirst.SplitTables().InitTables<EdgeWebSocketReceiveRecordEntity>());

            RunInitializationStep(nameof(EdgeDeviceEntity), () => _db.CodeFirst.InitTables<EdgeDeviceEntity, EdgeDeviceStatusEntity,
                EdgeDeviceConfigEntity, EdgeConfigVersionEntity, EdgeRuleEntity>());
            RunInitializationStep(nameof(EdgeWebSocketSourceEntity), () => _db.CodeFirst.InitTables<EdgeWebSocketSourceEntity,
                EdgeWebSocketSubscriptionDeviceEntity,
                EdgeWebSocketSubscriptionParameterEntity>());
            RunInitializationStep(nameof(EdgeRuleVersionEntity), () => _db.CodeFirst.InitTables<EdgeRuleVersionEntity, EdgeMessageQueueEntity,
                EdgeUploadRecordEntity, EdgeWebSocketSendRecordEntity,
                EdgeSystemEventEntity>());
            EnsureMessageQueueProjectionColumns();
            RunInitializationStep(nameof(EdgeAlarmRecordEntity), () => _db.CodeFirst.InitTables<EdgeAlarmRecordEntity>());
            RunInitializationStep(nameof(EdgeOperationLogEntity), () => _db.CodeFirst.InitTables<EdgeOperationLogEntity, EdgeBMappingRuleEntity,
                EdgeBPayloadTemplateEntity, EdgeBAutoForwardSettingEntity,
                EdgeGatewayFeatureSettingEntity>());
            RunInitializationStep(nameof(EdgeDataTraceSettingEntity), () => _db.CodeFirst.InitTables<EdgeDataTraceSettingEntity,
                EdgeDataTraceRuleEntity, EdgeDataTraceFieldEntity>());
            EnsureDataTraceSettingSchema();
            RunInitializationStep(nameof(EdgeDataTracePointEntity), () => _webSocketLogDb.CodeFirst.InitTables<EdgeDataTracePointEntity,
                EdgeDataTraceLatestEntity, EdgeDataTraceExportTaskEntity>());
            RunInitializationStep("PrepareRealtimeSnapshotSchema", _databaseMaintenanceService.PrepareRealtimeSnapshotSchema);
            RunInitializationStep(nameof(EdgeRealtimeDeviceSnapshotEntity), () => _db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity>());
            RunInitializationStep(nameof(EdgeRealtimeDeviceFieldLatestEntity), () => _db.CodeFirst.InitTables<EdgeRealtimeDeviceFieldLatestEntity>());
            RunInitializationStep(nameof(EdgeDashboardRobotBindingEntity), () => _db.CodeFirst.InitTables<EdgeDashboardRobotBindingEntity,
                EdgeDashboardRobotFieldBindingEntity,
                EdgeDashboardRobotStateRuleEntity,
                EdgeDashboardModelAlarmRuleEntity,
                EdgeDashboardRobotRuntimeStateEntity>());
            // 已部署库可能已有设备绑定数据。先以默认值补齐新字段，避免 CodeFirst
            // 直接向非空表新增 NOT NULL 列时阻断整个宿主启动。
            EnsureDeviceBindingStatisticColumns();
            RunInitializationStep(nameof(EdgeDashboardDeviceBindingEntity), () => _db.CodeFirst.InitTables<EdgeDashboardDeviceBindingEntity,
                EdgeDashboardDeviceFieldBindingEntity>());
            RunInitializationStep(nameof(EdgeDashboardModelDisplayMappingEntity), () => _db.CodeFirst.InitTables<EdgeDashboardModelDisplayMappingEntity>());
            await EnsureDashboardModelDisplayMappingsAsync(cancellationToken);
            RunInitializationStep(nameof(EdgeDashboardModelFieldCatalogEntity), () => _db.CodeFirst.InitTables<EdgeDashboardModelFieldCatalogEntity>());
            RunInitializationStep(nameof(EdgeDashboardModelFieldDisplayMappingEntity), () => _db.CodeFirst.InitTables<EdgeDashboardModelFieldDisplayMappingEntity>());
            RunInitializationStep(nameof(EdgeDashboardModelStatePriorityEntity), () => _db.CodeFirst.InitTables<EdgeDashboardModelStatePriorityEntity>());
            RunInitializationStep(nameof(EdgeDashboardRobotDailyStatisticEntity), () => _db.CodeFirst.InitTables<EdgeDashboardRobotDailyStatisticEntity,
                EdgeDashboardDeviceRuntimeStateEntity,
                EdgeDashboardEquipmentDailyStatisticEntity,
                EdgeDashboardRobotFaultRecordEntity,
                EdgeDashboardWidgetEntity>());
            RunInitializationStep(nameof(EdgeDashboardWidgetFieldEntity), () => _db.CodeFirst.InitTables<EdgeDashboardWidgetFieldEntity>());
            RunInitializationStep(nameof(EdgeDashboardLineConfigEntity), () => _db.CodeFirst.InitTables<EdgeDashboardLineConfigEntity,
                EdgeDashboardSceneLayoutEntity,
                EdgeDashboardLineOutputBindingEntity,
                EdgeDashboardLineOutputDailyStatisticEntity>());
            // 设备卡片字段的后续新增列也必须在 CodeFirst 前兼容旧表。
            EnsureDeviceCardFieldDisplayMappingColumn();
            EnsureDeviceCardAiAnalysisColumn();
            EnsureDeviceCardAlertColumns();
            RunInitializationStep(nameof(EdgeDashboardDeviceCardEntity), () => _db.CodeFirst.InitTables<EdgeDashboardDeviceCardEntity,
                EdgeDashboardDeviceCardFieldEntity,
                EdgeDashboardDeviceCardBoardSettingEntity,
                EdgeDashboardDeviceCardTraceStateEntity,
                EdgeDashboardDeviceCardChangeRecordEntity>());
            RunInitializationStep("DeviceCardAlertNullableTimes", () => EdgeDashboardDeviceCardSchema.EnsureNullablePushTimes(_db));
            RunInitializationStep(nameof(EdgeDeviceCardPushQueueSettingEntity), () =>
                EdgeDeviceCardPushQueueSchema.EnsureIntervalColumn(_db));
            RunInitializationStep(nameof(EdgeDeviceCardPushRuleEntity), () => _db.CodeFirst.InitTables(
                typeof(EdgeDeviceCardPushRuleEntity), typeof(EdgeDeviceCardPushRecipientEntity),
                typeof(EdgeDeviceCardPushStateEntity), typeof(EdgeDeviceCardPushTaskEntity),
                typeof(EdgeDeviceCardPushQueueSettingEntity)));
            RunInitializationStep("WeChatClawBotPushDependencies", () => WeChatClawBotDatabaseInitializer.Initialize(_db));
            RunInitializationStep(nameof(EdgeDashboardDeviceCardAlertStateEntity), () => _db.CodeFirst.InitTables<EdgeDashboardDeviceCardAlertStateEntity,
                EdgeDashboardDeviceCardAlertRecordEntity>());
            RunInitializationStep(nameof(EdgeDashboardExportSettingEntity), () => _db.CodeFirst.InitTables<EdgeDashboardExportSettingEntity,
                EdgeDashboardExportRecordEntity>());
            RunInitializationStep("PrepareDashboardSchema", _databaseMaintenanceService.PrepareDashboardSchema);
            RunInitializationStep("EnsureLargeTableIndexes", _databaseMaintenanceService.EnsureLargeTableIndexes);

            _initialized = true;
            stopwatch.Stop();
            _logger.LogInformation(
                "EdgeGateway center database initialization completed in {ElapsedMilliseconds} ms.",
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private void RunInitializationStep(string module, Action initialize)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            initialize();
        }
        finally
        {
            _logger.LogInformation("EdgeGateway initialization step {Module} took {ElapsedMilliseconds} ms.",
                module, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 为已部署的中心消息队列补齐日志投影完成标记。主消息和高频日志库分离后，
    /// 日表或追溯库短暂不可用不应使消息永久缺失，空标记会由后台补偿任务重试。
    /// </summary>
    private void EnsureMessageQueueProjectionColumns()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeGatewayMessageQueue', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeGatewayMessageQueue', N'ReceiveRecordProjectedTime') IS NULL
                ALTER TABLE dbo.EdgeGatewayMessageQueue
                ADD ReceiveRecordProjectedTime datetime NULL;

            IF OBJECT_ID(N'dbo.EdgeGatewayMessageQueue', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeGatewayMessageQueue', N'DataTraceProjectedTime') IS NULL
                ALTER TABLE dbo.EdgeGatewayMessageQueue
                ADD DataTraceProjectedTime datetime NULL;
            """);
    }

    /// <summary>
    /// 为已部署的设备卡片字段表补齐模型展示映射继承开关。
    /// 旧表先显式增加带默认值的非空列，避免 CodeFirst 向非空表增列时中断应用启动。
    /// </summary>
    private void EnsureDeviceCardFieldDisplayMappingColumn()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceCardField', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceCardField', N'UseModelDisplayMapping') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceCardField
                ADD UseModelDisplayMapping bit NOT NULL
                    CONSTRAINT DF_EdgeDashboardDeviceCardField_UseModelDisplayMapping DEFAULT (1);
            """);
    }

    /// <summary>
    /// 为已部署的设备卡片字段表补齐 AI 分析开关。新库由 CodeFirst 直接创建，旧库仅执行一次安全的增列。
    /// </summary>
    private void EnsureDeviceCardAiAnalysisColumn()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceCardField', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceCardField', N'AiAnalysisEnabled') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceCardField
                ADD AiAnalysisEnabled bit NOT NULL
                    CONSTRAINT DF_EdgeDashboardDeviceCardField_AiAnalysisEnabled DEFAULT (0);
            """);
    }

    /// <summary>兼容已部署设备绑定：补齐可选设备日统计的开关、业务日和状态映射字段。</summary>
    private void EnsureDeviceBindingStatisticColumns()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceBinding', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceBinding', N'StatisticsEnabled') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceBinding
                ADD StatisticsEnabled bit NOT NULL
                    CONSTRAINT DF_EdgeDashboardDeviceBinding_StatisticsEnabled DEFAULT (0);

            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceBinding', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceBinding', N'BusinessDayStartHour') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceBinding
                ADD BusinessDayStartHour int NOT NULL
                    CONSTRAINT DF_EdgeDashboardDeviceBinding_BusinessDayStartHour DEFAULT (0);

            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceBinding', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceBinding', N'StatusSourcePath') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceBinding ADD StatusSourcePath nvarchar(512) NULL;

            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceBinding', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceBinding', N'StatusMappingJson') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceBinding ADD StatusMappingJson nvarchar(max) NULL;
            """);
    }

    /// <summary>
    /// 兼容已发布环境：CodeFirst 不会为已有字段表自动补列，因此上下限需显式、安全地迁移。
    /// 告警状态和告警记录均为新表，由上方 CodeFirst 创建。
    /// </summary>
    private void EnsureDeviceCardAlertColumns()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceCardField', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceCardField', N'AlarmLowerLimit') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceCardField
                ADD AlarmLowerLimit decimal(18,6) NULL;

            IF OBJECT_ID(N'dbo.EdgeDashboardDeviceCardField', N'U') IS NOT NULL
               AND COL_LENGTH(N'dbo.EdgeDashboardDeviceCardField', N'AlarmUpperLimit') IS NULL
                ALTER TABLE dbo.EdgeDashboardDeviceCardField
                ADD AlarmUpperLimit decimal(18,6) NULL;
            """);
    }

    /// <summary>
    /// 兼容已部署环境中曾以字符串或可空字段创建的追溯开关。
    /// SqlSugar 将实体 bool 绑定到非 bit 列会失败，后台清理任务也就无法读取保留时长。
    /// </summary>
    private void EnsureDataTraceSettingSchema()
    {
        _db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'dbo.EdgeGatewayDataTraceSetting', N'U') IS NOT NULL
            BEGIN
                IF COL_LENGTH(N'dbo.EdgeGatewayDataTraceSetting', N'Enabled') IS NULL
                BEGIN
                    ALTER TABLE dbo.EdgeGatewayDataTraceSetting
                    ADD Enabled bit NOT NULL
                        CONSTRAINT DF_EdgeGatewayDataTraceSetting_Enabled DEFAULT (1);
                END
                ELSE
                BEGIN
                    UPDATE dbo.EdgeGatewayDataTraceSetting
                    SET Enabled = CASE
                        WHEN LOWER(LTRIM(RTRIM(CONVERT(nvarchar(16), Enabled))))
                            IN (N'0', N'false', N'no', N'off', N'') THEN 0
                        ELSE 1
                    END;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM sys.columns AS columnInfo
                        INNER JOIN sys.types AS typeInfo ON columnInfo.user_type_id = typeInfo.user_type_id
                        WHERE columnInfo.object_id = OBJECT_ID(N'dbo.EdgeGatewayDataTraceSetting')
                          AND columnInfo.name = N'Enabled'
                          AND typeInfo.name <> N'bit'
                    )
                    BEGIN
                        ALTER TABLE dbo.EdgeGatewayDataTraceSetting
                        ALTER COLUMN Enabled bit NOT NULL;
                    END
                    ELSE IF EXISTS
                    (
                        SELECT 1
                        FROM sys.columns AS columnInfo
                        WHERE columnInfo.object_id = OBJECT_ID(N'dbo.EdgeGatewayDataTraceSetting')
                          AND columnInfo.name = N'Enabled'
                          AND columnInfo.is_nullable = 1
                    )
                    BEGIN
                        ALTER TABLE dbo.EdgeGatewayDataTraceSetting
                        ALTER COLUMN Enabled bit NOT NULL;
                    END
                END
            END
            """);
    }

    /// <summary>使用当前中心库连接创建独立的 WebSocket 日志数据库，并验证新库可访问。</summary>
    private async Task EnsureWebSocketLogDatabaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _db.Ado.ExecuteCommandAsync($"""
            IF DB_ID(N'{WebSocketLogDatabaseName}') IS NULL
            BEGIN
                CREATE DATABASE [{WebSocketLogDatabaseName}];
            END
            """);
        cancellationToken.ThrowIfCancellationRequested();
        await _webSocketLogDb.Ado.ExecuteCommandAsync("SELECT 1");
        _logger.LogInformation(
            "EdgeGateway WebSocket log database {DatabaseName} is ready.",
            WebSocketLogDatabaseName);
    }

    /// <summary>补齐已确认的首条模型业务名称，后续均由看板配置页维护。</summary>
    private async Task EnsureDashboardModelDisplayMappingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string dashboardCode = "production-dashboard";
        const string modelMark = "CHUANPXX_JQR";
        // 已删除的记录也代表管理员明确处理过该映射，不应在每次重启时重新写回。
        var exists = await _db.Queryable<EdgeDashboardModelDisplayMappingEntity>().AnyAsync(x =>
            x.DashboardCode == dashboardCode && x.ModelMark == modelMark);
        if (exists) return;

        await _db.Insertable(new EdgeDashboardModelDisplayMappingEntity
        {
            DashboardCode = dashboardCode,
            ModelMark = modelMark,
            DisplayName = "穿片下线机器人",
            EquipmentType = "Robot",
            Sort = 10,
            Enabled = true
        }).ExecuteCommandAsync();
    }
}
