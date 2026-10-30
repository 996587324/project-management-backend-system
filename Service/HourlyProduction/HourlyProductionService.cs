using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Interface;
using Model.HourlyProduction;
using Model.SystemApplications;
using SqlSugar;

namespace Service;

public sealed partial class HourlyProductionService(
    SqlSugarScope scope,
    HourlyProductionCatalog catalog,
    ISystemApplicationService applications,
    IEmailPushService email,
    IWeChatPushQueueService wechat,
    IAgentModelClient model) : IHourlyProductionService
{
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    private void Ready()
    {
        if (!catalog.Ready) throw new InvalidOperationException("小时产量模块正在后台初始化，请稍后刷新；其他模块不受影响");
    }

    private static void Require(ApplicationAccessContext access, bool write = false)
    {
        if (access.AppCode is not ("A" or "B") ||
            !(write ? access.HasPermission("HourlyProductionConfig") :
                access.HasPermission("HourlyProduction") || access.HasPermission("HourlyProductionConfig")))
            throw new UnauthorizedAccessException("没有当前系统的小时产量看板权限");
    }

    private static bool CanRead(ApplicationAccessContext access, HourlyBoardDefinition board) =>
        access.AllResources || board.Lines.All(line => access.LineCodes.Contains(line.Code) &&
            line.Processes.SelectMany(process => process.Bindings).All(binding =>
                access.SourceKeys.Contains(binding.SourceKey + "\u001f" + binding.DeviceKey) &&
                (binding.Kind != "Edge" || access.DeviceCodes.Contains(binding.DeviceKey))));

    public async Task<List<HourlyBoardDefinition>> ListAsync(ApplicationAccessContext access)
    {
        Ready();
        Require(access);
        using var operation = DatabaseOperationScope.Begin(scope);
        var rows = await Db.Queryable<HourlyProductionBoardEntity>().Where(x => x.AppCode == access.AppCode).ToListAsync();
        return rows.Select(Definition).Where(board => CanRead(access, board)).ToList();
    }

    public async Task<HourlyBoardDefinition> SaveAsync(ApplicationAccessContext access, HourlyBoardDefinition definition)
    {
        Ready();
        Require(access, write: true);
        Validate(definition);
        if (!CanRead(access, definition)) throw new UnauthorizedAccessException("配置包含未授权的线体或数据源");
        if (definition.Push.Enabled &&
            (definition.Push.WeChatUserIds.Count > 0 && !access.HasPermission("WeChatClawBot") ||
             definition.Push.EmailUserIds.Count > 0 && !access.HasPermission("EmailPush")))
            throw new UnauthorizedAccessException("启用推送需要对应微信或邮件推送权限");
        using var operation = DatabaseOperationScope.Begin(scope);
        var saved = await Db.Ado.UseTranAsync(async () =>
        {
            await HourlyProcessLifecycle.LockAsync(Db, "hourly-lifecycle-schema", "Shared");
            await HourlyProcessLifecycle.LockAsync(Db, "hourly-config:" + definition.Code);
            var old = await Db.Queryable<HourlyProductionBoardEntity>().InSingleAsync(definition.Code);
            if (old != null)
            {
                if (old.AppCode != access.AppCode || !CanRead(access, Definition(old)))
                    throw new UnauthorizedAccessException("不可修改其他系统或未授权的看板");
                if (old.Version != definition.Version) throw new InvalidOperationException("配置已被修改，请刷新后重试");
                await ProtectHistoryAsync(Definition(old), definition);
            }
            else if (definition.Version != 0) throw new InvalidOperationException("看板不存在，请重新创建");
            definition.RemovedLines = old == null ? [] : Definition(old).RemovedLines;
            if (definition.Lines.Any(line => definition.RemovedLines.Any(removed => string.Equals(removed.Definition.Code, line.Code, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("该线体编码已移除并保留历史，请使用新的线体编码");
            if (old != null)
                foreach (var removed in Definition(old).Lines.Where(line => !definition.Lines.Any(next => string.Equals(next.Code, line.Code, StringComparison.OrdinalIgnoreCase))))
                    definition.RemovedLines.Add(new HourlyRemovedLine { Definition=removed, RemovedAt=HourlyProductionRules.Now, RemovedBy=access.UserId });
            definition.ShiftHistory = old == null ? [] : Definition(old).ShiftHistory;
            definition.CancelledShifts = old == null ? [] : Definition(old).CancelledShifts;
            if (definition.CancelShiftEffectiveAt is { } cancelAt)
            {
                if (definition.ShiftChange != null) throw new ArgumentException("请先完成版本撤销，再新增班次版本");
                var cancelledAt = HourlyProductionRules.Now;
                var cancelled = HourlyProductionRules.CancelPendingShift(definition, cancelAt, cancelledAt);
                definition.CancelledShifts.Add(new HourlyShiftCancellation { Plan=cancelled, CancelledAt=cancelledAt, CancelledBy=access.UserId });
                definition.CancelShiftEffectiveAt = null;
            }
            definition.LegacyTargets = old == null ? null : Definition(old).LegacyTargets;
            if(definition.ShiftChange!=null && definition.LegacyTargets==null)
            {
                var baseline=old==null?definition:Definition(old);
                definition.LegacyTargets=new();
                foreach(var l in baseline.Lines) {definition.LegacyTargets[l.Code]=l.Targets.ToList();foreach(var p in l.Processes)definition.LegacyTargets[l.Code+"/"+p.Code]=p.Targets.ToList();}
            }
            if (definition.ShiftChange is { } change)
            {
                HourlyShiftRules.Validate(change.DayStart,change.NightStart);
                if(change.EffectiveAt < HourlyProductionRules.Now) throw new ArgumentException("新班次必须从未来时间生效");
                if(definition.ShiftHistory.Any(x=>x.EffectiveAt>=change.EffectiveAt)) throw new ArgumentException("生效时间必须晚于已保存的班次版本");
                var nextWindow=HourlyShiftRules.At(change.DayStart,change.NightStart,change.EffectiveAt);
                if(nextWindow.Start!=change.EffectiveAt) throw new ArgumentException("生效时间必须是新白班或夜班的开始时间");
                var priorWindow=HourlyProductionRules.Window(definition,change.EffectiveAt.AddTicks(-1));
                if(old!=null && priorWindow.End!=change.EffectiveAt) throw new ArgumentException("生效时间必须与原班次结束时间衔接，避免截断正在生产的班次");
                foreach(var values in new[]{change.DayTargets,change.NightTargets})
                foreach(var pair in values)
                {
                    var isDay=ReferenceEquals(values,change.DayTargets);
                    var start=isDay?change.DayStart:change.NightStart; var end=isDay?change.NightStart:change.DayStart;
                    var duration=(TimeSpan.Parse(end)-TimeSpan.Parse(start)).TotalHours; if(duration<=0)duration+=24;
                    if(pair.Value==null || pair.Value.Count>Math.Ceiling(duration) || pair.Value.Any(x=>x is <0 or >1000000000)) throw new ArgumentException("班次目标数量或数值无效");
                }
                definition.ShiftHistory.Add(change); definition.ShiftChange=null;
            }
            await BindInstancesAsync(old == null ? null : Definition(old), definition, HourlyProductionRules.Now);
            var expectedVersion = definition.Version;
            definition.Version++;
            var row = new HourlyProductionBoardEntity
            {
                Code = definition.Code, AppCode = access.AppCode, DefinitionJson = JsonSerializer.Serialize(definition),
                Version = definition.Version, Published = definition.Published, UpdatedBy = access.UserId,
                UpdateTime = HourlyProductionRules.Now
            };
            if (old == null) await Db.Insertable(row).ExecuteCommandAsync();
            else if (await Db.Updateable(row).Where(x => x.Code == row.Code && x.Version == expectedVersion).ExecuteCommandAsync() == 0)
                throw new InvalidOperationException("配置已被修改，请刷新后重试");
        });
        if (!saved.IsSuccess) throw saved.ErrorException!;
        catalog.Boards = (await Db.Queryable<HourlyProductionBoardEntity>().ToListAsync()).Select(Definition).ToArray();
        return definition;
    }

    private async Task ProtectHistoryAsync(HourlyBoardDefinition old, HourlyBoardDefinition next)
    {
        var hasEvents = await Db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.BoardCode == old.Code) ||
            await Db.Queryable<HourlyProductionBucketEntity>().AnyAsync(x => x.BoardCode == old.Code);
        if (!hasEvents) return;
        if (old.StartTime != next.StartTime || old.EndTime != next.EndTime)
            throw new InvalidOperationException("已有过站记录的看板不能改班次边界，请复制为新编码看板，避免改写历史归属");
        foreach (var line in next.Lines)
        foreach (var process in line.Processes)
        {
            var prior = old.Lines.FirstOrDefault(x => x.Code == line.Code)?.Processes.FirstOrDefault(x => x.Code == process.Code);
            if (prior == null || process.CreationMode == "New" || process.CreationMode == "Restore") continue;
            var key = ProcessKey(line, prior);
            if (StatisticalRule(prior) != StatisticalRule(process) &&
                (await Db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.BoardCode == old.Code && x.ProcessKey == key) ||
                 await Db.Queryable<HourlyProductionBucketEntity>().AnyAsync(x => x.BoardCode == old.Code && x.ProcessKey == key)))
                throw new InvalidOperationException($"{line.Name}/{process.Name} 已有统计记录；修改统计口径请移除后以新工序添加，可沿用原编码并单独统计");
        }
    }

    private static string StatisticalRule(HourlyProcessDefinition process) => JsonSerializer.Serialize(new
    {
        process.Mode, process.ReworkMinutes, process.ReworkRequiresDifferentHour, process.IncludeReworkInOutput, process.Bindings
    });

    private static void Validate(HourlyBoardDefinition board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.Lines == null || board.Push == null || board.Lines.Any(x => x == null || x.Processes == null) ||
            board.Push.LineCodes == null || board.Push.WeChatUserIds == null || board.Push.EmailUserIds == null)
            throw new ArgumentException("线体、工序及推送配置集合不能为null");
        static void Code(string code, int length = 30)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > length || !Regex.IsMatch(code, "^[A-Za-z0-9_-]+$"))
                throw new ArgumentException($"编码需为1至{length}位字母、数字、下划线或短横线");
        }
        static void Name(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("名称需为1至100个字符");
        }
        Code(board.Code, 64);
        Name(board.Name);
        if (!TimeSpan.TryParseExact(board.StartTime, @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParseExact(board.EndTime, @"hh\:mm", CultureInfo.InvariantCulture, out var end) ||
            start.TotalHours >= 24 || end.TotalHours >= 24)
            throw new ArgumentException("班次时间必须为HH:mm，结束小于等于开始表示跨夜班");
        if (board.RefreshSeconds is < 5 or > 300 || board.AiIntervalMinutes is < 1 or > 120)
            throw new ArgumentException("刷新间隔需为5至300秒，AI间隔需为1至120分钟");
        // 复用已有生产看板模型配置，不允许配置任意 Agent 来绕过业务边界。
        board.AiAgentCode = "production-dashboard";
        if (board.Lines.Count is < 1 or > 20 || board.Lines.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != board.Lines.Count)
            throw new ArgumentException("一个看板支持1至20条不同编码的线体");
        if (board.Layout is { } layout)
        {
            if (layout.Mode is not ("Single" or "Compare" or "Rotate") || layout.RotationSeconds is < 5 or > 300 || layout.Groups == null || layout.Groups.Count is < 1 or > 20)
                throw new ArgumentException("布局模式、轮播间隔或对比组数量无效");
            if (layout.Mode != "Rotate" && layout.Groups.Count != 1) throw new ArgumentException("单线与双线布局仅允许一组");
            foreach (var group in layout.Groups)
                if (group == null || group.Count != (layout.Mode == "Single" ? 1 : 2) || group.Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count || group.Any(code => !board.Lines.Any(line => line.Code == code)))
                    throw new ArgumentException("请选择存在且不重复的对比线体");
        }
        var slots = HourlyProductionRules.Slots(board, HourlyProductionRules.Now).Count;
        foreach (var line in board.Lines)
        {
            Code(line.Code);
            Name(line.Name);
            if (line.Targets == null || line.Targets.Count > slots || line.Targets.Any(x => x is < 0 or > 1000000000))
                throw new ArgumentException("线体小时目标无效");
            if (line.Processes.Any(x => x == null || x.Bindings == null || x.Targets == null))
                throw new ArgumentException("工序、绑定与目标集合不能为null");
            if (line.Processes.Count is 0 or > 30 || line.Processes.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != line.Processes.Count ||
                line.Processes.Select(x => x.RowCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != line.Processes.Count)
                throw new ArgumentException("每条线配置1至30个工序，工序编码及对齐行编码不能重复");
            foreach (var process in line.Processes)
            {
                Code(process.Code);
                Code(process.RowCode);
                Name(process.Name);
                if (process.Mode is not ("DistinctSn" or "Count" or "Sum") || process.ReworkMinutes is < 0 or > 1440)
                    throw new ArgumentException("统计模式或重复抑制分钟数无效");
                if (process.Targets.Count > slots || process.Targets.Any(x => x is < 0 or > 1000000000))
                    throw new ArgumentException("目标不能小于0或超过十亿，数量不能超过班次小时数");
                if (process.Bindings.Any(x => x == null) || process.Bindings.Count > 20 ||
                    process.Bindings.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != process.Bindings.Count)
                    throw new ArgumentException("工序最多绑定20个数据源，绑定编码不能重复");
                foreach (var binding in process.Bindings)
                {
                    if (binding == null) throw new ArgumentException("绑定不能为null");
                    binding.SourceKey ??= "";
                    binding.DeviceKey ??= "";
                    binding.SourceDeviceCode ??= "";
                    binding.SnPath ??= "";
                    binding.TimePath ??= "";
                    binding.QuantityPath ??= "";
                    binding.FilterPath ??= "";
                    binding.FilterValue ??= "";
                    binding.ReworkPath ??= "";
                    binding.ReworkValue ??= "";
                    Code(binding.Code, 64);
                    if (binding.Kind is not ("Realtime" or "Edge") || string.IsNullOrWhiteSpace(binding.DeviceKey) ||
                        binding.SourceKey.Length > 128 || binding.DeviceKey.Length > 128 || binding.SourceDeviceCode.Length > 128 ||
                        process.Mode == "DistinctSn" && string.IsNullOrWhiteSpace(binding.SnPath) ||
                        process.Mode == "Sum" && string.IsNullOrWhiteSpace(binding.QuantityPath))
                        throw new ArgumentException($"线体“{line.Name}”工序“{process.Name}”数据绑定 {process.Bindings.IndexOf(binding) + 1}：请完整配置接入类型、设备及SN/求和字段；不需要此绑定时请移除绑定");
                    if (new[] { binding.SnPath, binding.TimePath, binding.QuantityPath, binding.FilterPath,
                        binding.ReworkPath, binding.FilterValue, binding.ReworkValue }.Any(x => x.Length > 256))
                        throw new ArgumentException("字段路径及条件值最多256字符");
                    foreach (var path in new[] { binding.SnPath, binding.TimePath, binding.QuantityPath, binding.FilterPath, binding.ReworkPath })
                        Token(new Newtonsoft.Json.Linq.JObject(), path);
                }
            }
            if (!line.Processes.Any(x => x.Code == line.SummaryProcessCode && x.Enabled))
                throw new ArgumentException("请选择启用的末工序作为线体总产出口径");
        }
        if (board.Push.DelayMinutes is < 0 or > 60 || board.Push.CatchUpHours is < 1 or > 24 ||
            board.Push.WeChatUserIds.Count + board.Push.EmailUserIds.Count > 100 ||
            board.Push.WeChatUserIds.Concat(board.Push.EmailUserIds).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) ||
            board.Push.LineCodes.Any(code => !board.Lines.Any(line => line.Code == code)))
            throw new ArgumentException("推送配置无效：缓冲0至60分钟，补发1至24小时，接收人合计最多100位");
    }

    private static HourlyBoardDefinition Definition(HourlyProductionBoardEntity row) =>
        JsonSerializer.Deserialize<HourlyBoardDefinition>(row.DefinitionJson) ?? throw new InvalidOperationException("看板配置损坏");

    private static string ProcessKey(HourlyLineDefinition line, HourlyProcessDefinition process) => HourlyProcessLifecycle.Key(line, process);

    private async Task<(HourlyProductionBoardEntity Row, HourlyBoardDefinition Definition)> GetAsync(
        ApplicationAccessContext? access, string code, bool publicRead)
    {
        Ready();
        var row = await Db.Queryable<HourlyProductionBoardEntity>().InSingleAsync(code)
            ?? throw new ArgumentException("看板不存在");
        var definition = Definition(row);
        if (publicRead)
        {
            if (!row.Published) throw new UnauthorizedAccessException("该看板尚未公开发布");
        }
        else
        {
            if (access == null) throw new UnauthorizedAccessException("需要登录");
            Require(access);
            if (row.AppCode != access.AppCode || !CanRead(access, definition))
                throw new UnauthorizedAccessException("没有当前看板的数据范围权限");
        }
        return (row, definition);
    }

    public async Task<HourlyBoardRuntime> RuntimeAsync(ApplicationAccessContext? access, string code, bool publicRead = false, bool previous = false)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var (_, definition) = await GetAsync(access, code, publicRead);
        var now=HourlyProductionRules.Now;
        var time=previous ? HourlyProductionRules.Window(definition,now).Start.AddTicks(-1) : now;
        return await BuildRuntimeAsync(definition, now,time);
    }

    private async Task<HourlyBoardRuntime> BuildRuntimeAsync(HourlyBoardDefinition board, DateTime now, DateTime? windowTime = null)
    {
        var slots = HourlyProductionRules.Slots(board, windowTime ?? now);
        var selectedTime=windowTime ?? now;
        var plan=HourlyProductionRules.Plan(board,selectedTime);
        var start = slots[0].Start;
        var end = slots[^1].End;
        var buckets = await Db.Queryable<HourlyProductionBucketEntity>()
            .Where(x => x.BoardCode == board.Code && x.SlotStart >= start && x.SlotStart < end).ToListAsync();
        var activeKeys = board.Lines.SelectMany(line => line.Processes.Where(process => process.Enabled).Select(process => ProcessKey(line, process))).ToHashSet(StringComparer.Ordinal);
        buckets = buckets.Where(x => activeKeys.Contains(x.ProcessKey)).ToList();
        var advice = await Db.Queryable<HourlyProductionAdviceEntity>().InSingleAsync(board.Code);
        var runtime = new HourlyBoardRuntime
        {
            Code = board.Code, Name = board.Name, Version = board.Version, RefreshSeconds = board.RefreshSeconds, Layout=board.Layout,
            ProductionDate = start.Date, ServerTime = now, ShiftStart=start, ShiftEnd=end,
            ShiftName=plan==null?"原班次":HourlyShiftRules.At(plan.DayStart,plan.NightStart,selectedTime).Code=="Day"?"白班":"夜班",
            DataUpdatedAt = buckets.Count > 0 ? buckets.Max(x => x.UpdatedAt) : null,
            Advice = board.AiEnabled && advice?.ExpiresAt > now ? advice.Content : null
        };
        foreach (var line in board.Lines)
        {
            var result = new HourlyLineRuntime { Code = line.Code, Name = line.Name };
            foreach (var process in line.Processes.Where(x => x.Enabled))
            {
                var item = new HourlyProcessRuntime
                {
                    Code = process.Code, Name = process.Name, RowCode = process.RowCode, Configured = process.Bindings.Count > 0
                };
                var key = ProcessKey(line, process);
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    var bucket = buckets.FirstOrDefault(x => x.ProcessKey == key && x.SlotStart == slot.Start);
                    var target = HourlyProductionRules.Targets(board,selectedTime,line.Code+"/"+process.Code,process.Targets).ElementAtOrDefault(i);
                    if (target is <= 0) target = null;
                    var output = item.Configured && now >= slot.Start ? bucket?.Output ?? 0 : (decimal?)null;
                    item.Points.Add(new HourlyPoint
                    {
                        Start = slot.Start, End = slot.End, Output = output,
                        Rework = output.HasValue ? bucket?.Rework ?? 0 : null, Target = target,
                        Achievement = output.HasValue && target > 0 ? decimal.Round(output.Value / target.Value * 100, 1) : null,
                        Status = !item.Configured ? "Unbound" : now < slot.Start ? "Pending" :
                            now < slot.End ? "Running" : !target.HasValue ? "NoTarget" : output >= target ? "Achieved" : "BelowTarget"
                    });
                }
                result.Processes.Add(item);
            }
            var summary = result.Processes.FirstOrDefault(x => x.Code == line.SummaryProcessCode);
            result.SummaryProcessName = summary?.Name;
            result.SummaryPoints = summary?.Points.Select((point, index) =>
            {
                var lineTargets=HourlyProductionRules.Targets(board,selectedTime,line.Code,line.Targets);
                var target = lineTargets.Count > 0 ? lineTargets.ElementAtOrDefault(index) : point.Target;
                if (target is <= 0) target = null;
                return new HourlyPoint
                {
                    Start = point.Start, End = point.End, Output = point.Output, Rework = point.Rework, Target = target,
                    Achievement = point.Output.HasValue && target > 0 ? decimal.Round(point.Output.Value / target.Value * 100, 1) : null,
                    Status = point.Status is "Pending" or "Running" or "Unbound" ? point.Status :
                        !target.HasValue ? "NoTarget" : point.Output >= target ? "Achieved" : "BelowTarget"
                };
            }).ToList() ?? [];
            result.TotalOutput = result.SummaryPoints.Sum(x => x.Output ?? 0);
            result.LatestCompleted = result.SummaryPoints.LastOrDefault(x => x.End <= now);
            var latestStart = slots.LastOrDefault(x => x.End <= now).Start;
            result.AchievedProcesses = result.Processes.Count(x => x.Points.Any(p => p.Start == latestStart && p.Status == "Achieved"));
            result.TargetedProcesses = result.Processes.Count(x => x.Points.Any(p => p.Start == latestStart && p.Target.HasValue && x.Configured));
            runtime.Lines.Add(result);
        }
        return runtime;
    }

    public async Task<object> ReportsAsync(ApplicationAccessContext access, string code, int page)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await GetAsync(access, code, false);
        return await Db.Queryable<HourlyProductionReportEntity>().Where(x => x.BoardCode == code)
            .OrderByDescending(x => x.Id).ToPageListAsync(Math.Clamp(page, 1, 10000), 20);
    }

    public async Task<object> DispatchesAsync(ApplicationAccessContext access, string code, int page)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        Require(access, true);
        await GetAsync(access, code, false);
        return await Db.Queryable<HourlyProductionDispatchEntity>().Where(x => x.BoardCode == code)
            .OrderByDescending(x => x.Id).ToPageListAsync(Math.Clamp(page, 1, 10000), 20);
    }

    public async Task RetryDispatchAsync(ApplicationAccessContext access, long id, bool confirm)
    {
        Ready();
        Require(access, true);
        if (!confirm) throw new ArgumentException("重试需要明确确认");
        using var operation = DatabaseOperationScope.Begin(scope);
        var job = await Db.Queryable<HourlyProductionDispatchEntity>().InSingleAsync(id)
            ?? throw new ArgumentException("任务不存在");
        await GetAsync(access, job.BoardCode, false);
        if (await Db.Updateable<HourlyProductionDispatchEntity>()
                .SetColumns(x => new HourlyProductionDispatchEntity { Status = "Pending", Attempts = 0, NextAttemptAt = null, Error = null })
                .Where(x => x.Id == id && x.Status == "Failed").ExecuteCommandAsync() == 0)
            throw new InvalidOperationException("只能重试明确入队失败的任务；已入队请到推送中心核对投递结果");
    }

    public async Task<object> PublishedBoardsAsync()
    {
        Ready();
        using var operation = DatabaseOperationScope.Begin(scope);
        var rows = await Db.Queryable<HourlyProductionBoardEntity>().Where(x => x.Published).ToListAsync();
        return rows.Select(Definition).Select(x => new { x.Code, x.Name, x.StartTime, x.EndTime,
            lines = x.Lines.Select(line => new { line.Code, line.Name }) }).ToList();
    }

    public async Task<object> PublishedReportsAsync(string code, int page)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await GetAsync(null, code, true);
        return await Db.Queryable<HourlyProductionReportEntity>().Where(x => x.BoardCode == code)
            .OrderByDescending(x => x.Id).ToPageListAsync(Math.Clamp(page, 1, 10000), 20);
    }
}
