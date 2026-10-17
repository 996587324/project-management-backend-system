using System.Text;
using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;

namespace Service;

public sealed partial class ProcessBoardService
{
    private static readonly SemaphoreSlim ExportGate = new(1, 1);
    private static string ExportRoot => Path.Combine(AppContext.BaseDirectory, "data", "process-board-exports");
    private const int MaximumExportRows = 1_000_000;
    private const long MaximumExportBytes = 64L * 1024 * 1024;

    public async Task<object> CreateExportAsync(ApplicationAccessContext access, ProcessExportRequest request)
    {
        RequireRange(request.Start, request.End);
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireFieldAsync(access, request.CardId, request.FieldCode);
        var pending = await Db.Queryable<ProcessBoardExportEntity>().Where(x => x.UserId == access.UserId &&
            (x.Status == "Pending" || x.Status == "Processing")).CountAsync();
        if (pending >= 3) throw new InvalidOperationException("同一用户最多同时等待三个导出任务");
        var created = DateTime.Now;
        long last;
        int count;
        if (request.Events)
        {
            var query = Db.Queryable<ProcessBoardEventEntity>().Where(x => x.CardId == request.CardId && x.FieldCode == request.FieldCode &&
                x.CreateTime >= request.Start && x.CreateTime < request.End && x.CreateTime <= created);
            count = await query.Clone().CountAsync();
            last = count == 0 ? 0 : await query.MaxAsync(x => x.Id);
        }
        else
        {
            var query = Db.Queryable<ProcessBoardSampleEntity>().Where(x => x.CardId == request.CardId && x.FieldCode == request.FieldCode &&
                x.CreateTime >= request.Start && x.CreateTime < request.End && x.CreateTime <= created);
            count = await query.Clone().CountAsync();
            last = count == 0 ? 0 : await query.MaxAsync(x => x.Id);
        }
        if (count > MaximumExportRows) throw new ArgumentException("导出超过 100 万行，请缩小时间范围后分批导出");
        var job = new ProcessBoardExportEntity
        {
            UserId = access.UserId, AppCode = access.AppCode, CardId = request.CardId, FieldCode = request.FieldCode,
            StartTime = request.Start, EndTime = request.End, Events = request.Events, LastRecordId = last,
            FileKey = Guid.NewGuid().ToString("N") + ".csv", CreateTime = created
        };
        job.Id = await Db.Insertable(job).ExecuteReturnBigIdentityAsync();
        return new { job.Id, job.Status, expectedRows = count };
    }

    private async Task<ProcessBoardExportEntity> RequireExportAsync(ApplicationAccessContext access, long id)
    {
        var job = await Db.Queryable<ProcessBoardExportEntity>().InSingleAsync(id);
        if (job == null || job.UserId != access.UserId || job.AppCode != access.AppCode)
            throw new UnauthorizedAccessException("导出任务不属于当前用户及系统");
        await RequireFieldAsync(access, job.CardId, job.FieldCode);
        return job;
    }

    public async Task<object> GetExportAsync(ApplicationAccessContext access, long id)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var job = await RequireExportAsync(access, id);
        return new { job.Id, job.Status, job.RowCount, job.Error, job.CompletedTime };
    }

    public async Task<string> GetExportFileAsync(ApplicationAccessContext access, long id)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var job = await RequireExportAsync(access, id);
        var path = ExportPath(job.FileKey);
        if (job.Status != "Ready" || !File.Exists(path)) throw new InvalidOperationException("导出尚未完成或文件已过期");
        return path;
    }

    private static string ExportPath(string fileKey)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(fileKey, "^[a-f0-9]{32}\\.csv$"))
            throw new InvalidOperationException("导出文件标识无效");
        return Path.Combine(ExportRoot, fileKey);
    }

    public async Task RunExportAsync(CancellationToken cancellationToken)
    {
        if (!await ExportGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var abandoned = DateTime.Now.AddHours(-1);
            await Db.Updateable<ProcessBoardExportEntity>().SetColumns(x => new ProcessBoardExportEntity
            {
                Status = "Failed", Error = "导出执行中断，请重新创建任务", CompletedTime = DateTime.Now
            }).Where(x => x.Status == "Processing" && x.StartedTime < abandoned).ExecuteCommandAsync();
            var job = await Db.Queryable<ProcessBoardExportEntity>().Where(x => x.Status == "Pending").OrderBy(x => x.Id).FirstAsync();
            if (job == null) return;
            var claimed = await Db.Updateable<ProcessBoardExportEntity>().SetColumns(x => new ProcessBoardExportEntity
            {
                Status = "Processing", StartedTime = DateTime.Now
            }).Where(x => x.Id == job.Id && x.Status == "Pending").ExecuteCommandAsync();
            if (claimed != 1) return;
            Directory.CreateDirectory(ExportRoot);
            var path = ExportPath(job.FileKey);
            try
            {
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                await writer.WriteLineAsync("采样时间,原始数据时间,原始值,计算值,配置版本,事件,异常");
                long cursor = 0;
                while (cursor < job.LastRecordId)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<(long Id, int Version, DateTime Time, string Runtime, string Event)> rows;
                    if (job.Events)
                    {
                        var batch = await Db.Queryable<ProcessBoardEventEntity>().Where(x => x.CardId == job.CardId &&
                            x.FieldCode == job.FieldCode && x.Id > cursor && x.Id <= job.LastRecordId &&
                            x.CreateTime >= job.StartTime && x.CreateTime < job.EndTime).OrderBy(x => x.Id).Take(1000).ToListAsync();
                        rows = batch.Select(x => (x.Id, x.Version, x.CreateTime, x.RuntimeJson, x.EventType)).ToList();
                    }
                    else
                    {
                        var batch = await Db.Queryable<ProcessBoardSampleEntity>().Where(x => x.CardId == job.CardId &&
                            x.FieldCode == job.FieldCode && x.Id > cursor && x.Id <= job.LastRecordId &&
                            x.CreateTime >= job.StartTime && x.CreateTime < job.EndTime).OrderBy(x => x.Id).Take(1000).ToListAsync();
                        rows = batch.Select(x => (x.Id, x.Version, x.CreateTime, x.RuntimeJson, string.Empty)).ToList();
                    }
                    if (rows.Count == 0) break;
                    foreach (var row in rows)
                    {
                        var value = JsonConvert.DeserializeObject<ProcessFieldRuntime>(row.Runtime)!;
                        var fields = new[] { row.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), value.DataTime?.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            value.RawJson, value.ValueJson, row.Version.ToString(), row.Event, value.Error };
                        await writer.WriteLineAsync(string.Join(',', fields.Select(Csv)).AsMemory(), cancellationToken);
                        job.RowCount++;
                    }
                    cursor = rows[^1].Id;
                    await writer.FlushAsync(cancellationToken);
                    if (stream.Position > MaximumExportBytes)
                        throw new InvalidOperationException("导出文件超过 64 MB，请缩小时间范围或按参数分批导出");
                }
                await writer.FlushAsync(cancellationToken);
                job.Status = "Ready";
            }
            catch (Exception exception)
            {
                job.Status = "Failed";
                job.Error = exception is OperationCanceledException ? "导出已中断，请重新创建" : exception.Message[..Math.Min(2000, exception.Message.Length)];
                if (File.Exists(path)) File.Delete(path);
            }
            job.CompletedTime = DateTime.Now;
            await Db.Updateable<ProcessBoardExportEntity>().SetColumns(x => new ProcessBoardExportEntity
            {
                Status = job.Status, RowCount = job.RowCount, Error = job.Error, CompletedTime = job.CompletedTime
            }).Where(x => x.Id == job.Id && x.Status == "Processing").ExecuteCommandAsync();
        }
        finally
        {
            ExportGate.Release();
        }
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        // Excel 公式注入防护：文本不会在打开 CSV 后执行公式。
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
            value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
