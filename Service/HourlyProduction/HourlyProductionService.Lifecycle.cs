using System.Text.Json;
using Model.HourlyProduction;
using Model.SystemApplications;

namespace Service;

public sealed partial class HourlyProductionService
{
    private async Task BindInstancesAsync(HourlyBoardDefinition? old, HourlyBoardDefinition next, DateTime now)
    {
        var registered = await Db.Queryable<HourlyProductionProcessEntity>().Where(x => x.BoardCode == next.Code).ToListAsync();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in next.Lines)
        foreach (var process in line.Processes)
        {
            if (process.CreationMode is not ("" or "New" or "Restore")) throw new ArgumentException("无效的工序新增方式");
            var prior = old?.Lines.FirstOrDefault(x => x.Code == line.Code)?.Processes.FirstOrDefault(x => x.Code == process.Code);
            HourlyProductionProcessEntity? row;
            if (process.CreationMode == "Restore")
            {
                row = registered.FirstOrDefault(x => x.ProcessKey == process.InstanceId && x.LineCode == line.Code && x.Code == process.Code);
                if (row == null || row.Active) throw new ArgumentException("原工序不存在或已恢复，请刷新配置后重试");
                var archived = row.DefinitionJson == null ? null : JsonSerializer.Deserialize<HourlyProcessDefinition>(row.DefinitionJson);
                if (archived == null) throw new ArgumentException("原工序配置缺失，无法恢复；可以使用同编码新建工序，历史数据仍保留");
                if (StatisticalRule(archived) != StatisticalRule(process)) throw new ArgumentException("恢复原工序需保留原设备、字段和统计规则；需要调整时请选择同编码新建工序");
                process.InstanceId = row.ProcessKey; process.ActiveSince = now;
            }
            else if (prior != null && process.CreationMode != "New")
            {
                var key = ProcessKey(line, prior);
                if (!string.IsNullOrEmpty(process.InstanceId) && process.InstanceId != key) throw new ArgumentException("不能修改已有工序的内部统计身份");
                row = registered.FirstOrDefault(x => x.ProcessKey == key)
                    ?? throw new InvalidOperationException("工序身份尚未初始化，请等待后台初始化完成");
                process.InstanceId = key; process.ActiveSince = row.ActivatedAt;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(process.InstanceId)) throw new ArgumentException("新建工序不能指定已有统计身份，请从已移除工序恢复，或以新工序重新添加");
                var legacyKey = line.Code + "/" + process.Code;
                var used = registered.Any(x => x.ProcessKey.Equals(legacyKey, StringComparison.OrdinalIgnoreCase)) ||
                    await Db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.BoardCode == next.Code && x.ProcessKey == legacyKey) ||
                    await Db.Queryable<HourlyProductionBucketEntity>().AnyAsync(x => x.BoardCode == next.Code && x.ProcessKey == legacyKey);
                process.InstanceId = used ? "p_" + Guid.NewGuid().ToString("N") : legacyKey;
                // 新看板保留原有离线过站能力；后续新增/恢复不擅自补算保存前的报文。
                process.ActiveSince = old == null && !used ? null : now;
                row = null;
            }
            if (!selected.Add(process.InstanceId)) throw new ArgumentException("同一工序统计身份不能重复关联");
            process.CreationMode = "";
            var saved = HourlyProcessLifecycle.Row(next.Code, line, process);
            if (row == null) { await Db.Insertable(saved).ExecuteCommandAsync(); registered.Add(saved); }
            else { saved.Id = row.Id; await Db.Updateable(saved).ExecuteCommandAsync(); }
        }
        foreach (var row in registered.Where(x => x.Active && !selected.Contains(x.ProcessKey)))
        {
            row.Active = false; row.RemovedAt = now;
            await Db.Updateable(row).ExecuteCommandAsync();
        }
    }

    public async Task<List<HourlyRemovedProcess>> RemovedProcessesAsync(ApplicationAccessContext access, string code, string lineCode)
    {
        Ready(); Require(access, true);
        using var operation = DatabaseOperationScope.Begin(scope);
        var (_, board) = await GetAsync(access, code, false);
        if (!board.Lines.Any(x => x.Code == lineCode)) throw new ArgumentException("线体不存在");
        var rows = await Db.Queryable<HourlyProductionProcessEntity>().Where(x => x.BoardCode == code && x.LineCode == lineCode && !x.Active)
            .OrderByDescending(x => x.RemovedAt).ToListAsync();
        var result = new List<HourlyRemovedProcess>();
        foreach (var row in rows)
        {
            var process = row.DefinitionJson == null ? null : JsonSerializer.Deserialize<HourlyProcessDefinition>(row.DefinitionJson);
            var allowed = process != null && (access.AllResources || process.Bindings.All(binding =>
                access.SourceKeys.Contains(binding.SourceKey + "\u001f" + binding.DeviceKey) &&
                (binding.Kind != "Edge" || access.DeviceCodes.Contains(binding.DeviceKey))));
            result.Add(new HourlyRemovedProcess { InstanceId = row.ProcessKey, LineCode = row.LineCode, Code = row.Code, Name = row.Name,
                RemovedAt = row.RemovedAt, CanRestore = allowed, Definition = allowed ? process : null,
                Reason = process == null ? "旧工序没有完整配置存档，可使用同编码新建，原历史保留" : allowed ? "" : "原数据绑定不在当前授权范围，不能恢复" });
        }
        return result;
    }
}
