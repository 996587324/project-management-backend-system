using Interface;
using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

public sealed partial class ProcessBoardService(
    SqlSugarScope scope,
    IProcessFormulaService formulas,
    IEdgeDeviceCardPushService push) : IProcessBoardService
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    public static bool CanReadLine(ApplicationAccessContext access, long lineId) =>
        access.AllResources || access.LineCodes.Contains($"process:{lineId}");

    public static ProcessBoardDefinition Read(ProcessBoardConfigEntity row)
    {
        var value = JsonConvert.DeserializeObject<ProcessBoardDefinition>(row.DefinitionJson)
            ?? throw new InvalidOperationException("工艺看板配置无法读取");
        value.Id = row.Id;
        value.Version = row.Version;
        return value;
    }

    public async Task<List<ProcessBoardDefinition>> GetDefinitionsAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var rows = await Db.Queryable<ProcessBoardConfigEntity>().OrderBy(x => x.Sort).ToListAsync();
        var lines = rows.Where(x => x.Kind == "Line" && CanReadLine(access, x.Id)).ToArray();
        var lineIds = lines.Select(x => x.Id).ToHashSet();
        var regionIds = lines.Select(x => x.ParentId).ToHashSet();
        return rows.Where(x => access.AllResources || x.Kind == "Line" && lineIds.Contains(x.Id) ||
                x.Kind == "Card" && lineIds.Contains(x.ParentId) || x.Kind == "Region" && regionIds.Contains(x.Id))
            .Select(Read).ToList();
    }

    public async Task<ProcessBoardDefinition> SaveAsync(ApplicationAccessContext access, ProcessBoardDefinition definition)
    {
        if (!access.HasPermission("ProcessBoardConfig")) throw new UnauthorizedAccessException("没有工艺看板配置权限");
        using var catalogOperation = DatabaseOperationScope.Begin(scope);
        await using var catalogLease = await EdgeModelFieldCatalogGuard.EnterAsync(Db);
        await WriteGate.WaitAsync();
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            await ValidateAsync(access, definition);
            var old = definition.Id == 0 ? null : await Db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(definition.Id);
            if (definition.Id != 0 && old == null) throw new ArgumentException("配置不存在");
            if (old != null && (old.Kind != definition.Kind || old.ParentId != definition.ParentId))
                throw new ArgumentException("不能更改配置类别或移动所属区域/线体，请新建配置");
            if (old != null && old.Version != definition.Version)
                throw new InvalidOperationException("配置已被其他人修改，请刷新后再保存");
            if (old?.Kind == "Line" && !CanReadLine(access, old.Id) ||
                definition.Kind == "Card" && !CanReadLine(access, definition.ParentId) ||
                (definition.Kind == "Region" || definition.Kind == "Line" && old == null) && !access.AllResources)
                throw new UnauthorizedAccessException("没有该工艺线体配置权限；创建区域或线体需要全部资源授权");
            await ValidateDeviceScopeAsync(definition, old == null ? null : Read(old));
            var id = definition.Id;
            var kind = definition.Kind;
            var code = definition.Code;
            if (await Db.Queryable<ProcessBoardConfigEntity>().AnyAsync(x => x.Kind == kind && x.Code == code && x.Id != id))
                throw new ArgumentException("同一类别的配置编码不能重复");
            definition.Version = (old?.Version ?? 0) + 1;
            var entity = new ProcessBoardConfigEntity
            {
                Id = definition.Id, Kind = kind, ParentId = definition.ParentId, Code = code,
                Name = definition.Name, Sort = definition.Sort, Enabled = definition.Enabled,
                Version = definition.Version, DefinitionJson = JsonConvert.SerializeObject(definition),
                CreateTime = old?.CreateTime ?? DateTime.Now, UpdateTime = DateTime.Now
            };
            var transaction = await Db.Ado.UseTranAsync(async () =>
            {
                if (old == null) entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
                else
                {
                    var affected = await Db.Updateable(entity).Where(x => x.Id == id && x.Version == old.Version)
                        .ExecuteCommandAsync();
                    if (affected != 1) throw new InvalidOperationException("配置已变更，请刷新");
                }
                definition.Id = entity.Id;
                await Db.Insertable(new ProcessBoardConfigAuditEntity
                {
                    ConfigId = entity.Id, Version = entity.Version, UserId = access.UserId,
                    DefinitionJson = JsonConvert.SerializeObject(definition), CreateTime = DateTime.Now
                }).ExecuteCommandAsync();
            });
            if (!transaction.IsSuccess) throw transaction.ErrorException;
            return definition;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private async Task ValidateAsync(ApplicationAccessContext access, ProcessBoardDefinition definition)
    {
        if (definition.Kind is not ("Region" or "Line" or "Card")) throw new ArgumentException("配置类别无效");
        if (definition.Code == null || definition.Name == null || definition.Fields == null || definition.Devices == null ||
            definition.Flow == null || definition.Edges == null || definition.Blocks == null)
            throw new ArgumentException("配置必要字段不能为 null");
        definition.Code = definition.Code.Trim();
        definition.Name = definition.Name.Trim();
        if (definition.Code.Length is < 1 or > 64 || definition.Name.Length is < 1 or > 128)
            throw new ArgumentException("编码为 1～64 字，名称为 1～128 字");
        if (definition.Layout is not ("grid" or "horizontal" or "vertical" or "free") ||
            definition.Columns is < 1 or > 12 || definition.Width is < 160 or > 3840 || definition.Height is < 100 or > 2160)
            throw new ArgumentException("布局或尺寸无效");
        if (definition.FieldColumns is < 1 or > 6)
        {
            throw new ArgumentException("每行参数数量必须为 1～6 组");
        }
        if (definition.PanelLayout is not ("tabs" or "split") || definition.FlowHeight is < 160 or > 2160 ||
            definition.TrendHeight is < 200 or > 2160 || definition.FlowWidth is < 0 or > 3840 || definition.TrendWidth is < 0 or > 3840)
            throw new ArgumentException("流程/曲线区域布局或尺寸无效");
        if (definition.Kind == "Region" && definition.ParentId != 0) throw new ArgumentException("区域不能有父级");
        if (definition.Kind != "Region")
        {
            var parent = await Db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(definition.ParentId);
            if (parent?.Kind != (definition.Kind == "Line" ? "Region" : "Line"))
                throw new ArgumentException("所属区域或线体不存在");
        }
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (definition.Fields.Count > 100 || definition.Devices.Count > 200 || definition.Flow.Count > 200)
            throw new ArgumentException("单卡片最多 100 个字段，单线体最多 200 台设备/工序");
        foreach (var source in definition.Devices) RequireSource(access, source, requirePath: false);
        foreach (var field in definition.Fields)
        {
            if (field == null || field.Code == null || field.Name == null || field.Unit == null || field.Formula == null ||
                field.Variables == null || field.Constants == null || field.Functions == null || field.RecipientUserIds == null)
                throw new ArgumentException("参数必要字段不能为 null");
            if (field.Code.Length is < 1 or > 64 || !codes.Add(field.Code) || field.Name.Length is < 1 or > 128)
                throw new ArgumentException("字段编码不能为空、不可重复，编码最多 64 字，名称最多 128 字");
            if (field.Unit.Length > 32 || field.Precision is < 0 or > 12 || field.ComparisonPrecision is < 0 or > 12 ||
                !double.IsFinite(field.ChangeThreshold) || field.ChangeThreshold < 0 ||
                field.LowerLimit.HasValue && !double.IsFinite(field.LowerLimit.Value) ||
                field.UpperLimit.HasValue && !double.IsFinite(field.UpperLimit.Value) || field.LowerLimit > field.UpperLimit)
                throw new ArgumentException("字段精度、阈值或上下限配置无效");
            if (field.FixedText == null) RequireSource(access, field.Source);
            foreach (var variable in field.Variables) RequireSource(access, variable.Value);
            if (field.Variables.ContainsKey("x") || field.Constants.ContainsKey("x") ||
                field.Constants.Keys.Any(field.Variables.ContainsKey))
                throw new ArgumentException("x 为原始值保留变量，变量与常量不能重名");
            if (field.Formula.Length > 8192 || field.Variables.Count > 64 || field.Functions.Count > 64 ||
                field.Constants.Count > 64 || field.Functions.Any(x => x.Length > 8192) || field.FixedText?.Length > 8192)
                throw new ArgumentException("公式或变量配置超出大小限制");
            if (field.RecipientUserIds.Count > 100 || field.RecipientUserIds.Any(x => x.Length > 64))
                throw new ArgumentException("接收人配置无效");
            if (field.PushEnabled && (field.RecipientUserIds.Count == 0 ||
                !field.PushOnChange && !field.PushOnLimit && !field.PushOnRecovery))
                throw new ArgumentException("开启推送时请配置接收人及触发条件");
            if (field.PushEnabled)
                await EdgeDeviceCardPushConfiguration.ValidateRecipientsAsync(Db, field.RecipientUserIds.Distinct().ToArray());
        }
        var sourceGroups = definition.Fields.SelectMany(field => field.Variables.Values.Concat(
                field.FixedText == null ? new[] { field.Source } : Array.Empty<ProcessSourceBinding>()))
            .Where(x => !x.FixedValue.HasValue).GroupBy(x => (x.TenantMark, x.DeviceMark));
        foreach (var group in sourceGroups)
        {
            await EdgeModelFieldCatalogGuard.RequireDevicePathsAsync(Db, group.Key.TenantMark,
                group.Key.DeviceMark, group.Select(x => x.SourcePath));
        }
        if (definition.Flow.Select(x => x.Id).Distinct().Count() != definition.Flow.Count ||
            definition.Flow.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Name.Length > 128 ||
                !double.IsFinite(x.X) || !double.IsFinite(x.Y)) ||
            definition.Edges.Any(x => !definition.Flow.Any(n => n.Id == x.From) || !definition.Flow.Any(n => n.Id == x.To)))
            throw new ArgumentException("工艺节点或连线配置无效");
        if (definition.Blocks.Count > 200 || definition.Blocks.Select(x => x.CardId).Distinct().Count() != definition.Blocks.Count ||
            definition.Blocks.Any(x => x.X is < 0 or > 8000 || x.Y is < 0 or > 8000 ||
                x.Width is < 160 or > 3840 || x.Height is < 100 or > 2160))
            throw new ArgumentException("卡片布局坐标或尺寸无效");
        if (definition.Kind == "Line" && definition.Id > 0)
        {
            var cardIds = await Db.Queryable<ProcessBoardConfigEntity>().Where(x => x.Kind == "Card" && x.ParentId == definition.Id)
                .Select(x => x.Id).ToListAsync();
            if (definition.Blocks.Any(x => !cardIds.Contains(x.CardId)) || definition.Flow.Any(x => x.CardId.HasValue && !cardIds.Contains(x.CardId.Value)))
                throw new ArgumentException("布局或工艺节点只能关联当前线体的卡片");
        }
        if (JsonConvert.SerializeObject(definition).Length > 500000) throw new ArgumentException("配置内容过大");
    }

    public static void RequireSource(ApplicationAccessContext access, ProcessSourceBinding source, bool requirePath = true)
    {
        if (source == null) throw new ArgumentException("数据源绑定不能为空");
        if (source.FixedValue.HasValue)
        {
            if (!double.IsFinite(source.FixedValue.Value)) throw new ArgumentException("固定数值无效");
            return;
        }
        if (string.IsNullOrWhiteSpace(source.TenantMark) || source.TenantMark.Length > 64 ||
            string.IsNullOrWhiteSpace(source.DeviceMark) || source.DeviceMark.Length > 128 ||
            requirePath && (string.IsNullOrWhiteSpace(source.SourcePath) || !source.SourcePath.StartsWith('/') || source.SourcePath.Length > 512))
            throw new ArgumentException("请选择有效的租户、设备及完整字段路径");
        if (!access.AllResources && !access.SourceKeys.Contains(source.TenantMark + "\u001f" + source.DeviceMark))
            throw new UnauthorizedAccessException("没有该数据源授权");
    }

    public async Task<ProcessBoardSetting> GetSettingAsync()
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var setting = await Db.Queryable<ProcessBoardSettingEntity>().InSingleAsync(1);
        return new ProcessBoardSetting
        {
            FormulaUseType = setting.FormulaUseType ?? "Personal", FormulaSignature = setting.FormulaSignature ?? string.Empty,
            FormulaTermsAccepted = setting.FormulaTermsAccepted,
            SampleIntervalSeconds = setting.SampleIntervalSeconds, RetentionHours = setting.RetentionHours,
            PushIntervalSeconds = (await push.GetQueueSettingAsync()).IntervalSeconds
        };
    }

    public async Task SaveSettingAsync(ApplicationAccessContext access, ProcessBoardSetting setting)
    {
        if (!access.PlatformAdministrator) throw new UnauthorizedAccessException("只有平台管理员可以修改采样、保留和共享发送间隔");
        if (setting.SampleIntervalSeconds is < 1 or > 3600 || setting.RetentionHours is < 1 or > 8760)
            throw new ArgumentException("采样间隔为 1～3600 秒，保留时间为 1～8760 小时");
        if (setting.FormulaUseType is not ("Personal" or "Commercial") || setting.FormulaSignature.Length > 128 ||
            setting.FormulaTermsAccepted && string.IsNullOrWhiteSpace(setting.FormulaSignature))
            throw new ArgumentException("请配置真实使用者署名并选择有效的 mXparser 使用类型");
        using var operation = DatabaseOperationScope.Begin(scope);
        await push.SaveQueueSettingAsync(new Model.EdgeGateway.EdgeDeviceCardPushQueueSettingDto
        {
            IntervalSeconds = setting.PushIntervalSeconds
        });
        await Db.Updateable(new ProcessBoardSettingEntity
        {
            Id = 1, SampleIntervalSeconds = setting.SampleIntervalSeconds,
            RetentionHours = setting.RetentionHours, UpdateTime = DateTime.Now,
            FormulaUseType = setting.FormulaUseType, FormulaSignature = setting.FormulaSignature.Trim(),
            FormulaTermsAccepted = setting.FormulaTermsAccepted
        }).ExecuteCommandAsync();
        await Db.Insertable(new ProcessBoardConfigAuditEntity
        {
            ConfigId = 0, Version = 0, UserId = access.UserId,
            DefinitionJson = JsonConvert.SerializeObject(setting), CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
    }
}
