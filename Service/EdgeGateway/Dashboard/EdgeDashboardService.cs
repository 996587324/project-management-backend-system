using AutoMapper;
using System.Globalization;
using Interface;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

/// <summary>实现机械手数据绑定、统计查询和数据库驱动的通用看板部件配置。</summary>
public sealed class EdgeDashboardService : IEdgeDashboardService
{
    private const string DefaultDashboardCode = "production-dashboard";
    private const string UnclassifiedModelMark = "__UNCLASSIFIED__";
    private static readonly (string State, int Priority)[] ModelStatePriorityDefaults =
    [
        (EdgeDashboardConstants.Fault, 10),
        (EdgeDashboardConstants.Idle, 20),
        (EdgeDashboardConstants.Stopped, 30),
        (EdgeDashboardConstants.Running, 40),
        (EdgeDashboardConstants.Unknown, 90)
    ];
    private static readonly SemaphoreSlim LineOutputGate = new(1, 1);
    private static readonly SemaphoreSlim DeviceCardTraceGate = new(1, 1);
    private static readonly SemaphoreSlim DeviceCardAlertClaimGate = new(1, 1);
    private static DateTime _lastDeviceCardTraceCleanupDate = DateTime.MinValue;
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    /// <summary>通过 IOC 注入项目现有 SqlSugarScope 和 AutoMapper。</summary>
    public EdgeDashboardService(SqlSugarScope dbScope, IMapper mapper)
    {
        _dbScope = dbScope;
        _mapper = mapper;
    }

    /// <summary>按 modelMark 汇总模型大类，让前端先选择小范围后再加载设备。</summary>
    public async Task<List<EdgeDashboardDataSourceModelOptionDto>> GetDataSourceModelsAsync()
    {
        var records = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Select(x => new { x.ModelMark, x.ReceiveTime })
            .ToListAsync();
        var mappings = await Db.Queryable<EdgeDashboardModelDisplayMappingEntity>()
            .Where(x => x.DashboardCode == DefaultDashboardCode && x.Enabled && x.IsDelete == 0)
            .ToListAsync();
        var displayNameMap = mappings
            .GroupBy(x => x.ModelMark, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Sort).First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
        return records
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ModelMark)
                ? UnclassifiedModelMark
                : x.ModelMark.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new EdgeDashboardDataSourceModelOptionDto
            {
                ModelMark = group.Key,
                DisplayName = group.Key == UnclassifiedModelMark
                    ? "未分类"
                    : displayNameMap.GetValueOrDefault(group.Key, group.Key),
                DeviceCount = group.Count(),
                LatestReceiveTime = group.Max(x => x.ReceiveTime)
            })
            .OrderByDescending(x => x.DeviceCount)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    /// <summary>在选定模型内查询最新设备；设备较多时可继续按 deviceMark 远程检索。</summary>
    public async Task<List<EdgeDashboardDataSourceOptionDto>> GetDataSourcesByModelAsync(
        string modelMark,
        string? keyword)
    {
        modelMark = Require(modelMark, "modelMark");
        keyword = Normalize(keyword);
        var unclassified = modelMark.Equals(UnclassifiedModelMark, StringComparison.OrdinalIgnoreCase);
        var records = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(unclassified, x => x.ModelMark == null || x.ModelMark == string.Empty)
            .WhereIF(!unclassified, x => x.ModelMark == modelMark)
            .WhereIF(keyword != null, x =>
                x.DeviceCode.Contains(keyword!) ||
                (x.TenantMark != null && x.TenantMark.Contains(keyword!)))
            .OrderByDescending(x => x.ReceiveTime)
            .Take(500)
            .ToListAsync();
        return _mapper.Map<List<EdgeDashboardDataSourceOptionDto>>(records);
    }

    /// <summary>按关键字查询当前 HTTP 实时快照数据源。</summary>
    public async Task<List<EdgeDashboardDataSourceOptionDto>> GetDataSourcesAsync(string? keyword)
    {
        keyword = Normalize(keyword);
        var onlineDeadline = DateTime.Now.Subtract(EdgeRealtimeDataReception.OnlineWindow);
        var records = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(keyword != null, x =>
                x.DeviceCode.Contains(keyword!) ||
                (x.ModelMark != null && x.ModelMark.Contains(keyword!)) ||
                (x.TenantMark != null && x.TenantMark.Contains(keyword!)))
            .OrderByDescending(x => x.ReceiveTime)
            .Take(500)
            .ToListAsync();
        return records.Select(x => new EdgeDashboardDataSourceOptionDto
        {
            TenantMark = x.TenantMark ?? string.Empty,
            DeviceMark = x.DeviceCode,
            ModelMark = x.ModelMark,
            DataOnline = x.ReceiveTime >= onlineDeadline,
            ReceiveTime = x.ReceiveTime
        }).ToList();
    }

    /// <summary>
    /// 返回设备可用于组态的字段目录：当前快照字段优先，并合并同模型历史接收过的字段。
    /// 同模型历史字段仍可选，但设备实际值只来自该设备字段最新值表，不使用其他设备样例。
    /// </summary>
    public async Task<List<EdgeDashboardSourceFieldOptionDto>> GetSourceFieldsAsync(
        string tenantMark,
        string deviceMark)
    {
        tenantMark = Require(tenantMark, "tenantMark");
        deviceMark = Require(deviceMark, "deviceMark");
        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == tenantMark && x.DeviceCode == deviceMark);
        if (snapshot == null) throw new InvalidOperationException("未找到该 HTTP 数据源的最新快照");
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, [snapshot]);

        var modelMark = NormalizeModelMark(snapshot.ModelMark ?? string.Empty);
        var fields = await QueryModelFieldCatalogAsync(modelMark);
        foreach (var field in fields) field.CurrentValue = null;
        var fieldMap = fields.ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
        // 目录只保留路径；展示值来自这台设备合并后的持久化状态。
        foreach (var current in EdgeDashboardJsonFieldResolver.Flatten(
                     EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot)))
        {
            fieldMap[current.SourcePath] = current;
        }

        // 模型字段展示映射是所有看板组件共用的默认规则。卡片配置页也从这里取值，
        // 避免同一个数据源在模型配置、生产看板和设备卡片中出现不同名称或格式。
        var displayMappings = await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
            .Where(x => x.DashboardCode == DefaultDashboardCode &&
                        x.ModelMark == modelMark &&
                        x.Enabled && x.IsDelete == 0)
            .ToListAsync();
        var mappingByPath = displayMappings
            .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.UpdateTime).First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var field in fieldMap.Values)
        {
            if (!mappingByPath.TryGetValue(field.SourcePath, out var mapping)) continue;
            field.FieldName = mapping.DisplayName;
            field.ValueType = mapping.ValueType;
            field.HasModelDisplayMapping = true;
            field.FormatType = mapping.FormatType;
            field.Unit = mapping.Unit;
            field.Decimals = mapping.Decimals;
            field.MappingJson = mapping.MappingJson;
        }
        return fieldMap.Values.OrderBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>从模型字段目录读取历史接收字段，供模型级报警映射下拉选择。</summary>
    public async Task<List<EdgeDashboardSourceFieldOptionDto>> GetModelSourceFieldsAsync(string modelMark)
    {
        modelMark = NormalizeModelMark(Require(modelMark, "modelMark"));
        var catalogFields = await QueryModelFieldCatalogAsync(modelMark);
        if (catalogFields.Count > 0) return catalogFields;

        // 兼容升级前已存在的实时快照：首次打开配置时把旧快照字段补写到目录表。
        await HydrateModelFieldCatalogFromSnapshotsAsync(modelMark);
        catalogFields = await QueryModelFieldCatalogAsync(modelMark);
        if (catalogFields.Count == 0) throw new InvalidOperationException("未找到该模型的历史 HTTP 数据字段");
        return catalogFields;
    }

    /// <summary>读取模型字段目录，并把保存的 JSON 样例值还原给前端展示。</summary>
    private async Task<List<EdgeDashboardSourceFieldOptionDto>> QueryModelFieldCatalogAsync(string modelMark)
    {
        var records = await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.ModelMark == modelMark && x.IsDelete == 0)
            .ToListAsync();
        var latestRecords = records
            .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.LastSeenTime).First())
            .OrderBy(x => x.SourcePath)
            .ToList();
        var result = _mapper.Map<List<EdgeDashboardSourceFieldOptionDto>>(latestRecords);
        for (var index = 0; index < result.Count; index++)
            result[index].CurrentValue = DeserializeFieldCurrentValue(latestRecords[index].LastValueJson);
        return result;
    }

    /// <summary>老数据升级兜底：从最近快照提取字段并沉淀到模型字段目录。</summary>
    private async Task HydrateModelFieldCatalogFromSnapshotsAsync(string modelMark)
    {
        var snapshots = await QuerySnapshotsByModel(modelMark)
            .OrderByDescending(x => x.ReceiveTime)
            .Take(50)
            .ToListAsync();
        if (snapshots.Count == 0) return;

        var discovered = new Dictionary<string, EdgeDashboardModelFieldCatalogEntity>(
            StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        foreach (var snapshot in snapshots)
        {
            foreach (var field in EdgeDashboardJsonFieldResolver.Flatten(
                         EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot)))
            {
                if (discovered.ContainsKey(field.SourcePath)) continue;
                discovered[field.SourcePath] = new EdgeDashboardModelFieldCatalogEntity
                {
                    ModelMark = modelMark,
                    SourcePath = field.SourcePath,
                    FieldName = field.FieldName,
                    ValueType = field.ValueType,
                    LastValueJson = SerializeFieldCurrentValue(field.CurrentValue),
                    SampleTenantMark = snapshot.TenantMark,
                    SampleDeviceMark = snapshot.DeviceCode,
                    SeenCount = 1,
                    FirstSeenTime = snapshot.ReceiveTime,
                    LastSeenTime = snapshot.ReceiveTime,
                    CreateTime = now,
                    UpdateTime = now
                };
            }
        }
        await UpsertModelFieldCatalogAsync(modelMark, discovered.Values.ToList());
    }

    /// <summary>按模型和字段路径更新字段目录；新字段插入，旧字段刷新最后样例值和最后出现时间。</summary>
    private async Task UpsertModelFieldCatalogAsync(
        string modelMark,
        IReadOnlyCollection<EdgeDashboardModelFieldCatalogEntity> discoveredFields)
    {
        if (discoveredFields.Count == 0) return;

        var now = DateTime.Now;
        var paths = discoveredFields
            .Select(x => x.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = new List<EdgeDashboardModelFieldCatalogEntity>();
        foreach (var pathChunk in paths.Chunk(1000))
        {
            var chunkPaths = pathChunk.ToList();
            existing.AddRange(await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
                .Where(x => x.ModelMark == modelMark && chunkPaths.Contains(x.SourcePath))
                .ToListAsync());
        }
        var existingMap = existing
            .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.LastSeenTime).First())
            .ToDictionary(x => x.SourcePath, StringComparer.OrdinalIgnoreCase);
        var inserts = new List<EdgeDashboardModelFieldCatalogEntity>();
        var updates = new List<EdgeDashboardModelFieldCatalogEntity>();

        foreach (var field in discoveredFields)
        {
            if (existingMap.TryGetValue(field.SourcePath, out var record))
            {
                record.FieldName = field.FieldName;
                record.ValueType = field.ValueType;
                record.LastValueJson = field.LastValueJson;
                record.SampleTenantMark = field.SampleTenantMark;
                record.SampleDeviceMark = field.SampleDeviceMark;
                record.SeenCount = Math.Max(0, record.SeenCount) + 1;
                record.IsDelete = 0;
                record.LastSeenTime = field.LastSeenTime > record.LastSeenTime
                    ? field.LastSeenTime
                    : now;
                record.UpdateTime = now;
                updates.Add(record);
                continue;
            }

            field.Id = 0;
            field.ModelMark = modelMark;
            field.IsDelete = 0;
            field.CreateTime = now;
            field.UpdateTime = now;
            inserts.Add(field);
        }

        foreach (var chunk in updates.Chunk(500))
            await Db.Updateable(chunk.ToList()).ExecuteCommandAsync();
        foreach (var chunk in inserts.Chunk(500))
            await Db.Insertable(chunk.ToList()).ExecuteCommandAsync();
    }

    /// <summary>读取指定模型共享的报警映射规则。</summary>
    public async Task<List<EdgeDashboardModelAlarmRuleOutputDto>> GetModelAlarmRulesAsync(
        string dashboardCode,
        string modelMark)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        modelMark = NormalizeModelMark(Require(modelMark, "modelMark"));
        var records = await Db.Queryable<EdgeDashboardModelAlarmRuleEntity>()
            .Where(x => x.DashboardCode == dashboardCode &&
                        x.ModelMark == modelMark &&
                        x.IsDelete == 0)
            .OrderBy(x => x.Priority)
            .ToListAsync();
        return _mapper.Map<List<EdgeDashboardModelAlarmRuleOutputDto>>(records);
    }

    /// <summary>整体覆盖保存模型级报警映射；同一模型下所有已绑定设备都会使用这套规则。</summary>
    public async Task<List<EdgeDashboardModelAlarmRuleOutputDto>> SaveModelAlarmRulesAsync(
        EdgeDashboardModelAlarmRuleListSaveDto dto)
    {
        var normalizedRules = await ValidateModelAlarmRulesAsync(dto);
        var now = DateTime.Now;
        var existing = await Db.Queryable<EdgeDashboardModelAlarmRuleEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode &&
                        x.ModelMark == dto.ModelMark &&
                        x.IsDelete == 0)
            .ToListAsync();
        var version = existing.Count == 0 ? 1 : existing.Max(x => x.RuleVersion) + 1;

        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Deleteable<EdgeDashboardModelAlarmRuleEntity>()
                .Where(x => x.DashboardCode == dto.DashboardCode &&
                            x.ModelMark == dto.ModelMark)
                .ExecuteCommandAsync();

            var entities = normalizedRules.Select((item, index) =>
            {
                var entity = _mapper.Map<EdgeDashboardModelAlarmRuleEntity>(item);
                entity.Id = 0;
                entity.DashboardCode = dto.DashboardCode;
                entity.ModelMark = dto.ModelMark;
                entity.Priority = item.Priority == 0 ? (index + 1) * 100 : item.Priority;
                entity.RuleVersion = version;
                entity.CreateTime = now;
                entity.UpdateTime = now;
                return entity;
            }).ToList();
            if (entities.Count > 0) await Db.Insertable(entities).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存模型报警映射事务失败");

        return await GetModelAlarmRulesAsync(dto.DashboardCode, dto.ModelMark);
    }

    /// <summary>读取模型共享的字段展示映射，供所有同模型组件复用。</summary>
    public async Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> GetModelFieldDisplayMappingsAsync(
        string dashboardCode,
        string modelMark)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        modelMark = NormalizeModelMark(Require(modelMark, "modelMark"));
        var rows = await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
            .Where(x => x.DashboardCode == dashboardCode &&
                        x.ModelMark == modelMark &&
                        x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        return _mapper.Map<List<EdgeDashboardModelFieldDisplayMappingOutputDto>>(rows);
    }

    /// <summary>整体保存模型字段展示规则；规则仅用于显示，不改写实时原始报文。</summary>
    public async Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> SaveModelFieldDisplayMappingsAsync(
        EdgeDashboardModelFieldDisplayMappingListSaveDto dto)
    {
        var mappings = await ValidateModelFieldDisplayMappingsAsync(dto);
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Deleteable<EdgeDashboardModelFieldDisplayMappingEntity>()
                .Where(x => x.DashboardCode == dto.DashboardCode && x.ModelMark == dto.ModelMark)
                .ExecuteCommandAsync();
            var entities = mappings.Select((item, index) =>
            {
                var entity = _mapper.Map<EdgeDashboardModelFieldDisplayMappingEntity>(item);
                entity.DashboardCode = dto.DashboardCode;
                entity.ModelMark = dto.ModelMark;
                entity.Sort = item.Sort == 0 ? (index + 1) * 10 : item.Sort;
                entity.CreateTime = now;
                entity.UpdateTime = now;
                return entity;
            }).ToList();
            if (entities.Count > 0) await Db.Insertable(entities).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存模型字段展示映射事务失败");
        return await GetModelFieldDisplayMappingsAsync(dto.DashboardCode, dto.ModelMark);
    }

    /// <summary>读取一个模型的状态冲突优先级；未保存过的状态使用系统默认顺序。</summary>
    public async Task<List<EdgeDashboardModelStatePriorityOutputDto>> GetModelStatePrioritiesAsync(
        string dashboardCode,
        string modelMark)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        modelMark = NormalizeModelMark(Require(modelMark, "modelMark"));
        var rows = await Db.Queryable<EdgeDashboardModelStatePriorityEntity>()
            .Where(x => x.DashboardCode == dashboardCode &&
                        x.ModelMark == modelMark &&
                        x.IsDelete == 0)
            .ToListAsync();
        var configured = rows.ToDictionary(x => x.State, StringComparer.OrdinalIgnoreCase);
        return ModelStatePriorityDefaults.Select(item =>
        {
            if (configured.TryGetValue(item.State, out var row))
                return _mapper.Map<EdgeDashboardModelStatePriorityOutputDto>(row);
            return new EdgeDashboardModelStatePriorityOutputDto
            {
                DashboardCode = dashboardCode,
                ModelMark = modelMark,
                State = item.State,
                Priority = item.Priority
            };
        }).ToList();
    }

    /// <summary>整体保存一个模型的状态冲突优先级，数值越小越优先。</summary>
    public async Task<List<EdgeDashboardModelStatePriorityOutputDto>> SaveModelStatePrioritiesAsync(
        EdgeDashboardModelStatePriorityListSaveDto dto)
    {
        var priorities = NormalizeModelStatePriorities(dto);
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Deleteable<EdgeDashboardModelStatePriorityEntity>()
                .Where(x => x.DashboardCode == dto.DashboardCode && x.ModelMark == dto.ModelMark)
                .ExecuteCommandAsync();
            var entities = priorities.Select(item =>
            {
                var entity = _mapper.Map<EdgeDashboardModelStatePriorityEntity>(item);
                entity.DashboardCode = dto.DashboardCode;
                entity.ModelMark = dto.ModelMark;
                entity.CreateTime = now;
                entity.UpdateTime = now;
                return entity;
            }).ToList();
            await Db.Insertable(entities).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存模型状态优先级事务失败");
        return await GetModelStatePrioritiesAsync(dto.DashboardCode, dto.ModelMark);
    }

    /// <summary>查询线体全部机械手绑定以及字段和状态规则。</summary>
    public async Task<List<EdgeDashboardRobotBindingOutputDto>> GetBindingsAsync(string lineCode)
    {
        lineCode = Require(lineCode, "lineCode");
        var bindings = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.IsDelete == 0)
            .OrderBy(x => x.RobotCode)
            .ToListAsync();
        return await BuildBindingOutputsAsync(bindings);
    }

    /// <summary>事务保存绑定主表，并整体替换展示字段和状态规则。</summary>
    public async Task<EdgeDashboardRobotBindingOutputDto> SaveBindingAsync(
        EdgeDashboardRobotBindingSaveDto dto)
    {
        await ValidateBindingAsync(dto);
        var now = DateTime.Now;
        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardRobotBindingEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : await Db.Queryable<EdgeDashboardRobotBindingEntity>().FirstAsync(x =>
                x.DashboardCode == dto.DashboardCode.Trim() &&
                x.LineCode == dto.LineCode.Trim() &&
                x.RobotCode == dto.RobotCode.Trim() &&
                x.IsDelete == 0);

        if (dto.Id > 0 && existing == null)
            throw new InvalidOperationException("机械手绑定不存在");

        var entity = _mapper.Map<EdgeDashboardRobotBindingEntity>(dto);
        entity.UpdateTime = now;
        entity.RuleVersion = existing == null ? 1 : existing.RuleVersion + 1;
        if (existing != null)
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
        }
        else
        {
            entity.CreateTime = now;
        }

        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            if (entity.Id == 0)
                entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
            else
                await Db.Updateable(entity).ExecuteCommandAsync();

            await Db.Deleteable<EdgeDashboardRobotFieldBindingEntity>()
                .Where(x => x.RobotBindingId == entity.Id)
                .ExecuteCommandAsync();
            await Db.Deleteable<EdgeDashboardRobotStateRuleEntity>()
                .Where(x => x.RobotBindingId == entity.Id)
                .ExecuteCommandAsync();

            var fields = dto.Fields.Select((item, index) =>
            {
                var field = _mapper.Map<EdgeDashboardRobotFieldBindingEntity>(item);
                field.Id = 0;
                field.RobotBindingId = entity.Id;
                field.Sort = item.Sort == 0 ? index : item.Sort;
                field.CreateTime = now;
                field.UpdateTime = now;
                return field;
            }).ToList();
            if (fields.Count > 0) await Db.Insertable(fields).ExecuteCommandAsync();

            var rules = dto.StateRules.Select((item, index) =>
            {
                var rule = _mapper.Map<EdgeDashboardRobotStateRuleEntity>(item);
                rule.Id = 0;
                rule.RobotBindingId = entity.Id;
                rule.Priority = item.Priority == 0 ? (index + 1) * 100 : item.Priority;
                rule.CreateTime = now;
                rule.UpdateTime = now;
                return rule;
            }).ToList();
            await Db.Insertable(rules).ExecuteCommandAsync();

            await ResetRuntimeForRuleChangeAsync(entity, now);
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存机械手绑定事务失败");

        var saved = (await BuildBindingOutputsAsync([entity])).Single();
        return saved;
    }

    /// <summary>查询一条线体中 Three.js 设备的数据源绑定。</summary>
    public async Task<List<EdgeDashboardDeviceBindingOutputDto>> GetDeviceBindingsAsync(string lineCode)
    {
        lineCode = Require(lineCode, "lineCode");
        var bindings = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.IsDelete == 0)
            .OrderBy(x => x.DeviceCode)
            .ToListAsync();
        return await BuildDeviceBindingOutputsAsync(bindings);
    }

    /// <summary>保存设备实时数据源、展示字段和可选的日统计状态映射。</summary>
    public async Task<EdgeDashboardDeviceBindingOutputDto> SaveDeviceBindingAsync(
        EdgeDashboardDeviceBindingSaveDto dto)
    {
        await ValidateDeviceBindingAsync(dto);
        var now = DateTime.Now;
        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : await Db.Queryable<EdgeDashboardDeviceBindingEntity>().FirstAsync(x =>
                x.DashboardCode == dto.DashboardCode &&
                x.LineCode == dto.LineCode &&
                x.DeviceCode == dto.DeviceCode &&
                x.IsDelete == 0);
        if (dto.Id > 0 && existing == null)
            throw new InvalidOperationException("设备绑定不存在");

        var entity = _mapper.Map<EdgeDashboardDeviceBindingEntity>(dto);
        entity.UpdateTime = now;
        if (existing == null)
        {
            entity.CreateTime = now;
        }
        else
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
        }

        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            if (entity.Id == 0)
                entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
            else
                await Db.Updateable(entity).ExecuteCommandAsync();

            await Db.Deleteable<EdgeDashboardDeviceFieldBindingEntity>()
                .Where(x => x.DeviceBindingId == entity.Id)
                .ExecuteCommandAsync();
            // 状态来源或映射调整后从下一条样本重新建立统计检查点，避免新旧规则串算。
            await Db.Deleteable<EdgeDashboardDeviceRuntimeStateEntity>()
                .Where(x => x.DeviceBindingId == entity.Id)
                .ExecuteCommandAsync();
            var fields = dto.Fields.Select((item, index) =>
            {
                var field = _mapper.Map<EdgeDashboardDeviceFieldBindingEntity>(item);
                field.Id = 0;
                field.DeviceBindingId = entity.Id;
                field.Sort = item.Sort == 0 ? index : item.Sort;
                field.CreateTime = now;
                field.UpdateTime = now;
                return field;
            }).ToList();
            if (fields.Count > 0) await Db.Insertable(fields).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存设备绑定事务失败");

        return (await BuildDeviceBindingOutputsAsync([entity])).Single();
    }

    /// <summary>读取线体独立的产量字段绑定，不要求该设备已经放入 Three.js 场景。</summary>
    public async Task<EdgeDashboardLineOutputBindingOutputDto?> GetLineOutputBindingAsync(string lineCode)
    {
        lineCode = Require(lineCode, "线体编码");
        var entity = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.IsDelete == 0)
            .OrderByDescending(x => x.UpdateTime)
            .FirstAsync();
        return entity == null ? null : ToLineOutputBinding(entity);
    }

    /// <summary>保存一条线体的产量来源；字段或设备变更后重建当天基准，避免混入旧计数器数据。</summary>
    public async Task<EdgeDashboardLineOutputBindingOutputDto> SaveLineOutputBindingAsync(
        EdgeDashboardLineOutputBindingSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.TenantMark = Require(dto.TenantMark, "租户标识");
        dto.DeviceMark = Require(dto.DeviceMark, "产量来源设备");
        dto.SourcePath = Require(dto.SourcePath, "产量字段");
        dto.FieldName = Require(dto.FieldName, "产量字段名称");
        dto.CounterMode = dto.CounterMode is "Cumulative" or "CurrentValue" ? dto.CounterMode : "Cumulative";
        dto.Decimals = Math.Clamp(dto.Decimals, 0, 3);
        dto.Unit = Normalize(dto.Unit);
        if (dto.DeviceMark.Length > 128 || dto.SourcePath.Length > 512 || dto.FieldName.Length > 128)
            throw new InvalidOperationException("产量来源配置长度超过允许范围");

        var sourceFields = await GetSourceFieldsAsync(dto.TenantMark, dto.DeviceMark);
        var selectedField = sourceFields.FirstOrDefault(x =>
            string.Equals(x.SourcePath, dto.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (selectedField == null)
            throw new InvalidOperationException("当前设备报文中不存在所选产量字段");
        if (!IsNumericValue(selectedField.CurrentValue))
            throw new InvalidOperationException("产量字段必须是数值类型");

        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
                .FirstAsync(x => x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode && x.IsDelete == 0);
        if (dto.Id > 0 && existing == null) throw new InvalidOperationException("线体产量绑定不存在");

        var existingId = existing?.Id ?? 0;
        var duplicate = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode &&
            x.Id != existingId && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("该线体已存在产量绑定");

        var now = DateTime.Now;
        var sourceChanged = existing != null &&
            (!string.Equals(existing.TenantMark, dto.TenantMark, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existing.DeviceMark, dto.DeviceMark, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existing.SourcePath, dto.SourcePath, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existing.CounterMode, dto.CounterMode, StringComparison.OrdinalIgnoreCase));
        var entity = existing ?? new EdgeDashboardLineOutputBindingEntity { CreateTime = now };
        entity.DashboardCode = dto.DashboardCode;
        entity.LineCode = dto.LineCode;
        entity.TenantMark = dto.TenantMark;
        entity.DeviceMark = dto.DeviceMark;
        entity.SourcePath = dto.SourcePath;
        entity.FieldName = dto.FieldName;
        entity.CounterMode = dto.CounterMode;
        entity.Unit = dto.Unit;
        entity.Decimals = dto.Decimals;
        entity.Enabled = dto.Enabled;
        entity.IsDelete = 0;
        entity.UpdateTime = now;
        if (entity.Id == 0)
            entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        else
            await Db.Updateable(entity).ExecuteCommandAsync();

        if (sourceChanged)
            await Db.Deleteable<EdgeDashboardLineOutputDailyStatisticEntity>()
                .Where(x => x.OutputBindingId == entity.Id && x.StatDate == now.Date)
                .ExecuteCommandAsync();
        return ToLineOutputBinding(entity);
    }

    /// <summary>从当前快照读取产量字段，并以持久化检查点累积自然日产出。</summary>
    public async Task<EdgeDashboardLineOutputDto> GetLineOutputAsync(string lineCode)
    {
        lineCode = Require(lineCode, "线体编码");
        var binding = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.Enabled && x.IsDelete == 0)
            .OrderByDescending(x => x.UpdateTime)
            .FirstAsync();
        if (binding == null) return new EdgeDashboardLineOutputDto { LineCode = lineCode };

        var now = DateTime.Now;
        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == binding.TenantMark && x.DeviceCode == binding.DeviceMark);
        var output = new EdgeDashboardLineOutputDto
        {
            Configured = true,
            LineCode = lineCode,
            FieldName = binding.FieldName,
            Unit = binding.Unit,
            Decimals = binding.Decimals,
            DeviceMark = binding.DeviceMark,
            DataTime = snapshot?.DataTime,
            DataOnline = snapshot != null && snapshot.ReceiveTime >= now.AddMinutes(-5)
        };
        if (snapshot == null) return output;
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, [snapshot]);

        var token = EdgeDashboardJsonFieldResolver.Resolve(
            EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot), binding.SourcePath);
        if (!TryReadDecimal(token, out var currentValue)) return output;
        output.CurrentValue = currentValue;

        await LineOutputGate.WaitAsync();
        try
        {
            var daily = await Db.Queryable<EdgeDashboardLineOutputDailyStatisticEntity>()
                .FirstAsync(x => x.OutputBindingId == binding.Id && x.StatDate == now.Date);
            if (daily == null)
            {
                daily = new EdgeDashboardLineOutputDailyStatisticEntity
                {
                    OutputBindingId = binding.Id,
                    StatDate = now.Date,
                    LineCode = lineCode,
                    BaselineValue = currentValue,
                    LastValue = currentValue,
                    OutputQuantity = binding.CounterMode == "CurrentValue" ? currentValue : 0,
                    LastDataTime = snapshot.DataTime,
                    CreateTime = now,
                    UpdateTime = now
                };
                await Db.Insertable(daily).ExecuteCommandAsync();
            }
            else if (!daily.LastDataTime.HasValue || snapshot.DataTime > daily.LastDataTime.Value)
            {
                decimal increase;
                if (binding.CounterMode == "CurrentValue")
                {
                    increase = currentValue - daily.OutputQuantity;
                    daily.OutputQuantity = currentValue;
                }
                else
                {
                    increase = currentValue >= daily.LastValue
                        ? currentValue - daily.LastValue
                        : currentValue; // 设备累计计数器归零后，从归零后的值重新累计。
                    daily.OutputQuantity += Math.Max(0, increase);
                }
                daily.LastValue = currentValue;
                daily.LastDataTime = snapshot.DataTime;
                daily.LineCode = lineCode;
                daily.UpdateTime = now;
                await Db.Updateable(daily).ExecuteCommandAsync();
                output.LatestIncrease = Math.Max(0, increase);
            }
            output.TodayQuantity = daily.OutputQuantity;
        }
        finally
        {
            LineOutputGate.Release();
        }
        return output;
    }

    /// <summary>软删除设备绑定，不影响实时采集原始数据。</summary>
    public async Task DeleteDeviceBindingAsync(long id)
    {
        var binding = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (binding == null) throw new InvalidOperationException("设备绑定不存在");
        binding.IsDelete = 1;
        binding.Enabled = false;
        binding.UpdateTime = DateTime.Now;
        await Db.Updateable(binding).ExecuteCommandAsync();
    }

    /// <summary>读取看板中维护的模型业务名称；无实时数据的预配置模型也会返回。</summary>
    public async Task<List<EdgeDashboardModelDisplayMappingOutputDto>> GetModelDisplayMappingsAsync(
        string dashboardCode)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        var rows = await Db.Queryable<EdgeDashboardModelDisplayMappingEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.ModelMark)
            .ToListAsync();
        return _mapper.Map<List<EdgeDashboardModelDisplayMappingOutputDto>>(rows);
    }

    /// <summary>保存 modelMark 到业务名称的唯一映射。</summary>
    public async Task<EdgeDashboardModelDisplayMappingOutputDto> SaveModelDisplayMappingAsync(
        EdgeDashboardModelDisplayMappingSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.ModelMark = NormalizeModelMark(Require(dto.ModelMark, "模型标识"));
        dto.DisplayName = Require(dto.DisplayName, "显示名称");
        dto.EquipmentType = Normalize(dto.EquipmentType) ?? "Device";
        if (dto.EquipmentType is not ("Robot" or "Device" or "Machine" or "Inspection"))
            throw new InvalidOperationException("设备类别仅支持 Robot、Device、Machine 或 Inspection");

        var duplicate = await Db.Queryable<EdgeDashboardModelDisplayMappingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.ModelMark == dto.ModelMark &&
            x.Id != dto.Id && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("该模型标识已配置映射");

        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardModelDisplayMappingEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : null;
        if (dto.Id > 0 && existing == null) throw new InvalidOperationException("模型映射不存在");

        var now = DateTime.Now;
        var entity = _mapper.Map<EdgeDashboardModelDisplayMappingEntity>(dto);
        entity.UpdateTime = now;
        if (existing == null)
        {
            entity.CreateTime = now;
            entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
            entity.IsDelete = existing.IsDelete;
            await Db.Updateable(entity).ExecuteCommandAsync();
        }
        return _mapper.Map<EdgeDashboardModelDisplayMappingOutputDto>(entity);
    }

    /// <summary>软删除模型显示映射；不影响既有数据源和绑定。</summary>
    public async Task DeleteModelDisplayMappingAsync(long id)
    {
        var row = await Db.Queryable<EdgeDashboardModelDisplayMappingEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (row == null) throw new InvalidOperationException("模型映射不存在");
        row.IsDelete = 1;
        row.Enabled = false;
        row.UpdateTime = DateTime.Now;
        await Db.Updateable(row).ExecuteCommandAsync();
    }

    /// <summary>查询设备卡片配置，供卡片组态页复用。</summary>
    public async Task<List<EdgeDashboardDeviceCardOutputDto>> GetDeviceCardsAsync(string? lineCode)
    {
        lineCode = Normalize(lineCode);
        var cards = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .OrderBy(x => x.LineCode)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        return await BuildDeviceCardOutputsAsync(cards);
    }

    /// <summary>保存卡片及字段。字段配置同时决定页面展示和是否追溯变化。</summary>
    public async Task<EdgeDashboardDeviceCardOutputDto> SaveDeviceCardAsync(
        EdgeDashboardDeviceCardSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.CardCode = Require(dto.CardCode, "卡片编码");
        dto.CardName = Require(dto.CardName, "卡片名称");
        dto.TenantMark = Require(dto.TenantMark, "租户标识");
        dto.DeviceMark = Require(dto.DeviceMark, "设备标识");
        dto.ModelMark = Normalize(dto.ModelMark);
        dto.StatusSourcePath = Normalize(dto.StatusSourcePath);
        dto.StatusMappingJson = Normalize(dto.StatusMappingJson);
        dto.OnlineTimeoutSeconds = Math.Clamp(dto.OnlineTimeoutSeconds, 5, 86400);
        if (dto.CardCode.Length > 64 || dto.CardName.Length > 128 || dto.DeviceMark.Length > 128)
            throw new InvalidOperationException("设备卡片配置长度超过允许范围");
        if (!string.IsNullOrWhiteSpace(dto.StatusMappingJson))
            ParseJsonObject(dto.StatusMappingJson, "状态值映射");

        var sourceFields = await GetSourceFieldsAsync(dto.TenantMark, dto.DeviceMark);
        var sourceFieldsByPath = sourceFields.ToDictionary(
            x => x.SourcePath,
            StringComparer.OrdinalIgnoreCase);
        var sourcePaths = sourceFieldsByPath.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dto.StatusSourcePath != null)
            dto.StatusSourcePath = ValidateSourcePath(dto.StatusSourcePath, sourcePaths);

        var fieldCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < dto.Fields.Count; index++)
        {
            var field = dto.Fields[index];
            field.FieldCode = Normalize(field.FieldCode) ?? $"field_{index + 1}";
            field.SourcePath = ValidateSourcePath(field.SourcePath, sourcePaths);
            var sourceField = sourceFieldsByPath[field.SourcePath];
            if (field.UseModelDisplayMapping && sourceField.HasModelDisplayMapping)
            {
                field.DisplayName = sourceField.FieldName;
                field.ValueType = sourceField.ValueType;
                field.FormatType = sourceField.FormatType;
                field.Unit = sourceField.Unit;
                field.Decimals = sourceField.Decimals;
                field.MappingJson = sourceField.MappingJson;
            }
            field.DisplayName = Require(field.DisplayName, "卡片展示字段名称");
            field.DisplayMode = field.DisplayMode is "Primary" or "Tag" or "Detail"
                ? field.DisplayMode : "Detail";
            field.FormatType = Normalize(field.FormatType) ?? "Direct";
            field.ValueType = Normalize(field.ValueType) ?? "String";
            field.Unit = Normalize(field.Unit);
            field.Decimals = field.Decimals.HasValue
                ? Math.Clamp(field.Decimals.Value, 0, 8) : null;
            field.TraceThreshold = field.TraceThreshold.HasValue
                ? Math.Max(0, field.TraceThreshold.Value) : null;
            if ((field.AlarmLowerLimit.HasValue || field.AlarmUpperLimit.HasValue) &&
                !IsDeviceCardNumericValueType(field.ValueType))
                throw new InvalidOperationException($"字段“{field.DisplayName}”不是数值类型，不能配置上下限");
            if (field.AlarmLowerLimit.HasValue && field.AlarmUpperLimit.HasValue &&
                field.AlarmLowerLimit.Value >= field.AlarmUpperLimit.Value)
                throw new InvalidOperationException($"字段“{field.DisplayName}”的下限必须小于上限");
            if (!string.IsNullOrWhiteSpace(field.MappingJson))
                ParseJsonObject(field.MappingJson, "字段值映射");
            if (!fieldCodes.Add(field.FieldCode))
                throw new InvalidOperationException("同一设备卡片中的字段编码不能重复");
        }

        await EdgeDeviceCardPushConfiguration.ValidateAsync(Db, dto);
        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardDeviceCardEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : await Db.Queryable<EdgeDashboardDeviceCardEntity>().FirstAsync(x =>
                x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode &&
                x.CardCode == dto.CardCode && x.IsDelete == 0);
        if (dto.Id > 0 && existing == null) throw new InvalidOperationException("设备卡片不存在");
        var existingId = existing?.Id ?? 0;
        var duplicate = await Db.Queryable<EdgeDashboardDeviceCardEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode &&
            x.CardCode == dto.CardCode && x.Id != existingId && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("该线体已存在相同的设备卡片编码");

        var now = DateTime.Now;
        var entity = _mapper.Map<EdgeDashboardDeviceCardEntity>(dto);
        entity.Id = existing?.Id ?? 0;
        entity.IsDelete = 0;
        entity.CreateTime = existing?.CreateTime ?? now;
        entity.UpdateTime = now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            if (entity.Id == 0)
                entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
            else
                await Db.Updateable(entity).ExecuteCommandAsync();

            await Db.Deleteable<EdgeDashboardDeviceCardFieldEntity>()
                .Where(x => x.CardId == entity.Id)
                .ExecuteCommandAsync();
            await Db.Deleteable<EdgeDashboardDeviceCardAlertStateEntity>()
                .Where(x => x.CardId == entity.Id)
                .ExecuteCommandAsync();
            var fields = dto.Fields.Select((item, index) =>
            {
                var field = _mapper.Map<EdgeDashboardDeviceCardFieldEntity>(item);
                field.CardId = entity.Id;
                field.Sort = item.Sort == 0 ? (index + 1) * 10 : item.Sort;
                field.CreateTime = now;
                field.UpdateTime = now;
                return field;
            }).ToList();
            if (fields.Count > 0) await Db.Insertable(fields).ExecuteCommandAsync();
            await EdgeDeviceCardPushConfiguration.SaveAsync(Db, dto, entity.Id);
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存设备卡片事务失败");
        return (await BuildDeviceCardOutputsAsync([entity])).Single();
    }

    public async Task DeleteDeviceCardAsync(long id)
    {
        var card = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (card == null) throw new InvalidOperationException("设备卡片不存在");
        var now = DateTime.Now;
        await Db.Ado.UseTranAsync(async () =>
        {
            card.IsDelete = 1;
            card.Enabled = false;
            card.UpdateTime = now;
            await Db.Updateable(card).ExecuteCommandAsync();
            await Db.Updateable<EdgeDashboardDeviceCardFieldEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardFieldEntity
                {
                    IsDelete = 1,
                    Enabled = false,
                    UpdateTime = now
                })
                .Where(x => x.CardId == id && x.IsDelete == 0)
                .ExecuteCommandAsync();
            await Db.Deleteable<EdgeDashboardDeviceCardAlertStateEntity>()
                .Where(x => x.CardId == id)
                .ExecuteCommandAsync();
        });
    }

    public async Task<EdgeDashboardDeviceCardBoardSettingDto> SaveDeviceCardBoardSettingAsync(
        EdgeDashboardDeviceCardBoardSettingDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.ColumnCount = Math.Clamp(dto.ColumnCount, 1, 6);
        var row = await Db.Queryable<EdgeDashboardDeviceCardBoardSettingEntity>().FirstAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode);
        var now = DateTime.Now;
        if (row == null)
        {
            row = new EdgeDashboardDeviceCardBoardSettingEntity
            {
                DashboardCode = dto.DashboardCode,
                LineCode = dto.LineCode,
                ColumnCount = dto.ColumnCount,
                CreateTime = now,
                UpdateTime = now
            };
            row.Id = await Db.Insertable(row).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            row.ColumnCount = dto.ColumnCount;
            row.UpdateTime = now;
            await Db.Updateable(row).ExecuteCommandAsync();
        }
        return new EdgeDashboardDeviceCardBoardSettingDto
        {
            Id = row.Id, DashboardCode = row.DashboardCode, LineCode = row.LineCode,
            ColumnCount = row.ColumnCount
        };
    }

    /// <summary>批量读取卡片实时值和顶部概览，避免浏览器按卡片逐个请求。</summary>
    public async Task<EdgeDashboardDeviceCardBoardRuntimeDto> GetDeviceCardBoardRuntimeAsync(string? lineCode)
    {
        lineCode = Normalize(lineCode);
        var now = DateTime.Now;
        var cards = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled)
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .OrderBy(x => x.LineCode).OrderBy(x => x.Sort)
            .ToListAsync();
        var output = new EdgeDashboardDeviceCardBoardRuntimeDto { LineCode = lineCode, GeneratedAt = now };
        if (cards.Count == 0) return output;

        if (lineCode != null)
        {
            var setting = await Db.Queryable<EdgeDashboardDeviceCardBoardSettingEntity>().FirstAsync(x =>
                x.DashboardCode == "device-card-dashboard" && x.LineCode == lineCode);
            output.ColumnCount = setting == null ? 4 : Math.Clamp(setting.ColumnCount, 1, 6);
        }

        var cardIds = cards.Select(x => x.Id).ToList();
        var fields = await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
            .Where(x => cardIds.Contains(x.CardId) && x.IsDelete == 0 && x.Enabled)
            .OrderBy(x => x.Sort).ToListAsync();
        var tenantMarks = cards.Select(x => x.TenantMark).Distinct().ToList();
        var deviceMarks = cards.Select(x => x.DeviceMark).Distinct().ToList();
        var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Where(x => tenantMarks.Contains(x.TenantMark!) && deviceMarks.Contains(x.DeviceCode))
            .ToListAsync();
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, snapshots);
        var snapshotMap = snapshots.ToDictionary(
            x => $"{x.TenantMark}\u001f{x.DeviceCode}", StringComparer.OrdinalIgnoreCase);
        var modelMarks = cards
            .Select(x => NormalizeModelMark(x.ModelMark ?? string.Empty))
            .Concat(snapshots.Select(x => NormalizeModelMark(x.ModelMark ?? string.Empty)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayMappings = modelMarks.Count == 0
            ? new List<EdgeDashboardModelFieldDisplayMappingEntity>()
            : await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
                .Where(x => x.DashboardCode == DefaultDashboardCode &&
                            modelMarks.Contains(x.ModelMark) &&
                            x.Enabled && x.IsDelete == 0)
                .ToListAsync();

        foreach (var card in cards)
        {
            snapshotMap.TryGetValue($"{card.TenantMark}\u001f{card.DeviceMark}", out var snapshot);
            var dataOnline = snapshot != null && snapshot.ReceiveTime >= now.AddSeconds(-card.OnlineTimeoutSeconds);
            var envelope = snapshot == null ? null : EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot);
            var runtime = new EdgeDashboardDeviceCardRuntimeDto
            {
                CardId = card.Id,
                LineCode = card.LineCode,
                CardCode = card.CardCode,
                CardName = card.CardName,
                DeviceMark = card.DeviceMark,
                ModelMark = card.ModelMark ?? snapshot?.ModelMark,
                DataOnline = dataOnline,
                DataTime = snapshot?.DataTime,
                ReceiveTime = snapshot?.ReceiveTime,
                Status = ResolveDeviceCardStatus(card, envelope, dataOnline)
            };
            var modelMark = NormalizeModelMark(card.ModelMark ?? snapshot?.ModelMark ?? string.Empty);
            foreach (var field in fields.Where(x => x.CardId == card.Id))
            {
                var raw = envelope == null ? null : EdgeDashboardJsonFieldResolver.Resolve(envelope, field.SourcePath);
                var displayMapping = field.UseModelDisplayMapping
                    ? FindModelFieldDisplayMapping(
                        displayMappings,
                        DefaultDashboardCode,
                        modelMark,
                        field.SourcePath)
                    : null;
                runtime.Fields.Add(new EdgeDashboardDeviceCardRuntimeFieldDto
                {
                    FieldCode = field.FieldCode,
                    DisplayName = displayMapping?.DisplayName ?? field.DisplayName,
                    DisplayMode = field.DisplayMode,
                    RawValue = EdgeDashboardJsonFieldResolver.ToObject(raw),
                    DisplayValue = displayMapping == null
                        ? FormatDeviceCardFieldValue(raw, field)
                        : FormatModelFieldValue(raw, displayMapping),
                    Unit = displayMapping?.Unit ?? field.Unit,
                    Sort = field.Sort
                });
            }
            output.Cards.Add(runtime);
        }

        output.Overview.TotalCount = output.Cards.Count;
        output.Overview.OnlineCount = output.Cards.Count(x => x.DataOnline);
        output.Overview.OfflineCount = output.Overview.TotalCount - output.Overview.OnlineCount;
        output.Overview.FaultCount = output.Cards.Count(x => x.Status.Equals("FAULT", StringComparison.OrdinalIgnoreCase));
        output.Overview.AlarmCount = output.Cards.Count(x =>
            x.Status.Equals("ALARM", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Equals("WARNING", StringComparison.OrdinalIgnoreCase));
        var todayRecords = await Db.Queryable<EdgeDashboardDeviceCardChangeRecordEntity>()
            .Where(x => cardIds.Contains(x.CardId) && x.CreateTime >= now.Date)
            .Select(x => new { x.CardId }).ToListAsync();
        var todayChangeCountMap = todayRecords
            .GroupBy(x => x.CardId)
            .ToDictionary(x => x.Key, x => x.Count());
        foreach (var runtimeCard in output.Cards)
            runtimeCard.TodayChangeCount = todayChangeCountMap.GetValueOrDefault(runtimeCard.CardId);
        output.Overview.TodayChangeCount = todayRecords.Count;
        output.Overview.TodayChangedDeviceCount = todayRecords.Select(x => x.CardId).Distinct().Count();
        return output;
    }

    public async Task<EdgePagedResultDto<EdgeDashboardDeviceCardChangeRecordOutputDto>> GetDeviceCardChangeRecordsAsync(
        EdgeDashboardDeviceCardChangeQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 500);
        var lineCode = Normalize(query.LineCode);
        var deviceMark = Normalize(query.DeviceMark);
        var fieldCode = Normalize(query.FieldCode);
        var dateToExclusive = GetDateToExclusive(query.DateTo);
        RefAsync<int> total = 0;
        var rows = await Db.Queryable<EdgeDashboardDeviceCardChangeRecordEntity>()
            .WhereIF(query.CardId.HasValue, x => x.CardId == query.CardId!.Value)
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(deviceMark != null, x => x.DeviceMark == deviceMark)
            .WhereIF(fieldCode != null, x => x.FieldCode == fieldCode)
            .WhereIF(query.DateFrom.HasValue, x => x.DataTime >= query.DateFrom!.Value)
            .WhereIF(dateToExclusive.HasValue, x => x.DataTime < dateToExclusive!.Value)
            .WhereIF(query.DateTo.HasValue && !dateToExclusive.HasValue, x => x.DataTime <= query.DateTo!.Value)
            .OrderByDescending(x => x.DataTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeDashboardDeviceCardChangeRecordOutputDto>
        {
            Records = _mapper.Map<List<EdgeDashboardDeviceCardChangeRecordOutputDto>>(rows),
            Current = page, Size = size, Total = total
        };
    }

    /// <summary>分页查询由设备字段上下限触发的告警与恢复事件。</summary>
    public async Task<EdgePagedResultDto<EdgeDashboardDeviceCardAlertRecordOutputDto>> GetDeviceCardAlertRecordsAsync(
        EdgeDashboardDeviceCardAlertQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 500);
        var lineCode = Normalize(query.LineCode);
        var deviceMark = Normalize(query.DeviceMark);
        var fieldCode = Normalize(query.FieldCode);
        var alertType = Normalize(query.AlertType);
        var pushStatus = Normalize(query.PushStatus);
        var dateToExclusive = GetDateToExclusive(query.DateTo);
        RefAsync<int> total = 0;
        var rows = await Db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>()
            .WhereIF(query.CardId.HasValue, x => x.CardId == query.CardId!.Value)
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(deviceMark != null, x => x.DeviceMark == deviceMark)
            .WhereIF(fieldCode != null, x => x.FieldCode == fieldCode)
            .WhereIF(alertType != null, x => x.AlertType == alertType)
            .WhereIF(pushStatus != null, x => x.PushStatus == pushStatus)
            .WhereIF(query.DateFrom.HasValue, x => x.DataTime >= query.DateFrom!.Value)
            .WhereIF(dateToExclusive.HasValue, x => x.DataTime < dateToExclusive!.Value)
            .WhereIF(query.DateTo.HasValue && !dateToExclusive.HasValue, x => x.DataTime <= query.DateTo!.Value)
            .OrderByDescending(x => x.DataTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeDashboardDeviceCardAlertRecordOutputDto>
        {
            Records = _mapper.Map<List<EdgeDashboardDeviceCardAlertRecordOutputDto>>(rows),
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>
    /// 原子认领待推送的越限事件。认领租约到期且尚未确认的事件会自动恢复为待推送，避免智能体异常时永久丢失。
    /// </summary>
    public async Task<List<EdgeDashboardDeviceCardAlertRecordOutputDto>> ClaimDeviceCardAlertsAsync(
        string consumerCode,
        int maximum,
        string? lineCode,
        string? deviceMark,
        string? fieldCode,
        CancellationToken cancellationToken = default)
    {
        consumerCode = Require(consumerCode, "consumerCode");
        maximum = Math.Clamp(maximum, 1, 100);
        lineCode = Normalize(lineCode);
        deviceMark = Normalize(deviceMark);
        fieldCode = Normalize(fieldCode);
        var now = DateTime.Now;
        var expireTime = now.AddMinutes(5);

        await DeviceCardAlertClaimGate.WaitAsync(cancellationToken);
        try
        {
            await Db.Updateable<EdgeDashboardDeviceCardAlertRecordEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardAlertRecordEntity
                {
                    PushStatus = "Pending",
                    ClaimedBy = null,
                    ClaimExpireTime = null,
                    UpdateTime = now
                })
                .Where(x => x.PushStatus == "Processing" && x.ClaimExpireTime != null && x.ClaimExpireTime <= now)
                .ExecuteCommandAsync();

            var candidates = await Db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>()
                .Where(x => x.PushStatus == "Pending")
                .WhereIF(lineCode != null, x => x.LineCode == lineCode)
                .WhereIF(deviceMark != null, x => x.DeviceMark == deviceMark)
                .WhereIF(fieldCode != null, x => x.FieldCode == fieldCode)
                .OrderBy(x => x.DataTime)
                .Take(maximum)
                .ToListAsync();
            var claimed = new List<EdgeDashboardDeviceCardAlertRecordEntity>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var affected = await Db.Updateable<EdgeDashboardDeviceCardAlertRecordEntity>()
                    .SetColumns(x => new EdgeDashboardDeviceCardAlertRecordEntity
                    {
                        PushStatus = "Processing",
                        ClaimedBy = consumerCode,
                        ClaimExpireTime = expireTime,
                        UpdateTime = now
                    })
                    .Where(x => x.Id == candidate.Id && x.PushStatus == "Pending")
                    .ExecuteCommandAsync();
                if (affected != 0)
                {
                    candidate.PushStatus = "Processing";
                    candidate.ClaimedBy = consumerCode;
                    candidate.ClaimExpireTime = expireTime;
                    candidate.UpdateTime = now;
                    claimed.Add(candidate);
                }
            }
            return _mapper.Map<List<EdgeDashboardDeviceCardAlertRecordOutputDto>>(claimed);
        }
        finally
        {
            DeviceCardAlertClaimGate.Release();
        }
    }

    /// <summary>推送智能体完成消息发送后确认事件；失败确认会立即退回待推送状态。</summary>
    public async Task<int> AcknowledgeDeviceCardAlertsAsync(
        string consumerCode,
        IReadOnlyCollection<long> alertIds,
        bool sent,
        string? message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        consumerCode = Require(consumerCode, "consumerCode");
        var ids = alertIds.Where(x => x > 0).Distinct().Take(100).ToList();
        if (ids.Count == 0) throw new ArgumentException("alertIds 至少包含一条有效告警记录");
        var now = DateTime.Now;
        var note = TrimToNull(message, 1000);
        if (sent)
        {
            return await Db.Updateable<EdgeDashboardDeviceCardAlertRecordEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardAlertRecordEntity
                {
                    PushStatus = "Sent",
                    ClaimedBy = consumerCode,
                    ClaimExpireTime = null,
                    PushedTime = now,
                    LastPushError = null,
                    UpdateTime = now
                })
                .Where(x => ids.Contains(x.Id) && x.PushStatus == "Processing" && x.ClaimedBy == consumerCode)
                .ExecuteCommandAsync();
        }
        return await Db.Updateable<EdgeDashboardDeviceCardAlertRecordEntity>()
            .SetColumns(x => new EdgeDashboardDeviceCardAlertRecordEntity
            {
                PushStatus = "Pending",
                ClaimedBy = null,
                ClaimExpireTime = null,
                PushedTime = null,
                LastPushError = note,
                UpdateTime = now
            })
            .Where(x => ids.Contains(x.Id) && x.PushStatus == "Processing" && x.ClaimedBy == consumerCode)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// 处理设备卡片字段的普通变化追溯和数值越限。两种状态分别维护，变化阈值不会抑制越限事件。
    /// </summary>
    public async Task TrackDeviceCardChangesAsync(
        EdgeDashboardTelemetrySampleDto sample,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sample.TenantMark) || string.IsNullOrWhiteSpace(sample.DeviceMark)) return;
        await DeviceCardTraceGate.WaitAsync(cancellationToken);
        try
        {
            var cards = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled &&
                            x.TenantMark == sample.TenantMark && x.DeviceMark == sample.DeviceMark)
                .ToListAsync();
            if (cards.Count == 0) return;
            var cardIds = cards.Select(x => x.Id).ToList();
            var fields = await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
                .Where(x => cardIds.Contains(x.CardId) && x.IsDelete == 0 && x.Enabled &&
                            (x.TraceEnabled || x.AlarmLowerLimit != null || x.AlarmUpperLimit != null))
                .ToListAsync();
            if (fields.Count == 0) return;
            var traceStates = await Db.Queryable<EdgeDashboardDeviceCardTraceStateEntity>()
                .Where(x => cardIds.Contains(x.CardId))
                .ToListAsync();
            var alertStates = await Db.Queryable<EdgeDashboardDeviceCardAlertStateEntity>()
                .Where(x => cardIds.Contains(x.CardId))
                .ToListAsync();
            var traceStateMap = traceStates.ToDictionary(x => $"{x.CardId}\u001f{x.FieldCode}", StringComparer.OrdinalIgnoreCase);
            var alertStateMap = alertStates.ToDictionary(x => $"{x.CardId}\u001f{x.FieldCode}", StringComparer.OrdinalIgnoreCase);
            var cardMap = cards.ToDictionary(x => x.Id);
            var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(sample);
            var reportedEnvelope = EdgeDashboardJsonFieldResolver.BuildReportedEnvelope(sample);
            var now = DateTime.Now;
            foreach (var field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EdgeDashboardJsonFieldResolver.ResolveIncludingNull(reportedEnvelope, field.SourcePath) == null)
                    continue; // 缺失不是事件，不更新该字段的追溯时间，也不产生额外数据库写入。
                var token = EdgeDashboardJsonFieldResolver.ResolveIncludingNull(envelope, field.SourcePath);
                if (token == null) continue; // 数据源暂未提供字段时不把缺失误判为变更。
                var valueJson = token.ToString(Formatting.None);
                var stateKey = $"{field.CardId}\u001f{field.FieldCode}";
                var card = cardMap[field.CardId];
                if (HasDeviceCardAlarmLimit(field))
                {
                    await TrackDeviceCardAlertAsync(
                        card,
                        field,
                        valueJson,
                        sample,
                        now,
                        alertStateMap,
                        cancellationToken);
                }

                if (!field.TraceEnabled) continue;
                if (traceStateMap.TryGetValue(stateKey, out var newerState) && sample.DataTime < newerState.LastDataTime)
                    continue; // 并发接收后的后处理可能乱序，不能把追溯检查点退回旧值。
                if (!traceStateMap.TryGetValue(stateKey, out var state))
                {
                    state = new EdgeDashboardDeviceCardTraceStateEntity
                    {
                        CardId = field.CardId,
                        FieldCode = field.FieldCode,
                        LastValueJson = valueJson,
                        LastDataTime = sample.DataTime,
                        UpdateTime = now
                    };
                    state.Id = await Db.Insertable(state).ExecuteReturnBigIdentityAsync();
                    traceStateMap[stateKey] = state;
                    continue;
                }
                if (!HasDeviceCardValueChanged(state.LastValueJson, valueJson, field.TraceThreshold))
                {
                    if (sample.DataTime > state.LastDataTime)
                    {
                        state.LastDataTime = sample.DataTime;
                        state.UpdateTime = now;
                        await Db.Updateable(state).ExecuteCommandAsync();
                    }
                    continue;
                }
                await Db.Insertable(new EdgeDashboardDeviceCardChangeRecordEntity
                {
                    CardId = card.Id,
                    LineCode = card.LineCode,
                    DeviceMark = card.DeviceMark,
                    FieldCode = field.FieldCode,
                    DisplayName = field.DisplayName,
                    SourcePath = field.SourcePath,
                    OldValueJson = state.LastValueJson,
                    NewValueJson = valueJson,
                    MessageId = sample.MessageId,
                    DataTime = sample.DataTime,
                    ReceiveTime = sample.ReceiveTime,
                    CreateTime = now
                }).ExecuteCommandAsync();
                state.LastValueJson = valueJson;
                state.LastDataTime = sample.DataTime;
                state.UpdateTime = now;
                await Db.Updateable(state).ExecuteCommandAsync();
            }
            if (_lastDeviceCardTraceCleanupDate < now.Date)
            {
                await Db.Deleteable<EdgeDashboardDeviceCardChangeRecordEntity>()
                    .Where(x => x.CreateTime < now.AddDays(-180))
                    .ExecuteCommandAsync();
                await Db.Deleteable<EdgeDashboardDeviceCardAlertRecordEntity>()
                    .Where(x => x.CreateTime < now.AddDays(-180))
                    .ExecuteCommandAsync();
                _lastDeviceCardTraceCleanupDate = now.Date;
            }
        }
        finally
        {
            DeviceCardTraceGate.Release();
        }
    }

    /// <summary>软删除绑定，并关闭活动故障，避免统计状态继续运行。</summary>
    public async Task DeleteBindingAsync(long id)
    {
        var binding = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (binding == null) throw new InvalidOperationException("机械手绑定不存在");
        var now = DateTime.Now;
        binding.IsDelete = 1;
        binding.Enabled = false;
        binding.UpdateTime = now;
        await Db.Updateable(binding).ExecuteCommandAsync();
        await CloseActiveFaultsAsync(binding.Id, now, null);
        await Db.Deleteable<EdgeDashboardRobotRuntimeStateEntity>()
            .Where(x => x.RobotBindingId == binding.Id)
            .ExecuteCommandAsync();
    }

    /// <summary>批量解析一条线体的机械手当前状态和多字段显示值。</summary>
    public async Task<EdgeDashboardLineRuntimeDto> GetLineRuntimeAsync(string lineCode)
    {
        lineCode = Require(lineCode, "lineCode");
        var bindings = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.Enabled && x.IsDelete == 0)
            .OrderBy(x => x.RobotCode)
            .ToListAsync();
        var deviceBindings = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
            .Where(x => x.LineCode == lineCode && x.Enabled && x.IsDelete == 0)
            .OrderBy(x => x.DeviceCode)
            .ToListAsync();
        if (bindings.Count == 0 && deviceBindings.Count == 0)
            return new EdgeDashboardLineRuntimeDto { LineCode = lineCode };

        var bindingIds = bindings.Select(x => x.Id).ToList();
        var deviceBindingIds = deviceBindings.Select(x => x.Id).ToList();
        var deviceMarks = bindings.Select(x => x.DeviceMark)
            .Concat(deviceBindings.Select(x => x.SourceDeviceMark))
            .Distinct()
            .ToList();
        var fields = await Db.Queryable<EdgeDashboardRobotFieldBindingEntity>()
            .Where(x => bindingIds.Contains(x.RobotBindingId) && x.Enabled && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var states = await Db.Queryable<EdgeDashboardRobotRuntimeStateEntity>()
            .Where(x => bindingIds.Contains(x.RobotBindingId))
            .ToListAsync();
        var deviceFields = await Db.Queryable<EdgeDashboardDeviceFieldBindingEntity>()
            .Where(x => deviceBindingIds.Contains(x.DeviceBindingId) && x.Enabled && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Where(x => deviceMarks.Contains(x.DeviceCode))
            .ToListAsync();
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, snapshots);
        var deviceDashboardCodes = deviceBindings.Select(x => x.DashboardCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deviceModelMarks = snapshots.Select(x => NormalizeModelMark(x.ModelMark ?? string.Empty))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var modelAlarmRules = deviceDashboardCodes.Count == 0 || deviceModelMarks.Count == 0
            ? new List<EdgeDashboardModelAlarmRuleEntity>()
            : await Db.Queryable<EdgeDashboardModelAlarmRuleEntity>()
                .Where(x => deviceDashboardCodes.Contains(x.DashboardCode) &&
                            deviceModelMarks.Contains(x.ModelMark) &&
                            x.Enabled && x.IsDelete == 0)
                .OrderBy(x => x.Priority)
                .ToListAsync();
        var modelStatePriorities = deviceDashboardCodes.Count == 0 || deviceModelMarks.Count == 0
            ? new List<EdgeDashboardModelStatePriorityEntity>()
            : await Db.Queryable<EdgeDashboardModelStatePriorityEntity>()
                .Where(x => deviceDashboardCodes.Contains(x.DashboardCode) &&
                            deviceModelMarks.Contains(x.ModelMark) &&
                            x.IsDelete == 0)
                .ToListAsync();
        var displayDashboardCodes = bindings.Select(x => x.DashboardCode)
            .Concat(deviceBindings.Select(x => x.DashboardCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var modelFieldDisplayMappings = displayDashboardCodes.Count == 0 || deviceModelMarks.Count == 0
            ? new List<EdgeDashboardModelFieldDisplayMappingEntity>()
            : await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
                .Where(x => displayDashboardCodes.Contains(x.DashboardCode) &&
                            deviceModelMarks.Contains(x.ModelMark) &&
                            x.Enabled && x.IsDelete == 0)
                .ToListAsync();

        var fieldGroups = fields.GroupBy(x => x.RobotBindingId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var deviceFieldGroups = deviceFields.GroupBy(x => x.DeviceBindingId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var stateMap = states.ToDictionary(x => x.RobotBindingId);
        var snapshotMap = snapshots.ToDictionary(
            x => $"{x.TenantMark}\u001f{x.DeviceCode}",
            StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var output = new EdgeDashboardLineRuntimeDto { LineCode = lineCode, GeneratedAt = now };

        foreach (var binding in bindings)
        {
            snapshotMap.TryGetValue($"{binding.TenantMark}\u001f{binding.DeviceMark}", out var snapshot);
            stateMap.TryGetValue(binding.Id, out var runtimeState);
            var online = snapshot != null &&
                         snapshot.ReceiveTime >= now.AddSeconds(-binding.OnlineTimeoutSeconds);
            var robot = new EdgeDashboardRobotRuntimeDto
            {
                BindingId = binding.Id,
                LineCode = binding.LineCode,
                RobotCode = binding.RobotCode,
                RobotName = binding.RobotName,
                TenantMark = binding.TenantMark,
                DeviceMark = binding.DeviceMark,
                Status = online
                    ? runtimeState?.CurrentState ?? EdgeDashboardConstants.Unknown
                    : EdgeDashboardConstants.Offline,
                DataOnline = online,
                DataTime = snapshot?.DataTime,
                ReceiveTime = snapshot?.ReceiveTime
            };

            if (snapshot != null)
            {
                var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot);
                var modelMark = NormalizeModelMark(snapshot.ModelMark ?? string.Empty);
                foreach (var field in fieldGroups.GetValueOrDefault(binding.Id) ?? [])
                {
                    var rawToken = EdgeDashboardJsonFieldResolver.Resolve(envelope, field.SourcePath);
                    var rawValue = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
                    var displayMapping = FindModelFieldDisplayMapping(
                        modelFieldDisplayMappings,
                        binding.DashboardCode,
                        modelMark,
                        field.SourcePath);
                    robot.Fields.Add(new EdgeDashboardRuntimeFieldDto
                    {
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = displayMapping?.DisplayName ?? field.DisplayName,
                        Purpose = field.Purpose,
                        RawValue = rawValue,
                        DisplayValue = displayMapping == null
                            ? FormatFieldValue(rawToken, field)
                            : FormatModelFieldValue(rawToken, displayMapping),
                        Unit = displayMapping?.Unit ?? field.Unit,
                        Sort = field.Sort
                    });
                }
            }
            output.Robots.Add(robot);
        }
        foreach (var binding in deviceBindings)
        {
            snapshotMap.TryGetValue($"{binding.TenantMark}\u001f{binding.SourceDeviceMark}", out var snapshot);
            var online = snapshot != null &&
                         snapshot.ReceiveTime >= now.AddSeconds(-binding.OnlineTimeoutSeconds);
            var device = new EdgeDashboardDeviceRuntimeDto
            {
                BindingId = binding.Id,
                LineCode = binding.LineCode,
                DeviceCode = binding.DeviceCode,
                DeviceName = binding.DeviceName,
                TenantMark = binding.TenantMark,
                SourceDeviceMark = binding.SourceDeviceMark,
                DataOnline = online,
                Status = online ? EdgeDashboardConstants.Running : EdgeDashboardConstants.Offline,
                DataTime = snapshot?.DataTime,
                ReceiveTime = snapshot?.ReceiveTime
            };
            if (snapshot != null)
            {
                var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot);
                var modelMark = NormalizeModelMark(snapshot.ModelMark ?? string.Empty);
                if (online)
                {
                    var sourceState = EdgeDashboardConstants.Running;
                    if (binding.StatisticsEnabled)
                    {
                        if (TryResolveDeviceMappedState(binding, envelope, out var mappedState))
                            sourceState = mappedState;
                        else if (!string.IsNullOrWhiteSpace(binding.StatusSourcePath))
                            sourceState = EdgeDashboardConstants.Unknown;
                    }
                    var matchedAlarm = modelAlarmRules
                        .Where(rule => rule.DashboardCode == binding.DashboardCode &&
                                       rule.ModelMark == modelMark &&
                                       MatchesDeviceModelAlarmRule(
                                           rule,
                                           EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.SourcePath)))
                        .OrderBy(rule => rule.Priority)
                        .FirstOrDefault();
                    if (matchedAlarm != null)
                    {
                        device.CurrentAlarm = ResolveDeviceModelAlarmMessage(matchedAlarm, envelope);
                    }
                    var finalState = ResolveDeviceRuntimeState(
                        sourceState,
                        matchedAlarm != null,
                        modelStatePriorities.Where(x =>
                            x.DashboardCode == binding.DashboardCode && x.ModelMark == modelMark));
                    device.Status = finalState == EdgeDashboardConstants.Fault
                        ? string.Equals(matchedAlarm?.FaultSeverity, "Critical", StringComparison.OrdinalIgnoreCase)
                            ? "ALARM"
                            : "WARNING"
                        : finalState;
                }
                foreach (var field in deviceFieldGroups.GetValueOrDefault(binding.Id) ?? [])
                {
                    var rawToken = EdgeDashboardJsonFieldResolver.Resolve(envelope, field.SourcePath);
                    var displayMapping = FindModelFieldDisplayMapping(
                        modelFieldDisplayMappings,
                        binding.DashboardCode,
                        modelMark,
                        field.SourcePath);
                    device.Fields.Add(new EdgeDashboardRuntimeFieldDto
                    {
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = displayMapping?.DisplayName ?? field.DisplayName,
                        Purpose = "DisplayValue",
                        RawValue = EdgeDashboardJsonFieldResolver.ToObject(rawToken),
                        DisplayValue = displayMapping == null
                            ? FormatDeviceFieldValue(rawToken, field)
                            : FormatModelFieldValue(rawToken, displayMapping),
                        Unit = displayMapping?.Unit ?? field.Unit,
                        Sort = field.Sort
                    });
                }
            }
            output.Devices.Add(device);
        }
        return output;
    }

    /// <summary>分页查询每日统计并计算运行率。</summary>
    public async Task<EdgePagedResultDto<EdgeDashboardDailyStatisticOutputDto>> GetDailyStatisticsAsync(
        EdgeDashboardDailyStatisticQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 500);
        var lineCode = Normalize(query.LineCode);
        var componentCode = Normalize(query.RobotCode);
        var componentType = Normalize(query.ComponentType)?.ToUpperInvariant() ?? "ALL";
        if (componentType is not ("ALL" or "ROBOT" or "DEVICE"))
            throw new InvalidOperationException("componentType 仅支持 All、Robot 或 Device");
        var dateFrom = query.DateFrom?.Date;
        var dateTo = query.DateTo?.Date;
        var robotRecords = componentType == "DEVICE"
            ? []
            : await Db.Queryable<EdgeDashboardRobotDailyStatisticEntity>()
                .WhereIF(lineCode != null, x => x.LineCode == lineCode)
                .WhereIF(componentCode != null, x => x.RobotCode == componentCode)
                .WhereIF(dateFrom.HasValue, x => x.StatDate >= dateFrom!.Value)
                .WhereIF(dateTo.HasValue, x => x.StatDate <= dateTo!.Value)
                .ToListAsync();
        var deviceRecords = componentType == "ROBOT"
            ? []
            : await Db.Queryable<EdgeDashboardEquipmentDailyStatisticEntity>()
                .Where(x => x.ComponentType == "Device")
                .WhereIF(lineCode != null, x => x.LineCode == lineCode)
                .WhereIF(componentCode != null, x => x.ComponentCode == componentCode)
                .WhereIF(dateFrom.HasValue, x => x.StatDate >= dateFrom!.Value)
                .WhereIF(dateTo.HasValue, x => x.StatDate <= dateTo!.Value)
                .ToListAsync();
        var outputs = _mapper.Map<List<EdgeDashboardDailyStatisticOutputDto>>(robotRecords);
        foreach (var output in outputs)
        {
            output.ComponentType = "Robot";
            output.ComponentBindingId = output.RobotBindingId;
            output.ComponentCode = output.RobotCode;
            output.ComponentName = output.RobotName;
        }
        outputs.AddRange(deviceRecords.Select(ToDailyStatisticOutput));
        foreach (var output in outputs)
        {
            var effective = output.RunningSeconds + output.IdleSeconds + output.FaultSeconds;
            output.AvailabilityRate = effective == 0
                ? 0
                : Math.Round(output.RunningSeconds * 100m / effective, 2);
        }
        return new EdgePagedResultDto<EdgeDashboardDailyStatisticOutputDto>
        {
            Records = outputs
                .OrderByDescending(x => x.StatDate)
                .ThenBy(x => x.ComponentType)
                .ThenBy(x => x.ComponentCode)
                .Skip((page - 1) * size)
                .Take(size)
                .ToList(),
            Current = page,
            Size = size,
            Total = outputs.Count
        };
    }

    /// <summary>分页查询故障记录。</summary>
    public async Task<EdgePagedResultDto<EdgeDashboardFaultRecordOutputDto>> GetFaultRecordsAsync(
        EdgeDashboardFaultQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 500);
        var lineCode = Normalize(query.LineCode);
        var robotCode = Normalize(query.RobotCode);
        var reason = Normalize(query.FaultReason);
        var status = Normalize(query.Status);
        var dateToExclusive = GetDateToExclusive(query.DateTo);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(robotCode != null, x => x.RobotCode == robotCode)
            .WhereIF(reason != null, x => x.FaultReason.Contains(reason!))
            .WhereIF(status != null, x => x.Status == status)
            .WhereIF(query.DateFrom.HasValue, x => x.StartTime >= query.DateFrom!.Value)
            .WhereIF(dateToExclusive.HasValue, x => x.StartTime < dateToExclusive!.Value)
            .WhereIF(query.DateTo.HasValue && !dateToExclusive.HasValue,
                x => x.StartTime <= query.DateTo!.Value)
            .OrderByDescending(x => x.StartTime)
            .ToPageListAsync(page, size, total);
        var outputs = _mapper.Map<List<EdgeDashboardFaultRecordOutputDto>>(records);
        NormalizeModelFaultReasons(outputs);
        RefreshFaultRecordDurations(outputs, DateTime.Now);
        return new EdgePagedResultDto<EdgeDashboardFaultRecordOutputDto>
        {
            Records = outputs,
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>使用 SQL 分组统计高频故障原因。</summary>
    public async Task<List<EdgeDashboardFaultSummaryDto>> GetFaultSummaryAsync(
        EdgeDashboardFaultQueryDto query,
        int topN)
    {
        var lineCode = Normalize(query.LineCode);
        var robotCode = Normalize(query.RobotCode);
        var dateToExclusive = GetDateToExclusive(query.DateTo);
        var groupedRecords = await Db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(robotCode != null, x => x.RobotCode == robotCode)
            .WhereIF(query.DateFrom.HasValue, x => x.StartTime >= query.DateFrom!.Value)
            .WhereIF(dateToExclusive.HasValue, x => x.StartTime < dateToExclusive!.Value)
            .WhereIF(query.DateTo.HasValue && !dateToExclusive.HasValue,
                x => x.StartTime <= query.DateTo!.Value)
            .GroupBy(x => new { x.FaultCode, x.RuleScope, x.FaultReason })
            .Select(x => new
            {
                FaultCode = x.FaultCode,
                RuleScope = x.RuleScope,
                FaultReason = x.FaultReason,
                FaultCount = SqlFunc.AggregateCount(x.Id),
                DurationSeconds = SqlFunc.AggregateSum(x.DurationSeconds)
            })
            .ToListAsync();

        // 历史模型报警可能把判断字段值（例如 1）写入原因，汇总时统一按 FaultCode 归类。
        return groupedRecords
            .Select(x => new EdgeDashboardFaultSummaryDto
            {
                FaultCode = x.FaultCode,
                FaultReason = IsModelRuleScope(x.RuleScope) ? x.FaultCode : x.FaultReason,
                FaultCount = x.FaultCount,
                DurationSeconds = x.DurationSeconds
            })
            .GroupBy(x => new { x.FaultCode, x.FaultReason })
            .Select(x => new EdgeDashboardFaultSummaryDto
            {
                FaultCode = x.Key.FaultCode,
                FaultReason = x.Key.FaultReason,
                FaultCount = x.Sum(item => item.FaultCount),
                DurationSeconds = x.Sum(item => item.DurationSeconds)
            })
            .OrderByDescending(x => x.FaultCount)
            .Take(Math.Clamp(topN, 1, 100))
            .ToList();
    }

    /// <summary>查询部件配置；首次查询一条线体时生成可编辑默认部件。</summary>
    public async Task<List<EdgeDashboardWidgetOutputDto>> GetWidgetsAsync(
        string dashboardCode,
        string? lineCode)
    {
        dashboardCode = Require(dashboardCode, "dashboardCode");
        lineCode = Normalize(lineCode);
        await EnsureDefaultWidgetsAsync(dashboardCode, lineCode);
        var widgets = await Db.Queryable<EdgeDashboardWidgetEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0 && x.Enabled)
            .WhereIF(lineCode != null, x => x.LineCode == null || x.LineCode == lineCode)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        return await BuildWidgetOutputsAsync(widgets);
    }

    /// <summary>事务保存通用部件及字段角色配置。</summary>
    public async Task<EdgeDashboardWidgetOutputDto> SaveWidgetAsync(EdgeDashboardWidgetSaveDto dto)
    {
        await ValidateWidgetAsync(dto);
        var now = DateTime.Now;
        var entity = _mapper.Map<EdgeDashboardWidgetEntity>(dto);
        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardWidgetEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : null;
        if (dto.Id > 0 && existing == null) throw new InvalidOperationException("看板部件不存在");
        if (existing != null)
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
        }
        entity.UpdateTime = now;

        try
        {
            var transaction = await Db.Ado.UseTranAsync(async () =>
            {
                if (entity.Id == 0)
                    entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
                else
                    await Db.Updateable(entity).ExecuteCommandAsync();

                await Db.Deleteable<EdgeDashboardWidgetFieldEntity>()
                    .Where(x => x.WidgetId == entity.Id)
                    .ExecuteCommandAsync();

                var fields = dto.Fields.Select((item, index) =>
                {
                    var field = _mapper.Map<EdgeDashboardWidgetFieldEntity>(item);
                    field.Id = 0;
                    field.WidgetId = entity.Id;
                    field.Sort = item.Sort == 0 ? index : item.Sort;
                    field.CreateTime = now;
                    field.UpdateTime = now;
                    return field;
                }).ToList();

                if (fields.Count > 0)
                    await Db.Insertable(fields).ExecuteCommandAsync();
            });

            // 正常走到这里代表事务提交成功
            if (!transaction.IsSuccess)
                throw transaction.ErrorException ?? new InvalidOperationException("保存看板部件事务失败");
        }
        catch
        {
            throw;
        }

        return (await BuildWidgetOutputsAsync([entity])).Single();
    }

    /// <summary>软删除一个看板部件。</summary>
    public async Task DeleteWidgetAsync(long id)
    {
        var widget = await Db.Queryable<EdgeDashboardWidgetEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (widget == null) throw new InvalidOperationException("看板部件不存在");
        widget.IsDelete = 1;
        widget.Enabled = false;
        widget.UpdateTime = DateTime.Now;
        await Db.Updateable(widget).ExecuteCommandAsync();
    }

    /// <summary>返回前端配置器可用的安全字段目录。</summary>
    public List<EdgeDashboardFieldCatalogDto> GetWidgetFieldCatalog() =>
    [
        Field("statDate", "日期", "DateTime", "DailyStatistics"),
        Field("lineCode", "线体", "String", "RealtimeRobot", "DailyStatistics", "FaultRecords"),
        Field("componentType", "组件类型", "String", "DailyStatistics"),
        Field("componentCode", "组件编码", "String", "DailyStatistics"),
        Field("componentName", "组件名称", "String", "DailyStatistics"),
        Field("robotCode", "机械手编码", "String", "RealtimeRobot", "DailyStatistics", "FaultRecords"),
        Field("robotName", "机械手名称", "String", "RealtimeRobot", "DailyStatistics", "FaultRecords"),
        Field("deviceMark", "设备标识", "String", "RealtimeRobot", "FaultRecords"),
        Field("status", "当前状态", "String", "RealtimeRobot", "FaultRecords"),
        Field("dataOnline", "数据在线", "Boolean", "RealtimeRobot"),
        Field("receiveTime", "接收时间", "DateTime", "RealtimeRobot"),
        Field("runningSeconds", "运行时间", "Duration", "DailyStatistics"),
        Field("idleSeconds", "空闲时间", "Duration", "DailyStatistics"),
        Field("faultSeconds", "故障时间", "Duration", "DailyStatistics"),
        Field("stoppedSeconds", "停止时间", "Duration", "DailyStatistics"),
        Field("offlineSeconds", "离线时间", "Duration", "DailyStatistics"),
        Field("unknownSeconds", "未知时间", "Duration", "DailyStatistics"),
        Field("availabilityRate", "运行率", "Number", "DailyStatistics"),
        Field("faultCount", "故障次数", "Number", "DailyStatistics", "FaultSummary"),
        Field("faultCode", "故障编码", "String", "FaultRecords", "FaultSummary"),
        Field("faultReason", "故障原因", "String", "FaultRecords", "FaultSummary"),
        Field("severity", "故障等级", "String", "FaultRecords"),
        Field("startTime", "开始时间", "DateTime", "FaultRecords"),
        Field("endTime", "结束时间", "DateTime", "FaultRecords"),
        Field("durationSeconds", "持续时间", "Duration", "FaultRecords", "FaultSummary")
    ];

    /// <summary>
    /// 一次读取线体主配置和全部场景，避免看板首次进入时按线体重复请求。
    /// 旧版本仅有场景表时，会把场景中的线体元数据一次性迁移到线体主表。
    /// </summary>
    public async Task<EdgeDashboardBootstrapOutputDto> GetBootstrapAsync(
        string dashboardCode,
        bool migrateLegacyScenes = true)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        var scenes = await Db.Queryable<EdgeDashboardSceneLayoutEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
            .OrderBy(x => x.Id)
            .ToListAsync();
        var lines = await Db.Queryable<EdgeDashboardLineConfigEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.Id)
            .ToListAsync();

        var hasLineHistory = await Db.Queryable<EdgeDashboardLineConfigEntity>()
            .AnyAsync(x => x.DashboardCode == dashboardCode);
        if (migrateLegacyScenes && lines.Count == 0 && !hasLineHistory && scenes.Count > 0)
        {
            var migratedLines = scenes.Select((scene, index) =>
                CreateLineConfigFromScene(scene, index * 10)).ToList();
            await Db.Insertable(migratedLines).ExecuteCommandAsync();
            lines = await Db.Queryable<EdgeDashboardLineConfigEntity>()
                .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
                .OrderBy(x => x.Sort)
                .OrderBy(x => x.Id)
                .ToListAsync();
        }

        return new EdgeDashboardBootstrapOutputDto
        {
            DashboardCode = dashboardCode,
            Lines = _mapper.Map<List<EdgeDashboardLineConfigOutputDto>>(lines),
            SceneLayouts = _mapper.Map<List<EdgeDashboardSceneLayoutOutputDto>>(scenes)
        };
    }

    /// <summary>校验并新增或修改一条线体主配置。</summary>
    public async Task<EdgeDashboardLineConfigOutputDto> SaveLineConfigAsync(
        EdgeDashboardLineConfigSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.LineName = Require(dto.LineName, "线体名称");
        dto.Area = Normalize(dto.Area) ?? string.Empty;
        dto.Color = Require(dto.Color, "线体颜色");
        dto.Sort = Math.Clamp(dto.Sort, -100_000, 100_000);
        if (dto.DashboardCode.Length > 64 || dto.LineCode.Length > 64 || dto.LineName.Length > 128)
            throw new InvalidOperationException("看板编码、线体编码或线体名称超过允许长度");
        if (dto.Area.Length > 128)
            throw new InvalidOperationException("线体区域不能超过 128 个字符");
        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Color, "^#[0-9a-fA-F]{6}$"))
            throw new InvalidOperationException("线体颜色必须是六位十六进制颜色");

        var existing = dto.Id > 0
            ? await Db.Queryable<EdgeDashboardLineConfigEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0)
            : await Db.Queryable<EdgeDashboardLineConfigEntity>().FirstAsync(x =>
                x.DashboardCode == dto.DashboardCode &&
                x.LineCode == dto.LineCode &&
                x.IsDelete == 0);
        if (dto.Id > 0 && existing == null)
            throw new InvalidOperationException("线体配置不存在");

        // 先在表达式外求出值，避免 SqlSugar 解析闭包中的“对象为空 ? 0 : 对象.Id”时访问空对象。
        var existingId = existing?.Id ?? 0;
        var duplicate = await Db.Queryable<EdgeDashboardLineConfigEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.LineCode &&
            x.Id != existingId &&
            x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("同一看板的线体编码不能重复");

        var now = DateTime.Now;
        var entity = _mapper.Map<EdgeDashboardLineConfigEntity>(dto);
        entity.UpdateTime = now;
        if (existing == null)
        {
            entity.CreateTime = now;
            var scene = new EdgeDashboardSceneLayoutEntity
            {
                DashboardCode = entity.DashboardCode,
                LineCode = entity.LineCode,
                LineName = entity.LineName,
                LayoutVersion = 1,
                Revision = 1,
                LayoutJson = CreateEmptySceneLayoutJson(entity.LineCode, entity.LineName, now),
                CreateTime = now,
                UpdateTime = now
            };
            var transaction = await Db.Ado.UseTranAsync(async () =>
            {
                entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
                await Db.Insertable(scene).ExecuteCommandAsync();
            });
            if (!transaction.IsSuccess)
                throw transaction.ErrorException ?? new InvalidOperationException("新增线体和初始场景事务失败");
        }
        else
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
            await Db.Updateable(entity).ExecuteCommandAsync();
        }

        return _mapper.Map<EdgeDashboardLineConfigOutputDto>(entity);
    }

    /// <summary>
    /// 复制一条线体的可编辑配置。新线体从零开始接收实时状态与统计，绝不复用源线体的历史记录或运行状态。
    /// </summary>
    public async Task<EdgeDashboardLineConfigOutputDto> CopyLineConfigAsync(
        EdgeDashboardLineCopyDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.SourceLineCode = Require(dto.SourceLineCode, "来源线体编码");
        dto.TargetLineCode = Require(dto.TargetLineCode, "新线体编码");
        dto.TargetLineName = Require(dto.TargetLineName, "新线体名称");
        dto.Area = Normalize(dto.Area) ?? string.Empty;
        dto.Color = Normalize(dto.Color) ?? "#55d5ff";
        if (dto.DashboardCode.Length > 64 || dto.SourceLineCode.Length > 64 ||
            dto.TargetLineCode.Length > 64 || dto.TargetLineName.Length > 128 ||
            dto.Area.Length > 128 || dto.Color.Length > 16)
            throw new InvalidOperationException("线体复制参数长度不合法");
        if (string.Equals(dto.SourceLineCode, dto.TargetLineCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新线体编码不能与来源线体编码相同");

        var sourceLine = await Db.Queryable<EdgeDashboardLineConfigEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.SourceLineCode &&
                             x.IsDelete == 0);
        if (sourceLine == null) throw new InvalidOperationException("来源线体不存在或已删除");

        var sourceScene = await Db.Queryable<EdgeDashboardSceneLayoutEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.SourceLineCode &&
                             x.IsDelete == 0);
        var sourceRobotBindings = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode &&
                        x.LineCode == dto.SourceLineCode && x.IsDelete == 0)
            .ToListAsync();
        var sourceRobotBindingIds = sourceRobotBindings.Select(x => x.Id).ToList();
        var sourceRobotFields = sourceRobotBindingIds.Count == 0
            ? []
            : await Db.Queryable<EdgeDashboardRobotFieldBindingEntity>()
                .Where(x => sourceRobotBindingIds.Contains(x.RobotBindingId) && x.IsDelete == 0)
                .ToListAsync();
        var sourceRobotRules = sourceRobotBindingIds.Count == 0
            ? []
            : await Db.Queryable<EdgeDashboardRobotStateRuleEntity>()
                .Where(x => sourceRobotBindingIds.Contains(x.RobotBindingId) && x.IsDelete == 0)
                .ToListAsync();

        var sourceDeviceBindings = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode &&
                        x.LineCode == dto.SourceLineCode && x.IsDelete == 0)
            .ToListAsync();
        var sourceDeviceBindingIds = sourceDeviceBindings.Select(x => x.Id).ToList();
        var sourceDeviceFields = sourceDeviceBindingIds.Count == 0
            ? []
            : await Db.Queryable<EdgeDashboardDeviceFieldBindingEntity>()
                .Where(x => sourceDeviceBindingIds.Contains(x.DeviceBindingId) && x.IsDelete == 0)
                .ToListAsync();

        var sourceOutputBinding = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.SourceLineCode && x.IsDelete == 0);
        var sourceWidgets = await Db.Queryable<EdgeDashboardWidgetEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode &&
                        x.LineCode == dto.SourceLineCode && x.IsDelete == 0)
            .ToListAsync();
        var sourceWidgetIds = sourceWidgets.Select(x => x.Id).ToList();
        var sourceWidgetFields = sourceWidgetIds.Count == 0
            ? []
            : await Db.Queryable<EdgeDashboardWidgetFieldEntity>()
                .Where(x => sourceWidgetIds.Contains(x.WidgetId) && x.IsDelete == 0)
                .ToListAsync();

        var sourceCards = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
            .Where(x => x.LineCode == dto.SourceLineCode && x.IsDelete == 0)
            .ToListAsync();
        var sourceCardIds = sourceCards.Select(x => x.Id).ToList();
        var sourceCardFields = sourceCardIds.Count == 0
            ? []
            : await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
                .Where(x => sourceCardIds.Contains(x.CardId) && x.IsDelete == 0)
                .ToListAsync();
        var sourceCardBoardSettings = await Db.Queryable<EdgeDashboardDeviceCardBoardSettingEntity>()
            .Where(x => x.LineCode == dto.SourceLineCode)
            .ToListAsync();

        var targetHasConfiguration = await HasLineConfigurationAsync(dto.DashboardCode, dto.TargetLineCode);
        if (targetHasConfiguration)
            throw new InvalidOperationException("新线体编码已存在关联配置，请更换编码");

        var now = DateTime.Now;
        var layoutVersion = sourceScene?.LayoutVersion ?? 1;
        var layoutJson = sourceScene == null
            ? CreateEmptySceneLayoutJson(dto.TargetLineCode, dto.TargetLineName, now)
            : BuildCopiedSceneLayoutJson(sourceScene.LayoutJson, dto, now);
        layoutJson = ValidateSceneLayoutJson(layoutJson, dto.TargetLineCode, layoutVersion);
        var usedWidgetCodes = (await Db.Queryable<EdgeDashboardWidgetEntity>()
                .Where(x => x.DashboardCode == dto.DashboardCode && x.IsDelete == 0)
                .Select(x => x.WidgetCode)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetLine = new EdgeDashboardLineConfigEntity
        {
            DashboardCode = dto.DashboardCode,
            LineCode = dto.TargetLineCode,
            LineName = dto.TargetLineName,
            Area = dto.Area,
            Color = dto.Color,
            Enabled = dto.Enabled,
            Sort = dto.Sort,
            CreateTime = now,
            UpdateTime = now
        };

        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            targetLine.Id = await Db.Insertable(targetLine).ExecuteReturnBigIdentityAsync();
            await Db.Insertable(new EdgeDashboardSceneLayoutEntity
            {
                DashboardCode = dto.DashboardCode,
                LineCode = dto.TargetLineCode,
                LineName = dto.TargetLineName,
                LayoutVersion = layoutVersion,
                Revision = 1,
                LayoutJson = layoutJson,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();

            foreach (var source in sourceRobotBindings)
            {
                var target = new EdgeDashboardRobotBindingEntity
                {
                    DashboardCode = dto.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    RobotCode = source.RobotCode,
                    RobotName = source.RobotName,
                    TenantMark = source.TenantMark,
                    DeviceMark = source.DeviceMark,
                    OnlineTimeoutSeconds = source.OnlineTimeoutSeconds,
                    BusinessDayStartHour = source.BusinessDayStartHour,
                    RuleVersion = source.RuleVersion,
                    Enabled = source.Enabled,
                    CreateTime = now,
                    UpdateTime = now
                };
                target.Id = await Db.Insertable(target).ExecuteReturnBigIdentityAsync();
                foreach (var field in sourceRobotFields.Where(x => x.RobotBindingId == source.Id))
                {
                    await Db.Insertable(new EdgeDashboardRobotFieldBindingEntity
                    {
                        RobotBindingId = target.Id,
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = field.DisplayName,
                        Purpose = field.Purpose,
                        ValueType = field.ValueType,
                        FormatType = field.FormatType,
                        Unit = field.Unit,
                        Decimals = field.Decimals,
                        MappingJson = field.MappingJson,
                        Sort = field.Sort,
                        Enabled = field.Enabled,
                        CreateTime = now,
                        UpdateTime = now
                    }).ExecuteCommandAsync();
                }
                foreach (var rule in sourceRobotRules.Where(x => x.RobotBindingId == source.Id))
                {
                    await Db.Insertable(new EdgeDashboardRobotStateRuleEntity
                    {
                        RobotBindingId = target.Id,
                        RuleCode = rule.RuleCode,
                        RuleName = rule.RuleName,
                        TargetState = rule.TargetState,
                        SourcePath = rule.SourcePath,
                        Operator = rule.Operator,
                        CompareValue = rule.CompareValue,
                        FaultCode = rule.FaultCode,
                        FaultReason = rule.FaultReason,
                        FaultReasonPath = rule.FaultReasonPath,
                        FaultSeverity = rule.FaultSeverity,
                        Priority = rule.Priority,
                        Enabled = rule.Enabled,
                        CreateTime = now,
                        UpdateTime = now
                    }).ExecuteCommandAsync();
                }
            }

            foreach (var source in sourceDeviceBindings)
            {
                var target = new EdgeDashboardDeviceBindingEntity
                {
                    DashboardCode = dto.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    DeviceCode = source.DeviceCode,
                    DeviceName = source.DeviceName,
                    TenantMark = source.TenantMark,
                    SourceDeviceMark = source.SourceDeviceMark,
                    OnlineTimeoutSeconds = source.OnlineTimeoutSeconds,
                    Enabled = source.Enabled,
                    CreateTime = now,
                    UpdateTime = now
                };
                target.Id = await Db.Insertable(target).ExecuteReturnBigIdentityAsync();
                foreach (var field in sourceDeviceFields.Where(x => x.DeviceBindingId == source.Id))
                {
                    await Db.Insertable(new EdgeDashboardDeviceFieldBindingEntity
                    {
                        DeviceBindingId = target.Id,
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = field.DisplayName,
                        ValueType = field.ValueType,
                        FormatType = field.FormatType,
                        Unit = field.Unit,
                        Decimals = field.Decimals,
                        MappingJson = field.MappingJson,
                        Sort = field.Sort,
                        Enabled = field.Enabled,
                        CreateTime = now,
                        UpdateTime = now
                    }).ExecuteCommandAsync();
                }
            }

            if (sourceOutputBinding != null)
            {
                await Db.Insertable(new EdgeDashboardLineOutputBindingEntity
                {
                    DashboardCode = dto.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    TenantMark = sourceOutputBinding.TenantMark,
                    DeviceMark = sourceOutputBinding.DeviceMark,
                    SourcePath = sourceOutputBinding.SourcePath,
                    FieldName = sourceOutputBinding.FieldName,
                    CounterMode = sourceOutputBinding.CounterMode,
                    Unit = sourceOutputBinding.Unit,
                    Decimals = sourceOutputBinding.Decimals,
                    Enabled = sourceOutputBinding.Enabled,
                    CreateTime = now,
                    UpdateTime = now
                }).ExecuteCommandAsync();
            }

            foreach (var source in sourceWidgets)
            {
                var target = new EdgeDashboardWidgetEntity
                {
                    DashboardCode = dto.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    WidgetCode = BuildCopiedWidgetCode(source.WidgetCode, dto.TargetLineCode, usedWidgetCodes),
                    Title = source.Title,
                    WidgetType = source.WidgetType,
                    DataSourceType = source.DataSourceType,
                    Period = source.Period,
                    RefreshSeconds = source.RefreshSeconds,
                    TopN = source.TopN,
                    Stacked = source.Stacked,
                    Orientation = source.Orientation,
                    ConfigJson = source.ConfigJson,
                    Sort = source.Sort,
                    Enabled = source.Enabled,
                    CreateTime = now,
                    UpdateTime = now
                };
                target.Id = await Db.Insertable(target).ExecuteReturnBigIdentityAsync();
                foreach (var field in sourceWidgetFields.Where(x => x.WidgetId == source.Id))
                {
                    await Db.Insertable(new EdgeDashboardWidgetFieldEntity
                    {
                        WidgetId = target.Id,
                        FieldKey = field.FieldKey,
                        DisplayName = field.DisplayName,
                        Role = field.Role,
                        Aggregate = field.Aggregate,
                        Unit = field.Unit,
                        Decimals = field.Decimals,
                        Color = field.Color,
                        Width = field.Width,
                        Sort = field.Sort,
                        Visible = field.Visible,
                        CreateTime = now,
                        UpdateTime = now
                    }).ExecuteCommandAsync();
                }
            }

            foreach (var source in sourceCards)
            {
                var target = new EdgeDashboardDeviceCardEntity
                {
                    DashboardCode = source.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    CardCode = source.CardCode,
                    CardName = source.CardName,
                    TenantMark = source.TenantMark,
                    DeviceMark = source.DeviceMark,
                    ModelMark = source.ModelMark,
                    StatusSourcePath = source.StatusSourcePath,
                    StatusMappingJson = source.StatusMappingJson,
                    OnlineTimeoutSeconds = source.OnlineTimeoutSeconds,
                    Sort = source.Sort,
                    Enabled = source.Enabled,
                    CreateTime = now,
                    UpdateTime = now
                };
                target.Id = await Db.Insertable(target).ExecuteReturnBigIdentityAsync();
                foreach (var field in sourceCardFields.Where(x => x.CardId == source.Id))
                {
                    await Db.Insertable(new EdgeDashboardDeviceCardFieldEntity
                    {
                        CardId = target.Id,
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = field.DisplayName,
                        DisplayMode = field.DisplayMode,
                        ValueType = field.ValueType,
                        FormatType = field.FormatType,
                        Unit = field.Unit,
                        Decimals = field.Decimals,
                        MappingJson = field.MappingJson,
                        UseModelDisplayMapping = field.UseModelDisplayMapping,
                        TraceEnabled = field.TraceEnabled,
                        TraceThreshold = field.TraceThreshold,
                        AiAnalysisEnabled = field.AiAnalysisEnabled,
                        Sort = field.Sort,
                        Enabled = field.Enabled,
                        CreateTime = now,
                        UpdateTime = now
                    }).ExecuteCommandAsync();
                }
            }

            foreach (var setting in sourceCardBoardSettings)
            {
                await Db.Insertable(new EdgeDashboardDeviceCardBoardSettingEntity
                {
                    DashboardCode = setting.DashboardCode,
                    LineCode = dto.TargetLineCode,
                    ColumnCount = setting.ColumnCount,
                    CreateTime = now,
                    UpdateTime = now
                }).ExecuteCommandAsync();
            }
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("线体配置复制失败");

        return _mapper.Map<EdgeDashboardLineConfigOutputDto>(targetLine);
    }

    /// <summary>软删除线体主配置和场景，历史统计及机械手绑定继续保留。</summary>
    public async Task DeleteLineConfigAsync(string dashboardCode, string lineCode)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        lineCode = Require(lineCode, "线体编码");
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Updateable<EdgeDashboardLineConfigEntity>()
                .SetColumns(x => new EdgeDashboardLineConfigEntity
                {
                    IsDelete = 1,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dashboardCode &&
                            x.LineCode == lineCode &&
                            x.IsDelete == 0)
                .ExecuteCommandAsync();
            await Db.Updateable<EdgeDashboardSceneLayoutEntity>()
                .SetColumns(x => new EdgeDashboardSceneLayoutEntity
                {
                    IsDelete = 1,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dashboardCode &&
                            x.LineCode == lineCode &&
                            x.IsDelete == 0)
                .ExecuteCommandAsync();
            await Db.Updateable<EdgeDashboardDeviceBindingEntity>()
                .SetColumns(x => new EdgeDashboardDeviceBindingEntity
                {
                    IsDelete = 1,
                    Enabled = false,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dashboardCode &&
                            x.LineCode == lineCode &&
                            x.IsDelete == 0)
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("线体配置删除失败");
    }

    /// <summary>按看板和线体读取最新有效的 Three.js 场景组态。</summary>
    public async Task<EdgeDashboardSceneLayoutOutputDto?> GetSceneLayoutAsync(
        string dashboardCode,
        string lineCode)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        lineCode = Require(lineCode, "线体编码");
        var entity = await Db.Queryable<EdgeDashboardSceneLayoutEntity>()
            .FirstAsync(x => x.DashboardCode == dashboardCode &&
                             x.LineCode == lineCode &&
                             x.IsDelete == 0);
        return entity == null
            ? null
            : _mapper.Map<EdgeDashboardSceneLayoutOutputDto>(entity);
    }

    /// <summary>校验场景 JSON，并按看板和线体新增或覆盖数据库配置。</summary>
    public async Task<EdgeDashboardSceneLayoutOutputDto> SaveSceneLayoutAsync(
        EdgeDashboardSceneLayoutSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.LineName = Require(dto.LineName, "线体名称");
        if (dto.LayoutVersion < 1)
            throw new InvalidOperationException("场景布局版本必须大于 0");
        dto.LayoutJson = ValidateSceneLayoutJson(
            dto.LayoutJson,
            dto.LineCode,
            dto.LayoutVersion);

        var now = DateTime.Now;
        var existing = await Db.Queryable<EdgeDashboardSceneLayoutEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.LineCode &&
                             x.IsDelete == 0);
        var entity = _mapper.Map<EdgeDashboardSceneLayoutEntity>(dto);
        entity.UpdateTime = now;
        entity.Revision = existing == null ? 1 : existing.Revision + 1;
        if (existing == null)
        {
            entity.CreateTime = now;
            entity.Id = await Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        }
        else
        {
            entity.Id = existing.Id;
            entity.CreateTime = existing.CreateTime;
            await Db.Updateable(entity).ExecuteCommandAsync();
        }

        return _mapper.Map<EdgeDashboardSceneLayoutOutputDto>(entity);
    }

    /// <summary>
    /// 修改线体编码，并在同一事务内同步场景、机械手/设备绑定、统计、故障和图表配置。
    /// 机械手编码保持不变，避免破坏已经建立的数据源绑定和历史统计主键。
    /// </summary>
    public async Task<EdgeDashboardSceneLayoutOutputDto> ChangeLineCodeAsync(
        EdgeDashboardLineCodeChangeDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.OldLineCode = Require(dto.OldLineCode, "原线体编码");
        dto.NewLineCode = Require(dto.NewLineCode, "新线体编码");
        dto.LineName = Require(dto.LineName, "线体名称");
        if (string.Equals(dto.OldLineCode, dto.NewLineCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新旧线体编码相同，无需迁移");
        if (dto.LayoutVersion < 1)
            throw new InvalidOperationException("场景布局版本必须大于 0");
        dto.LayoutJson = ValidateSceneLayoutJson(
            dto.LayoutJson,
            dto.NewLineCode,
            dto.LayoutVersion);

        var scene = await Db.Queryable<EdgeDashboardSceneLayoutEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.OldLineCode &&
                             x.IsDelete == 0);
        var sceneId = scene?.Id ?? 0;

        var sceneConflict = await Db.Queryable<EdgeDashboardSceneLayoutEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.Id != sceneId &&
            x.IsDelete == 0);
        var bindingConflict = await Db.Queryable<EdgeDashboardRobotBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.IsDelete == 0);
        var deviceBindingConflict = await Db.Queryable<EdgeDashboardDeviceBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.IsDelete == 0);
        var outputBindingConflict = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.IsDelete == 0);
        var widgetConflict = await Db.Queryable<EdgeDashboardWidgetEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.IsDelete == 0);
        var deviceCardConflict = await Db.Queryable<EdgeDashboardDeviceCardEntity>().AnyAsync(x =>
            x.LineCode == dto.NewLineCode && x.IsDelete == 0);
        var lineConfig = await Db.Queryable<EdgeDashboardLineConfigEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.OldLineCode &&
                             x.IsDelete == 0);
        var lineConflict = await Db.Queryable<EdgeDashboardLineConfigEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.Id != (lineConfig == null ? 0 : lineConfig.Id) &&
            x.IsDelete == 0);
        if (sceneConflict || bindingConflict || deviceBindingConflict || outputBindingConflict || widgetConflict || deviceCardConflict || lineConflict)
            throw new InvalidOperationException("新线体编码已存在关联配置，请更换编码");

        var bindingIds = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode && x.LineCode == dto.OldLineCode)
            .Select(x => x.Id)
            .ToListAsync();
        var deviceBindingIds = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
            .Where(x => x.DashboardCode == dto.DashboardCode && x.LineCode == dto.OldLineCode)
            .Select(x => x.Id)
            .ToListAsync();
        var now = DateTime.Now;
        scene ??= new EdgeDashboardSceneLayoutEntity
        {
            CreateTime = now,
            Revision = 0
        };
        lineConfig ??= CreateLineConfigFromScene(scene, 0);
        var lineMetadata = ReadSceneLineMetadata(dto.LayoutJson);

        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            _mapper.Map(dto, scene);
            scene.Revision += 1;
            scene.UpdateTime = now;
            if (scene.Id == 0)
                scene.Id = await Db.Insertable(scene).ExecuteReturnBigIdentityAsync();
            else
                await Db.Updateable(scene).ExecuteCommandAsync();

            lineConfig.DashboardCode = dto.DashboardCode;
            lineConfig.LineCode = dto.NewLineCode;
            lineConfig.LineName = dto.LineName;
            lineConfig.Area = lineMetadata.Area;
            lineConfig.Color = lineMetadata.Color;
            lineConfig.Enabled = lineMetadata.Enabled;
            lineConfig.UpdateTime = now;
            if (lineConfig.Id == 0)
            {
                lineConfig.CreateTime = now;
                lineConfig.Id = await Db.Insertable(lineConfig).ExecuteReturnBigIdentityAsync();
            }
            else
            {
                await Db.Updateable(lineConfig).ExecuteCommandAsync();
            }

            await Db.Updateable<EdgeDashboardRobotBindingEntity>()
                .SetColumns(x => new EdgeDashboardRobotBindingEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dto.DashboardCode &&
                            x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();

            await Db.Updateable<EdgeDashboardDeviceBindingEntity>()
                .SetColumns(x => new EdgeDashboardDeviceBindingEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dto.DashboardCode &&
                            x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();

            await Db.Updateable<EdgeDashboardDeviceCardEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();
            await Db.Updateable<EdgeDashboardDeviceCardBoardSettingEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardBoardSettingEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();
            await Db.Updateable<EdgeDashboardDeviceCardChangeRecordEntity>()
                .SetColumns(x => new EdgeDashboardDeviceCardChangeRecordEntity { LineCode = dto.NewLineCode })
                .Where(x => x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();

            var outputBindingIds = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
                .Where(x => x.DashboardCode == dto.DashboardCode && x.LineCode == dto.OldLineCode)
                .Select(x => x.Id)
                .ToListAsync();
            await Db.Updateable<EdgeDashboardLineOutputBindingEntity>()
                .SetColumns(x => new EdgeDashboardLineOutputBindingEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dto.DashboardCode && x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();
            if (outputBindingIds.Count > 0)
                await Db.Updateable<EdgeDashboardLineOutputDailyStatisticEntity>()
                    .SetColumns(x => new EdgeDashboardLineOutputDailyStatisticEntity
                    {
                        LineCode = dto.NewLineCode,
                        UpdateTime = now
                    })
                    .Where(x => outputBindingIds.Contains(x.OutputBindingId))
                    .ExecuteCommandAsync();

            if (bindingIds.Count > 0)
            {
                await Db.Updateable<EdgeDashboardRobotDailyStatisticEntity>()
                    .SetColumns(x => new EdgeDashboardRobotDailyStatisticEntity
                    {
                        LineCode = dto.NewLineCode,
                        UpdateTime = now
                    })
                    .Where(x => bindingIds.Contains(x.RobotBindingId))
                    .ExecuteCommandAsync();
                await Db.Updateable<EdgeDashboardRobotFaultRecordEntity>()
                    .SetColumns(x => new EdgeDashboardRobotFaultRecordEntity
                    {
                        LineCode = dto.NewLineCode,
                        UpdateTime = now
                    })
                    .Where(x => bindingIds.Contains(x.RobotBindingId))
                    .ExecuteCommandAsync();
            }
            if (deviceBindingIds.Count > 0)
            {
                await Db.Updateable<EdgeDashboardEquipmentDailyStatisticEntity>()
                    .SetColumns(x => new EdgeDashboardEquipmentDailyStatisticEntity
                    {
                        LineCode = dto.NewLineCode,
                        UpdateTime = now
                    })
                    .Where(x => x.ComponentType == "Device" &&
                                deviceBindingIds.Contains(x.ComponentBindingId))
                    .ExecuteCommandAsync();
            }

            await Db.Updateable<EdgeDashboardWidgetEntity>()
                .SetColumns(x => new EdgeDashboardWidgetEntity
                {
                    LineCode = dto.NewLineCode,
                    UpdateTime = now
                })
                .Where(x => x.DashboardCode == dto.DashboardCode &&
                            x.LineCode == dto.OldLineCode)
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("线体编码迁移失败");

        return _mapper.Map<EdgeDashboardSceneLayoutOutputDto>(scene);
    }

    private async Task ValidateBindingAsync(EdgeDashboardRobotBindingSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.RobotCode = Require(dto.RobotCode, "机械手编码");
        dto.RobotName = Require(dto.RobotName, "机械手名称");
        dto.TenantMark = Require(dto.TenantMark, "tenantMark");
        dto.DeviceMark = Require(dto.DeviceMark, "deviceMark");
        dto.OnlineTimeoutSeconds = Math.Clamp(dto.OnlineTimeoutSeconds, 6, 3600);
        if (dto.BusinessDayStartHour is < 0 or > 23)
            throw new InvalidOperationException("业务日开始小时必须在 0 到 23 之间");

        var duplicate = await Db.Queryable<EdgeDashboardRobotBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode &&
            x.RobotCode == dto.RobotCode && x.Id != dto.Id && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("同一线体的机械手编码不能重复绑定");

        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == dto.TenantMark && x.DeviceCode == dto.DeviceMark);
        if (snapshot == null) throw new InvalidOperationException("选择的 HTTP 数据源不存在");
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, [snapshot]);
        var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot);

        if (dto.Fields.Select(x => x.FieldCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != dto.Fields.Count)
            throw new InvalidOperationException("同一机械手的字段编码不能重复");
        foreach (var field in dto.Fields)
        {
            field.FieldCode = Require(field.FieldCode, "字段编码");
            field.SourcePath = ValidateSourcePath(field.SourcePath, envelope);
            field.DisplayName = Require(field.DisplayName, "字段名称");
            if (!string.IsNullOrWhiteSpace(field.MappingJson))
                ParseJsonObject(field.MappingJson, "字段值映射 JSON");
        }

        var enabledRules = dto.StateRules.Where(x => x.Enabled).ToList();
        if (dto.StateRules.Select(x => x.RuleCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != dto.StateRules.Count)
            throw new InvalidOperationException("同一机械手的规则编码不能重复");
        foreach (var state in EdgeDashboardConstants.RequiredStatisticStates)
        {
            if (!enabledRules.Any(x => string.Equals(x.TargetState, state,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"必须至少配置一条 {state} 状态规则");
        }
        foreach (var rule in dto.StateRules)
        {
            rule.RuleCode = Require(rule.RuleCode, "规则编码");
            rule.RuleName = Require(rule.RuleName, "规则名称");
            rule.TargetState = Require(rule.TargetState, "目标状态").ToUpperInvariant();
            if (!EdgeDashboardConstants.SupportedStates.Contains(rule.TargetState))
                throw new InvalidOperationException($"不支持的目标状态：{rule.TargetState}");
            rule.SourcePath = ValidateSourcePath(rule.SourcePath, envelope);
            if (!EdgeDashboardConstants.SupportedOperators.Contains(rule.Operator))
                throw new InvalidOperationException($"不支持的比较方式：{rule.Operator}");
            if (rule.Operator is not ("IsEmpty" or "IsNotEmpty") &&
                string.IsNullOrWhiteSpace(rule.CompareValue))
                throw new InvalidOperationException($"规则 {rule.RuleName} 必须填写比较值");
            if (rule.TargetState == EdgeDashboardConstants.Fault)
            {
                rule.FaultCode = string.IsNullOrWhiteSpace(rule.FaultCode)
                    ? rule.RuleCode
                    : rule.FaultCode.Trim();
                if (!string.IsNullOrWhiteSpace(rule.FaultReasonPath))
                    rule.FaultReasonPath = ValidateSourcePath(rule.FaultReasonPath, envelope);
                if (string.IsNullOrWhiteSpace(rule.FaultReason) &&
                    string.IsNullOrWhiteSpace(rule.FaultReasonPath))
                    throw new InvalidOperationException("故障规则必须配置固定原因或原因字段");
            }
        }
    }

    /// <summary>校验设备组件、实时数据源、展示字段和可选的日统计状态映射。</summary>
    private async Task ValidateDeviceBindingAsync(EdgeDashboardDeviceBindingSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.LineCode = Require(dto.LineCode, "线体编码");
        dto.DeviceCode = Require(dto.DeviceCode, "Three.js 设备编码");
        dto.TenantMark = Require(dto.TenantMark, "tenantMark");
        dto.SourceDeviceMark = Require(dto.SourceDeviceMark, "deviceMark");
        dto.OnlineTimeoutSeconds = Math.Clamp(dto.OnlineTimeoutSeconds, 6, 3600);
        dto.BusinessDayStartHour = Math.Clamp(dto.BusinessDayStartHour, 0, 23);

        var scene = await GetSceneLayoutAsync(dto.DashboardCode, dto.LineCode);
        if (scene == null) throw new InvalidOperationException("该线体尚未创建 Three.js 场景");
        var root = JObject.Parse(scene.LayoutJson);
        var device = (root["devices"] as JArray)?.OfType<JObject>().FirstOrDefault(item =>
            string.Equals(item.Value<string>("id"), dto.DeviceCode, StringComparison.OrdinalIgnoreCase));
        if (device == null) throw new InvalidOperationException("Three.js 场景中不存在指定设备组件");
        dto.DeviceName = device.Value<string>("name") ?? Require(dto.DeviceName, "设备名称");

        var duplicate = await Db.Queryable<EdgeDashboardDeviceBindingEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.LineCode == dto.LineCode &&
            x.DeviceCode == dto.DeviceCode && x.Id != dto.Id && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("同一线体的设备组件不能重复绑定");

        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == dto.TenantMark && x.DeviceCode == dto.SourceDeviceMark);
        if (snapshot == null) throw new InvalidOperationException("选择的 HTTP 数据源不存在");
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, [snapshot]);
        var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot);

        if (!dto.StatisticsEnabled)
        {
            dto.StatusSourcePath = null;
            dto.StatusMappingJson = null;
        }
        else if (!string.IsNullOrWhiteSpace(dto.StatusSourcePath))
        {
            dto.StatusSourcePath = ValidateSourcePath(dto.StatusSourcePath, envelope);
            if (string.IsNullOrWhiteSpace(dto.StatusMappingJson))
                throw new InvalidOperationException("选择设备状态来源字段后必须填写状态值映射 JSON");
            ParseJsonObject(dto.StatusMappingJson, "设备状态值映射 JSON");
            var mapping = JObject.Parse(dto.StatusMappingJson!);
            foreach (var value in mapping.Properties().Select(x => x.Value.ToString().Trim()))
            {
                if (!EdgeDashboardConstants.SupportedStates.Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"设备状态映射仅支持：{string.Join("、", EdgeDashboardConstants.SupportedStates)}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(dto.StatusMappingJson))
        {
            throw new InvalidOperationException("填写设备状态值映射前请先选择状态来源字段");
        }

        if (dto.Fields.Select(x => x.FieldCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != dto.Fields.Count)
            throw new InvalidOperationException("同一设备的字段编码不能重复");
        foreach (var field in dto.Fields)
        {
            field.FieldCode = Require(field.FieldCode, "字段编码");
            field.SourcePath = ValidateSourcePath(field.SourcePath, envelope);
            field.DisplayName = Require(field.DisplayName, "字段名称");
            if (!string.IsNullOrWhiteSpace(field.MappingJson))
                ParseJsonObject(field.MappingJson, "字段值映射 JSON");
        }
    }

    /// <summary>校验模型级报警映射，并标准化编码、字段、条件和值。</summary>
    private async Task<List<EdgeDashboardModelAlarmRuleSaveDto>> ValidateModelAlarmRulesAsync(
        EdgeDashboardModelAlarmRuleListSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.ModelMark = NormalizeModelMark(Require(dto.ModelMark, "modelMark"));
        var sourcePaths = (await GetModelSourceFieldsAsync(dto.ModelMark))
            .Select(x => x.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (dto.Rules.Select(x => x.RuleCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != dto.Rules.Count)
            throw new InvalidOperationException("同一模型的报警编码不能重复");

        var result = new List<EdgeDashboardModelAlarmRuleSaveDto>();
        foreach (var rule in dto.Rules)
        {
            rule.RuleCode = Require(rule.RuleCode, "报警编码");
            rule.RuleName = Require(rule.RuleName, "报警名称");
            rule.SourcePath = ValidateSourcePath(rule.SourcePath, sourcePaths);
            if (!EdgeDashboardConstants.SupportedOperators.Contains(rule.Operator))
                throw new InvalidOperationException($"不支持的比较方式：{rule.Operator}");
            if (rule.Operator is not ("IsEmpty" or "IsNotEmpty") &&
                string.IsNullOrWhiteSpace(rule.CompareValue))
                throw new InvalidOperationException($"报警 {rule.RuleName} 必须填写比较值");
            rule.FaultCode = string.IsNullOrWhiteSpace(rule.FaultCode)
                ? rule.RuleCode
                : rule.FaultCode.Trim();
            if (!string.IsNullOrWhiteSpace(rule.FaultReasonPath))
                rule.FaultReasonPath = ValidateSourcePath(rule.FaultReasonPath, sourcePaths);
            rule.FaultSeverity = string.IsNullOrWhiteSpace(rule.FaultSeverity)
                ? "Warning"
                : rule.FaultSeverity.Trim();
            if (string.IsNullOrWhiteSpace(rule.FaultReason) &&
                string.IsNullOrWhiteSpace(rule.FaultReasonPath))
                throw new InvalidOperationException("模型报警必须配置固定名称或动态原因字段");
            result.Add(rule);
        }
        return result;
    }

    /// <summary>校验模型级字段展示映射，字段路径必须来自该模型已接收的历史字段目录。</summary>
    private async Task<List<EdgeDashboardModelFieldDisplayMappingSaveDto>> ValidateModelFieldDisplayMappingsAsync(
        EdgeDashboardModelFieldDisplayMappingListSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.ModelMark = NormalizeModelMark(Require(dto.ModelMark, "modelMark"));
        var sourceFields = (await GetModelSourceFieldsAsync(dto.ModelMark))
            .ToDictionary(x => x.SourcePath, StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in dto.Mappings)
        {
            mapping.SourcePath = Require(mapping.SourcePath, "来源字段");
            if (!sourceFields.ContainsKey(mapping.SourcePath))
                throw new InvalidOperationException($"模型历史字段目录中不存在字段：{mapping.SourcePath}");
            if (!paths.Add(mapping.SourcePath))
                throw new InvalidOperationException("同一模型的字段展示映射不能重复");
            mapping.DisplayName = Require(mapping.DisplayName, "展示名称");
            mapping.ValueType = string.IsNullOrWhiteSpace(mapping.ValueType)
                ? sourceFields[mapping.SourcePath].ValueType
                : mapping.ValueType.Trim();
            mapping.FormatType = string.IsNullOrWhiteSpace(mapping.FormatType)
                ? "Direct"
                : mapping.FormatType.Trim();
            if (mapping.FormatType is not ("Direct" or "ValueMap" or "UnixMilliseconds"))
                throw new InvalidOperationException("字段格式仅支持 Direct、ValueMap 或 UnixMilliseconds");
            mapping.Decimals = mapping.Decimals.HasValue
                ? Math.Clamp(mapping.Decimals.Value, 0, 8)
                : null;
            mapping.Unit = Normalize(mapping.Unit);
            mapping.MappingJson = Normalize(mapping.MappingJson);
            if (mapping.FormatType == "ValueMap" && string.IsNullOrWhiteSpace(mapping.MappingJson))
                throw new InvalidOperationException($"字段“{mapping.DisplayName}”选择值映射时必须填写映射 JSON");
            if (!string.IsNullOrWhiteSpace(mapping.MappingJson))
                ParseJsonObject(mapping.MappingJson, "字段值映射 JSON");
        }
        return dto.Mappings;
    }

    /// <summary>标准化模型状态优先级；未提交的状态沿用系统默认值，避免配置不完整导致状态无法裁决。</summary>
    private static List<EdgeDashboardModelStatePrioritySaveDto> NormalizeModelStatePriorities(
        EdgeDashboardModelStatePriorityListSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.ModelMark = NormalizeModelMark(Require(dto.ModelMark, "modelMark"));
        var input = dto.Priorities ?? [];
        var priorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in input)
        {
            var state = Require(item.State, "状态").ToUpperInvariant();
            if (!ModelStatePriorityDefaults.Any(x => x.State == state))
                throw new InvalidOperationException($"不支持配置状态优先级：{state}");
            if (!priorityMap.TryAdd(state, Math.Clamp(item.Priority, 0, 100000)))
                throw new InvalidOperationException($"状态优先级不能重复：{state}");
        }
        return ModelStatePriorityDefaults.Select(item => new EdgeDashboardModelStatePrioritySaveDto
        {
            State = item.State,
            Priority = priorityMap.GetValueOrDefault(item.State, item.Priority)
        }).ToList();
    }

    private async Task ValidateWidgetAsync(EdgeDashboardWidgetSaveDto dto)
    {
        dto.DashboardCode = Require(dto.DashboardCode, "看板编码");
        dto.WidgetCode = Require(dto.WidgetCode, "部件编码");
        dto.Title = Require(dto.Title, "部件标题");
        if (!EdgeDashboardConstants.SupportedWidgetTypes.Contains(dto.WidgetType))
            throw new InvalidOperationException($"不支持的部件类型：{dto.WidgetType}");
        if (!EdgeDashboardConstants.SupportedDataSourceTypes.Contains(dto.DataSourceType))
            throw new InvalidOperationException($"不支持的数据源类型：{dto.DataSourceType}");
        dto.RefreshSeconds = Math.Clamp(dto.RefreshSeconds, 3, 3600);
        dto.TopN = Math.Clamp(dto.TopN, 1, 100);
        ParseJsonObject(dto.ConfigJson, "部件配置 JSON");
        ValidateWidgetLayoutConfig(dto.ConfigJson);
        ValidateWidgetColumnWidths(dto);

        var duplicate = await Db.Queryable<EdgeDashboardWidgetEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode && x.WidgetCode == dto.WidgetCode &&
            x.Id != dto.Id && x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("同一看板的部件编码不能重复");
        var allowedFields = GetWidgetFieldCatalog()
            .Where(x => x.DataSourceTypes.Contains(dto.DataSourceType))
            .Select(x => x.FieldKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in dto.Fields)
        {
            field.FieldKey = Require(field.FieldKey, "部件字段");
            field.DisplayName = Require(field.DisplayName, "部件字段名称");
            var isConfiguredRealtimeField = dto.DataSourceType == "RealtimeRobot" &&
                                            field.FieldKey.StartsWith(
                                                "field:",
                                                StringComparison.OrdinalIgnoreCase);
            if (!allowedFields.Contains(field.FieldKey) && !isConfiguredRealtimeField)
                throw new InvalidOperationException(
                    $"字段 {field.FieldKey} 不属于数据源 {dto.DataSourceType}");
        }
        if (dto.WidgetType == "Table" && dto.Fields.All(x => !x.Visible))
            throw new InvalidOperationException("表格至少需要一个可见列");
        if (dto.WidgetType is "Pie" or "Bar" && dto.Fields.Count == 0)
            throw new InvalidOperationException("图表至少需要一个字段");
    }

    /// <summary>校验部件在十二列网格中的占比及表格滚动参数。</summary>
    private static void ValidateWidgetLayoutConfig(string configJson)
    {
        var root = JObject.Parse(configJson);
        if (root["layout"] == null) return;
        if (root["layout"] is not JObject layout)
            throw new InvalidOperationException("部件配置 layout 必须是 JSON 对象");

        ValidateInteger(layout, "columnSpan", 3, 12, [3, 4, 6, 8, 9, 12]);
        ValidateInteger(layout, "rowSpan", 1, 3);
        ValidateInteger(layout, "tableRowHeight", 32, 72);
        ValidateInteger(layout, "scrollIntervalSeconds", 1, 30);
        if (layout["autoScroll"] is { Type: not JTokenType.Boolean })
            throw new InvalidOperationException("自动滚动配置必须是布尔值");
    }

    /// <summary>表格配置列宽后，要求全部可见列都有占比且合计为百分之百。</summary>
    private static void ValidateWidgetColumnWidths(EdgeDashboardWidgetSaveDto dto)
    {
        if (dto.WidgetType != "Table") return;
        var visibleFields = dto.Fields.Where(x => x.Visible).ToList();
        if (!visibleFields.Any(x => x.Width.HasValue)) return;
        if (visibleFields.Any(x => !x.Width.HasValue || x.Width is < 1 or > 100))
            throw new InvalidOperationException("表格可见列宽占比必须在 1% 到 100% 之间");
        if (visibleFields.Sum(x => x.Width!.Value) != 100)
            throw new InvalidOperationException("表格可见列宽占比合计必须为 100%");
    }

    /// <summary>读取并校验一个可选的整数配置项。</summary>
    private static void ValidateInteger(
        JObject source,
        string propertyName,
        int min,
        int max,
        int[]? allowedValues = null)
    {
        var token = source[propertyName];
        if (token == null) return;
        if (token.Type != JTokenType.Integer ||
            !int.TryParse(token.ToString(), out var value) ||
            value < min || value > max ||
            (allowedValues != null && !allowedValues.Contains(value)))
            throw new InvalidOperationException($"部件布局参数 {propertyName} 不合法");
    }

    private async Task<List<EdgeDashboardRobotBindingOutputDto>> BuildBindingOutputsAsync(
        List<EdgeDashboardRobotBindingEntity> bindings)
    {
        if (bindings.Count == 0) return [];
        var ids = bindings.Select(x => x.Id).ToList();
        var fields = await Db.Queryable<EdgeDashboardRobotFieldBindingEntity>()
            .Where(x => ids.Contains(x.RobotBindingId) && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var rules = await Db.Queryable<EdgeDashboardRobotStateRuleEntity>()
            .Where(x => ids.Contains(x.RobotBindingId) && x.IsDelete == 0)
            .OrderBy(x => x.Priority)
            .ToListAsync();
        return bindings.Select(binding =>
        {
            var output = _mapper.Map<EdgeDashboardRobotBindingOutputDto>(binding);
            output.Fields = _mapper.Map<List<EdgeDashboardRobotFieldSaveDto>>(
                fields.Where(x => x.RobotBindingId == binding.Id));
            output.StateRules = _mapper.Map<List<EdgeDashboardRobotStateRuleSaveDto>>(
                rules.Where(x => x.RobotBindingId == binding.Id));
            return output;
        }).ToList();
    }

    private async Task<List<EdgeDashboardDeviceBindingOutputDto>> BuildDeviceBindingOutputsAsync(
        List<EdgeDashboardDeviceBindingEntity> bindings)
    {
        if (bindings.Count == 0) return [];
        var ids = bindings.Select(x => x.Id).ToList();
        var fields = await Db.Queryable<EdgeDashboardDeviceFieldBindingEntity>()
            .Where(x => ids.Contains(x.DeviceBindingId) && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        return bindings.Select(binding =>
        {
            var output = _mapper.Map<EdgeDashboardDeviceBindingOutputDto>(binding);
            output.Fields = _mapper.Map<List<EdgeDashboardDeviceFieldSaveDto>>(
                fields.Where(x => x.DeviceBindingId == binding.Id));
            return output;
        }).ToList();
    }

    private async Task<List<EdgeDashboardWidgetOutputDto>> BuildWidgetOutputsAsync(
        List<EdgeDashboardWidgetEntity> widgets)
    {
        if (widgets.Count == 0) return [];
        var ids = widgets.Select(x => x.Id).ToList();
        var fields = await Db.Queryable<EdgeDashboardWidgetFieldEntity>()
            .Where(x => ids.Contains(x.WidgetId) && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        return widgets.Select(widget =>
        {
            var output = _mapper.Map<EdgeDashboardWidgetOutputDto>(widget);
            output.Fields = _mapper.Map<List<EdgeDashboardWidgetFieldSaveDto>>(
                fields.Where(x => x.WidgetId == widget.Id));
            return output;
        }).ToList();
    }

    private async Task EnsureDefaultWidgetsAsync(string dashboardCode, string? lineCode)
    {
        var exists = await Db.Queryable<EdgeDashboardWidgetEntity>().AnyAsync(x =>
            x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0);
        if (exists) return;

        var defaults = new[]
        {
            new EdgeDashboardWidgetSaveDto
            {
                DashboardCode = dashboardCode, LineCode = lineCode,
                WidgetCode = $"{lineCode ?? "all"}-daily-state-bar", Title = "运行 / 空闲 / 故障时间",
                WidgetType = "Bar", DataSourceType = "DailyStatistics", Period = "Day",
                RefreshSeconds = 30, Sort = 10, Stacked = true,
                Fields =
                [
                    WidgetField("statDate", "日期", "Category", null, 0),
                    WidgetField("runningSeconds", "运行", "Value", "#20e5a3", 10, "h"),
                    WidgetField("idleSeconds", "空闲", "Value", "#38bdf8", 20, "h"),
                    WidgetField("faultSeconds", "故障", "Value", "#ff4d67", 30, "h")
                ]
            },
            new EdgeDashboardWidgetSaveDto
            {
                DashboardCode = dashboardCode, LineCode = lineCode,
                WidgetCode = $"{lineCode ?? "all"}-state-pie", Title = "今日状态时长占比",
                WidgetType = "Pie", DataSourceType = "DailyStatistics", Period = "Day",
                RefreshSeconds = 30, Sort = 20, TopN = 6,
                Fields =
                [
                    WidgetField("runningSeconds", "运行", "Value", "#20e5a3", 10, "h"),
                    WidgetField("idleSeconds", "空闲", "Value", "#38bdf8", 20, "h"),
                    WidgetField("faultSeconds", "故障", "Value", "#ff4d67", 30, "h")
                ]
            },
            new EdgeDashboardWidgetSaveDto
            {
                DashboardCode = dashboardCode, LineCode = lineCode,
                WidgetCode = $"{lineCode ?? "all"}-fault-table", Title = "故障记录",
                WidgetType = "Table", DataSourceType = "FaultRecords", Period = "Week",
                RefreshSeconds = 10, Sort = 30,
                Fields =
                [
                    WidgetField("startTime", "开始时间", "Column", null, 0),
                    WidgetField("robotName", "机械手", "Column", null, 10),
                    WidgetField("faultReason", "故障原因", "Column", null, 20),
                    WidgetField("durationSeconds", "持续时间", "Column", null, 30, "s"),
                    WidgetField("status", "状态", "Column", null, 40)
                ]
            }
        };
        foreach (var widget in defaults) await SaveWidgetAsync(widget);
    }

    private async Task ResetRuntimeForRuleChangeAsync(
        EdgeDashboardRobotBindingEntity binding,
        DateTime now)
    {
        await CloseActiveFaultsAsync(binding.Id, now, null);
        var runtime = await Db.Queryable<EdgeDashboardRobotRuntimeStateEntity>()
            .FirstAsync(x => x.RobotBindingId == binding.Id);
        if (runtime == null) return;
        runtime.CurrentState = EdgeDashboardConstants.Unknown;
        runtime.StateStartTime = now;
        runtime.LastSampleTime = now;
        runtime.LastAccountedTime = now;
        runtime.LastMessageId = null;
        runtime.LastSourceTimestamp = null;
        runtime.RuleVersion = binding.RuleVersion;
        runtime.UpdateTime = now;
        await Db.Updateable(runtime).ExecuteCommandAsync();
    }

    /// <summary>把设备组件日统计投影为通用的每日统计输出，同时保留旧前端使用的机械手别名字段。</summary>
    private static EdgeDashboardDailyStatisticOutputDto ToDailyStatisticOutput(
        EdgeDashboardEquipmentDailyStatisticEntity source) =>
        new()
        {
            Id = source.Id,
            ComponentType = source.ComponentType,
            ComponentBindingId = source.ComponentBindingId,
            ComponentCode = source.ComponentCode,
            ComponentName = source.ComponentName,
            // 兼容已存在的表格、图表和导出字段，不要求旧配置修改。
            RobotCode = source.ComponentCode,
            RobotName = source.ComponentName,
            StatDate = source.StatDate,
            LineCode = source.LineCode,
            RunningSeconds = source.RunningSeconds,
            IdleSeconds = source.IdleSeconds,
            FaultSeconds = source.FaultSeconds,
            StoppedSeconds = source.StoppedSeconds,
            OfflineSeconds = source.OfflineSeconds,
            UnknownSeconds = source.UnknownSeconds,
            FaultCount = source.FaultCount,
            SampleCount = source.SampleCount,
            LastState = source.LastState,
            UpdateTime = source.UpdateTime
        };

    private async Task CloseActiveFaultsAsync(long bindingId, DateTime endTime, string? messageId)
    {
        var active = await Db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .Where(x => x.RobotBindingId == bindingId && x.EndTime == null)
            .ToListAsync();
        foreach (var fault in active)
        {
            fault.EndTime = endTime < fault.StartTime ? fault.StartTime : endTime;
            fault.LastSeenTime = fault.EndTime.Value;
            fault.DurationSeconds = Math.Max(0, (long)(fault.EndTime.Value - fault.StartTime).TotalSeconds);
            fault.Status = "Cleared";
            fault.EndMessageId = messageId;
            fault.UpdateTime = DateTime.Now;
        }
        if (active.Count > 0) await Db.Updateable(active).ExecuteCommandAsync();
    }

    /// <summary>查询故障列表时动态计算持续时间，避免活动报警在前端刷新时一直显示旧的 0 分钟。</summary>
    private static void RefreshFaultRecordDurations(
        IEnumerable<EdgeDashboardFaultRecordOutputDto> records,
        DateTime now)
    {
        foreach (var record in records)
        {
            var endTime = record.EndTime ?? now;
            if (endTime < record.StartTime) endTime = record.StartTime;
            var actualSeconds = (long)(endTime - record.StartTime).TotalSeconds;
            record.DurationSeconds = Math.Max(record.DurationSeconds, actualSeconds);
        }
    }

    /// <summary>兼容历史模型报警记录，展示时使用报警映射编码替代无意义的 0/1 判断值。</summary>
    private static void NormalizeModelFaultReasons(
        IEnumerable<EdgeDashboardFaultRecordOutputDto> records)
    {
        foreach (var record in records)
        {
            if (IsModelRuleScope(record.RuleScope) && !string.IsNullOrWhiteSpace(record.FaultCode))
                record.FaultReason = record.FaultCode;
        }
    }

    private static bool IsModelRuleScope(string? ruleScope) =>
        string.Equals(ruleScope, "Model", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 将不带具体时刻的结束日期转换为次日零点，用于实现包含结束当天的半开区间查询。
    /// 调用方传入具体时刻时仍按原有精确时间上限处理。
    /// </summary>
    private static DateTime? GetDateToExclusive(DateTime? dateTo)
    {
        if (!dateTo.HasValue || dateTo.Value.TimeOfDay != TimeSpan.Zero) return null;
        return dateTo.Value.Date.AddDays(1);
    }

    private static object? FormatFieldValue(
        JToken? rawToken,
        EdgeDashboardRobotFieldBindingEntity field)
    {
        if (rawToken == null) return null;
        var raw = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
        if (field.FormatType == "ValueMap" && !string.IsNullOrWhiteSpace(field.MappingJson))
        {
            var map = JsonConvert.DeserializeObject<Dictionary<string, object?>>(field.MappingJson);
            var key = Convert.ToString(raw) ?? string.Empty;
            if (map != null && map.TryGetValue(key, out var mapped)) return mapped;
            if (map != null && map.TryGetValue("*", out mapped)) return mapped;
        }
        if (field.FormatType == "UnixMilliseconds" && long.TryParse(Convert.ToString(raw), out var value))
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (raw is decimal or double or float && field.Decimals.HasValue &&
            decimal.TryParse(Convert.ToString(raw), out var number))
            return Math.Round(number, Math.Clamp(field.Decimals.Value, 0, 8));
        return raw;
    }

    private static object? FormatDeviceFieldValue(
        JToken? rawToken,
        EdgeDashboardDeviceFieldBindingEntity field)
    {
        if (rawToken == null) return null;
        var raw = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
        if (field.FormatType == "ValueMap" && !string.IsNullOrWhiteSpace(field.MappingJson))
        {
            var map = JsonConvert.DeserializeObject<Dictionary<string, object?>>(field.MappingJson);
            var key = Convert.ToString(raw) ?? string.Empty;
            if (map != null && map.TryGetValue(key, out var mapped)) return mapped;
            if (map != null && map.TryGetValue("*", out mapped)) return mapped;
        }
        if (field.FormatType == "UnixMilliseconds" && long.TryParse(Convert.ToString(raw), out var value))
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (raw is decimal or double or float && field.Decimals.HasValue &&
            decimal.TryParse(Convert.ToString(raw), out var number))
            return Math.Round(number, Math.Clamp(field.Decimals.Value, 0, 8));
        return raw;
    }

    /// <summary>按模型统一展示规则格式化字段值，原始快照和业务判断仍使用未经转换的 rawToken。</summary>
    private static object? FormatModelFieldValue(
        JToken? rawToken,
        EdgeDashboardModelFieldDisplayMappingEntity mapping)
    {
        if (rawToken == null) return null;
        var raw = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
        if (mapping.FormatType == "ValueMap" && !string.IsNullOrWhiteSpace(mapping.MappingJson))
        {
            var map = JsonConvert.DeserializeObject<Dictionary<string, object?>>(mapping.MappingJson);
            var key = Convert.ToString(raw) ?? string.Empty;
            if (map != null && map.TryGetValue(key, out var mapped)) return mapped;
            if (map != null && map.TryGetValue("*", out mapped)) return mapped;
        }
        if (mapping.FormatType == "UnixMilliseconds" && long.TryParse(Convert.ToString(raw), out var value))
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (raw is decimal or double or float && mapping.Decimals.HasValue &&
            decimal.TryParse(Convert.ToString(raw), out var number))
            return Math.Round(number, Math.Clamp(mapping.Decimals.Value, 0, 8));
        return raw;
    }

    /// <summary>按看板、模型和字段路径定位可复用的模型展示映射。</summary>
    private static EdgeDashboardModelFieldDisplayMappingEntity? FindModelFieldDisplayMapping(
        IEnumerable<EdgeDashboardModelFieldDisplayMappingEntity> mappings,
        string dashboardCode,
        string modelMark,
        string sourcePath) =>
        mappings.FirstOrDefault(x =>
            x.DashboardCode == dashboardCode &&
            x.ModelMark == modelMark &&
            x.SourcePath == sourcePath);

    private async Task<List<EdgeDashboardDeviceCardOutputDto>> BuildDeviceCardOutputsAsync(
        List<EdgeDashboardDeviceCardEntity> cards)
    {
        if (cards.Count == 0) return [];
        var ids = cards.Select(x => x.Id).ToList();
        var fields = await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
            .Where(x => ids.Contains(x.CardId) && x.IsDelete == 0)
            .OrderBy(x => x.Sort).ToListAsync();
        var outputs = _mapper.Map<List<EdgeDashboardDeviceCardOutputDto>>(cards);
        foreach (var output in outputs)
            output.Fields = _mapper.Map<List<EdgeDashboardDeviceCardFieldSaveDto>>(
                fields.Where(x => x.CardId == output.Id).ToList());
        await EdgeDeviceCardPushConfiguration.AttachAsync(Db, outputs);
        return outputs;
    }

    private static object? FormatDeviceCardFieldValue(
        JToken? rawToken,
        EdgeDashboardDeviceCardFieldEntity field)
    {
        if (rawToken == null) return null;
        var raw = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
        if (field.FormatType == "ValueMap" && !string.IsNullOrWhiteSpace(field.MappingJson))
        {
            var map = JsonConvert.DeserializeObject<Dictionary<string, object?>>(field.MappingJson);
            var key = Convert.ToString(raw) ?? string.Empty;
            if (map != null && map.TryGetValue(key, out var mapped)) return mapped;
            if (map != null && map.TryGetValue("*", out mapped)) return mapped;
        }
        if (field.FormatType == "UnixMilliseconds" && long.TryParse(Convert.ToString(raw), out var value))
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (raw is decimal or double or float && field.Decimals.HasValue &&
            decimal.TryParse(Convert.ToString(raw), out var number))
            return Math.Round(number, Math.Clamp(field.Decimals.Value, 0, 8));
        return raw;
    }

    private static string ResolveDeviceCardStatus(
        EdgeDashboardDeviceCardEntity card,
        JObject? envelope,
        bool dataOnline)
    {
        if (!dataOnline) return "OFFLINE";
        if (envelope == null || string.IsNullOrWhiteSpace(card.StatusSourcePath)) return "ONLINE";
        var raw = EdgeDashboardJsonFieldResolver.ToCompactText(
            EdgeDashboardJsonFieldResolver.Resolve(envelope, card.StatusSourcePath));
        if (string.IsNullOrWhiteSpace(raw)) return "ONLINE";
        if (!string.IsNullOrWhiteSpace(card.StatusMappingJson))
        {
            try
            {
                var map = JsonConvert.DeserializeObject<Dictionary<string, object?>>(card.StatusMappingJson);
                if (map != null && map.TryGetValue(raw, out var mapped)) raw = Convert.ToString(mapped) ?? raw;
                else if (map != null && map.TryGetValue("*", out mapped)) raw = Convert.ToString(mapped) ?? raw;
            }
            catch (JsonException)
            {
                // 保存时已经校验；历史异常配置仅降级为原始状态，不影响整页看板。
            }
        }
        return raw.Trim().ToUpperInvariant() switch
        {
            "故障" => "FAULT",
            "告警" or "报警" => "ALARM",
            "预警" => "WARNING",
            "离线" => "OFFLINE",
            "在线" or "正常" or "运行" => "ONLINE",
            var status => status
        };
    }

    /// <summary>数值字段上下限的实时状态：Normal、Lower 或 Upper。</summary>
    private static string GetDeviceCardLimitState(
        decimal value,
        EdgeDashboardDeviceCardFieldEntity field)
    {
        if (field.AlarmLowerLimit.HasValue && value < field.AlarmLowerLimit.Value) return "Lower";
        if (field.AlarmUpperLimit.HasValue && value > field.AlarmUpperLimit.Value) return "Upper";
        return "Normal";
    }

    private static bool HasDeviceCardAlarmLimit(EdgeDashboardDeviceCardFieldEntity field) =>
        field.AlarmLowerLimit.HasValue || field.AlarmUpperLimit.HasValue;

    internal static bool IsDeviceCardNumericValueType(string? valueType) =>
        !string.IsNullOrWhiteSpace(valueType) &&
        (valueType.Contains("number", StringComparison.OrdinalIgnoreCase) ||
         valueType.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
         valueType.Contains("double", StringComparison.OrdinalIgnoreCase) ||
         valueType.Contains("float", StringComparison.OrdinalIgnoreCase) ||
         valueType.Contains("int", StringComparison.OrdinalIgnoreCase) ||
         valueType.Contains("long", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetDeviceCardNumericValue(string valueJson, out decimal value)
    {
        value = default;
        try
        {
            var token = JToken.Parse(valueJson);
            var text = EdgeDashboardJsonFieldResolver.ToCompactText(token);
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
                   decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task TrackDeviceCardAlertAsync(
        EdgeDashboardDeviceCardEntity card,
        EdgeDashboardDeviceCardFieldEntity field,
        string valueJson,
        EdgeDashboardTelemetrySampleDto sample,
        DateTime now,
        Dictionary<string, EdgeDashboardDeviceCardAlertStateEntity> stateMap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetDeviceCardNumericValue(valueJson, out var numericValue)) return;
        var key = $"{field.CardId}\u001f{field.FieldCode}";
        var currentState = GetDeviceCardLimitState(numericValue, field);
        if (!stateMap.TryGetValue(key, out var state))
        {
            state = new EdgeDashboardDeviceCardAlertStateEntity
            {
                CardId = field.CardId,
                FieldCode = field.FieldCode,
                LastLimitState = currentState,
                LastValueJson = valueJson,
                LastDataTime = sample.DataTime,
                UpdateTime = now
            };
            state.Id = await Db.Insertable(state).ExecuteReturnBigIdentityAsync();
            stateMap[key] = state;
            if (currentState != "Normal")
                await CreateDeviceCardAlertRecordAsync(card, field, numericValue, currentState, sample, now);
            return;
        }

        // 乱序旧报文不能把已经恢复的告警重新置为越限状态。
        if (sample.DataTime < state.LastDataTime) return;
        var previousState = string.IsNullOrWhiteSpace(state.LastLimitState) ? "Normal" : state.LastLimitState;
        if (!string.Equals(previousState, currentState, StringComparison.OrdinalIgnoreCase))
            await CreateDeviceCardAlertRecordAsync(card, field, numericValue, currentState, sample, now);

        state.LastLimitState = currentState;
        state.LastValueJson = valueJson;
        state.LastDataTime = sample.DataTime;
        state.UpdateTime = now;
        await Db.Updateable(state).ExecuteCommandAsync();
    }

    private async Task CreateDeviceCardAlertRecordAsync(
        EdgeDashboardDeviceCardEntity card,
        EdgeDashboardDeviceCardFieldEntity field,
        decimal numericValue,
        string limitState,
        EdgeDashboardTelemetrySampleDto sample,
        DateTime now)
    {
        var alertType = limitState switch
        {
            "Lower" => "LowerExceeded",
            "Upper" => "UpperExceeded",
            _ => "Recovered"
        };
        var systemOwned = await Db.Queryable<EdgeDeviceCardPushRuleEntity>().AnyAsync(x =>
            x.CardId == card.Id && x.FieldCode == field.FieldCode && x.Enabled && x.Channel == "WeChat");
        await Db.Insertable(new EdgeDashboardDeviceCardAlertRecordEntity
        {
            CardId = card.Id,
            LineCode = card.LineCode,
            DeviceMark = card.DeviceMark,
            FieldCode = field.FieldCode,
            DisplayName = field.DisplayName,
            AlertType = alertType,
            NumericValue = numericValue,
            AlarmLowerLimit = field.AlarmLowerLimit,
            AlarmUpperLimit = field.AlarmUpperLimit,
            // 恢复事件需要保留供后台追溯，但不进入外部推送待办队列。
            PushStatus = alertType == "Recovered" ? "NotRequired" : systemOwned ? "Internal" : "Pending",
            MessageId = sample.MessageId,
            DataTime = sample.DataTime,
            ReceiveTime = sample.ReceiveTime,
            CreateTime = now,
            UpdateTime = now
        }).ExecuteCommandAsync();
    }

    private static bool HasDeviceCardValueChanged(
        string oldValueJson,
        string newValueJson,
        decimal? threshold)
    {
        if (string.Equals(oldValueJson, newValueJson, StringComparison.Ordinal)) return false;
        if (!threshold.HasValue || threshold.Value <= 0) return true;
        try
        {
            var oldToken = JToken.Parse(oldValueJson);
            var newToken = JToken.Parse(newValueJson);
            var oldText = EdgeDashboardJsonFieldResolver.ToCompactText(oldToken);
            var newText = EdgeDashboardJsonFieldResolver.ToCompactText(newToken);
            return !decimal.TryParse(oldText, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldValue) ||
                   !decimal.TryParse(newText, NumberStyles.Any, CultureInfo.InvariantCulture, out var newValue) ||
                   Math.Abs(newValue - oldValue) >= threshold.Value;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>设备展示状态与日统计使用相同的模型优先级，数值越小越优先。</summary>
    private static string ResolveDeviceRuntimeState(
        string sourceState,
        bool hasAlarm,
        IEnumerable<EdgeDashboardModelStatePriorityEntity> priorities)
    {
        var priorityRows = priorities.ToList();
        var candidates = new List<string> { sourceState };
        if (hasAlarm) candidates.Add(EdgeDashboardConstants.Fault);
        return candidates
            .OrderBy(state => ResolveModelStatePriority(state, priorityRows))
            .ThenBy(state => state, StringComparer.Ordinal)
            .FirstOrDefault() ?? EdgeDashboardConstants.Unknown;
    }

    private static int ResolveModelStatePriority(
        string state,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> priorities) =>
        priorities.FirstOrDefault(item =>
            string.Equals(item.State, state, StringComparison.OrdinalIgnoreCase))?.Priority ??
        state switch
        {
            EdgeDashboardConstants.Fault => 10,
            EdgeDashboardConstants.Idle => 20,
            EdgeDashboardConstants.Stopped => 30,
            EdgeDashboardConstants.Running => 40,
            _ => 90
        };

    /// <summary>使用模型报警映射判断设备组件的当前报警状态，比较规则与机械手保持一致。</summary>
    private static bool MatchesDeviceModelAlarmRule(EdgeDashboardModelAlarmRuleEntity rule, JToken? token)
    {
        var raw = EdgeDashboardJsonFieldResolver.ToObject(token);
        var rawText = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var compareText = rule.CompareValue?.Trim() ?? string.Empty;
        return rule.Operator switch
        {
            "IsEmpty" => string.IsNullOrEmpty(rawText),
            "IsNotEmpty" => !string.IsNullOrEmpty(rawText),
            "Contains" => rawText.Contains(compareText, StringComparison.OrdinalIgnoreCase),
            "NotContains" => !rawText.Contains(compareText, StringComparison.OrdinalIgnoreCase),
            "Equals" => CompareDeviceModelAlarmValue(rawText, compareText) == 0,
            "NotEquals" => CompareDeviceModelAlarmValue(rawText, compareText) != 0,
            "GreaterThan" => CompareDeviceModelAlarmValue(rawText, compareText) > 0,
            "GreaterThanOrEqual" => CompareDeviceModelAlarmValue(rawText, compareText) >= 0,
            "LessThan" => CompareDeviceModelAlarmValue(rawText, compareText) < 0,
            "LessThanOrEqual" => CompareDeviceModelAlarmValue(rawText, compareText) <= 0,
            _ => false
        };
    }

    /// <summary>优先按数值比较，兼容设备常见的数值、布尔和文本状态。</summary>
    private static int CompareDeviceModelAlarmValue(string left, string right)
    {
        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var leftNumber) &&
            decimal.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        if (bool.TryParse(left, out var leftBoolean) && bool.TryParse(right, out var rightBoolean))
            return leftBoolean.CompareTo(rightBoolean);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>优先读取动态报警原因，缺失时回退到报警映射的固定说明。</summary>
    private static string ResolveDeviceModelAlarmMessage(
        EdgeDashboardModelAlarmRuleEntity rule,
        JObject envelope)
    {
        if (!string.IsNullOrWhiteSpace(rule.FaultReasonPath))
        {
            var reason = EdgeDashboardJsonFieldResolver.ToCompactText(
                EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.FaultReasonPath));
            if (!string.IsNullOrWhiteSpace(reason)) return reason;
        }
        if (!string.IsNullOrWhiteSpace(rule.FaultReason)) return rule.FaultReason;
        return string.IsNullOrWhiteSpace(rule.RuleName) ? rule.RuleCode : rule.RuleName;
    }

    /// <summary>按设备绑定的状态字段和值映射解析当前状态；未配置或无法匹配时由调用方保留默认状态。</summary>
    private static bool TryResolveDeviceMappedState(
        EdgeDashboardDeviceBindingEntity binding,
        JObject envelope,
        out string state)
    {
        state = string.Empty;
        if (string.IsNullOrWhiteSpace(binding.StatusSourcePath) ||
            string.IsNullOrWhiteSpace(binding.StatusMappingJson))
            return false;
        try
        {
            var rawValue = EdgeDashboardJsonFieldResolver.ToCompactText(
                EdgeDashboardJsonFieldResolver.Resolve(envelope, binding.StatusSourcePath)) ?? string.Empty;
            var mapping = JObject.Parse(binding.StatusMappingJson);
            var mappedValue = mapping.Properties().FirstOrDefault(item =>
                string.Equals(item.Name, rawValue, StringComparison.OrdinalIgnoreCase))?.Value.ToString().Trim();
            if (string.IsNullOrWhiteSpace(mappedValue) ||
                !EdgeDashboardConstants.SupportedStates.Contains(mappedValue, StringComparer.OrdinalIgnoreCase))
                return false;
            state = mappedValue.ToUpperInvariant();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static EdgeDashboardFieldCatalogDto Field(
        string key,
        string name,
        string valueType,
        params string[] sourceTypes) =>
        new() { FieldKey = key, DisplayName = name, ValueType = valueType, DataSourceTypes = sourceTypes };

    private static EdgeDashboardWidgetFieldSaveDto WidgetField(
        string key,
        string name,
        string role,
        string? color,
        int sort,
        string? unit = null) =>
        new()
        {
            FieldKey = key, DisplayName = name, Role = role, Color = color,
            Sort = sort, Unit = unit, Visible = true
        };

    /// <summary>从旧场景 JSON 中提取线体元数据，用于无损迁移线体主配置。</summary>
    private static EdgeDashboardLineConfigEntity CreateLineConfigFromScene(
        EdgeDashboardSceneLayoutEntity scene,
        int sort)
    {
        var metadata = ReadSceneLineMetadata(scene.LayoutJson);
        return new EdgeDashboardLineConfigEntity
        {
            DashboardCode = scene.DashboardCode,
            LineCode = scene.LineCode,
            LineName = scene.LineName,
            Area = metadata.Area,
            Color = metadata.Color,
            Enabled = metadata.Enabled,
            Sort = sort,
            CreateTime = scene.CreateTime,
            UpdateTime = scene.UpdateTime
        };
    }

    /// <summary>兼容旧场景缺少展示字段的情况，并返回稳定默认值。</summary>
    private static (string Area, string Color, bool Enabled) ReadSceneLineMetadata(string? json)
    {
        try
        {
            var root = JObject.Parse(json ?? "{}");
            var area = root.Value<string>("lineArea")?.Trim() ?? string.Empty;
            var color = root.Value<string>("lineColor")?.Trim();
            if (string.IsNullOrWhiteSpace(color) ||
                !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
                color = "#55d5ff";
            var enabled = root["enabled"]?.Type == JTokenType.Boolean
                ? root.Value<bool>("enabled")
                : true;
            return (area, color, enabled);
        }
        catch (JsonReaderException)
        {
            return (string.Empty, "#55d5ff", true);
        }
    }

    private static string ValidateSourcePath(string path, JObject envelope)
    {
        path = Require(path, "来源字段");
        if (!path.StartsWith('/')) throw new InvalidOperationException("来源字段必须从字段树中选择");
        if (EdgeDashboardJsonFieldResolver.Resolve(envelope, path) == null)
            throw new InvalidOperationException($"最新报文中不存在字段：{path}");
        return path;
    }

    private static string ValidateSourcePath(string path, ISet<string> sourcePaths)
    {
        path = Require(path, "来源字段");
        if (!path.StartsWith('/')) throw new InvalidOperationException("来源字段必须从字段树中选择");
        if (!sourcePaths.Contains(path)) throw new InvalidOperationException($"模型历史字段目录中不存在字段：{path}");
        return path;
    }

    /// <summary>把字段最新样例值保存为 JSON，后续配置界面可看到字段类型和值示例。</summary>
    private static string? SerializeFieldCurrentValue(object? value) =>
        value == null ? null : JsonConvert.SerializeObject(value, Formatting.None);

    /// <summary>把字段目录中的 JSON 样例值还原成前端可直接展示的普通值。</summary>
    private static object? DeserializeFieldCurrentValue(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson)) return null;
        try
        {
            return JsonConvert.DeserializeObject<object>(valueJson);
        }
        catch (JsonException)
        {
            return valueJson;
        }
    }

    /// <summary>按模型读取实时快照，未分类模型使用统一占位编码。</summary>
    private ISugarQueryable<EdgeRealtimeDeviceSnapshotEntity> QuerySnapshotsByModel(string modelMark)
    {
        var unclassified = modelMark.Equals(UnclassifiedModelMark, StringComparison.OrdinalIgnoreCase);
        return Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(unclassified, x => x.ModelMark == null || x.ModelMark == string.Empty)
            .WhereIF(!unclassified, x => x.ModelMark == modelMark);
    }

    private static void ParseJsonObject(string? json, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || JToken.Parse(json).Type != JTokenType.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{name} 必须是 JSON 对象");
        }
    }

    /// <summary>新增线体时一并写入可直接编辑的空 Three.js 场景。</summary>
    private static string CreateEmptySceneLayoutJson(string lineCode, string lineName, DateTime now) =>
        new JObject
        {
            ["version"] = 1,
            ["lineId"] = lineCode,
            ["lineName"] = lineName,
            ["updatedAt"] = now.ToUniversalTime().ToString("O"),
            ["robots"] = new JArray(),
            ["devices"] = new JArray(),
            ["conveyors"] = new JArray(),
            ["workCells"] = new JArray(),
            ["junctions"] = new JArray()
        }.ToString(Formatting.None);

    /// <summary>复制场景 JSON 时仅替换目标线体自身元数据，场景中的设备、机械手和坐标保持不变。</summary>
    private static string BuildCopiedSceneLayoutJson(
        string sourceLayoutJson,
        EdgeDashboardLineCopyDto dto,
        DateTime now)
    {
        try
        {
            var root = JObject.Parse(sourceLayoutJson);
            root["lineId"] = dto.TargetLineCode;
            root["lineName"] = dto.TargetLineName;
            root["lineArea"] = dto.Area;
            root["lineColor"] = dto.Color;
            root["enabled"] = dto.Enabled;
            root["updatedAt"] = now.ToUniversalTime().ToString("O");
            return root.ToString(Formatting.None);
        }
        catch (JsonReaderException)
        {
            throw new InvalidOperationException("来源线体的 Three.js 场景配置不是有效 JSON，无法复制");
        }
    }

    /// <summary>目标线体必须为全新编码，避免把复制配置写入既有线体。</summary>
    private async Task<bool> HasLineConfigurationAsync(string dashboardCode, string lineCode)
    {
        if (await Db.Queryable<EdgeDashboardLineConfigEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardSceneLayoutEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardRobotBindingEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardDeviceBindingEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardLineOutputBindingEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardWidgetEntity>().AnyAsync(x =>
                x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsDelete == 0)) return true;
        if (await Db.Queryable<EdgeDashboardDeviceCardEntity>().AnyAsync(x =>
                x.LineCode == lineCode && x.IsDelete == 0)) return true;
        return await Db.Queryable<EdgeDashboardDeviceCardBoardSettingEntity>()
            .AnyAsync(x => x.LineCode == lineCode);
    }

    /// <summary>部件编码在整个看板内唯一，复制时加入新线体编码并自动消除冲突。</summary>
    private static string BuildCopiedWidgetCode(
        string sourceCode,
        string targetLineCode,
        ISet<string> usedCodes)
    {
        // 线体编码允许最大 64 位，部件编码也限制为 64 位；后缀取前 48 位以始终留下源编码空间。
        var targetCodePart = targetLineCode.Length > 48 ? targetLineCode[..48] : targetLineCode;
        var suffix = $"_{targetCodePart}";
        var prefixLength = Math.Max(1, 64 - suffix.Length);
        var prefix = sourceCode.Length > prefixLength ? sourceCode[..prefixLength] : sourceCode;
        var candidate = $"{prefix}{suffix}";
        var index = 2;
        while (!usedCodes.Add(candidate))
        {
            var numberedSuffix = $"_{targetCodePart}_{index}";
            prefixLength = Math.Max(1, 64 - numberedSuffix.Length);
            prefix = sourceCode.Length > prefixLength ? sourceCode[..prefixLength] : sourceCode;
            candidate = $"{prefix}{numberedSuffix}";
            index++;
        }
        return candidate;
    }

    /// <summary>验证前端 Three.js 布局的关键结构，并压缩 JSON 后入库。</summary>
    private static string ValidateSceneLayoutJson(
        string? json,
        string lineCode,
        int layoutVersion)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("场景布局 JSON 不能为空");
        if (json.Length > 5_000_000)
            throw new InvalidOperationException("场景布局 JSON 不能超过 5 MB");

        try
        {
            var root = JObject.Parse(json);
            var jsonLineCode = root.Value<string>("lineId")?.Trim();
            if (!string.Equals(jsonLineCode, lineCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("场景布局中的 lineId 必须与线体编码一致");
            var jsonVersion = root.Value<int?>("version") ?? 0;
            if (jsonVersion != layoutVersion)
                throw new InvalidOperationException("场景布局中的 version 必须与布局版本一致");

            foreach (var propertyName in new[] { "robots", "conveyors", "workCells", "junctions" })
            {
                if (root[propertyName]?.Type != JTokenType.Array)
                    throw new InvalidOperationException($"场景布局中的 {propertyName} 必须是数组");
            }

            if (root["devices"] == null)
                root["devices"] = new JArray();
            else if (root["devices"]?.Type != JTokenType.Array)
                throw new InvalidOperationException("场景布局中的 devices 必须是数组");

            return root.ToString(Formatting.None);
        }
        catch (JsonReaderException)
        {
            throw new InvalidOperationException("场景布局必须是有效的 JSON 对象");
        }
    }

    private static EdgeDashboardLineOutputBindingOutputDto ToLineOutputBinding(
        EdgeDashboardLineOutputBindingEntity entity) =>
        new()
        {
            Id = entity.Id,
            DashboardCode = entity.DashboardCode,
            LineCode = entity.LineCode,
            TenantMark = entity.TenantMark,
            DeviceMark = entity.DeviceMark,
            SourcePath = entity.SourcePath,
            FieldName = entity.FieldName,
            CounterMode = entity.CounterMode,
            Unit = entity.Unit,
            Decimals = entity.Decimals,
            Enabled = entity.Enabled,
            CreateTime = entity.CreateTime,
            UpdateTime = entity.UpdateTime
        };

    private static bool IsNumericValue(object? value) =>
        value != null && decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out _);

    private static bool TryReadDecimal(JToken? token, out decimal value)
    {
        value = 0;
        var raw = EdgeDashboardJsonFieldResolver.ToObject(token);
        return raw != null && decimal.TryParse(
            Convert.ToString(raw, CultureInfo.InvariantCulture),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} 不能为空")
            : value.Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TrimToNull(string? value, int maximumLength)
    {
        var normalized = Normalize(value);
        return normalized == null ? null : normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private static string NormalizeModelMark(string modelMark) =>
        string.IsNullOrWhiteSpace(modelMark) ||
        modelMark.Equals(UnclassifiedModelMark, StringComparison.OrdinalIgnoreCase)
            ? UnclassifiedModelMark
            : modelMark.Trim();
}
