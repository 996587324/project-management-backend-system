using System.Text.Json;
using Model.ProcessBoard;
using Model.SystemApplications;

namespace Service;

public sealed partial class ProcessBoardAgentExecutor
{
    private async Task<object> WriteAsync(string action, JsonElement args, ApplicationAccessContext access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (action == "export-create")
        {
            return await board.CreateExportAsync(access, new ProcessExportRequest
            {
                CardId = Id(args), FieldCode = Text(args, "fieldCode"), Start = Date(args, "start"),
                End = Date(args, "end"), Events = Flag(args, "events")
            });
        }
        if (action == "setting-update") return await UpdateSettingAsync(access, args);
        if (action == "create")
        {
            var created = Deserialize<ProcessBoardDefinition>(args);
            return Summary(await board.SaveAsync(access, created));
        }

        var current = await GetDefinitionAsync(access, Id(args));
        var version = Int(args, "version", 0);
        if (current.Version != version) throw new InvalidOperationException("配置版本已变化，请重新读取并确认，不自动覆盖");
        var patch = args.GetProperty("patch");
        if (!patch.EnumerateObject().Any()) throw new ArgumentException("请明确要修改的配置内容");
        switch (action)
        {
            case "update":
                current = ApplyPatch(current, patch);
                break;
            case "field-upsert":
            {
                if (current.Kind is not ("Line" or "Card")) throw new ArgumentException("只有工艺线体和参数卡片可以配置字段");
                var code = Text(patch, "code");
                var existing = current.Fields.FirstOrDefault(field => field.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                var updated = ApplyPatch(existing ?? new ProcessFieldDefinition(), patch);
                if (existing != null) updated.Code = existing.Code;
                await RequireAvailableSourcesAsync(access, updated);
                if (existing == null) current.Fields.Add(updated);
                else current.Fields[current.Fields.IndexOf(existing)] = updated;
                break;
            }
            case "flows-upsert":
                RequireLine(current);
                MergeFlow(current, patch);
                break;
            case "devices-bind":
                RequireLine(current);
                foreach (var element in patch.GetProperty("devices").EnumerateArray())
                {
                    var binding = Deserialize<ProcessSourceBinding>(element);
                    if (binding.FixedValue.HasValue) throw new ArgumentException("设备绑定必须选择真实租户及设备，不能使用固定值");
                    ProcessBoardService.RequireSource(access, binding, false);
                    var tenant = binding.TenantMark;
                    var device = binding.DeviceMark;
                    if (!await Db.Queryable<Model.EdgeGateway.EdgeRealtimeDeviceSnapshotEntity>()
                        .AnyAsync(item => item.TenantMark == tenant && item.DeviceCode == device))
                        throw new ArgumentException("绑定设备不在当前可用数据源中，请先查询真实数据源");
                    if (!current.Devices.Any(source => source.TenantMark == binding.TenantMark && source.DeviceMark == binding.DeviceMark))
                        current.Devices.Add(binding);
                }
                break;
            case "layout-update":
                current = MergeLayout(current, patch);
                break;
            default:
                throw new ArgumentException("未开放的写入动作");
        }
        token.ThrowIfCancellationRequested();
        var saved = await board.SaveAsync(access, current);
        return Summary(saved);
    }

    private static object Summary(ProcessBoardDefinition definition)
    {
        return new { definition.Id, definition.Kind, definition.Code, definition.Name, definition.Version,
            definition.Enabled, fields = definition.Fields.Count, devices = definition.Devices.Count,
            nodes = definition.Flow.Count, message = "保存成功；后续修改必须使用当前返回的version" };
    }

    private static void RequireLine(ProcessBoardDefinition definition)
    {
        if (definition.Kind != "Line") throw new ArgumentException("该操作需要工艺线体ID");
    }

    private static void MergeFlow(ProcessBoardDefinition current, JsonElement patch)
    {
        if (patch.TryGetProperty("nodes", out var nodes))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in nodes.EnumerateArray())
            {
                var id = Text(element, "id");
                if (!ids.Add(id)) throw new ArgumentException("同一次修改中的工艺节点ID不能重复");
                var existing = current.Flow.FirstOrDefault(node => node.Id == id);
                var updated = ApplyPatch(existing ?? new ProcessFlowNode(), element);
                if (existing == null) current.Flow.Add(updated);
                else current.Flow[current.Flow.IndexOf(existing)] = updated;
            }
        }
        if (patch.TryGetProperty("edges", out var edges))
        {
            foreach (var element in edges.EnumerateArray())
            {
                var edge = Deserialize<ProcessFlowEdge>(element);
                if (!current.Edges.Any(item => item.From == edge.From && item.To == edge.To)) current.Edges.Add(edge);
            }
        }
    }

    private static ProcessBoardDefinition MergeLayout(ProcessBoardDefinition current, JsonElement patch)
    {
        var blocks = current.Blocks;
        current = ApplyPatch(current, patch);
        current.Blocks = blocks;
        if (patch.TryGetProperty("blocks", out var changes))
        {
            RequireLine(current);
            var ids = new HashSet<long>();
            foreach (var element in changes.EnumerateArray())
            {
                var block = Deserialize<ProcessBlockLayout>(element);
                if (!ids.Add(block.CardId)) throw new ArgumentException("同一次修改中的卡片布局不能重复");
                var index = current.Blocks.FindIndex(item => item.CardId == block.CardId);
                if (index < 0) current.Blocks.Add(block);
                else current.Blocks[index] = block;
            }
        }
        return current;
    }

    private async Task<object> UpdateSettingAsync(ApplicationAccessContext access, JsonElement args)
    {
        if (!access.PlatformAdministrator) throw new UnauthorizedAccessException("只有平台管理员可以修改采样、保留和共享推送间隔");
        var current = await board.GetSettingAsync();
        var expected = Deserialize<ProcessBoardSetting>(args.GetProperty("expected"));
        if (current.SampleIntervalSeconds != expected.SampleIntervalSeconds || current.RetentionHours != expected.RetentionHours ||
            current.PushIntervalSeconds != expected.PushIntervalSeconds)
            throw new InvalidOperationException("工艺设置已变化，请重新查询后确认");
        var patch = args.GetProperty("patch");
        if (!patch.EnumerateObject().Any()) throw new ArgumentException("请明确要修改的设置");
        var updated = ApplyPatch(current, patch);
        await board.SaveSettingAsync(access, updated);
        return new { updated.SampleIntervalSeconds, updated.RetentionHours, updated.PushIntervalSeconds };
    }

    private async Task RequireAvailableSourcesAsync(ApplicationAccessContext access, ProcessFieldDefinition field)
    {
        RequireSources(access, field);
        var bindings = field.Variables.Values.AsEnumerable();
        if (field.FixedText == null) bindings = bindings.Append(field.Source);
        foreach (var group in bindings.Where(source => !source.FixedValue.HasValue).GroupBy(source => (source.TenantMark, source.DeviceMark)))
        {
            var fields = await dashboard.GetSourceFieldsAsync(group.Key.TenantMark, group.Key.DeviceMark);
            if (group.Any(source => !fields.Any(option => option.SourcePath.Equals(source.SourcePath, StringComparison.Ordinal))))
                throw new ArgumentException("字段不在可用的实时/历史字段库中，请先查询真实sourcePath");
        }
    }
}
