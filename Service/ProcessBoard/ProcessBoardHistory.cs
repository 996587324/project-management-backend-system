using Model.ProcessBoard;
using Model.SystemApplications;
using SqlSugar;

namespace Service;

public sealed partial class ProcessBoardService
{
    private async Task RequireFieldAsync(ApplicationAccessContext access, long cardId, string fieldCode)
    {
        var row = await Db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(cardId);
        if (row?.Kind is not ("Card" or "Line") || !CanReadLine(access, row.Kind == "Line" ? row.Id : row.ParentId))
            throw new UnauthorizedAccessException("没有该工艺卡片/线体参数授权");
        var field = Read(row).Fields.FirstOrDefault(x => x.Code == fieldCode) ?? throw new ArgumentException("参数不存在");
        if (field.FixedText == null) RequireSource(access, field.Source);
        foreach (var source in field.Variables.Values) RequireSource(access, source);
        // 历史输入可能来自之前绑定的另一设备；不把旧授权数据泄漏给只拥有当前来源的用户。
        if (!access.AllResources)
        {
            var versions = await Db.Queryable<ProcessBoardConfigAuditEntity>().Where(x => x.ConfigId == cardId).ToListAsync();
            foreach (var version in versions)
            {
                var historical = Newtonsoft.Json.JsonConvert.DeserializeObject<ProcessBoardDefinition>(version.DefinitionJson)!
                    .Fields.FirstOrDefault(x => x.Code == fieldCode);
                if (historical == null) continue;
                if (historical.FixedText == null) RequireSource(access, historical.Source);
                foreach (var source in historical.Variables.Values) RequireSource(access, source);
            }
        }
    }

    private static void RequireRange(DateTime start, DateTime end)
    {
        if (start.Year < 1753 || end <= start || end - start > TimeSpan.FromDays(366))
            throw new ArgumentException("请选择有效时间范围，单次最多 366 天");
    }

    public async Task<object> GetPushTasksAsync(ApplicationAccessContext access, long lineId, int page)
    {
        if (!CanReadLine(access, lineId)) throw new UnauthorizedAccessException("没有该工艺线体授权");
        using var operation = DatabaseOperationScope.Begin(scope);
        if (!access.AllResources)
        {
            var definitions = await GetDefinitionsAsync(access);
            foreach (var definition in definitions.Where(x => x.Id == lineId || x.Kind == "Card" && x.ParentId == lineId))
                foreach (var field in definition.Fields) await RequireFieldAsync(access, definition.Id, field.Code);
        }
        return await push.GetTasksAsync(new Model.EdgeGateway.EdgeDeviceCardPushTaskQueryDto
        {
            LineCode = $"process:{lineId}", Current = page, Size = 50
        });
    }

    public async Task<object> GetHistoryAsync(ApplicationAccessContext access, long cardId, string fieldCode,
        DateTime start, DateTime end, int page, int size, bool events)
    {
        RequireRange(start, end);
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireFieldAsync(access, cardId, fieldCode);
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 500);
        RefAsync<int> total = 0;
        if (events)
        {
            var records = await Db.Queryable<ProcessBoardEventEntity>().Where(x => x.CardId == cardId &&
                x.FieldCode == fieldCode && x.CreateTime >= start && x.CreateTime < end)
                .OrderByDescending(x => x.Id).ToPageListAsync(page, size, total);
            return new { records, total = (int)total, page, size };
        }
        var samples = await Db.Queryable<ProcessBoardSampleEntity>().Where(x => x.CardId == cardId &&
            x.FieldCode == fieldCode && x.CreateTime >= start && x.CreateTime < end)
            .OrderByDescending(x => x.Id).ToPageListAsync(page, size, total);
        return new { records = samples, total = (int)total, page, size };
    }

    public async Task<object> GetCurveAsync(ApplicationAccessContext access, long cardId, string fieldCode,
        DateTime start, DateTime end, int points, bool raw)
    {
        RequireRange(start, end);
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireFieldAsync(access, cardId, fieldCode);
        points = Math.Clamp(points, 50, 2000);
        var seconds = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds / points));
        // 数据库按时间桶聚合，不在服务内加载几十万条记录再抽样。
        var column = raw ? "RawNumber" : "ValueNumber";
        var buckets = await Db.Ado.SqlQueryAsync<ProcessCurveBucket>($"""
            SELECT DATEDIFF_BIG(SECOND, @start, CreateTime) / @seconds AS Bucket,
                AVG({column}) AS Average,
                MIN({column}) AS Minimum,
                MAX({column}) AS Maximum,
                COUNT(*) AS Count
            FROM ProcessBoardSample
            WHERE CardId = @cardId AND FieldCode = @fieldCode AND CreateTime >= @start AND CreateTime < @end
            GROUP BY DATEDIFF_BIG(SECOND, @start, CreateTime) / @seconds
            ORDER BY Bucket
            """, new SugarParameter("@start", start), new SugarParameter("@end", end),
            new SugarParameter("@seconds", seconds),
            new SugarParameter("@cardId", cardId), new SugarParameter("@fieldCode", fieldCode));
        return new
        {
            bucketSeconds = seconds,
            points = buckets.Select(x => new { time = start.AddSeconds(x.Bucket * seconds), x.Average, x.Minimum, x.Maximum, x.Count })
        };
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var setting = await Db.Queryable<ProcessBoardSettingEntity>().InSingleAsync(1);
        var cutoff = DateTime.Now.AddHours(-setting.RetentionHours);
        var expire = DateTime.Now.AddDays(-1);
        var expiredJobs = await Db.Queryable<ProcessBoardExportEntity>()
            .Where(x => x.CreateTime < expire && x.Status != "Processing" && x.Status != "Expired").Take(100).ToListAsync();
        foreach (var job in expiredJobs)
        {
            var path = ExportPath(job.FileKey);
            if (File.Exists(path)) File.Delete(path);
            await Db.Updateable<ProcessBoardExportEntity>().SetColumns(x => x.Status == "Expired").Where(x => x.Id == job.Id).ExecuteCommandAsync();
        }
        var protectedJobs = await Db.Queryable<ProcessBoardExportEntity>()
            .Where(x => (x.Status == "Pending" || x.Status == "Processing") && x.CreateTime >= expire).ToListAsync();
        if (protectedJobs.Count > 0) cutoff = new[] { cutoff, protectedJobs.Min(x => x.StartTime) }.Min();
        // 分批硬删除，避免长事务和大量锁；配置版本审计不跟随实时数据清理。
        for (var round = 0; round < 20; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await Db.Queryable<ProcessBoardSampleEntity>().Where(x => x.CreateTime < cutoff)
                .OrderBy(x => x.Id).Take(2000).Select(x => x.Id).ToListAsync();
            if (ids.Count == 0) break;
            await Db.Deleteable<ProcessBoardSampleEntity>().In(ids).ExecuteCommandAsync();
        }
        for (var round = 0; round < 20; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await Db.Queryable<ProcessBoardEventEntity>().Where(x => x.CreateTime < cutoff)
                .OrderBy(x => x.Id).Take(2000).Select(x => x.Id).ToListAsync();
            if (ids.Count == 0) break;
            await Db.Deleteable<ProcessBoardEventEntity>().In(ids).ExecuteCommandAsync();
        }
    }
}
