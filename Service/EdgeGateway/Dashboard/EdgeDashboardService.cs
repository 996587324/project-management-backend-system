using AutoMapper;
using Interface;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

/// <summary>实现机械手数据绑定、统计查询和数据库驱动的通用看板部件配置。</summary>
public sealed class EdgeDashboardService : IEdgeDashboardService
{
    private const string UnclassifiedModelMark = "__UNCLASSIFIED__";
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

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
        return records
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ModelMark)
                ? UnclassifiedModelMark
                : x.ModelMark.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new EdgeDashboardDataSourceModelOptionDto
            {
                ModelMark = group.Key,
                DisplayName = group.Key == UnclassifiedModelMark ? "未分类" : group.Key,
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

    /// <summary>从数据源最新快照生成完整 HTTP 报文叶子字段目录。</summary>
    public async Task<List<EdgeDashboardSourceFieldOptionDto>> GetSourceFieldsAsync(
        string tenantMark,
        string deviceMark)
    {
        tenantMark = Require(tenantMark, "tenantMark");
        deviceMark = Require(deviceMark, "deviceMark");
        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == tenantMark && x.DeviceCode == deviceMark);
        if (snapshot == null) throw new InvalidOperationException("未找到该 HTTP 数据源的最新快照");
        return EdgeDashboardJsonFieldResolver.Flatten(
            EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot));
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
        if (bindings.Count == 0)
            return new EdgeDashboardLineRuntimeDto { LineCode = lineCode };

        var bindingIds = bindings.Select(x => x.Id).ToList();
        var deviceMarks = bindings.Select(x => x.DeviceMark).Distinct().ToList();
        var fields = await Db.Queryable<EdgeDashboardRobotFieldBindingEntity>()
            .Where(x => bindingIds.Contains(x.RobotBindingId) && x.Enabled && x.IsDelete == 0)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var states = await Db.Queryable<EdgeDashboardRobotRuntimeStateEntity>()
            .Where(x => bindingIds.Contains(x.RobotBindingId))
            .ToListAsync();
        var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Where(x => deviceMarks.Contains(x.DeviceCode))
            .ToListAsync();

        var fieldGroups = fields.GroupBy(x => x.RobotBindingId)
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
                foreach (var field in fieldGroups.GetValueOrDefault(binding.Id) ?? [])
                {
                    var rawToken = EdgeDashboardJsonFieldResolver.Resolve(envelope, field.SourcePath);
                    var rawValue = EdgeDashboardJsonFieldResolver.ToObject(rawToken);
                    robot.Fields.Add(new EdgeDashboardRuntimeFieldDto
                    {
                        FieldCode = field.FieldCode,
                        SourcePath = field.SourcePath,
                        DisplayName = field.DisplayName,
                        Purpose = field.Purpose,
                        RawValue = rawValue,
                        DisplayValue = FormatFieldValue(rawToken, field),
                        Unit = field.Unit,
                        Sort = field.Sort
                    });
                }
            }
            output.Robots.Add(robot);
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
        var robotCode = Normalize(query.RobotCode);
        var dateFrom = query.DateFrom?.Date;
        var dateTo = query.DateTo?.Date;
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeDashboardRobotDailyStatisticEntity>()
            .WhereIF(lineCode != null, x => x.LineCode == lineCode)
            .WhereIF(robotCode != null, x => x.RobotCode == robotCode)
            .WhereIF(dateFrom.HasValue, x => x.StatDate >= dateFrom!.Value)
            .WhereIF(dateTo.HasValue, x => x.StatDate <= dateTo!.Value)
            .OrderByDescending(x => x.StatDate)
            .OrderBy(x => x.RobotCode)
            .ToPageListAsync(page, size, total);
        var outputs = _mapper.Map<List<EdgeDashboardDailyStatisticOutputDto>>(records);
        foreach (var output in outputs)
        {
            var effective = output.RunningSeconds + output.IdleSeconds + output.FaultSeconds;
            output.AvailabilityRate = effective == 0
                ? 0
                : Math.Round(output.RunningSeconds * 100m / effective, 2);
        }
        return new EdgePagedResultDto<EdgeDashboardDailyStatisticOutputDto>
        {
            Records = outputs,
            Current = page,
            Size = size,
            Total = total
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
    public async Task<EdgeDashboardBootstrapOutputDto> GetBootstrapAsync(string dashboardCode)
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
        if (lines.Count == 0 && !hasLineHistory && scenes.Count > 0)
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

        var duplicate = await Db.Queryable<EdgeDashboardLineConfigEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.LineCode &&
            x.Id != (existing == null ? 0 : existing.Id) &&
            x.IsDelete == 0);
        if (duplicate) throw new InvalidOperationException("同一看板的线体编码不能重复");

        var now = DateTime.Now;
        var entity = _mapper.Map<EdgeDashboardLineConfigEntity>(dto);
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
            await Db.Updateable(entity).ExecuteCommandAsync();
        }

        return _mapper.Map<EdgeDashboardLineConfigOutputDto>(entity);
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
    /// 修改线体编码，并在同一事务内同步场景、机械手绑定、统计、故障和图表配置。
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
        var widgetConflict = await Db.Queryable<EdgeDashboardWidgetEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.IsDelete == 0);
        var lineConfig = await Db.Queryable<EdgeDashboardLineConfigEntity>()
            .FirstAsync(x => x.DashboardCode == dto.DashboardCode &&
                             x.LineCode == dto.OldLineCode &&
                             x.IsDelete == 0);
        var lineConflict = await Db.Queryable<EdgeDashboardLineConfigEntity>().AnyAsync(x =>
            x.DashboardCode == dto.DashboardCode &&
            x.LineCode == dto.NewLineCode &&
            x.Id != (lineConfig == null ? 0 : lineConfig.Id) &&
            x.IsDelete == 0);
        if (sceneConflict || bindingConflict || widgetConflict || lineConflict)
            throw new InvalidOperationException("新线体编码已存在关联配置，请更换编码");

        var bindingIds = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
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

            return root.ToString(Formatting.None);
        }
        catch (JsonReaderException)
        {
            throw new InvalidOperationException("场景布局必须是有效的 JSON 对象");
        }
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} 不能为空")
            : value.Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeModelMark(string modelMark) =>
        string.IsNullOrWhiteSpace(modelMark) ||
        modelMark.Equals(UnclassifiedModelMark, StringComparison.OrdinalIgnoreCase)
            ? UnclassifiedModelMark
            : modelMark.Trim();
}
