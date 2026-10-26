using Model.ProcessBoard;
using SqlSugar;

namespace Service;

/// <summary>按完整租户/设备标识约束卡片；仅原字段原槽位的原绑定可以保留历史越界配置。</summary>
public static class ProcessBoardDeviceScope
{
    public static (string Tenant, string Device) DeviceKey(ProcessSourceBinding source) =>
        (source.TenantMark, source.DeviceMark);

    public static IEnumerable<ProcessSourceBinding> Devices(ProcessBoardDefinition line) =>
        line.Devices.Where(x => x != null && !x.FixedValue.HasValue &&
            !string.IsNullOrWhiteSpace(x.TenantMark) && !string.IsNullOrWhiteSpace(x.DeviceMark));

    public sealed record Reference(string FieldCode, string Slot, string Label, ProcessSourceBinding Source);

    public static IEnumerable<Reference> References(ProcessBoardDefinition card)
    {
        foreach (var source in card.Devices.Where(x => !x.FixedValue.HasValue))
            yield return new("", "device", "关联设备", source);
        foreach (var field in card.Fields)
        {
            if (field.FixedText == null && !field.Source.FixedValue.HasValue)
                yield return new(field.Code, "source", $"字段“{field.Name}”", field.Source);
            foreach (var variable in field.Variables.Where(x => !x.Value.FixedValue.HasValue))
                yield return new(field.Code, "variable:" + variable.Key, $"字段“{field.Name}”变量“{variable.Key}”", variable.Value);
        }
    }

    public static bool SameBinding(ProcessSourceBinding first, ProcessSourceBinding second) =>
        DeviceKey(first) == DeviceKey(second) && first.SourcePath == second.SourcePath;

    public static void ValidateCard(ProcessBoardDefinition card, ProcessBoardDefinition line, ProcessBoardDefinition? old)
    {
        var allowed = Devices(line).Select(DeviceKey).ToHashSet();
        var historical = old == null ? [] : References(old).ToArray();
        foreach (var reference in References(card))
        {
            if (allowed.Contains(DeviceKey(reference.Source))) continue;
            if (historical.Any(x => x.FieldCode.Equals(reference.FieldCode, StringComparison.OrdinalIgnoreCase) &&
                x.Slot == reference.Slot && SameBinding(x.Source, reference.Source))) continue;
            throw new ArgumentException($"卡片“{card.Name}”{reference.Label}的设备 {reference.Source.TenantMark}/{reference.Source.DeviceMark} " +
                $"未关联到工艺线体“{line.Name}”，请先在线体配置中关联设备");
        }
    }
}

public sealed partial class ProcessBoardService
{
    private async Task ValidateDeviceScopeAsync(ProcessBoardDefinition definition, ProcessBoardDefinition? old)
    {
        if (definition.Kind == "Card")
        {
            var parent = await Db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(definition.ParentId);
            if (parent?.Kind != "Line") throw new ArgumentException("所属工艺线体不存在");
            ProcessBoardDeviceScope.ValidateCard(definition, Read(parent), old);
        }
        else if (definition.Kind == "Line" && old != null)
        {
            var removed = ProcessBoardDeviceScope.Devices(old).Select(ProcessBoardDeviceScope.DeviceKey)
                .Except(ProcessBoardDeviceScope.Devices(definition).Select(ProcessBoardDeviceScope.DeviceKey)).ToHashSet();
            if (removed.Count == 0) return;
            var cards = await Db.Queryable<ProcessBoardConfigEntity>()
                .Where(x => x.Kind == "Card" && x.ParentId == definition.Id).ToListAsync();
            var references = cards.Select(Read).SelectMany(card => ProcessBoardDeviceScope.References(card)
                .Where(x => removed.Contains(ProcessBoardDeviceScope.DeviceKey(x.Source)))
                .Select(x => $"卡片“{card.Name}”{x.Label}（{x.Source.TenantMark}/{x.Source.DeviceMark}）")).ToArray();
            if (references.Length > 0)
                throw new ArgumentException("不能移除正在使用的线体设备，请先调整以下绑定：" +
                    string.Join("；", references.Take(8)) + (references.Length > 8 ? "……" : ""));
        }
    }
}
