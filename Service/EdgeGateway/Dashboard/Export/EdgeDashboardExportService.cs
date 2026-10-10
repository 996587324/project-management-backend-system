using System.Globalization;
using AutoMapper;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>实现看板日报手动流式下载、定时分线体生成和历史文件读取。</summary>
public sealed class EdgeDashboardExportService : IEdgeDashboardExportService
{
    private const int StreamBufferSize = 64 * 1024;
    private const string SpreadsheetContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly SemaphoreSlim ScheduledExportGate = new(1, 1);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");
    private static string ExportRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "App_Data",
        "EdgeDashboardExports"));

    /// <summary>通过 IOC 注入项目单例 SqlSugarScope 和 AutoMapper。</summary>
    public EdgeDashboardExportService(SqlSugarScope dbScope, IMapper mapper)
    {
        _dbScope = dbScope;
        _mapper = mapper;
    }

    /// <summary>读取导出配置，首次访问时创建默认关闭且导出前一天日报的配置。</summary>
    public async Task<EdgeDashboardExportSettingOutputDto> GetSettingAsync(string dashboardCode)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        var setting = await Db.Queryable<EdgeDashboardExportSettingEntity>()
            .FirstAsync(x => x.DashboardCode == dashboardCode);
        if (setting == null)
        {
            setting = new EdgeDashboardExportSettingEntity
            {
                DashboardCode = dashboardCode,
                Enabled = false,
                ExportTime = "00:10",
                ExportDayOffset = 1
            };
            setting.Id = await Db.Insertable(setting).ExecuteReturnBigIdentityAsync();
        }
        return BuildSettingOutput(setting, DateTime.Now);
    }

    /// <summary>保存数据库驱动的每日执行时间，后台服务下一轮扫描即使用新配置。</summary>
    public async Task<EdgeDashboardExportSettingOutputDto> SaveSettingAsync(
        EdgeDashboardExportSettingSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.ExportTime = NormalizeExportTime(dto.ExportTime);
        if (dto.ExportDayOffset is not (0 or 1))
            throw new InvalidOperationException("定时导出日期只能选择当天或前一天");
        dto.UpdatedBy = Normalize(dto.UpdatedBy);

        var entity = await Db.Queryable<EdgeDashboardExportSettingEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode);
        var now = DateTime.Now;
        if (entity == null)
        {
            entity = _mapper.Map<EdgeDashboardExportSettingEntity>(dto);
            entity.CreateTime = now;
            entity.UpdateTime = now;
            entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            var previousTime = entity.ExportTime;
            var previousOffset = entity.ExportDayOffset;
            _mapper.Map(dto, entity);
            // 修改执行时间或日期口径后允许当天按新配置重新执行一次。
            if (!string.Equals(previousTime, entity.ExportTime, StringComparison.Ordinal) ||
                previousOffset != entity.ExportDayOffset)
                entity.LastScheduledRunDate = null;
            entity.UpdateTime = now;
            await Db.Updateable(entity).ExecuteCommandAsync();
        }
        return BuildSettingOutput(entity, now);
    }

    /// <summary>生成当前线体日报临时文件，并返回关闭后自动删除的读取流。</summary>
    public async Task<EdgeDashboardExportStreamDto> ExportLineAsync(
        string dashboardCode,
        string lineCode,
        DateTime reportDate,
        CancellationToken cancellationToken = default)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        lineCode = Require(lineCode, "线体编码");
        reportDate = reportDate.Date;
        if (reportDate > DateTime.Today)
            throw new InvalidOperationException("不能导出未来日期的看板日报");

        var data = await BuildWorkbookDataAsync(
            dashboardCode,
            lineCode,
            reportDate,
            cancellationToken);
        var fileName = BuildFileName(data.Line, reportDate);
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"edge-dashboard-{Guid.NewGuid():N}.xlsx");
        try
        {
            await WriteWorkbookFileAsync(tempFilePath, data, cancellationToken);
            var content = new FileStream(
                tempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                StreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return new EdgeDashboardExportStreamDto
            {
                FileName = fileName,
                ContentType = SpreadsheetContentType,
                Content = content
            };
        }
        catch
        {
            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            throw;
        }
    }

    /// <summary>分页返回定时生成结果，文件内容和服务器路径不进入普通 JSON 响应。</summary>
    public async Task<EdgePagedResultDto<EdgeDashboardExportRecordOutputDto>> GetRecordsAsync(
        EdgeDashboardExportRecordQueryDto query)
    {
        var dashboardCode = Require(query.DashboardCode, "看板编码");
        var lineCode = Normalize(query.LineCode);
        var status = Normalize(query.Status);
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 100);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeDashboardExportRecordEntity>()
            .Where(x => x.DashboardCode == dashboardCode)
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(status != null, x => x.Status == status)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeDashboardExportRecordOutputDto>
        {
            Records = _mapper.Map<List<EdgeDashboardExportRecordOutputDto>>(records),
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>校验记录状态和相对路径后，以顺序读取流返回后台生成的日报文件。</summary>
    public async Task<EdgeDashboardExportStreamDto> OpenRecordAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = await Db.Queryable<EdgeDashboardExportRecordEntity>()
            .FirstAsync(x => x.Id == id);
        if (record == null) throw new InvalidOperationException("导出记录不存在");
        if (!string.Equals(record.Status, "Success", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.RelativePath))
            throw new InvalidOperationException("该日报尚未生成成功，暂时不能下载");

        var fullPath = ResolveStoredFilePath(record.RelativePath);
        if (!File.Exists(fullPath)) throw new InvalidOperationException("服务器上的日报文件不存在");
        var content = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new EdgeDashboardExportStreamDto
        {
            FileName = record.FileName,
            ContentType = SpreadsheetContentType,
            Content = content
        };
    }

    /// <summary>扫描所有到期配置，同一天内每条启用线体只保留一份成功日报。</summary>
    public async Task<int> RunScheduledExportsAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        if (!await ScheduledExportGate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            using var db = _dbScope.GetConnection("calmadmin").CopyNew();
            var generatedCount = 0;
            var settings = await db.Queryable<EdgeDashboardExportSettingEntity>()
                .Where(x => x.Enabled)
                .ToListAsync();
            foreach (var setting in settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSettingDue(setting, now)) continue;
                generatedCount += await RunSettingAsync(db, setting, now, cancellationToken);
            }
            return generatedCount;
        }
        finally
        {
            ScheduledExportGate.Release();
        }
    }

    /// <summary>顺序处理一个看板下的启用线体，失败记录五分钟后允许再次尝试。</summary>
    private async Task<int> RunSettingAsync(
        ISqlSugarClient db,
        EdgeDashboardExportSettingEntity setting,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var reportDate = now.Date.AddDays(-setting.ExportDayOffset);
        var lines = await db.Queryable<EdgeDashboardLineConfigEntity>()
            .Where(x => x.DashboardCode == setting.DashboardCode &&
                        x.IsDelete == 0 && x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var generatedCount = 0;
        var allCompleted = true;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await db.Queryable<EdgeDashboardExportRecordEntity>()
                .Where(x => x.DashboardCode == setting.DashboardCode &&
                            x.LineCode == line.LineCode &&
                            x.ReportDate == reportDate &&
                            x.TriggerType == "Scheduled")
                .OrderByDescending(x => x.CreateTime)
                .FirstAsync();
            if (existing != null && existing.Status == "Success") continue;
            if (existing != null && existing.UpdateTime > now.AddMinutes(-5))
            {
                allCompleted = false;
                continue;
            }

            var record = existing ?? new EdgeDashboardExportRecordEntity
            {
                DashboardCode = setting.DashboardCode,
                LineCode = line.LineCode,
                LineName = line.LineName,
                ReportDate = reportDate,
                TriggerType = "Scheduled",
                FileName = BuildFileName(line, reportDate),
                CreateTime = now
            };
            record.Status = "Pending";
            record.ErrorMessage = null;
            record.UpdateTime = now;
            if (record.Id == 0)
                record.Id = await db.Insertable(record).ExecuteReturnBigIdentityAsync();
            else
                await db.Updateable(record).ExecuteCommandAsync();

            try
            {
                var data = await BuildWorkbookDataAsync(
                    setting.DashboardCode,
                    line.LineCode,
                    reportDate,
                    cancellationToken,
                    db);
                var relativePath = await WriteScheduledWorkbookAsync(
                    data,
                    record.FileName,
                    cancellationToken);
                var fullPath = ResolveStoredFilePath(relativePath);
                record.RelativePath = relativePath;
                record.FileSize = new FileInfo(fullPath).Length;
                record.Status = "Success";
                record.CompleteTime = DateTime.Now;
                record.UpdateTime = record.CompleteTime.Value;
                generatedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                record.Status = "Failed";
                record.ErrorMessage = ex.Message;
                record.UpdateTime = DateTime.Now;
                allCompleted = false;
            }
            await db.Updateable(record).ExecuteCommandAsync();
        }

        if (lines.Count == 0 || allCompleted)
        {
            setting.LastScheduledRunDate = now.Date;
            setting.UpdateTime = DateTime.Now;
            await db.Updateable(setting).ExecuteCommandAsync();
        }
        return generatedCount;
    }

    /// <summary>读取一条线体指定日期的统计和故障快照，供手动与定时导出复用。</summary>
    private async Task<EdgeDashboardWorkbookData> BuildWorkbookDataAsync(
        string dashboardCode,
        string lineCode,
        DateTime reportDate,
        CancellationToken cancellationToken,
        ISqlSugarClient? db = null)
    {
        db ??= Db;
        cancellationToken.ThrowIfCancellationRequested();
        var line = await db.Queryable<EdgeDashboardLineConfigEntity>()
            .FirstAsync(x => x.DashboardCode == dashboardCode &&
                             x.LineCode == lineCode && x.IsDelete == 0);
        if (line == null) throw new InvalidOperationException("线体配置不存在");
        var reportEnd = reportDate.AddDays(1);
        var statistics = await db.Queryable<EdgeDashboardRobotDailyStatisticEntity>()
            .Where(x => x.LineCode == lineCode && x.StatDate == reportDate)
            .OrderBy(x => x.RobotCode)
            .ToListAsync();
        var faults = await db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .Where(x => x.LineCode == lineCode &&
                        x.StartTime >= reportDate && x.StartTime < reportEnd)
            .OrderByDescending(x => x.StartTime)
            .ToListAsync();
        var configuredRobotCount = await db.Queryable<EdgeDashboardRobotBindingEntity>()
            .CountAsync(x => x.DashboardCode == dashboardCode && x.LineCode == lineCode &&
                             x.IsDelete == 0 && x.Enabled);
        return new EdgeDashboardWorkbookData
        {
            Line = line,
            ReportDate = reportDate,
            GeneratedAt = DateTime.Now,
            ConfiguredRobotCount = configuredRobotCount,
            Statistics = statistics,
            Faults = faults
        };
    }

    /// <summary>先写同目录临时文件，再原子移动为最终文件，避免下载到未完成工作簿。</summary>
    private static async Task<string> WriteScheduledWorkbookAsync(
        EdgeDashboardWorkbookData data,
        string fileName,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            ExportRoot,
            SanitizePathSegment(data.Line.DashboardCode),
            data.ReportDate.ToString("yyyy"),
            data.ReportDate.ToString("MM"),
            data.ReportDate.ToString("dd"));
        Directory.CreateDirectory(directory);
        var physicalFileName = $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}.xlsx";
        var finalPath = Path.Combine(directory, physicalFileName);
        var tempPath = finalPath + ".tmp";
        try
        {
            await WriteWorkbookFileAsync(tempPath, data, cancellationToken);
            File.Move(tempPath, finalPath);
            return Path.GetRelativePath(ExportRoot, finalPath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>将 SXSSFWorkbook 顺序写入磁盘，控制大数据导出的托管内存占用。</summary>
    private static async Task WriteWorkbookFileAsync(
        string filePath,
        EdgeDashboardWorkbookData data,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        EdgeDashboardWorkbookWriter.Write(output, data, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private EdgeDashboardExportSettingOutputDto BuildSettingOutput(
        EdgeDashboardExportSettingEntity entity,
        DateTime now)
    {
        var output = _mapper.Map<EdgeDashboardExportSettingOutputDto>(entity);
        if (!entity.Enabled) return output;
        var executionTime = ParseExportTime(entity.ExportTime);
        var next = now.Date.Add(executionTime);
        if (next <= now || entity.LastScheduledRunDate?.Date == now.Date) next = next.AddDays(1);
        output.NextScheduledRunTime = next;
        return output;
    }

    private static bool IsSettingDue(EdgeDashboardExportSettingEntity setting, DateTime now)
    {
        if (setting.LastScheduledRunDate?.Date == now.Date) return false;
        return now.TimeOfDay >= ParseExportTime(setting.ExportTime);
    }

    private static string NormalizeExportTime(string value)
    {
        if (!TimeOnly.TryParseExact(
                value?.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            throw new InvalidOperationException("定时导出时间格式必须为 HH:mm");
        return parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static TimeSpan ParseExportTime(string value) =>
        TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();

    private static string BuildFileName(EdgeDashboardLineConfigEntity line, DateTime reportDate) =>
        $"看板日报-{SanitizeFileName(line.LineCode)}-{reportDate:yyyyMMdd}.xlsx";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "line" : sanitized;
    }

    private static string SanitizePathSegment(string value) => SanitizeFileName(value);

    private static string ResolveStoredFilePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(ExportRoot, relativePath));
        var rootPrefix = ExportRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导出文件路径无效");
        return fullPath;
    }

    private static string Require(string? value, string fieldName)
    {
        var normalized = Normalize(value);
        return normalized ?? throw new InvalidOperationException($"{fieldName}不能为空");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
