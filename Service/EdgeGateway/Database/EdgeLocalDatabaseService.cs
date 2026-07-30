using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// EdgeGateway 本地 SQLite 单例访问服务。一个物理连接配合一把异步锁，
/// 防止采集、上传和心跳循环在事务期间交叉使用连接。
/// </summary>
public sealed class EdgeLocalDatabaseService : IEdgeLocalDatabaseService
{
    private const string EdgeDatabaseConfigId = "edge-gateway";
    private readonly ISqlSugarClient _db;
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private bool _initialized;

    /// <summary>从项目单例 SqlSugarScope 中固定取得本地 SQLite 连接实例。</summary>
    public EdgeLocalDatabaseService(
        SqlSugarScope dbScope,
        EdgeGatewayBootstrapOptions bootstrapOptions)
    {
        _db = dbScope.GetConnectionScope(EdgeDatabaseConfigId);
        _bootstrapOptions = bootstrapOptions;
    }

    /// <summary>初始化唯一连接、SQLite 运行参数、CodeFirst 表结构和查询索引。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var databasePath = Path.GetFullPath(
                _bootstrapOptions.DatabasePath,
                AppContext.BaseDirectory);
            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
                Directory.CreateDirectory(databaseDirectory);

            // 保持单连接 WAL 模式，并在短暂写锁冲突时等待而不是立即失败。
            _db.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
            _db.Ado.ExecuteCommand("PRAGMA synchronous=NORMAL;");
            _db.Ado.ExecuteCommand("PRAGMA busy_timeout=10000;");
            _db.Ado.ExecuteCommand("PRAGMA foreign_keys=ON;");
            _db.CodeFirst.InitTables<EdgeLocalDeviceConfigEntity, EdgeLocalSourceConfigEntity,
                EdgeLocalMappingRuleEntity, EdgeLocalRuleEntity, EdgeLocalRuleVersionEntity>();
            _db.CodeFirst.InitTables<EdgeLocalMessageQueueEntity, EdgeLocalUploadRecordEntity,
                EdgeLocalSystemEventEntity>();
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_source_config_source_id ON source_config(source_id);");
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_message_queue_message_id ON message_queue(message_id);");
            _db.Ado.ExecuteCommand(
                "CREATE INDEX IF NOT EXISTS ix_message_queue_due ON message_queue(status, next_retry_time, create_time);");

            _initialized = true;
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>串行执行去重查询和插入，唯一索引仍作为最终并发保护。</summary>
    public Task<bool> InsertMessageIfAbsentAsync(
        EdgeLocalMessageQueueEntity entity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            if (await _db.Queryable<EdgeLocalMessageQueueEntity>()
                    .AnyAsync(x => x.MessageId == entity.MessageId))
                return false;

            try
            {
                return await _db.Insertable(entity).ExecuteCommandAsync() > 0;
            }
            catch (SqlSugarException)
            {
                if (await _db.Queryable<EdgeLocalMessageQueueEntity>()
                        .AnyAsync(x => x.MessageId == entity.MessageId))
                    return false;
                throw;
            }
        }, cancellationToken);

    /// <summary>在单例连接上读取一批到期消息，避免与配置事务交叉执行。</summary>
    public Task<List<EdgeLocalMessageQueueEntity>> GetDueMessagesAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => _db.Queryable<EdgeLocalMessageQueueEntity>()
                .Where(x => (x.Status == "Pending" || x.Status == "Failed") &&
                            (x.NextRetryTime == null || x.NextRetryTime <= now))
                .OrderBy(x => x.CreateTime)
                .Take(Math.Clamp(limit, 1, 500))
                .ToListAsync(),
            cancellationToken);

    /// <summary>在一次串行访问中完成本地队列状态统计。</summary>
    public Task<QueueStatistics> GetQueueStatisticsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var pending = await _db.Queryable<EdgeLocalMessageQueueEntity>()
                .Where(x => x.Status == "Pending")
                .CountAsync();
            var failed = await _db.Queryable<EdgeLocalMessageQueueEntity>()
                .Where(x => x.Status == "Failed")
                .CountAsync();
            var uploaded = await _db.Queryable<EdgeLocalMessageQueueEntity>()
                .Where(x => x.Status == "Uploaded")
                .CountAsync();
            return new QueueStatistics(pending, failed, uploaded);
        }, cancellationToken);

    /// <summary>通过统一写入入口插入本地事件，调用方不再直接操作 SqlSugar。</summary>
    public Task InsertSystemEventAsync(
        EdgeLocalSystemEventEntity entity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            async () =>
            {
                await _db.Insertable(entity).ExecuteCommandAsync();
            },
            cancellationToken);

    /// <summary>在同一物理连接及同一事务中替换连接、映射和规则快照。</summary>
    public Task SaveEffectiveConfigurationAsync(
        EdgeLocalConfigurationWriteModel model,
        CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(async () =>
        {
            if (model.SourceVersion > 0)
            {
                await _db.Deleteable<EdgeLocalSourceConfigEntity>().ExecuteCommandAsync();
                if (model.Sources.Count > 0)
                    await _db.Insertable(model.Sources).ExecuteCommandAsync();
            }

            if (model.ConfigVersion > 0 && model.DeviceConfig != null)
            {
                await _db.Deleteable<EdgeLocalDeviceConfigEntity>()
                    .Where(x => x.DeviceId == model.DeviceId &&
                                x.Version == model.ConfigVersion)
                    .ExecuteCommandAsync();
                await _db.Insertable(model.DeviceConfig).ExecuteCommandAsync();
                await _db.Deleteable<EdgeLocalMappingRuleEntity>().ExecuteCommandAsync();
                if (model.Mappings.Count > 0)
                    await _db.Insertable(model.Mappings).ExecuteCommandAsync();
            }

            if (model.RuleVersion > 0 && model.RuleVersionSnapshot != null)
            {
                await _db.Deleteable<EdgeLocalRuleVersionEntity>()
                    .Where(x => x.Version == model.RuleVersion)
                    .ExecuteCommandAsync();
                await _db.Insertable(model.RuleVersionSnapshot).ExecuteCommandAsync();
                await _db.Deleteable<EdgeLocalRuleEntity>().ExecuteCommandAsync();
                if (model.Rules.Count > 0)
                    await _db.Insertable(model.Rules).ExecuteCommandAsync();
            }
        }, "保存边缘配置事务失败", cancellationToken);

    /// <summary>在同一物理连接及同一事务中更新队列并新增上传记录。</summary>
    public Task CompleteUploadAttemptAsync(
        EdgeLocalUploadAttemptWriteModel model,
        CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(async () =>
        {
            if (model.Success)
            {
                await _db.Updateable<EdgeLocalMessageQueueEntity>()
                    .SetColumns(x => new EdgeLocalMessageQueueEntity
                    {
                        Status = "Uploaded",
                        UploadTime = model.AttemptTime,
                        LastError = null
                    })
                    .Where(x => x.Id == model.QueueId)
                    .ExecuteCommandAsync();
            }
            else
            {
                await _db.Updateable<EdgeLocalMessageQueueEntity>()
                    .SetColumns(x => new EdgeLocalMessageQueueEntity
                    {
                        Status = "Failed",
                        RetryCount = model.RetryCount,
                        NextRetryTime = model.NextRetryTime,
                        LastError = model.ErrorMessage
                    })
                    .Where(x => x.Id == model.QueueId)
                    .ExecuteCommandAsync();
            }

            await _db.Insertable(model.UploadRecord).ExecuteCommandAsync();
        }, "更新边缘队列事务失败", cancellationToken);

    /// <summary>串行执行一个普通 SQLite 操作并返回结果。</summary>
    private async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            return await action();
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>串行执行一个不返回结果的普通 SQLite 操作。</summary>
    private Task ExecuteAsync(
        Func<Task> action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await action();
            return true;
        }, cancellationToken);

    /// <summary>在访问锁保护下使用当前固定连接执行 SqlSugar 事务。</summary>
    private async Task ExecuteTransactionAsync(
        Func<Task> action,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = await _db.Ado.UseTranAsync(action);
            if (!transaction.IsSuccess)
                throw transaction.ErrorException ??
                      new InvalidOperationException(errorMessage);
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>阻止初始化完成前执行本地 SQLite 业务操作。</summary>
    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("EdgeGateway 本地 SQLite 尚未初始化");
    }
}
