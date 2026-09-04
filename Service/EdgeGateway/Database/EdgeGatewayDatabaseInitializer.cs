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
            _webSocketLogDb.CodeFirst.SplitTables().InitTables<EdgeWebSocketReceiveRecordEntity>();

            _db.CodeFirst.InitTables<EdgeDeviceEntity, EdgeDeviceStatusEntity,
                EdgeDeviceConfigEntity, EdgeConfigVersionEntity, EdgeRuleEntity>();
            _db.CodeFirst.InitTables<EdgeWebSocketSourceEntity,
                EdgeWebSocketSubscriptionDeviceEntity,
                EdgeWebSocketSubscriptionParameterEntity>();
            _db.CodeFirst.InitTables<EdgeRuleVersionEntity, EdgeMessageQueueEntity,
                EdgeUploadRecordEntity, EdgeWebSocketSendRecordEntity,
                EdgeSystemEventEntity>();
            _db.CodeFirst.InitTables<EdgeAlarmRecordEntity>();
            _db.CodeFirst.InitTables<EdgeOperationLogEntity, EdgeBMappingRuleEntity,
                EdgeBPayloadTemplateEntity, EdgeBAutoForwardSettingEntity,
                EdgeGatewayFeatureSettingEntity>();
            _databaseMaintenanceService.PrepareRealtimeSnapshotSchema();
            _db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardRobotBindingEntity,
                EdgeDashboardRobotFieldBindingEntity,
                EdgeDashboardRobotStateRuleEntity,
                EdgeDashboardModelAlarmRuleEntity,
                EdgeDashboardRobotRuntimeStateEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardDeviceBindingEntity,
                EdgeDashboardDeviceFieldBindingEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardModelFieldCatalogEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardRobotDailyStatisticEntity,
                EdgeDashboardRobotFaultRecordEntity,
                EdgeDashboardWidgetEntity,
                EdgeDashboardWidgetFieldEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardLineConfigEntity,
                EdgeDashboardSceneLayoutEntity>();
            _db.CodeFirst.InitTables<EdgeDashboardExportSettingEntity,
                EdgeDashboardExportRecordEntity>();
            _databaseMaintenanceService.PrepareDashboardSchema();
            _databaseMaintenanceService.EnsureLargeTableIndexes();

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
}
