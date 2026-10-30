using System.Text.Json;
using Model.HourlyProduction;
using SqlSugar;

namespace Service;

internal static class HourlyProcessLifecycle
{
    internal static string Key(HourlyLineDefinition line, HourlyProcessDefinition process) =>
        string.IsNullOrWhiteSpace(process.InstanceId) ? line.Code + "/" + process.Code : process.InstanceId;

    internal static async Task LockAsync(ISqlSugarClient db, string resource, string mode = "Exclusive") =>
        await db.Ado.ExecuteCommandAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode=@mode,@LockOwner='Transaction',@LockTimeout=15000; IF @r < 0 THROW 51000,'hourly configuration lock timeout',1;", new { key = resource, mode });

    internal static HourlyProductionProcessEntity Row(string boardCode, HourlyLineDefinition line, HourlyProcessDefinition process) => new()
    {
        Id = HourlyProductionRules.Hash(boardCode + "\u001f" + Key(line, process)), BoardCode = boardCode,
        ProcessKey = Key(line, process), LineCode = line.Code, Code = process.Code, Name = process.Name,
        DefinitionJson = JsonSerializer.Serialize(process), Active = true, ActivatedAt = process.ActiveSince
    };

    // 不改过站、小时桶或冻结报表。旧键原样注册，未知的删除时间和原配置保持 NULL。
    internal static async Task MigrateAsync(ISqlSugarClient db)
    {
        var boards = await db.Queryable<HourlyProductionBoardEntity>().ToListAsync();
        foreach (var board in boards)
        {
            var definition = JsonSerializer.Deserialize<HourlyBoardDefinition>(board.DefinitionJson)!;
            var registered = await db.Queryable<HourlyProductionProcessEntity>().Where(x => x.BoardCode == board.Code).ToListAsync();
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var line in definition.Lines)
            foreach (var process in line.Processes)
            {
                if (string.IsNullOrWhiteSpace(process.InstanceId)) { process.InstanceId = line.Code + "/" + process.Code; changed = true; }
                active.Add(process.InstanceId);
                if (!registered.Any(x => x.ProcessKey.Equals(process.InstanceId, StringComparison.OrdinalIgnoreCase)))
                {
                    var row = Row(board.Code, line, process);
                    await db.Insertable(row).ExecuteCommandAsync(); registered.Add(row);
                }
            }
            foreach (var row in registered.Where(x => x.Active && !active.Contains(x.ProcessKey)))
            {
                row.Active = false;
                await db.Updateable(row).ExecuteCommandAsync();
            }
            var historical = (await db.Queryable<HourlyProductionPassEntity>().Where(x => x.BoardCode == board.Code).Select(x => x.ProcessKey).Distinct().ToListAsync())
                .Concat(await db.Queryable<HourlyProductionBucketEntity>().Where(x => x.BoardCode == board.Code).Select(x => x.ProcessKey).Distinct().ToListAsync()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var key in historical.Where(key => !registered.Any(x => x.ProcessKey.Equals(key, StringComparison.OrdinalIgnoreCase))))
            {
                var parts = key.Split('/');
                if (parts.Length != 2 || parts.Any(x => x.Length is 0 or > 30)) continue;
                var row = new HourlyProductionProcessEntity { Id = HourlyProductionRules.Hash(board.Code + "\u001f" + key), BoardCode = board.Code,
                    ProcessKey = key, LineCode = parts[0], Code = parts[1], Name = parts[1], Active = false };
                await db.Insertable(row).ExecuteCommandAsync(); registered.Add(row);
            }
            if (changed)
            {
                var json = JsonSerializer.Serialize(definition);
                await db.Updateable<HourlyProductionBoardEntity>().SetColumns(x => new HourlyProductionBoardEntity { DefinitionJson = json })
                    .Where(x => x.Code == board.Code).ExecuteCommandAsync();
            }
        }
    }
}
