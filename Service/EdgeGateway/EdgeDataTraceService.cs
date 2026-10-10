using Interface;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;
using Newtonsoft.Json;
using SqlSugar;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Service;

/// <summary>
/// 管理中转数据追溯规则、参数历史点、曲线查询和大数据导出。
/// 配置从主库读取并短时缓存，历史点始终写入独立日志库。
/// </summary>
public sealed class EdgeDataTraceService : IEdgeDataTraceService
{
    private const string SettingCacheKey = "edge-data-trace-matcher";
    private const string SettingValueCacheKey = "edge-data-trace-setting";
    private const int MinimumRetentionHours = 1;
    private const int MaximumRetentionHours = 168;
    private const int DeleteBatchSize = 5000;
    private const int ExportBatchSize = 5000;
    private const int ExportCsvRowsPerFile = 200000;
    private readonly SqlSugarScope _dbScope;
    private ISqlSugarClient _db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");
    private ISqlSugarClient _logDb => DatabaseOperationScope.GetConnection(_dbScope, "edge-gateway-logs");
    private readonly IMemoryCache _cache;
    private readonly ILogger<EdgeDataTraceService> _logger;

    public EdgeDataTraceService(
        SqlSugarScope dbScope,
        IMemoryCache cache,
        ILogger<EdgeDataTraceService> logger)
    {
        _dbScope = dbScope;
        _cache = cache;
        _logger = logger;
    }

    public async Task<EdgeDataTraceSettingDto> GetSettingAsync() => ToDto(await GetOrCreateSettingAsync());

    public async Task<EdgeDataTraceSettingDto> SaveSettingAsync(EdgeDataTraceSettingSaveDto dto, string? operatorName)
    {
        if (dto.RetentionHours is < MinimumRetentionHours or > MaximumRetentionHours)
            throw new InvalidOperationException($"追溯保留时长必须在 {MinimumRetentionHours}～{MaximumRetentionHours} 小时之间");

        var setting = await GetOrCreateSettingAsync();
        setting.RetentionHours = dto.RetentionHours;
        setting.Enabled = dto.Enabled;
        setting.UpdateBy = TrimToNull(operatorName, 64);
        setting.UpdateTime = DateTime.Now;
        using (var db = _db.CopyNew())
        {
            await db.Updateable(setting).ExecuteCommandAsync();
        }
        _cache.Remove(SettingValueCacheKey);
        await AddOperationAsync("DataTrace.Setting.Update", "Setting", setting.UpdateBy, dto);
        return ToDto(setting);
    }

    public async Task<List<EdgeDataTraceRuleDto>> GetRulesAsync()
    {
        var rules = await _db.Queryable<EdgeDataTraceRuleEntity>().OrderBy(x => x.Id).ToListAsync();
        return await BuildRuleDtosAsync(rules);
    }

    public async Task<EdgeDataTraceRuleDto> SaveRuleAsync(EdgeDataTraceRuleSaveDto dto, string? operatorName)
    {
        ValidateRule(dto);
        var device = await _db.Queryable<EdgeDeviceEntity>()
            .FirstAsync(x => x.Id == dto.EdgeDeviceId && x.IsDelete == 0)
            ?? throw new InvalidOperationException("边缘设备不存在或已删除");
        var duplicate = await _db.Queryable<EdgeDataTraceRuleEntity>().AnyAsync(x =>
            x.Id != dto.Id && x.EdgeDeviceId == dto.EdgeDeviceId && x.SourceId == dto.SourceId.Trim() &&
            x.SourceDeviceCode == dto.SourceDeviceCode.Trim());
        if (duplicate) throw new InvalidOperationException("该边缘设备、数据源和来源业务设备已存在追溯规则");

        var now = DateTime.Now;
        EdgeDataTraceRuleEntity entity;
        if (dto.Id > 0)
        {
            entity = await _db.Queryable<EdgeDataTraceRuleEntity>().FirstAsync(x => x.Id == dto.Id)
                ?? throw new InvalidOperationException("追溯规则不存在");
            entity.RuleName = dto.RuleName.Trim();
            entity.SourceType = "WebSocket";
            entity.EdgeDeviceId = dto.EdgeDeviceId;
            entity.EdgeDeviceCode = device.DeviceCode;
            entity.SourceId = dto.SourceId.Trim();
            entity.SourceDeviceCode = dto.SourceDeviceCode.Trim();
            entity.SourceDeviceName = TrimToNull(dto.SourceDeviceName, 128);
            entity.Enabled = dto.Enabled;
            entity.Remark = TrimToNull(dto.Remark, 256);
            entity.UpdateBy = TrimToNull(operatorName, 64);
            entity.UpdateTime = now;
            await _db.Updateable(entity).ExecuteCommandAsync();
        }
        else
        {
            entity = new EdgeDataTraceRuleEntity
            {
                RuleName = dto.RuleName.Trim(),
                SourceType = "WebSocket",
                EdgeDeviceId = dto.EdgeDeviceId,
                EdgeDeviceCode = device.DeviceCode,
                SourceId = dto.SourceId.Trim(),
                SourceDeviceCode = dto.SourceDeviceCode.Trim(),
                SourceDeviceName = TrimToNull(dto.SourceDeviceName, 128),
                Enabled = dto.Enabled,
                Remark = TrimToNull(dto.Remark, 256),
                CreateBy = TrimToNull(operatorName, 64),
                UpdateBy = TrimToNull(operatorName, 64),
                CreateTime = now,
                UpdateTime = now
            };
            entity.Id = await _db.Insertable(entity).ExecuteReturnIdentityAsync();
        }

        await SaveFieldsAsync(entity.Id, dto.Fields);
        InvalidateMatcherCache();
        await AddOperationAsync(dto.Id > 0 ? "DataTrace.Rule.Update" : "DataTrace.Rule.Create", entity.Id.ToString(), operatorName, dto);
        return (await BuildRuleDtosAsync(new List<EdgeDataTraceRuleEntity> { entity })).Single();
    }

    public async Task<bool> DeleteRuleAsync(int id, string? operatorName)
    {
        var fields = await _db.Queryable<EdgeDataTraceFieldEntity>().Where(x => x.TraceRuleId == id).ToListAsync();
        if (fields.Count > 0)
        {
            var fieldIds = fields.Select(x => x.Id).ToList();
            await _logDb.Deleteable<EdgeDataTraceLatestEntity>().In(fieldIds).ExecuteCommandAsync();
            await _db.Deleteable<EdgeDataTraceFieldEntity>().Where(x => x.TraceRuleId == id).ExecuteCommandAsync();
        }
        var deleted = await _db.Deleteable<EdgeDataTraceRuleEntity>().Where(x => x.Id == id).ExecuteCommandAsync() > 0;
        if (deleted)
        {
            InvalidateMatcherCache();
            await AddOperationAsync("DataTrace.Rule.Delete", id.ToString(), operatorName, new { id });
        }
        return deleted;
    }

    public async Task<List<EdgeDataTraceTreeNodeDto>> GetTreeAsync()
    {
        var rules = await _db.Queryable<EdgeDataTraceRuleEntity>().Where(x => x.Enabled).ToListAsync();
        var fields = rules.Count == 0 ? new List<EdgeDataTraceFieldEntity>() : await _db.Queryable<EdgeDataTraceFieldEntity>()
            .Where(x => rules.Select(rule => rule.Id).Contains(x.TraceRuleId) && x.Enabled)
            .OrderBy(x => x.Sort).ToListAsync();
        var fieldIds = fields.Select(x => x.Id).ToList();
        var latest = fieldIds.Count == 0 ? new List<EdgeDataTraceLatestEntity>() : await _logDb.Queryable<EdgeDataTraceLatestEntity>()
            .Where(x => fieldIds.Contains(x.TraceFieldId)).ToListAsync();
        var latestByField = latest.ToDictionary(x => x.TraceFieldId);
        var devices = await _db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceNames = devices.ToDictionary(x => x.Id, x => x.DeviceName);

        return rules.GroupBy(x => new { x.EdgeDeviceId, x.EdgeDeviceCode }).OrderBy(x => x.Key.EdgeDeviceCode).Select(deviceGroup =>
        {
            var device = new EdgeDataTraceTreeNodeDto
            {
                Key = $"device:{deviceGroup.Key.EdgeDeviceId}", Label = deviceNames.GetValueOrDefault(deviceGroup.Key.EdgeDeviceId, deviceGroup.Key.EdgeDeviceCode),
                NodeType = "EdgeDevice", EdgeDeviceCode = deviceGroup.Key.EdgeDeviceCode
            };
            foreach (var sourceGroup in deviceGroup.GroupBy(x => x.SourceId).OrderBy(x => x.Key))
            {
                var source = new EdgeDataTraceTreeNodeDto { Key = $"source:{deviceGroup.Key.EdgeDeviceId}:{sourceGroup.Key}", Label = sourceGroup.Key, NodeType = "Source", EdgeDeviceCode = deviceGroup.Key.EdgeDeviceCode, SourceId = sourceGroup.Key };
                foreach (var rule in sourceGroup.OrderBy(x => x.SourceDeviceCode))
                {
                    var businessDevice = new EdgeDataTraceTreeNodeDto { Key = $"business:{rule.Id}", Label = string.IsNullOrWhiteSpace(rule.SourceDeviceName) ? rule.SourceDeviceCode : $"{rule.SourceDeviceName} ({rule.SourceDeviceCode})", NodeType = "SourceDevice", EdgeDeviceCode = rule.EdgeDeviceCode, SourceId = rule.SourceId, SourceDeviceCode = rule.SourceDeviceCode };
                    foreach (var field in fields.Where(x => x.TraceRuleId == rule.Id))
                    {
                        latestByField.TryGetValue(field.Id, out var value);
                        businessDevice.Children.Add(new EdgeDataTraceTreeNodeDto
                        {
                            Key = $"field:{field.Id}", Label = field.DisplayName, NodeType = "Field", TraceFieldId = field.Id,
                            EdgeDeviceCode = rule.EdgeDeviceCode, SourceId = rule.SourceId, SourceDeviceCode = rule.SourceDeviceCode,
                            FieldCode = field.FieldCode, Unit = field.Unit, ValueType = field.ValueType,
                            LatestValue = value == null ? null : FormatValue(value.NumericValue, value.TextValue, field.DecimalPlaces),
                            LastReceiveTime = value?.CreateTime
                        });
                    }
                    source.Children.Add(businessDevice);
                }
                device.Children.Add(source);
            }
            return device;
        }).ToList();
    }

    public async Task<List<EdgeDataTraceTrendSeriesDto>> GetTrendAsync(EdgeDataTraceTrendQueryDto query)
    {
        var (start, end) = await NormalizeRangeAsync(query.StartTime, query.EndTime);
        var ids = query.TraceFieldIds.Distinct().Take(10).ToList();
        if (ids.Count == 0) return new();
        var maximum = Math.Clamp(query.MaxPointsPerSeries, 100, 2000);
        var fields = await _db.Queryable<EdgeDataTraceFieldEntity>().Where(x => ids.Contains(x.Id)).ToListAsync();
        // 不把高频原始点全部读到内存。SQL Server 先在每个参数序列内均匀抽取，
        // 随后前端放大局部时间范围时再以更高精度请求该范围。
        var all = await QueryTrendPointsAsync(ids, start, end, maximum);
        return all.GroupBy(x => x.TraceFieldId).Select(group =>
        {
            var field = fields.FirstOrDefault(x => x.Id == group.Key);
            var points = group.Select(x => new EdgeDataTraceTrendPointDto { Time = x.MessageTime == default ? x.CreateTime : x.MessageTime, NumericValue = x.NumericValue, TextValue = x.TextValue }).ToList();
            return new EdgeDataTraceTrendSeriesDto
            {
                TraceFieldId = group.Key,
                Name = field?.DisplayName ?? group.First().DisplayName,
                Unit = field?.Unit ?? group.First().Unit,
                ValueType = field?.ValueType ?? group.First().ValueType,
                Points = Downsample(points, maximum)
            };
        }).ToList();
    }

    public async Task<EdgeDataTraceDetailPageDto> GetDetailAsync(EdgeDataTraceDetailQueryDto query)
    {
        var (start, end) = await NormalizeRangeAsync(query.StartTime, query.EndTime);
        var ids = query.TraceFieldIds.Distinct().Take(100).ToList();
        if (ids.Count == 0) return new();
        var size = Math.Clamp(query.Size, 20, 500);
        var points = _logDb.Queryable<EdgeDataTracePointEntity>()
            .Where(x => ids.Contains(x.TraceFieldId) && x.CreateTime >= start && x.CreateTime <= end);
        if (query.CursorTime.HasValue && query.CursorId.HasValue)
        {
            var cursorTime = query.CursorTime.Value;
            var cursorId = query.CursorId.Value;
            points = points.Where(x => x.CreateTime < cursorTime || (x.CreateTime == cursorTime && x.Id < cursorId));
        }
        var result = await points.OrderByDescending(x => x.CreateTime).OrderByDescending(x => x.Id).Take(size + 1).ToListAsync();
        var hasMore = result.Count > size;
        var page = result.Take(size).Select(ToPointDto).ToList();
        var last = page.LastOrDefault();
        return new EdgeDataTraceDetailPageDto { Records = page, HasMore = hasMore, NextCursorTime = last?.CreateTime, NextCursorId = last?.Id };
    }

    public async Task<List<EdgeDataTraceSummaryDto>> GetSummariesAsync(EdgeDataTraceDetailQueryDto query)
    {
        var (start, end) = await NormalizeRangeAsync(query.StartTime, query.EndTime);
        var fieldIds = query.TraceFieldIds.Distinct().Where(x => x > 0).Take(100).ToList();
        if (fieldIds.Count == 0) return new();

        // 聚合与最新点均在 SQL Server 完成，避免在长时间范围内将所有原始点读取到应用内存。
        var idList = string.Join(',', fieldIds);
        const string aggregateSql = """
            SELECT TraceFieldId,
                   COUNT_BIG(1) AS PointCount,
                   MIN(NumericValue) AS MinValue,
                   MAX(NumericValue) AS MaxValue,
                   AVG(NumericValue) AS AverageValue
            FROM dbo.EdgeGatewayDataTracePoint
            WHERE TraceFieldId IN ({0})
              AND CreateTime >= @traceStart AND CreateTime <= @traceEnd
            GROUP BY TraceFieldId;
            """;
        const string latestSql = """
            WITH LatestPoint AS
            (
                SELECT TraceFieldId, NumericValue, CreateTime,
                       ROW_NUMBER() OVER (PARTITION BY TraceFieldId ORDER BY CreateTime DESC, Id DESC) AS RowNumber
                FROM dbo.EdgeGatewayDataTracePoint
                WHERE TraceFieldId IN ({0})
                  AND CreateTime >= @traceStart AND CreateTime <= @traceEnd
            )
            SELECT TraceFieldId, NumericValue AS LatestValue, CreateTime AS LastReceiveTime
            FROM LatestPoint
            WHERE RowNumber = 1;
            """;
        var parameters = new[] { new SugarParameter("@traceStart", start), new SugarParameter("@traceEnd", end) };
        var aggregates = _logDb.Ado.SqlQuery<DataTraceSummaryAggregate>(string.Format(aggregateSql, idList), parameters);
        var latest = _logDb.Ado.SqlQuery<DataTraceSummaryLatest>(string.Format(latestSql, idList), parameters)
            .ToDictionary(x => x.TraceFieldId);
        var aggregateByField = aggregates.ToDictionary(x => x.TraceFieldId);
        return fieldIds.Select(fieldId =>
        {
            aggregateByField.TryGetValue(fieldId, out var aggregate);
            latest.TryGetValue(fieldId, out var current);
            return new EdgeDataTraceSummaryDto
            {
                TraceFieldId = fieldId,
                PointCount = aggregate?.PointCount ?? 0,
                MinValue = aggregate?.MinValue,
                MaxValue = aggregate?.MaxValue,
                AverageValue = aggregate?.AverageValue,
                LatestValue = current?.LatestValue,
                LastReceiveTime = current?.LastReceiveTime
            };
        }).ToList();
    }

    public async Task<EdgeDataTraceExportTaskDto> CreateExportTaskAsync(EdgeDataTraceExportRequestDto dto, string? operatorName)
    {
        var (start, end) = await NormalizeRangeAsync(dto.StartTime, dto.EndTime);
        if (dto.TraceFieldIds.Distinct().Count() == 0) throw new InvalidOperationException("请选择至少一个要导出的参数");
        var task = new EdgeDataTraceExportTaskEntity { RequestJson = JsonConvert.SerializeObject(new EdgeDataTraceExportRequestDto { TraceFieldIds = dto.TraceFieldIds.Distinct().Take(100).ToList(), StartTime = start, EndTime = end }), CreatedBy = TrimToNull(operatorName, 64) };
        task.Id = await _logDb.Insertable(task).ExecuteReturnBigIdentityAsync();
        return ToTaskDto(task);
    }

    public async Task<EdgeDataTraceExportTaskDto?> GetExportTaskAsync(long id)
    {
        var task = await _logDb.Queryable<EdgeDataTraceExportTaskEntity>().FirstAsync(x => x.Id == id);
        return task == null ? null : ToTaskDto(task);
    }

    public async Task<EdgeDataTraceExportFileDto?> OpenExportFileAsync(long id, string? operatorName, bool isAdministrator)
    {
        var task = await _logDb.Queryable<EdgeDataTraceExportTaskEntity>().FirstAsync(x => x.Id == id);
        if (task == null || task.Status != "Completed" || task.ExpireTime <= DateTime.Now || string.IsNullOrWhiteSpace(task.FilePath)) return null;
        if (!isAdministrator && !string.Equals(task.CreatedBy, TrimToNull(operatorName, 64), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能下载本人创建的导出文件");
        if (!File.Exists(task.FilePath)) throw new InvalidOperationException("导出文件已不存在，请重新导出");
        return new EdgeDataTraceExportFileDto { FileName = task.FileName ?? $"数据追溯_{id}.csv", Content = new FileStream(task.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan) };
    }

    public async Task ProcessPendingExportsAsync(CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        using var logDb = _logDb.CopyNew();
        var task = await logDb.Queryable<EdgeDataTraceExportTaskEntity>().Where(x => x.Status == "Pending").OrderBy(x => x.CreateTime).FirstAsync();
        if (task == null) return;
        task.Status = "Processing";
        await logDb.Updateable(task).UpdateColumns(x => new { x.Status }).ExecuteCommandAsync();
        string? filePath = null;
        try
        {
            var request = JsonConvert.DeserializeObject<EdgeDataTraceExportRequestDto>(task.RequestJson) ?? throw new InvalidOperationException("导出任务参数无效");
            var directory = Path.Combine(AppContext.BaseDirectory, "data", "edge-gateway-exports");
            Directory.CreateDirectory(directory);
            var ids = request.TraceFieldIds.Distinct().ToList();
            var total = await logDb.Queryable<EdgeDataTracePointEntity>().Where(x => ids.Contains(x.TraceFieldId) && x.CreateTime >= request.StartTime && x.CreateTime <= request.EndTime).CountAsync();
            task.TotalCount = total;
            var useZip = total > ExportCsvRowsPerFile;
            var fileName = $"数据追溯_{task.Id}_{DateTime.Now:yyyyMMddHHmmss}.{(useZip ? "zip" : "csv")}";
            filePath = Path.Combine(directory, fileName);

            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            ZipArchive? archive = useZip ? new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true) : null;
            StreamWriter? writer = null;
            var csvPart = 0;
            var rowsInCurrentCsv = 0;
            try
            {
                writer = CreateExportWriter(stream, archive, task.Id, ++csvPart);
                await WriteExportHeaderAsync(writer);

                long? cursorId = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var page = logDb.Queryable<EdgeDataTracePointEntity>()
                        .Where(x => ids.Contains(x.TraceFieldId) && x.CreateTime >= request.StartTime && x.CreateTime <= request.EndTime)
                        .WhereIF(cursorId.HasValue, x => x.Id > cursorId!.Value)
                        .OrderBy(x => x.Id)
                        .Take(ExportBatchSize);
                    var records = await page.ToListAsync();
                    if (records.Count == 0) break;
                    foreach (var point in records)
                    {
                        if (rowsInCurrentCsv >= ExportCsvRowsPerFile)
                        {
                            await writer.DisposeAsync();
                            writer = CreateExportWriter(stream, archive, task.Id, ++csvPart);
                            await WriteExportHeaderAsync(writer);
                            rowsInCurrentCsv = 0;
                        }
                        var value = point.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? point.TextValue ?? string.Empty;
                        await writer.WriteLineAsync(string.Join(',', Csv(point.MessageTime), Csv(point.CreateTime), Csv(point.EdgeDeviceCode), Csv(point.SourceId), Csv(point.SourceDeviceCode), Csv(point.FieldCode), Csv(point.DisplayName), Csv(value), Csv(point.Unit), Csv(point.MessageId)));
                        rowsInCurrentCsv++;
                    }
                    cursorId = records[^1].Id;
                    task.ProcessedCount += records.Count;
                    await logDb.Updateable(task)
                        .UpdateColumns(x => new { x.ProcessedCount, x.TotalCount })
                        .ExecuteCommandAsync();
                }
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                if (writer != null) await writer.DisposeAsync();
                archive?.Dispose();
            }
            await stream.FlushAsync(cancellationToken);
            task.Status = cancellationToken.IsCancellationRequested ? "Failed" : "Completed";
            task.FileName = cancellationToken.IsCancellationRequested ? null : fileName;
            task.FilePath = cancellationToken.IsCancellationRequested ? null : filePath;
            task.ErrorMessage = cancellationToken.IsCancellationRequested ? "导出任务已取消" : null;
            task.CompleteTime = DateTime.Now;
            task.ExpireTime = DateTime.Now.AddHours(24);
            await logDb.Updateable(task).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            task.Status = "Failed";
            task.ErrorMessage = TrimToNull(ex.Message, 1024);
            task.CompleteTime = DateTime.Now;
            await logDb.Updateable(task).ExecuteCommandAsync();
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)) File.Delete(filePath);
            _logger.LogError(ex, "Data trace export task {TaskId} failed.", task.Id);
        }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var setting = await GetOrCreateSettingAsync();
        var cutoff = DateTime.Now.AddHours(-setting.RetentionHours);
        var deleted = 0;
        using var logDb = _logDb.CopyNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var ids = await logDb.Queryable<EdgeDataTracePointEntity>().Where(x => x.CreateTime < cutoff).OrderBy(x => x.Id).Select(x => x.Id).Take(DeleteBatchSize).ToListAsync();
            if (ids.Count == 0) break;
            deleted += await logDb.Deleteable<EdgeDataTracePointEntity>().In(ids).ExecuteCommandAsync();
            if (ids.Count < DeleteBatchSize) break;
        }
        var expiredTasks = await logDb.Queryable<EdgeDataTraceExportTaskEntity>().Where(x => x.ExpireTime != null && x.ExpireTime < DateTime.Now).ToListAsync();
        foreach (var task in expiredTasks)
        {
            if (!string.IsNullOrWhiteSpace(task.FilePath) && File.Exists(task.FilePath)) File.Delete(task.FilePath);
        }
        if (expiredTasks.Count > 0) await logDb.Deleteable<EdgeDataTraceExportTaskEntity>().In(expiredTasks.Select(x => x.Id).ToList()).ExecuteCommandAsync();
        return deleted;
    }

    public async Task CaptureAsync(EdgeStandardMessageDto message, string edgeDeviceCode, CancellationToken cancellationToken)
    {
        var setting = await GetOrCreateSettingAsync();
        if (!setting.Enabled || string.IsNullOrWhiteSpace(message.SourceDeviceCode) || message.Values.Count == 0) return;
        var matcher = await GetMatcherAsync();
        var key = MatcherKey(edgeDeviceCode, message.SourceId, message.SourceDeviceCode);
        if (!matcher.TryGetValue(key, out var fields)) return;
        var now = DateTime.Now;
        var points = new List<EdgeDataTracePointEntity>();
        foreach (var field in fields)
        {
            if (!message.Values.TryGetValue(field.FieldCode, out var raw)) continue;
            var (numeric, text) = NormalizeValue(raw, field.ValueType);
            points.Add(new EdgeDataTracePointEntity { TraceFieldId = field.Id, EdgeDeviceCode = edgeDeviceCode, SourceId = message.SourceId, SourceDeviceCode = message.SourceDeviceCode!, FieldCode = field.FieldCode, DisplayName = field.DisplayName, ValueType = field.ValueType, Unit = field.Unit, NumericValue = numeric, TextValue = text, MessageId = message.MessageId, MessageTime = message.Timestamp, CreateTime = now });
        }
        if (points.Count == 0) return;
        try
        {
            using var logDb = _logDb.CopyNew();
            // 同一中心消息在日志库写入成功、但完成标记尚未来得及更新时可能被补偿任务再次处理。
            // 先排除已有点，既保证重试幂等，也能补齐某次部分写入后遗漏的参数。
            var traceFieldIds = points.Select(x => x.TraceFieldId).Distinct().ToList();
            var existingFieldIds = await logDb.Queryable<EdgeDataTracePointEntity>()
                .Where(x => x.MessageId == message.MessageId && traceFieldIds.Contains(x.TraceFieldId))
                .Select(x => x.TraceFieldId)
                .ToListAsync();
            var missingPoints = points.Where(x => !existingFieldIds.Contains(x.TraceFieldId)).ToList();
            if (missingPoints.Count > 0)
                await logDb.Insertable(missingPoints).ExecuteCommandAsync();
            foreach (var point in points)
            {
                var updated = await logDb.Updateable<EdgeDataTraceLatestEntity>().SetColumns(x => new EdgeDataTraceLatestEntity { NumericValue = point.NumericValue, TextValue = point.TextValue, MessageId = point.MessageId, MessageTime = point.MessageTime, CreateTime = point.CreateTime }).Where(x => x.TraceFieldId == point.TraceFieldId).ExecuteCommandAsync();
                if (updated == 0) await logDb.Insertable(new EdgeDataTraceLatestEntity { TraceFieldId = point.TraceFieldId, NumericValue = point.NumericValue, TextValue = point.TextValue, MessageId = point.MessageId, MessageTime = point.MessageTime, CreateTime = point.CreateTime }).ExecuteCommandAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Data trace capture failed for {DeviceCode}/{SourceId}/{SourceDeviceCode}.", edgeDeviceCode, message.SourceId, message.SourceDeviceCode);
            throw;
        }
    }

    private async Task<EdgeDataTraceSettingEntity> GetOrCreateSettingAsync()
    {
        if (_cache.TryGetValue(SettingValueCacheKey, out EdgeDataTraceSettingEntity? cached) && cached != null)
            return cached;
        using var db = _db.CopyNew();
        var setting = await db.Queryable<EdgeDataTraceSettingEntity>().FirstAsync(x => x.Id == 1);
        if (setting == null)
        {
            setting = new EdgeDataTraceSettingEntity();
            try { await db.Insertable(setting).ExecuteCommandAsync(); }
            catch (SqlSugarException) { setting = await db.Queryable<EdgeDataTraceSettingEntity>().FirstAsync(x => x.Id == 1) ?? setting; }
        }
        _cache.Set(SettingValueCacheKey, setting, TimeSpan.FromSeconds(60));
        return setting;
    }

    private async Task SaveFieldsAsync(int ruleId, List<EdgeDataTraceFieldSaveDto> dtoFields)
    {
        var existing = await _db.Queryable<EdgeDataTraceFieldEntity>().Where(x => x.TraceRuleId == ruleId).ToListAsync();
        var submittedIds = dtoFields.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
        var removed = existing.Where(x => !submittedIds.Contains(x.Id)).Select(x => x.Id).ToList();
        if (removed.Count > 0)
        {
            await _db.Deleteable<EdgeDataTraceFieldEntity>().In(removed).ExecuteCommandAsync();
            await _logDb.Deleteable<EdgeDataTraceLatestEntity>().In(removed).ExecuteCommandAsync();
        }
        foreach (var (field, index) in dtoFields.Select((value, index) => (value, index)))
        {
            var target = existing.FirstOrDefault(x => x.Id == field.Id);
            if (target == null)
            {
                await _db.Insertable(ToFieldEntity(field, ruleId, index)).ExecuteCommandAsync();
                continue;
            }
            target.FieldCode = field.FieldCode.Trim(); target.DisplayName = field.DisplayName.Trim(); target.ValueType = NormalizeValueType(field.ValueType); target.Unit = TrimToNull(field.Unit, 32); target.DecimalPlaces = Math.Clamp(field.DecimalPlaces, 0, 8); target.StateMappingJson = TrimToNull(field.StateMappingJson, 4000); target.Enabled = field.Enabled; target.Sort = index;
            await _db.Updateable(target).ExecuteCommandAsync();
        }
    }

    private async Task<Dictionary<string, List<EdgeDataTraceFieldEntity>>> GetMatcherAsync()
    {
        if (_cache.TryGetValue(SettingCacheKey, out Dictionary<string, List<EdgeDataTraceFieldEntity>>? cached) && cached != null) return cached;
        var rules = await _db.Queryable<EdgeDataTraceRuleEntity>().Where(x => x.Enabled).ToListAsync();
        var ruleIds = rules.Select(x => x.Id).ToList();
        var fields = ruleIds.Count == 0 ? new List<EdgeDataTraceFieldEntity>() : await _db.Queryable<EdgeDataTraceFieldEntity>().Where(x => ruleIds.Contains(x.TraceRuleId) && x.Enabled).ToListAsync();
        var matcher = rules.ToDictionary(x => MatcherKey(x.EdgeDeviceCode, x.SourceId, x.SourceDeviceCode), x => fields.Where(field => field.TraceRuleId == x.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        _cache.Set(SettingCacheKey, matcher, TimeSpan.FromSeconds(60));
        return matcher;
    }

    private async Task<(DateTime Start, DateTime End)> NormalizeRangeAsync(DateTime start, DateTime end)
    {
        if (start == default || end == default || start > end) throw new InvalidOperationException("查询时间范围无效");
        var oldest = DateTime.Now.AddHours(-(await GetOrCreateSettingAsync()).RetentionHours);
        return (start < oldest ? oldest : start, end > DateTime.Now ? DateTime.Now : end);
    }

    private async Task<List<EdgeDataTraceRuleDto>> BuildRuleDtosAsync(List<EdgeDataTraceRuleEntity> rules)
    {
        var ruleIds = rules.Select(x => x.Id).ToList();
        var fields = ruleIds.Count == 0 ? new List<EdgeDataTraceFieldEntity>() : await _db.Queryable<EdgeDataTraceFieldEntity>().Where(x => ruleIds.Contains(x.TraceRuleId)).OrderBy(x => x.Sort).ToListAsync();
        var devices = await _db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        return rules.Select(rule => new EdgeDataTraceRuleDto { Id = rule.Id, RuleName = rule.RuleName, SourceType = rule.SourceType, EdgeDeviceId = rule.EdgeDeviceId, EdgeDeviceCode = rule.EdgeDeviceCode, EdgeDeviceName = devices.FirstOrDefault(x => x.Id == rule.EdgeDeviceId)?.DeviceName, SourceId = rule.SourceId, SourceDeviceCode = rule.SourceDeviceCode, SourceDeviceName = rule.SourceDeviceName, Enabled = rule.Enabled, Remark = rule.Remark, CreateTime = rule.CreateTime, UpdateTime = rule.UpdateTime, Fields = fields.Where(x => x.TraceRuleId == rule.Id).Select(ToFieldDto).ToList() }).ToList();
    }

    private static EdgeDataTraceFieldEntity ToFieldEntity(EdgeDataTraceFieldSaveDto dto, int ruleId, int sort) => new() { TraceRuleId = ruleId, FieldCode = dto.FieldCode.Trim(), DisplayName = dto.DisplayName.Trim(), ValueType = NormalizeValueType(dto.ValueType), Unit = TrimToNull(dto.Unit, 32), DecimalPlaces = Math.Clamp(dto.DecimalPlaces, 0, 8), StateMappingJson = TrimToNull(dto.StateMappingJson, 4000), Enabled = dto.Enabled, Sort = sort };
    private Task<List<EdgeDataTracePointEntity>> QueryTrendPointsAsync(List<int> fieldIds, DateTime start, DateTime end, int maximum)
    {
        var idList = string.Join(',', fieldIds.Where(x => x > 0));
        var sql = $"""
            WITH TracePoints AS
            (
                SELECT Id, TraceFieldId, EdgeDeviceCode, SourceId, SourceDeviceCode, FieldCode,
                       DisplayName, ValueType, Unit, NumericValue, TextValue, MessageId, MessageTime, CreateTime,
                       ROW_NUMBER() OVER (PARTITION BY TraceFieldId ORDER BY CreateTime, Id) AS RowNumber,
                       COUNT(1) OVER (PARTITION BY TraceFieldId) AS TotalRows
                FROM dbo.EdgeGatewayDataTracePoint
                WHERE TraceFieldId IN ({idList})
                  AND CreateTime >= @traceStart AND CreateTime <= @traceEnd
            )
            SELECT Id, TraceFieldId, EdgeDeviceCode, SourceId, SourceDeviceCode, FieldCode,
                   DisplayName, ValueType, Unit, NumericValue, TextValue, MessageId, MessageTime, CreateTime
            FROM TracePoints
            WHERE TotalRows <= @traceMaximum
               OR RowNumber = 1
               OR RowNumber = TotalRows
               OR RowNumber % CEILING(CAST(TotalRows AS decimal(18, 4)) / @traceMaximum) = 0
            ORDER BY TraceFieldId, CreateTime, Id;
            """;
        return Task.FromResult(_logDb.Ado.SqlQuery<EdgeDataTracePointEntity>(sql,
            new SugarParameter("@traceStart", start), new SugarParameter("@traceEnd", end),
            new SugarParameter("@traceMaximum", maximum)));
    }
    private static EdgeDataTraceFieldSaveDto ToFieldDto(EdgeDataTraceFieldEntity entity) => new() { Id = entity.Id, FieldCode = entity.FieldCode, DisplayName = entity.DisplayName, ValueType = entity.ValueType, Unit = entity.Unit, DecimalPlaces = entity.DecimalPlaces, StateMappingJson = entity.StateMappingJson, Enabled = entity.Enabled, Sort = entity.Sort };
    private static EdgeDataTraceSettingDto ToDto(EdgeDataTraceSettingEntity setting) => new() { RetentionHours = setting.RetentionHours, Enabled = setting.Enabled, UpdateBy = setting.UpdateBy, UpdateTime = setting.UpdateTime };
    private static EdgeDataTracePointDto ToPointDto(EdgeDataTracePointEntity value) => new() { Id = value.Id, TraceFieldId = value.TraceFieldId, EdgeDeviceCode = value.EdgeDeviceCode, SourceId = value.SourceId, SourceDeviceCode = value.SourceDeviceCode, FieldCode = value.FieldCode, DisplayName = value.DisplayName, Unit = value.Unit, ValueType = value.ValueType, NumericValue = value.NumericValue, TextValue = value.TextValue, MessageId = value.MessageId, MessageTime = value.MessageTime, CreateTime = value.CreateTime };
    private static EdgeDataTraceExportTaskDto ToTaskDto(EdgeDataTraceExportTaskEntity task) => new() { Id = task.Id, Status = task.Status, ProcessedCount = task.ProcessedCount, TotalCount = task.TotalCount, FileName = task.FileName, ErrorMessage = task.ErrorMessage, CreateTime = task.CreateTime, CompleteTime = task.CompleteTime, ExpireTime = task.ExpireTime };
    private static string MatcherKey(string deviceCode, string sourceId, string sourceDeviceCode) => $"{deviceCode.Trim()}|{sourceId.Trim()}|{sourceDeviceCode.Trim()}".ToUpperInvariant();
    private void InvalidateMatcherCache() => _cache.Remove(SettingCacheKey);
    private static string NormalizeValueType(string? value) => value?.Trim().ToUpperInvariant() switch { "BOOLEAN" => "Boolean", "ENUM" => "Enum", "TEXT" or "STRING" => "Text", _ => "Number" };
    private static string? TrimToNull(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string FormatValue(decimal? numeric, string? text, int decimals) => numeric.HasValue ? numeric.Value.ToString($"F{Math.Clamp(decimals, 0, 8)}", CultureInfo.InvariantCulture) : text ?? "-";
    private static StreamWriter CreateExportWriter(Stream output, ZipArchive? archive, long taskId, int part)
    {
        var target = archive == null
            ? output
            : archive.CreateEntry($"数据追溯_{taskId}_{part:D3}.csv", CompressionLevel.Fastest).Open();
        return new StreamWriter(target, new UTF8Encoding(true), 65536, leaveOpen: archive == null);
    }
    private static Task WriteExportHeaderAsync(StreamWriter writer) => writer.WriteLineAsync("业务时间,中心接收时间,边缘设备,数据源,来源业务设备,参数编码,参数名称,参数值,单位,消息ID");
    private static string Csv(object? value) => $"\"{(value?.ToString() ?? string.Empty).Replace("\"", "\"\"")}\"";
    private static List<EdgeDataTraceTrendPointDto> Downsample(List<EdgeDataTraceTrendPointDto> points, int maximum)
    {
        if (points.Count <= maximum) return points;
        var step = (double)(points.Count - 1) / (maximum - 1);
        return Enumerable.Range(0, maximum).Select(index => points[(int)Math.Round(index * step)]).ToList();
    }
    private static (decimal? Numeric, string? Text) NormalizeValue(object? raw, string valueType)
    {
        if (raw == null) return (null, null);
        if (NormalizeValueType(valueType) == "Boolean" && bool.TryParse(raw.ToString(), out var boolean)) return (boolean ? 1m : 0m, boolean ? "true" : "false");
        if (decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var numeric)) return (numeric, null);
        return (null, TrimToNull(raw is string text ? text : JsonConvert.SerializeObject(raw), 1024));
    }
    private static void ValidateRule(EdgeDataTraceRuleSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.RuleName) || dto.EdgeDeviceId <= 0 || string.IsNullOrWhiteSpace(dto.SourceId) || string.IsNullOrWhiteSpace(dto.SourceDeviceCode)) throw new InvalidOperationException("规则名称、边缘设备、数据源和来源业务设备不能为空");
        if (dto.Fields.Count == 0) throw new InvalidOperationException("请至少选择一个追溯参数");
        if (dto.Fields.Any(x => string.IsNullOrWhiteSpace(x.FieldCode) || string.IsNullOrWhiteSpace(x.DisplayName))) throw new InvalidOperationException("追溯参数编码和名称不能为空");
        if (dto.Fields.GroupBy(x => x.FieldCode.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) throw new InvalidOperationException("同一规则中参数编码不能重复");
    }

    private sealed class DataTraceSummaryAggregate
    {
        public int TraceFieldId { get; set; }
        public long PointCount { get; set; }
        public decimal? MinValue { get; set; }
        public decimal? MaxValue { get; set; }
        public decimal? AverageValue { get; set; }
    }

    private sealed class DataTraceSummaryLatest
    {
        public int TraceFieldId { get; set; }
        public decimal? LatestValue { get; set; }
        public DateTime? LastReceiveTime { get; set; }
    }

    private Task AddOperationAsync(string type, string target, string? user, object data) => _db.Insertable(new EdgeOperationLogEntity { OperationType = type, Target = target, Operator = TrimToNull(user, 64), DetailJson = JsonConvert.SerializeObject(data), CreateTime = DateTime.Now }).ExecuteCommandAsync();
}
