using System.Globalization;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

/// <summary>
/// 将 HTTP 最新数据样本转换为机械手当前状态、按业务日累计时长和故障生命周期记录。
/// </summary>
public sealed class EdgeDashboardTelemetryProcessor : IEdgeDashboardTelemetryProcessor
{
    private static readonly SemaphoreSlim ProcessingGate = new(1, 1);
    private const string BindingRuleScope = "Binding";
    private const string ModelRuleScope = "Model";
    private const string UnclassifiedModelMark = "__UNCLASSIFIED__";
    private readonly SqlSugarScope _dbScope;
    private readonly ILogger<EdgeDashboardTelemetryProcessor> _logger;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    /// <summary>通过 IOC 获取项目现有 SqlSugarScope 和日志组件。</summary>
    public EdgeDashboardTelemetryProcessor(
        SqlSugarScope dbScope,
        ILogger<EdgeDashboardTelemetryProcessor> logger)
    {
        _dbScope = dbScope;
        _logger = logger;
    }

    /// <summary>处理一条新样本；一份 HTTP 数据源可以同时驱动多个已绑定机械手。</summary>
    public async Task ProcessAsync(
        EdgeDashboardTelemetrySampleDto sample,
        CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await ProcessingGate.WaitAsync(cancellationToken);
        try
        {
            var modelMark = NormalizeModelMark(sample.ModelMark);
            var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(sample);
            await RecordModelFieldsAsync(modelMark, EdgeDashboardJsonFieldResolver.BuildReportedEnvelope(sample), sample);

            var bindings = await Db.Queryable<EdgeDashboardRobotBindingEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled &&
                            x.TenantMark == sample.TenantMark &&
                            x.DeviceMark == sample.DeviceMark)
                .ToListAsync();
            var deviceBindings = await Db.Queryable<EdgeDashboardDeviceBindingEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled && x.StatisticsEnabled &&
                            x.TenantMark == sample.TenantMark &&
                            x.SourceDeviceMark == sample.DeviceMark)
                .ToListAsync();
            var outputBindings = await Db.Queryable<EdgeDashboardLineOutputBindingEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled &&
                            x.TenantMark == sample.TenantMark &&
                            x.DeviceMark == sample.DeviceMark)
                .ToListAsync();
            if (bindings.Count == 0 && deviceBindings.Count == 0 && outputBindings.Count == 0) return;

            foreach (var outputBinding in outputBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessLineOutputBindingAsync(outputBinding, envelope, sample);
            }
            var bindingIds = bindings.Select(x => x.Id).ToList();
            var dashboardCodes = bindings.Select(x => x.DashboardCode)
                .Concat(deviceBindings.Select(x => x.DashboardCode))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rules = bindingIds.Count == 0
                ? []
                : await Db.Queryable<EdgeDashboardRobotStateRuleEntity>()
                    .Where(x => bindingIds.Contains(x.RobotBindingId) &&
                                x.IsDelete == 0 && x.Enabled)
                    .OrderBy(x => x.Priority)
                    .ToListAsync();
            var modelAlarmRules = dashboardCodes.Count == 0
                ? []
                : await Db.Queryable<EdgeDashboardModelAlarmRuleEntity>()
                    .Where(x => dashboardCodes.Contains(x.DashboardCode) &&
                                x.ModelMark == modelMark &&
                                x.IsDelete == 0 && x.Enabled)
                    .OrderBy(x => x.Priority)
                    .ToListAsync();
            var modelStatePriorities = dashboardCodes.Count == 0
                ? []
                : await Db.Queryable<EdgeDashboardModelStatePriorityEntity>()
                    .Where(x => dashboardCodes.Contains(x.DashboardCode) &&
                                x.ModelMark == modelMark &&
                                x.IsDelete == 0)
                    .ToListAsync();
            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessBindingAsync(
                    binding,
                    rules.Where(x => x.RobotBindingId == binding.Id).ToList(),
                    modelAlarmRules.Where(x => x.DashboardCode == binding.DashboardCode).ToList(),
                    modelStatePriorities.Where(x => x.DashboardCode == binding.DashboardCode).ToList(),
                    envelope,
                    sample);
            }
            foreach (var binding in deviceBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessDeviceBindingAsync(
                    binding,
                    modelAlarmRules.Where(x => x.DashboardCode == binding.DashboardCode).ToList(),
                    modelStatePriorities.Where(x => x.DashboardCode == binding.DashboardCode).ToList(),
                    envelope,
                    sample);
            }
        }
        finally
        {
            ProcessingGate.Release();
        }
    }

    /// <summary>定时把超过在线阈值的机械手推进为离线，并持续累计离线秒数。</summary>
    public async Task SweepOfflineAsync(DateTime now, CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await ProcessingGate.WaitAsync(cancellationToken);
        try
        {
            using var db = _dbScope.GetConnection("calmadmin").CopyNew();
            var bindings = await db.Queryable<EdgeDashboardRobotBindingEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled)
                .ToListAsync();
            var deviceBindings = await db.Queryable<EdgeDashboardDeviceBindingEntity>()
                .Where(x => x.IsDelete == 0 && x.Enabled && x.StatisticsEnabled)
                .ToListAsync();
            if (bindings.Count == 0 && deviceBindings.Count == 0) return;

            var bindingMap = bindings.ToDictionary(x => x.Id);
            var bindingIds = bindingMap.Keys.ToList();
            var runtimeStates = bindingIds.Count == 0
                ? []
                : await db.Queryable<EdgeDashboardRobotRuntimeStateEntity>()
                    .Where(x => bindingIds.Contains(x.RobotBindingId))
                    .ToListAsync();
            foreach (var runtime in runtimeStates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bindingMap.TryGetValue(runtime.RobotBindingId, out var binding) ||
                    now <= runtime.LastAccountedTime)
                    continue;

                var offlineStart = runtime.LastSampleTime.AddSeconds(binding.OnlineTimeoutSeconds);
                if (runtime.CurrentState == EdgeDashboardConstants.Offline)
                {
                    await AddDurationAsync(
                        binding,
                        EdgeDashboardConstants.Offline,
                        runtime.LastAccountedTime,
                        now,
                        db);
                }
                else
                {
                    var stateEnd = Min(now, offlineStart);
                    await AddDurationAsync(
                        binding,
                        runtime.CurrentState,
                        runtime.LastAccountedTime,
                        stateEnd,
                        db);
                    if (now > offlineStart)
                    {
                        await AddDurationAsync(
                            binding,
                            EdgeDashboardConstants.Offline,
                            Max(runtime.LastAccountedTime, offlineStart),
                            now,
                            db);
                        await CloseActiveFaultsAsync(binding.Id, offlineStart, null, db);
                        runtime.CurrentState = EdgeDashboardConstants.Offline;
                        runtime.StateStartTime = offlineStart;
                    }
                }

                runtime.LastAccountedTime = now;
                runtime.UpdateTime = now;
                await db.Updateable(runtime).ExecuteCommandAsync();
            }

            var deviceBindingMap = deviceBindings.ToDictionary(x => x.Id);
            var deviceBindingIds = deviceBindingMap.Keys.ToList();
            var deviceRuntimeStates = deviceBindingIds.Count == 0
                ? []
                : await db.Queryable<EdgeDashboardDeviceRuntimeStateEntity>()
                    .Where(x => deviceBindingIds.Contains(x.DeviceBindingId))
                    .ToListAsync();
            foreach (var runtime in deviceRuntimeStates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!deviceBindingMap.TryGetValue(runtime.DeviceBindingId, out var binding) ||
                    now <= runtime.LastAccountedTime)
                    continue;

                var offlineStart = runtime.LastSampleTime.AddSeconds(binding.OnlineTimeoutSeconds);
                if (runtime.CurrentState == EdgeDashboardConstants.Offline)
                {
                    await AddDeviceDurationAsync(
                        binding,
                        EdgeDashboardConstants.Offline,
                        runtime.LastAccountedTime,
                        now,
                        db);
                }
                else
                {
                    await AddDeviceDurationAsync(
                        binding,
                        runtime.CurrentState,
                        runtime.LastAccountedTime,
                        Min(now, offlineStart),
                        db);
                    if (now > offlineStart)
                    {
                        await AddDeviceDurationAsync(
                            binding,
                            EdgeDashboardConstants.Offline,
                            Max(runtime.LastAccountedTime, offlineStart),
                            now,
                            db);
                        runtime.CurrentState = EdgeDashboardConstants.Offline;
                        runtime.StateStartTime = offlineStart;
                    }
                }

                runtime.LastAccountedTime = now;
                runtime.UpdateTime = now;
                await db.Updateable(runtime).ExecuteCommandAsync();
            }
        }
        finally
        {
            ProcessingGate.Release();
        }
    }

    /// <summary>保存当前模型已接收过的字段路径，供模型报警映射配置页面稳定下拉选择。</summary>
    private async Task RecordModelFieldsAsync(
        string modelMark,
        JObject envelope,
        EdgeDashboardTelemetrySampleDto sample)
    {
        var fields = EdgeDashboardJsonFieldResolver.Flatten(envelope)
            .GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (fields.Count == 0) return;

        var now = DateTime.Now;
        var paths = fields
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

        foreach (var field in fields)
        {
            if (existingMap.TryGetValue(field.SourcePath, out var record))
            {
                record.FieldName = field.FieldName;
                record.ValueType = field.ValueType;
                record.LastValueJson = SerializeFieldCurrentValue(field.CurrentValue);
                record.SampleTenantMark = sample.TenantMark;
                record.SampleDeviceMark = sample.DeviceMark;
                record.SeenCount = Math.Max(0, record.SeenCount) + 1;
                record.IsDelete = 0;
                record.LastSeenTime = sample.ReceiveTime;
                record.UpdateTime = now;
                updates.Add(record);
                continue;
            }

            inserts.Add(new EdgeDashboardModelFieldCatalogEntity
            {
                ModelMark = modelMark,
                SourcePath = field.SourcePath,
                FieldName = field.FieldName,
                ValueType = field.ValueType,
                LastValueJson = SerializeFieldCurrentValue(field.CurrentValue),
                SampleTenantMark = sample.TenantMark,
                SampleDeviceMark = sample.DeviceMark,
                SeenCount = 1,
                FirstSeenTime = sample.ReceiveTime,
                LastSeenTime = sample.ReceiveTime,
                CreateTime = now,
                UpdateTime = now
            });
        }

        // SQL Server BulkCopy + 固定结构的批量更新，避免每条报文生成包含变化值的 UNION ALL SQL。
        // 保留逐条数据的计数、最新值及时间语义，不采用延迟刷盘。
        var updateColumns = new[]
        {
            nameof(EdgeDashboardModelFieldCatalogEntity.FieldName),
            nameof(EdgeDashboardModelFieldCatalogEntity.ValueType),
            nameof(EdgeDashboardModelFieldCatalogEntity.LastValueJson),
            nameof(EdgeDashboardModelFieldCatalogEntity.SampleTenantMark),
            nameof(EdgeDashboardModelFieldCatalogEntity.SampleDeviceMark),
            nameof(EdgeDashboardModelFieldCatalogEntity.SeenCount),
            nameof(EdgeDashboardModelFieldCatalogEntity.IsDelete),
            nameof(EdgeDashboardModelFieldCatalogEntity.LastSeenTime),
            nameof(EdgeDashboardModelFieldCatalogEntity.UpdateTime)
        };
        foreach (var chunk in updates.Chunk(200))
        {
            await Db.Fastest<EdgeDashboardModelFieldCatalogEntity>().BulkUpdateAsync(
                chunk.ToList(),
                new[] { nameof(EdgeDashboardModelFieldCatalogEntity.Id) },
                updateColumns);
        }
        foreach (var chunk in inserts.Chunk(500))
            await Db.Insertable(chunk.ToList()).ExecuteCommandAsync();
    }

    /// <summary>推进一台绑定机械手的互斥状态，并同步本次样本对应的故障记录。</summary>
    private async Task ProcessBindingAsync(
        EdgeDashboardRobotBindingEntity binding,
        IReadOnlyCollection<EdgeDashboardRobotStateRuleEntity> rules,
        IReadOnlyCollection<EdgeDashboardModelAlarmRuleEntity> modelAlarmRules,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities,
        JObject envelope,
        EdgeDashboardTelemetrySampleDto sample)
    {
        var matchedRules = rules
            .Where(rule => Matches(rule, EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.SourcePath)))
            .Select(rule => ToMatchedRule(rule, binding.RuleVersion))
            .Concat(modelAlarmRules
                .Where(rule => Matches(rule, EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.SourcePath)))
                .Select(ToMatchedRule))
            .OrderBy(rule => StatePriority(rule, modelStatePriorities))
            .ThenBy(x => x.Priority)
            .ToList();
        var newState = matchedRules.FirstOrDefault()?.TargetState ?? EdgeDashboardConstants.Unknown;
        var runtime = await Db.Queryable<EdgeDashboardRobotRuntimeStateEntity>()
            .FirstAsync(x => x.RobotBindingId == binding.Id);

        if (runtime == null)
        {
            await SynchronizeFaultsAsync(binding, [], matchedRules, envelope, sample.DataTime, sample.MessageId);
            runtime = new EdgeDashboardRobotRuntimeStateEntity
            {
                RobotBindingId = binding.Id,
                CurrentState = newState,
                StateStartTime = sample.DataTime,
                LastSampleTime = sample.DataTime,
                LastAccountedTime = sample.DataTime,
                LastMessageId = sample.MessageId,
                LastSourceTimestamp = sample.SourceTimestamp,
                RuleVersion = binding.RuleVersion,
                UpdateTime = sample.ReceiveTime
            };
            await Db.Insertable(runtime).ExecuteCommandAsync();
            await IncrementSampleCountAsync(binding, sample.DataTime, newState);
            return;
        }

        if (sample.DataTime < runtime.LastSampleTime ||
            (!string.IsNullOrWhiteSpace(sample.MessageId) &&
             string.Equals(sample.MessageId, runtime.LastMessageId, StringComparison.Ordinal)))
            return;

        var accountedSampleTime = Max(sample.DataTime, runtime.LastAccountedTime);
        var offlineStart = runtime.LastSampleTime.AddSeconds(binding.OnlineTimeoutSeconds);
        if (accountedSampleTime > runtime.LastAccountedTime)
        {
            if (runtime.CurrentState == EdgeDashboardConstants.Offline)
            {
                await AddDurationAsync(
                    binding,
                    EdgeDashboardConstants.Offline,
                    runtime.LastAccountedTime,
                    accountedSampleTime);
            }
            else
            {
                await AddDurationAsync(
                    binding,
                    runtime.CurrentState,
                    runtime.LastAccountedTime,
                    Min(accountedSampleTime, offlineStart));
                if (accountedSampleTime > offlineStart)
                {
                    await AddDurationAsync(
                        binding,
                        EdgeDashboardConstants.Offline,
                        Max(runtime.LastAccountedTime, offlineStart),
                        accountedSampleTime);
                    await CloseActiveFaultsAsync(binding.Id, offlineStart, sample.MessageId);
                }
            }
        }

        var activeFaults = await Db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .Where(x => x.RobotBindingId == binding.Id && x.EndTime == null)
            .ToListAsync();
        await SynchronizeFaultsAsync(
            binding,
            activeFaults,
            matchedRules,
            envelope,
            accountedSampleTime,
            sample.MessageId);

        if (!string.Equals(runtime.CurrentState, newState, StringComparison.OrdinalIgnoreCase))
            runtime.StateStartTime = accountedSampleTime;
        runtime.CurrentState = newState;
        runtime.LastSampleTime = sample.DataTime;
        runtime.LastAccountedTime = accountedSampleTime;
        runtime.LastMessageId = sample.MessageId;
        runtime.LastSourceTimestamp = sample.SourceTimestamp;
        runtime.RuleVersion = binding.RuleVersion;
        runtime.UpdateTime = sample.ReceiveTime;
        await Db.Updateable(runtime).ExecuteCommandAsync();
        await IncrementSampleCountAsync(binding, sample.DataTime, newState);
    }

    /// <summary>推进启用统计的设备组件状态，并把时长写入独立的设备日统计表。</summary>
    private async Task ProcessDeviceBindingAsync(
        EdgeDashboardDeviceBindingEntity binding,
        IReadOnlyCollection<EdgeDashboardModelAlarmRuleEntity> modelAlarmRules,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities,
        JObject envelope,
        EdgeDashboardTelemetrySampleDto sample)
    {
        var newState = ResolveDeviceStatisticState(
            binding,
            modelAlarmRules,
            modelStatePriorities,
            envelope);
        var runtime = await Db.Queryable<EdgeDashboardDeviceRuntimeStateEntity>()
            .FirstAsync(x => x.DeviceBindingId == binding.Id);

        if (runtime == null)
        {
            runtime = new EdgeDashboardDeviceRuntimeStateEntity
            {
                DeviceBindingId = binding.Id,
                CurrentState = newState,
                StateStartTime = sample.DataTime,
                LastSampleTime = sample.DataTime,
                LastAccountedTime = sample.DataTime,
                LastMessageId = sample.MessageId,
                LastSourceTimestamp = sample.SourceTimestamp,
                UpdateTime = sample.ReceiveTime
            };
            await Db.Insertable(runtime).ExecuteCommandAsync();
            await IncrementDeviceSampleCountAsync(binding, sample.DataTime, newState);
            if (newState == EdgeDashboardConstants.Fault)
                await IncrementDeviceFaultCountAsync(binding, sample.DataTime);
            return;
        }

        if (sample.DataTime < runtime.LastSampleTime ||
            (!string.IsNullOrWhiteSpace(sample.MessageId) &&
             string.Equals(sample.MessageId, runtime.LastMessageId, StringComparison.Ordinal)))
            return;

        var accountedSampleTime = Max(sample.DataTime, runtime.LastAccountedTime);
        var offlineStart = runtime.LastSampleTime.AddSeconds(binding.OnlineTimeoutSeconds);
        if (accountedSampleTime > runtime.LastAccountedTime)
        {
            if (runtime.CurrentState == EdgeDashboardConstants.Offline)
            {
                await AddDeviceDurationAsync(
                    binding,
                    EdgeDashboardConstants.Offline,
                    runtime.LastAccountedTime,
                    accountedSampleTime);
            }
            else
            {
                await AddDeviceDurationAsync(
                    binding,
                    runtime.CurrentState,
                    runtime.LastAccountedTime,
                    Min(accountedSampleTime, offlineStart));
                if (accountedSampleTime > offlineStart)
                {
                    await AddDeviceDurationAsync(
                        binding,
                        EdgeDashboardConstants.Offline,
                        Max(runtime.LastAccountedTime, offlineStart),
                        accountedSampleTime);
                }
            }
        }

        var enteredFault = runtime.CurrentState != EdgeDashboardConstants.Fault &&
                           newState == EdgeDashboardConstants.Fault;
        if (!string.Equals(runtime.CurrentState, newState, StringComparison.OrdinalIgnoreCase))
            runtime.StateStartTime = accountedSampleTime;
        runtime.CurrentState = newState;
        runtime.LastSampleTime = sample.DataTime;
        runtime.LastAccountedTime = accountedSampleTime;
        runtime.LastMessageId = sample.MessageId;
        runtime.LastSourceTimestamp = sample.SourceTimestamp;
        runtime.UpdateTime = sample.ReceiveTime;
        await Db.Updateable(runtime).ExecuteCommandAsync();
        await IncrementDeviceSampleCountAsync(binding, sample.DataTime, newState);
        if (enteredFault) await IncrementDeviceFaultCountAsync(binding, sample.DataTime);
    }

    /// <summary>设备在模型报警与状态映射同时命中时，按模型状态优先级裁决最终统计状态。</summary>
    private static string ResolveDeviceStatisticState(
        EdgeDashboardDeviceBindingEntity binding,
        IReadOnlyCollection<EdgeDashboardModelAlarmRuleEntity> modelAlarmRules,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities,
        JObject envelope)
    {
        var candidates = new List<string>();
        var hasAlarm = modelAlarmRules.Any(rule =>
            Matches(rule, EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.SourcePath)));
        if (hasAlarm) candidates.Add(EdgeDashboardConstants.Fault);
        if (string.IsNullOrWhiteSpace(binding.StatusSourcePath))
        {
            candidates.Add(EdgeDashboardConstants.Running);
            return ResolveHighestPriorityState(candidates, modelStatePriorities);
        }

        var rawValue = EdgeDashboardJsonFieldResolver.ToCompactText(
                EdgeDashboardJsonFieldResolver.Resolve(envelope, binding.StatusSourcePath)) ?? string.Empty;
        var mapping = ReadDeviceStatusMapping(binding.StatusMappingJson);
        candidates.Add(mapping.TryGetValue(rawValue, out var state)
            ? state
            : EdgeDashboardConstants.Unknown);
        return ResolveHighestPriorityState(candidates, modelStatePriorities);
    }

    /// <summary>解析设备状态映射；保存时已校验，运行时异常则安全回退为空映射。</summary>
    private static IReadOnlyDictionary<string, string> ReadDeviceStatusMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JObject.Parse(json).Properties()
                .ToDictionary(
                    property => property.Name.Trim(),
                    property => property.Value.ToString().Trim().ToUpperInvariant(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>每次收到产量来源设备的新报文时推进日统计，避免看板无人打开时遗漏产量增量。</summary>
    private async Task ProcessLineOutputBindingAsync(
        EdgeDashboardLineOutputBindingEntity binding,
        JObject envelope,
        EdgeDashboardTelemetrySampleDto sample)
    {
        var token = EdgeDashboardJsonFieldResolver.Resolve(envelope, binding.SourcePath);
        if (!TryReadDecimal(token, out var currentValue)) return;

        var statDate = sample.DataTime.Date;
        var now = sample.ReceiveTime;
        var daily = await Db.Queryable<EdgeDashboardLineOutputDailyStatisticEntity>()
            .FirstAsync(x => x.OutputBindingId == binding.Id && x.StatDate == statDate);
        if (daily == null)
        {
            await Db.Insertable(new EdgeDashboardLineOutputDailyStatisticEntity
            {
                OutputBindingId = binding.Id,
                StatDate = statDate,
                LineCode = binding.LineCode,
                BaselineValue = currentValue,
                LastValue = currentValue,
                OutputQuantity = binding.CounterMode == "CurrentValue" ? currentValue : 0,
                LastDataTime = sample.DataTime,
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
            return;
        }

        if (daily.LastDataTime.HasValue && sample.DataTime <= daily.LastDataTime.Value) return;
        if (binding.CounterMode == "CurrentValue")
            daily.OutputQuantity = currentValue;
        else
            daily.OutputQuantity += currentValue >= daily.LastValue
                ? currentValue - daily.LastValue
                : currentValue; // 计数器归零，从归零后的数值继续累计。
        daily.LastValue = currentValue;
        daily.LastDataTime = sample.DataTime;
        daily.LineCode = binding.LineCode;
        daily.UpdateTime = now;
        await Db.Updateable(daily).ExecuteCommandAsync();
    }

    /// <summary>根据当前命中的故障规则新增、更新或关闭故障生命周期。</summary>
    private async Task SynchronizeFaultsAsync(
        EdgeDashboardRobotBindingEntity binding,
        IReadOnlyCollection<EdgeDashboardRobotFaultRecordEntity> activeFaults,
        IReadOnlyCollection<DashboardMatchedRule> matchedRules,
        JObject envelope,
        DateTime eventTime,
        string? messageId)
    {
        var faultRules = matchedRules
            .Where(x => x.TargetState == EdgeDashboardConstants.Fault)
            .ToList();
        var matchedKeys = faultRules
            .Select(x => CreateRuleKey(x.RuleScope, x.Id))
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTime.Now;

        foreach (var active in activeFaults.Where(x =>
                     !matchedKeys.Contains(CreateRuleKey(NormalizeRuleScope(x.RuleScope), x.FaultRuleId))))
        {
            CloseFault(active, eventTime, messageId, now);
            await Db.Updateable(active).ExecuteCommandAsync();
        }

        foreach (var rule in faultRules)
        {
            var rawToken = EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.SourcePath);
            var faultCode = string.IsNullOrWhiteSpace(rule.FaultCode)
                ? rule.RuleCode
                : rule.FaultCode;
            var faultReason = ResolveFaultReason(rule, envelope, faultCode);
            var existing = activeFaults.FirstOrDefault(x =>
                x.FaultRuleId == rule.Id &&
                string.Equals(NormalizeRuleScope(x.RuleScope), rule.RuleScope, StringComparison.Ordinal));
            if (existing != null)
            {
                // 持续中的模型报警也同步修正为报警映射编码，避免旧记录一直显示判断值 0/1。
                existing.FaultCode = faultCode;
                existing.FaultReason = faultReason;
                existing.LastSeenTime = eventTime;
                existing.DurationSeconds = Math.Max(0, (long)(eventTime - existing.StartTime).TotalSeconds);
                existing.RawValue = EdgeDashboardJsonFieldResolver.ToCompactText(rawToken);
                existing.UpdateTime = now;
                await Db.Updateable(existing).ExecuteCommandAsync();
                continue;
            }

            var fault = new EdgeDashboardRobotFaultRecordEntity
            {
                RobotBindingId = binding.Id,
                FaultRuleId = rule.Id,
                LineCode = binding.LineCode,
                RobotCode = binding.RobotCode,
                RobotName = binding.RobotName,
                TenantMark = binding.TenantMark,
                DeviceMark = binding.DeviceMark,
                FaultCode = faultCode,
                RuleScope = rule.RuleScope,
                FaultReason = faultReason,
                Severity = string.IsNullOrWhiteSpace(rule.FaultSeverity) ? "Warning" : rule.FaultSeverity,
                SourcePath = rule.SourcePath,
                RawValue = EdgeDashboardJsonFieldResolver.ToCompactText(rawToken),
                StartTime = eventTime,
                LastSeenTime = eventTime,
                DurationSeconds = 0,
                Status = "Active",
                StartMessageId = messageId,
                RuleVersion = rule.RuleVersion,
                CreateTime = now,
                UpdateTime = now
            };
            await Db.Insertable(fault).ExecuteCommandAsync();
            await IncrementFaultCountAsync(binding, eventTime);
        }
    }

    /// <summary>
    /// 模型报警使用报警映射的 FaultCode 作为故障原因；机械手状态规则继续支持动态原因和固定原因。
    /// </summary>
    private static string ResolveFaultReason(
        DashboardMatchedRule rule,
        JObject envelope,
        string faultCode)
    {
        if (string.Equals(rule.RuleScope, ModelRuleScope, StringComparison.Ordinal))
            return faultCode;

        var reasonToken = string.IsNullOrWhiteSpace(rule.FaultReasonPath)
            ? null
            : EdgeDashboardJsonFieldResolver.Resolve(envelope, rule.FaultReasonPath);
        var reason = EdgeDashboardJsonFieldResolver.ToCompactText(reasonToken);
        if (string.IsNullOrWhiteSpace(reason)) reason = rule.FaultReason;
        return string.IsNullOrWhiteSpace(reason) ? rule.RuleName : reason;
    }

    /// <summary>把一段互斥状态时间按业务日边界拆分后累计，避免跨零点记错日期。</summary>
    private async Task AddDurationAsync(
        EdgeDashboardRobotBindingEntity binding,
        string state,
        DateTime start,
        DateTime end,
        ISqlSugarClient? db = null)
    {
        if (end <= start) return;
        db ??= Db;
        var cursor = start;
        while (cursor < end)
        {
            var statDate = GetBusinessDate(cursor, binding.BusinessDayStartHour);
            var nextBoundary = statDate.AddDays(1).AddHours(binding.BusinessDayStartHour);
            var segmentEnd = Min(end, nextBoundary);
            var seconds = Math.Max(
                0,
                (long)Math.Round((segmentEnd - cursor).TotalSeconds, MidpointRounding.AwayFromZero));
            if (seconds > 0)
            {
                var daily = await GetOrCreateDailyAsync(binding, statDate, db);
                AddSeconds(daily, state, seconds);
                daily.LastState = state;
                daily.RuleVersion = binding.RuleVersion;
                daily.UpdateTime = DateTime.Now;
                await SaveDailyAsync(daily, db);
            }
            cursor = segmentEnd;
        }
    }

    /// <summary>累计样本数，并记录该业务日最后一次规则计算出的状态。</summary>
    private async Task IncrementSampleCountAsync(
        EdgeDashboardRobotBindingEntity binding,
        DateTime sampleTime,
        string state)
    {
        var daily = await GetOrCreateDailyAsync(
            binding,
            GetBusinessDate(sampleTime, binding.BusinessDayStartHour));
        daily.SampleCount++;
        daily.LastState = state;
        daily.RuleVersion = binding.RuleVersion;
        daily.UpdateTime = DateTime.Now;
        await SaveDailyAsync(daily);
    }

    /// <summary>把一段设备组件状态按业务日边界切分累计，设备和机械手历史表保持独立。</summary>
    private async Task AddDeviceDurationAsync(
        EdgeDashboardDeviceBindingEntity binding,
        string state,
        DateTime start,
        DateTime end,
        ISqlSugarClient? db = null)
    {
        if (end <= start) return;
        db ??= Db;
        var cursor = start;
        while (cursor < end)
        {
            var statDate = GetBusinessDate(cursor, binding.BusinessDayStartHour);
            var nextBoundary = statDate.AddDays(1).AddHours(binding.BusinessDayStartHour);
            var segmentEnd = Min(end, nextBoundary);
            var seconds = Math.Max(
                0,
                (long)Math.Round((segmentEnd - cursor).TotalSeconds, MidpointRounding.AwayFromZero));
            if (seconds > 0)
            {
                var daily = await GetOrCreateDeviceDailyAsync(binding, statDate, db);
                AddSeconds(daily, state, seconds);
                daily.LastState = state;
                daily.UpdateTime = DateTime.Now;
                await SaveDeviceDailyAsync(daily, db);
            }
            cursor = segmentEnd;
        }
    }

    /// <summary>累计设备组件样本数，并写入当前业务日的最后状态。</summary>
    private async Task IncrementDeviceSampleCountAsync(
        EdgeDashboardDeviceBindingEntity binding,
        DateTime sampleTime,
        string state)
    {
        var daily = await GetOrCreateDeviceDailyAsync(
            binding,
            GetBusinessDate(sampleTime, binding.BusinessDayStartHour),
            Db);
        daily.SampleCount++;
        daily.LastState = state;
        daily.UpdateTime = DateTime.Now;
        await SaveDeviceDailyAsync(daily, Db);
    }

    /// <summary>设备进入故障状态时增加当前业务日故障次数。</summary>
    private async Task IncrementDeviceFaultCountAsync(
        EdgeDashboardDeviceBindingEntity binding,
        DateTime faultTime)
    {
        var daily = await GetOrCreateDeviceDailyAsync(
            binding,
            GetBusinessDate(faultTime, binding.BusinessDayStartHour),
            Db);
        daily.FaultCount++;
        daily.UpdateTime = DateTime.Now;
        await SaveDeviceDailyAsync(daily, Db);
    }

    /// <summary>每次新建故障生命周期时，把当日故障次数增加一次。</summary>
    private async Task IncrementFaultCountAsync(
        EdgeDashboardRobotBindingEntity binding,
        DateTime faultTime)
    {
        var daily = await GetOrCreateDailyAsync(
            binding,
            GetBusinessDate(faultTime, binding.BusinessDayStartHour));
        daily.FaultCount++;
        daily.UpdateTime = DateTime.Now;
        await SaveDailyAsync(daily);
    }

    /// <summary>读取业务日统计；不存在时构造待新增记录。</summary>
    private async Task<EdgeDashboardRobotDailyStatisticEntity> GetOrCreateDailyAsync(
        EdgeDashboardRobotBindingEntity binding,
        DateTime statDate,
        ISqlSugarClient? db = null)
    {
        db ??= Db;
        try
        {
            var daily = await db.Queryable<EdgeDashboardRobotDailyStatisticEntity>()
                .FirstAsync(x => x.RobotBindingId == binding.Id && x.StatDate == statDate);
            return daily ?? new EdgeDashboardRobotDailyStatisticEntity
            {
                RobotBindingId = binding.Id,
                StatDate = statDate,
                LineCode = binding.LineCode,
                RobotCode = binding.RobotCode,
                RobotName = binding.RobotName,
                RuleVersion = binding.RuleVersion,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "读取或创建机械手每日统计失败。BindingId: {BindingId}, LineCode: {LineCode}, RobotCode: {RobotCode}, StatDate: {StatDate:yyyy-MM-dd}",
                binding.Id,
                binding.LineCode,
                binding.RobotCode,
                statDate);
            throw;
        }
    }

    /// <summary>根据主键决定新增或更新每日统计。</summary>
    private async Task SaveDailyAsync(
        EdgeDashboardRobotDailyStatisticEntity daily,
        ISqlSugarClient? db = null)
    {
        db ??= Db;
        var operation = daily.Id == 0 ? "Insert" : "Update";
        try
        {
            if (daily.Id == 0)
                daily.Id = await db.Insertable(daily).ExecuteReturnBigIdentityAsync();
            else
                await db.Updateable(daily).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "保存机械手每日统计失败。Operation: {Operation}, Id: {Id}, BindingId: {BindingId}, LineCode: {LineCode}, RobotCode: {RobotCode}, StatDate: {StatDate:yyyy-MM-dd}, LastState: {LastState}, RunningSeconds: {RunningSeconds}, IdleSeconds: {IdleSeconds}, FaultSeconds: {FaultSeconds}, StoppedSeconds: {StoppedSeconds}, OfflineSeconds: {OfflineSeconds}, UnknownSeconds: {UnknownSeconds}, FaultCount: {FaultCount}, SampleCount: {SampleCount}",
                operation,
                daily.Id,
                daily.RobotBindingId,
                daily.LineCode,
                daily.RobotCode,
                daily.StatDate,
                daily.LastState,
                daily.RunningSeconds,
                daily.IdleSeconds,
                daily.FaultSeconds,
                daily.StoppedSeconds,
                daily.OfflineSeconds,
                daily.UnknownSeconds,
                daily.FaultCount,
                daily.SampleCount);
            throw;
        }
    }

    /// <summary>读取一台设备组件的业务日统计；不存在时构造待新增记录。</summary>
    private static async Task<EdgeDashboardEquipmentDailyStatisticEntity> GetOrCreateDeviceDailyAsync(
        EdgeDashboardDeviceBindingEntity binding,
        DateTime statDate,
        ISqlSugarClient db)
    {
        var daily = await db.Queryable<EdgeDashboardEquipmentDailyStatisticEntity>()
            .FirstAsync(x => x.ComponentType == "Device" &&
                             x.ComponentBindingId == binding.Id &&
                             x.StatDate == statDate);
        return daily ?? new EdgeDashboardEquipmentDailyStatisticEntity
        {
            ComponentType = "Device",
            ComponentBindingId = binding.Id,
            StatDate = statDate,
            LineCode = binding.LineCode,
            ComponentCode = binding.DeviceCode,
            ComponentName = binding.DeviceName,
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };
    }

    /// <summary>根据主键决定新增或更新设备组件日统计。</summary>
    private static async Task SaveDeviceDailyAsync(
        EdgeDashboardEquipmentDailyStatisticEntity daily,
        ISqlSugarClient db)
    {
        if (daily.Id == 0)
            daily.Id = await db.Insertable(daily).ExecuteReturnBigIdentityAsync();
        else
            await db.Updateable(daily).ExecuteCommandAsync();
    }

    /// <summary>关闭指定绑定的全部活动故障，供通信超时扫描调用。</summary>
    private async Task CloseActiveFaultsAsync(
        long bindingId,
        DateTime endTime,
        string? messageId,
        ISqlSugarClient? db = null)
    {
        db ??= Db;
        var activeFaults = await db.Queryable<EdgeDashboardRobotFaultRecordEntity>()
            .Where(x => x.RobotBindingId == bindingId && x.EndTime == null)
            .ToListAsync();
        var now = DateTime.Now;
        foreach (var fault in activeFaults)
            CloseFault(fault, endTime, messageId, now);
        if (activeFaults.Count > 0)
            await db.Updateable(activeFaults).ExecuteCommandAsync();
    }

    /// <summary>为一条活动故障补齐恢复时间和最终持续秒数。</summary>
    private static void CloseFault(
        EdgeDashboardRobotFaultRecordEntity fault,
        DateTime endTime,
        string? messageId,
        DateTime updateTime)
    {
        fault.EndTime = Max(fault.StartTime, endTime);
        fault.LastSeenTime = fault.EndTime.Value;
        fault.DurationSeconds = Math.Max(0, (long)(fault.EndTime.Value - fault.StartTime).TotalSeconds);
        fault.Status = "Cleared";
        fault.EndMessageId = messageId;
        fault.UpdateTime = updateTime;
    }

    /// <summary>按规则操作符比较 JSON 原值，数值优先按 decimal 比较。</summary>
    private static bool Matches(EdgeDashboardRobotStateRuleEntity rule, JToken? token)
    {
        return Matches(rule.Operator, rule.CompareValue, token);
    }

    /// <summary>按模型报警规则比较 JSON 原值，使用与机械手状态规则一致的操作符。</summary>
    private static bool Matches(EdgeDashboardModelAlarmRuleEntity rule, JToken? token)
    {
        return Matches(rule.Operator, rule.CompareValue, token);
    }

    private static bool Matches(string @operator, string? compareValue, JToken? token)
    {
        var raw = EdgeDashboardJsonFieldResolver.ToObject(token);
        var rawText = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var compareText = compareValue?.Trim() ?? string.Empty;
        return @operator switch
        {
            "IsEmpty" => string.IsNullOrEmpty(rawText),
            "IsNotEmpty" => !string.IsNullOrEmpty(rawText),
            "Contains" => rawText.Contains(compareText, StringComparison.OrdinalIgnoreCase),
            "NotContains" => !rawText.Contains(compareText, StringComparison.OrdinalIgnoreCase),
            "Equals" => Compare(rawText, compareText) == 0,
            "NotEquals" => Compare(rawText, compareText) != 0,
            "GreaterThan" => Compare(rawText, compareText) > 0,
            "GreaterThanOrEqual" => Compare(rawText, compareText) >= 0,
            "LessThan" => Compare(rawText, compareText) < 0,
            "LessThanOrEqual" => Compare(rawText, compareText) <= 0,
            _ => false
        };
    }

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

    /// <summary>按模型配置裁决状态冲突；未配置时维持故障优先、空闲次之的默认行为。</summary>
    private static int StatePriority(
        DashboardMatchedRule rule,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities) =>
        ResolveStatePriority(rule.TargetState, modelStatePriorities);

    private static string ResolveHighestPriorityState(
        IEnumerable<string> candidates,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities) =>
        candidates
            .OrderBy(state => ResolveStatePriority(state, modelStatePriorities))
            .ThenBy(state => state, StringComparer.Ordinal)
            .FirstOrDefault() ?? EdgeDashboardConstants.Unknown;

    private static int ResolveStatePriority(
        string state,
        IReadOnlyCollection<EdgeDashboardModelStatePriorityEntity> modelStatePriorities) =>
        modelStatePriorities.FirstOrDefault(x =>
            string.Equals(x.State, state, StringComparison.OrdinalIgnoreCase))?.Priority ??
        state switch
        {
            EdgeDashboardConstants.Fault => 10,
            EdgeDashboardConstants.Idle => 20,
            EdgeDashboardConstants.Stopped => 30,
            EdgeDashboardConstants.Running => 40,
            _ => 90
        };

    /// <summary>把机械手自己的状态规则包装为统一规则，便于与模型报警规则合并处理。</summary>
    private static DashboardMatchedRule ToMatchedRule(
        EdgeDashboardRobotStateRuleEntity rule,
        int ruleVersion) =>
        new()
        {
            Id = rule.Id,
            RuleScope = BindingRuleScope,
            RuleCode = rule.RuleCode,
            RuleName = rule.RuleName,
            TargetState = rule.TargetState,
            SourcePath = rule.SourcePath,
            FaultCode = rule.FaultCode,
            FaultReason = rule.FaultReason,
            FaultReasonPath = rule.FaultReasonPath,
            FaultSeverity = rule.FaultSeverity,
            Priority = rule.Priority,
            RuleVersion = ruleVersion
        };

    /// <summary>把模型报警规则包装为故障状态规则，使模型下设备共用同一套报警映射。</summary>
    private static DashboardMatchedRule ToMatchedRule(EdgeDashboardModelAlarmRuleEntity rule) =>
        new()
        {
            Id = rule.Id,
            RuleScope = ModelRuleScope,
            RuleCode = rule.RuleCode,
            RuleName = rule.RuleName,
            TargetState = EdgeDashboardConstants.Fault,
            SourcePath = rule.SourcePath,
            FaultCode = rule.FaultCode,
            FaultReason = rule.FaultReason,
            FaultReasonPath = rule.FaultReasonPath,
            FaultSeverity = rule.FaultSeverity,
            Priority = rule.Priority,
            RuleVersion = rule.RuleVersion
        };

    private static string CreateRuleKey(string ruleScope, long ruleId) =>
        $"{NormalizeRuleScope(ruleScope)}:{ruleId}";

    private static string NormalizeRuleScope(string? ruleScope) =>
        string.IsNullOrWhiteSpace(ruleScope) ? BindingRuleScope : ruleScope.Trim();

    private static string NormalizeModelMark(string? modelMark) =>
        string.IsNullOrWhiteSpace(modelMark) ? UnclassifiedModelMark : modelMark.Trim();

    /// <summary>把字段样例值压缩成 JSON 保存，避免动态类型入库时丢失数字、布尔等原始类型。</summary>
    private static string? SerializeFieldCurrentValue(object? value) =>
        value == null ? null : JsonConvert.SerializeObject(value, Formatting.None);

    /// <summary>数值、布尔值和字符串依次比较，兼容设备常见的 0/1 与文本状态。</summary>
    private static int Compare(string left, string right)
    {
        if (decimal.TryParse(left, NumberStyles.Any, CultureInfo.InvariantCulture, out var leftNumber) &&
            decimal.TryParse(right, NumberStyles.Any, CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);
        if (bool.TryParse(left, out var leftBoolean) && bool.TryParse(right, out var rightBoolean))
            return leftBoolean.CompareTo(rightBoolean);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DashboardMatchedRule
    {
        public long Id { get; init; }
        public string RuleScope { get; init; } = BindingRuleScope;
        public string RuleCode { get; init; } = string.Empty;
        public string RuleName { get; init; } = string.Empty;
        public string TargetState { get; init; } = EdgeDashboardConstants.Unknown;
        public string SourcePath { get; init; } = string.Empty;
        public string? FaultCode { get; init; }
        public string? FaultReason { get; init; }
        public string? FaultReasonPath { get; init; }
        public string? FaultSeverity { get; init; }
        public int Priority { get; init; }
        public int RuleVersion { get; init; }
    }

    /// <summary>把时间转换成配置起始小时对应的业务日期。</summary>
    private static DateTime GetBusinessDate(DateTime value, int startHour) =>
        value.AddHours(-startHour).Date;

    /// <summary>把秒数加到目标状态列，未知状态也有独立兜底列。</summary>
    private static void AddSeconds(
        EdgeDashboardRobotDailyStatisticEntity daily,
        string state,
        long seconds)
    {
        switch (state)
        {
            case EdgeDashboardConstants.Running:
                daily.RunningSeconds += seconds;
                break;
            case EdgeDashboardConstants.Idle:
                daily.IdleSeconds += seconds;
                break;
            case EdgeDashboardConstants.Fault:
                daily.FaultSeconds += seconds;
                break;
            case EdgeDashboardConstants.Stopped:
                daily.StoppedSeconds += seconds;
                break;
            case EdgeDashboardConstants.Offline:
                daily.OfflineSeconds += seconds;
                break;
            default:
                daily.UnknownSeconds += seconds;
                break;
        }
    }

    /// <summary>把秒数加到设备组件日统计的目标状态列。</summary>
    private static void AddSeconds(
        EdgeDashboardEquipmentDailyStatisticEntity daily,
        string state,
        long seconds)
    {
        switch (state)
        {
            case EdgeDashboardConstants.Running:
                daily.RunningSeconds += seconds;
                break;
            case EdgeDashboardConstants.Idle:
                daily.IdleSeconds += seconds;
                break;
            case EdgeDashboardConstants.Fault:
                daily.FaultSeconds += seconds;
                break;
            case EdgeDashboardConstants.Stopped:
                daily.StoppedSeconds += seconds;
                break;
            case EdgeDashboardConstants.Offline:
                daily.OfflineSeconds += seconds;
                break;
            default:
                daily.UnknownSeconds += seconds;
                break;
        }
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;
}
