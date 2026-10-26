using Interface;
using Model.EdgeGateway;
using SqlSugar;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Service;

/// <summary>
/// EdgeGateway 本地 SQLite 存储。采集数据按自然日保存在独立的
/// edge-gateway-yyyyMMdd.db 文件中，文件内表名保持原样。
/// </summary>
public sealed class EdgeLocalDatabaseService : IEdgeLocalDatabaseService
{
    private const string EdgeDatabaseConfigId = "edge-gateway";
    private const string LegacyMigrationKey = "edge_gateway_daily_database_v1";
    private readonly ILogger<EdgeLocalDatabaseService>? _logger;
    private readonly ISqlSugarClient _legacyDb;
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private readonly Dictionary<string, SqlSugarClient> _dailyDatabases = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private DateTime _lastCleanupDate = DateTime.MinValue;

    public EdgeLocalDatabaseService(
        SqlSugarScope dbScope,
        EdgeGatewayBootstrapOptions bootstrapOptions,
        ILogger<EdgeLocalDatabaseService>? logger = null)
    {
        // 旧 edge-gateway.db 仅用于首次迁移今天和昨天尚未上传的消息。
        _legacyDb = dbScope.GetConnectionScope(EdgeDatabaseConfigId);
        _bootstrapOptions = bootstrapOptions;
        _logger = logger;
    }

    /// <summary>初始化当天数据库、迁移旧单文件待传数据，并执行一次过期文件清理。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            var now = DateTime.UtcNow;
            _ = GetDailyDatabase(now);
            await MigrateLegacyPendingMessagesAsync(now, cancellationToken);
            await CleanupExpiredDatabaseFilesIfDueAsync(now, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _accessGate.Release();
        }
    }

    public Task<bool> InsertMessageIfAbsentAsync(
        EdgeLocalMessageQueueEntity entity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var db = GetDailyDatabase(entity.CreateTime);
            if (await db.Queryable<EdgeLocalMessageQueueEntity>()
                    .AnyAsync(x => x.MessageId == entity.MessageId))
                return false;

            try
            {
                return await db.Insertable(entity).ExecuteCommandAsync() > 0;
            }
            catch (SqlSugarException)
            {
                if (await db.Queryable<EdgeLocalMessageQueueEntity>()
                        .AnyAsync(x => x.MessageId == entity.MessageId))
                    return false;
                throw;
            }
        }, cancellationToken);

    /// <summary>只读取今天和未完成上传的昨天数据库文件。</summary>
    public Task<List<EdgeLocalMessageQueueEntity>> GetDueMessagesAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => GetDueMessagesFromActiveDaysAsync(now, limit, cancellationToken), cancellationToken);

    public Task<QueueStatistics> GetQueueStatisticsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            var days = await GetReadableDaysAsync(now, cancellationToken);
            long pending = 0;
            long failed = 0;
            long uploaded = 0;
            foreach (var day in days)
            {
                var db = GetDailyDatabase(day);
                pending += await db.Queryable<EdgeLocalMessageQueueEntity>()
                    .Where(x => x.Status == "Pending").CountAsync();
                failed += await db.Queryable<EdgeLocalMessageQueueEntity>()
                    .Where(x => x.Status == "Failed").CountAsync();
                uploaded += await db.Queryable<EdgeLocalMessageQueueEntity>()
                    .Where(x => x.Status == "Uploaded").CountAsync();
            }
            return new QueueStatistics(pending, failed, uploaded);
        }, cancellationToken);

    public Task InsertSystemEventAsync(
        EdgeLocalSystemEventEntity entity,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await GetDailyDatabase(entity.CreateTime).Insertable(entity).ExecuteCommandAsync();
        }, cancellationToken);

    /// <summary>配置快照与采集队列均保存到当天边缘数据库文件中。</summary>
    public Task SaveEffectiveConfigurationAsync(
        EdgeLocalConfigurationWriteModel model,
        CancellationToken cancellationToken) =>
        ExecuteTransactionAsync(GetDailyDatabase(DateTime.UtcNow), async db =>
        {
            if (model.SourceVersion > 0)
            {
                await db.Deleteable<EdgeLocalSourceConfigEntity>().ExecuteCommandAsync();
                if (model.Sources.Count > 0)
                    await db.Insertable(model.Sources).ExecuteCommandAsync();
            }

            if (model.ConfigVersion > 0 && model.DeviceConfig != null)
            {
                await db.Deleteable<EdgeLocalDeviceConfigEntity>()
                    .Where(x => x.DeviceId == model.DeviceId && x.Version == model.ConfigVersion)
                    .ExecuteCommandAsync();
                await db.Insertable(model.DeviceConfig).ExecuteCommandAsync();
                await db.Deleteable<EdgeLocalMappingRuleEntity>().ExecuteCommandAsync();
                if (model.Mappings.Count > 0)
                    await db.Insertable(model.Mappings).ExecuteCommandAsync();
            }

            if (model.RuleVersion > 0 && model.RuleVersionSnapshot != null)
            {
                await db.Deleteable<EdgeLocalRuleVersionEntity>()
                    .Where(x => x.Version == model.RuleVersion)
                    .ExecuteCommandAsync();
                await db.Insertable(model.RuleVersionSnapshot).ExecuteCommandAsync();
                await db.Deleteable<EdgeLocalRuleEntity>().ExecuteCommandAsync();
                if (model.Rules.Count > 0)
                    await db.Insertable(model.Rules).ExecuteCommandAsync();
            }
        }, "保存边缘配置事务失败", cancellationToken);

    /// <summary>在消息所属日期的数据库文件中更新队列状态并记录上传结果。</summary>
    public async Task CompleteUploadAttemptAsync(
        EdgeLocalUploadAttemptWriteModel model,
        CancellationToken cancellationToken)
    {
        var databaseDay = model.QueueCreateTime.Date;
        var db = GetDailyDatabase(databaseDay);
        await ExecuteTransactionAsync(db, async transactionDb =>
        {
            if (model.Success)
            {
                await transactionDb.Updateable<EdgeLocalMessageQueueEntity>()
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
                await transactionDb.Updateable<EdgeLocalMessageQueueEntity>()
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
            await transactionDb.Insertable(model.UploadRecord).ExecuteCommandAsync();
        }, "更新边缘队列事务失败", cancellationToken);

        if (model.Success)
        {
            await _accessGate.WaitAsync(cancellationToken);
            try
            {
                await MarkDayCompletedIfReadyAsync(databaseDay, cancellationToken);
            }
            finally
            {
                _accessGate.Release();
            }
        }
    }

    private async Task<List<EdgeLocalMessageQueueEntity>> GetDueMessagesFromActiveDaysAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken)
    {
        await CleanupExpiredDatabaseFilesIfDueAsync(now, cancellationToken);
        var records = new List<EdgeLocalMessageQueueEntity>();
        foreach (var day in await GetReadableDaysAsync(now, cancellationToken))
        {
            var remaining = Math.Clamp(limit, 1, 500) - records.Count;
            if (remaining <= 0) break;
            var dayRecords = await GetDailyDatabase(day).Queryable<EdgeLocalMessageQueueEntity>()
                .Where(x => (x.Status == "Pending" || x.Status == "Failed") &&
                            (x.NextRetryTime == null || x.NextRetryTime <= now))
                .OrderBy(x => x.CreateTime)
                .Take(remaining)
                .ToListAsync();
            records.AddRange(dayRecords);
        }
        return records.OrderBy(x => x.CreateTime).Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    /// <summary>昨天无待传数据时写入完成标记；已完成则不进入读取范围。</summary>
    private async Task<List<DateTime>> GetReadableDaysAsync(DateTime now, CancellationToken cancellationToken)
    {
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var days = new List<DateTime>();
        if (DailyDatabaseFileExists(yesterday) &&
            !await MarkDayCompletedIfReadyAsync(yesterday, cancellationToken))
            days.Add(yesterday);
        days.Add(today);
        return days;
    }

    /// <summary>当一个历史日数据库中的所有消息均已上传时写入完成标记。</summary>
    private async Task<bool> MarkDayCompletedIfReadyAsync(DateTime day, CancellationToken cancellationToken)
    {
        if (day.Date >= DateTime.UtcNow.Date || !DailyDatabaseFileExists(day)) return false;
        var db = GetDailyDatabase(day);
        var status = await db.Queryable<EdgeLocalQueueDayStatusEntity>()
            .Where(x => x.DayKey == GetDayKey(day))
            .FirstAsync();
        if (status?.UploadCompleted == true) return true;

        cancellationToken.ThrowIfCancellationRequested();
        var hasUnfinished = await db.Queryable<EdgeLocalMessageQueueEntity>()
            .AnyAsync(x => x.Status == "Pending" || x.Status == "Failed");
        if (hasUnfinished) return false;

        var now = DateTime.UtcNow;
        if (status == null)
        {
            await db.Insertable(new EdgeLocalQueueDayStatusEntity
            {
                DayKey = GetDayKey(day),
                UploadCompleted = true,
                CompletedTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
        }
        else
        {
            await db.Updateable<EdgeLocalQueueDayStatusEntity>()
                .SetColumns(x => new EdgeLocalQueueDayStatusEntity
                {
                    UploadCompleted = true,
                    CompletedTime = now,
                    UpdateTime = now
                })
                .Where(x => x.DayKey == status.DayKey)
                .ExecuteCommandAsync();
        }
        return true;
    }

    /// <summary>每天检查一次：超过四个工作日的整份日库直接删除，不检查上传状态。</summary>
    private Task CleanupExpiredDatabaseFilesIfDueAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var today = now.Date;
        if (_lastCleanupDate == today) return Task.CompletedTask;

        var oldestRetainedDate = EdgeLocalRetentionPolicy.GetOldestRetainedDate(today);
        foreach (var day in GetDailyDatabaseDays())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (day.Date >= oldestRetainedDate)
                continue;
            CloseDailyDatabase(day);
            DeleteDailyDatabaseFile(day);
            _logger?.LogWarning(
                "Deleted local EdgeGateway daily database {FileName}: older than {RetentionWorkdays} workdays (UTC). Upload status was not checked; pending data is permanently removed.",
                Path.GetFileName(GetDailyDatabasePath(day)), EdgeLocalRetentionPolicy.RetentionWorkdays);
        }
        _lastCleanupDate = today;
        return Task.CompletedTask;
    }

    /// <summary>首次升级时将旧单文件中今天、昨天未上传的数据复制到对应日文件。</summary>
    private async Task MigrateLegacyPendingMessagesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var todayDb = GetDailyDatabase(now);
        if (await todayDb.Queryable<EdgeLocalStorageMigrationEntity>()
                .Where(x => x.MigrationKey == LegacyMigrationKey)
                .AnyAsync() ||
            !_legacyDb.DbMaintenance.IsAnyTable("message_queue", false))
            return;

        var start = now.Date.AddDays(-1);
        var legacyMessages = await _legacyDb.Queryable<EdgeLocalMessageQueueEntity>()
            .Where(x => (x.Status == "Pending" || x.Status == "Failed") && x.CreateTime >= start)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        foreach (var message in legacyMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetDailyDatabase(message.CreateTime);
            if (await target.Queryable<EdgeLocalMessageQueueEntity>()
                    .AnyAsync(x => x.MessageId == message.MessageId))
                continue;
            message.Id = 0;
            await target.Insertable(message).ExecuteCommandAsync();
        }
        await todayDb.Insertable(new EdgeLocalStorageMigrationEntity
        {
            MigrationKey = LegacyMigrationKey,
            CompletedTime = DateTime.UtcNow
        }).ExecuteCommandAsync();
    }

    /// <summary>创建或获取指定日期的 SQLite 文件，文件内部表名保持原来的固定名称。</summary>
    private SqlSugarClient GetDailyDatabase(DateTime day)
    {
        var path = GetDailyDatabasePath(day);
        if (_dailyDatabases.TryGetValue(path, out var existing)) return existing;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        });
        db.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
        db.Ado.ExecuteCommand("PRAGMA synchronous=NORMAL;");
        db.Ado.ExecuteCommand("PRAGMA busy_timeout=10000;");
        db.Ado.ExecuteCommand("PRAGMA foreign_keys=ON;");
        db.CodeFirst.InitTables<EdgeLocalDeviceConfigEntity, EdgeLocalSourceConfigEntity,
            EdgeLocalMappingRuleEntity, EdgeLocalRuleEntity, EdgeLocalRuleVersionEntity>();
        db.CodeFirst.InitTables<EdgeLocalMessageQueueEntity, EdgeLocalUploadRecordEntity,
            EdgeLocalSystemEventEntity, EdgeLocalQueueDayStatusEntity, EdgeLocalStorageMigrationEntity>();
        db.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_source_config_source_id ON source_config(source_id);");
        db.Ado.ExecuteCommand("CREATE UNIQUE INDEX IF NOT EXISTS ux_message_queue_message_id ON message_queue(message_id);");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_queue_due ON message_queue(status, next_retry_time, create_time);");
        _dailyDatabases[path] = db;
        return db;
    }

    private string GetDailyDatabasePath(DateTime day)
    {
        var configuredPath = Path.GetFullPath(_bootstrapOptions.DatabasePath, AppContext.BaseDirectory);
        var directory = Path.GetDirectoryName(configuredPath) ?? AppContext.BaseDirectory;
        var name = Path.GetFileNameWithoutExtension(configuredPath);
        var extension = Path.GetExtension(configuredPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".db";
        return Path.Combine(directory, $"{name}-{day:yyyyMMdd}{extension}");
    }

    private bool DailyDatabaseFileExists(DateTime day) => File.Exists(GetDailyDatabasePath(day));

    private IEnumerable<DateTime> GetDailyDatabaseDays()
    {
        var todayPath = GetDailyDatabasePath(DateTime.UtcNow);
        var directory = Path.GetDirectoryName(todayPath);
        var extension = Path.GetExtension(todayPath);
        var configuredPath = Path.GetFullPath(_bootstrapOptions.DatabasePath, AppContext.BaseDirectory);
        var namePrefix = Path.GetFileNameWithoutExtension(configuredPath) + "-";
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;

        foreach (var file in Directory.EnumerateFiles(directory, $"*{extension}"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var dateText = name[namePrefix.Length..];
            if (dateText.Length == 8 && DateTime.TryParseExact(dateText, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return day.Date;
        }
    }

    private void CloseDailyDatabase(DateTime day)
    {
        var path = GetDailyDatabasePath(day);
        if (!_dailyDatabases.Remove(path, out var db)) return;
        db.Dispose();
    }

    private void DeleteDailyDatabaseFile(DateTime day)
    {
        var path = GetDailyDatabasePath(day);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }

    private static string GetDayKey(DateTime day) => day.ToString("yyyyMMdd");

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
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

    private Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await action();
            return true;
        }, cancellationToken);

    private async Task ExecuteTransactionAsync(
        SqlSugarClient db,
        Func<SqlSugarClient, Task> action,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await _accessGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = await db.Ado.UseTranAsync(() => action(db));
            if (!transaction.IsSuccess)
                throw transaction.ErrorException ?? new InvalidOperationException(errorMessage);
        }
        finally
        {
            _accessGate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("EdgeGateway 本地 SQLite 尚未初始化");
    }
}
