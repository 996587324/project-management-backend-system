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
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static bool _initialized;
    private readonly ISqlSugarClient _db;
    private readonly IEdgeGatewayDatabaseMaintenanceService _databaseMaintenanceService;
    private readonly ILogger<EdgeGatewayDatabaseInitializer> _logger;

    /// <summary>通过 IOC 获取项目 SqlSugar 连接、索引维护服务和日志组件。</summary>
    public EdgeGatewayDatabaseInitializer(
        SqlSugarScope dbScope,
        IEdgeGatewayDatabaseMaintenanceService databaseMaintenanceService,
        ILogger<EdgeGatewayDatabaseInitializer> logger)
    {
        _db = dbScope.GetConnectionScope("calmadmin");
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

            _db.CodeFirst.InitTables<EdgeDeviceEntity, EdgeDeviceStatusEntity,
                EdgeDeviceConfigEntity, EdgeConfigVersionEntity, EdgeRuleEntity>();
            _db.CodeFirst.InitTables<EdgeWebSocketSourceEntity,
                EdgeWebSocketSubscriptionDeviceEntity,
                EdgeWebSocketSubscriptionParameterEntity>();
            _db.CodeFirst.InitTables<EdgeRuleVersionEntity, EdgeMessageQueueEntity,
                EdgeUploadRecordEntity, EdgeSystemEventEntity, EdgeAlarmRecordEntity>();
            _db.CodeFirst.InitTables<EdgeOperationLogEntity, EdgeBMappingRuleEntity,
                EdgeBPayloadTemplateEntity, EdgeBAutoForwardSettingEntity,
                EdgeGatewayFeatureSettingEntity>();
            _databaseMaintenanceService.PrepareRealtimeSnapshotSchema();
            _db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity>();
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
}
