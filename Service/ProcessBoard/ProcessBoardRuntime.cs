using System.Globalization;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Service;

public sealed partial class ProcessBoardService
{
    public async Task<object> GetRuntimeAsync(ApplicationAccessContext access, long lineId)
    {
        if (!CanReadLine(access, lineId)) throw new UnauthorizedAccessException("没有该工艺线体授权");
        using var operation = DatabaseOperationScope.Begin(scope);
        var definitions = await GetDefinitionsAsync(access);
        var line = definitions.FirstOrDefault(x => x.Kind == "Line" && x.Id == lineId && x.Enabled)
            ?? throw new ArgumentException("工艺线体不存在或已停用");
        if (!definitions.Any(x => x.Kind == "Region" && x.Id == line.ParentId && x.Enabled))
            throw new ArgumentException("所属区域已停用");
        var cards = definitions.Where(x => x.Kind == "Card" && x.ParentId == lineId && x.Enabled).ToArray();
        var ids = cards.Select(x => x.Id).ToArray();
        var states = ids.Length == 0 ? [] : await Db.Queryable<ProcessBoardValueEntity>()
            .Where(x => ids.Contains(x.CardId)).ToListAsync();
        return new
        {
            line,
            cards = cards.Select(card => new
            {
                definition = card,
                fields = card.Fields.Select(field =>
                {
                    try
                    {
                        if (field.FixedText == null) RequireSource(access, field.Source);
                        foreach (var source in field.Variables.Values) RequireSource(access, source);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return new ProcessFieldRuntime { Code = field.Code, Error = "该字段的数据源未授权" };
                    }
                    var state = states.FirstOrDefault(x => x.CardId == card.Id && x.FieldCode == field.Code && x.Version == card.Version);
                    return state == null
                        ? new ProcessFieldRuntime { Code = field.Code, Error = "等待后台采样" }
                        : JsonConvert.DeserializeObject<ProcessFieldRuntime>(state.RuntimeJson)!;
                }).ToArray()
            }).ToArray(),
            generatedAt = DateTime.Now
        };
    }

    public async Task CaptureAsync(CancellationToken cancellationToken)
    {
        if (!await CaptureGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var definitions = (await Db.Queryable<ProcessBoardConfigEntity>().Where(x => x.Enabled).ToListAsync()).Select(Read).ToList();
            var regions = definitions.Where(x => x.Kind == "Region").Select(x => x.Id).ToHashSet();
            var lines = definitions.Where(x => x.Kind == "Line" && regions.Contains(x.ParentId)).ToDictionary(x => x.Id);
            foreach (var line in lines.Values.Where(x => x.Fields.Count > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CaptureCardAsync(line, line, cancellationToken);
            }
            foreach (var card in definitions.Where(x => x.Kind == "Card" && lines.ContainsKey(x.ParentId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CaptureCardAsync(card, lines[card.ParentId], cancellationToken);
            }
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    private async Task CaptureCardAsync(ProcessBoardDefinition card, ProcessBoardDefinition line, CancellationToken cancellationToken)
    {
        var sources = card.Fields.Select(x => x.Source).Concat(card.Fields.SelectMany(x => x.Variables.Values))
            .Where(x => !x.FixedValue.HasValue && !string.IsNullOrEmpty(x.DeviceMark)).ToArray();
        var tenants = sources.Select(x => x.TenantMark).Distinct().ToArray();
        var devices = sources.Select(x => x.DeviceMark).Distinct().ToArray();
        var snapshots = devices.Length == 0 ? [] : await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Where(x => tenants.Contains(x.TenantMark!) && devices.Contains(x.DeviceCode)).ToListAsync();
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, snapshots);
        var latest = devices.Length == 0 ? [] : await Db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>()
            .Where(x => tenants.Contains(x.TenantMark) && devices.Contains(x.DeviceCode)).ToListAsync();
        var previous = await Db.Queryable<ProcessBoardValueEntity>().Where(x => x.CardId == card.Id).ToListAsync();
        foreach (var field in card.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = previous.FirstOrDefault(x => x.FieldCode == field.Code);
            var runtime = new ProcessFieldRuntime
            {
                Code = field.Code, CalculatedAt = DateTime.Now,
                FieldIdentity = JsonConvert.SerializeObject(field)
            };
            var times = new Dictionary<string, DateTime?>();
            JToken? ReadSource(ProcessSourceBinding binding, string variable)
            {
                if (binding.FixedValue.HasValue) return new JValue(binding.FixedValue.Value);
                var sample = snapshots.FirstOrDefault(x => x.TenantMark == binding.TenantMark && x.DeviceCode == binding.DeviceMark);
                if (sample == null) return null;
                var persisted = latest.FirstOrDefault(x => x.TenantMark == binding.TenantMark &&
                    x.DeviceCode == binding.DeviceMark && x.SourcePath == binding.SourcePath);
                times[variable] = persisted?.DataTime ?? sample.DataTime;
                return EdgeDashboardJsonFieldResolver.ResolveIncludingNull(EdgeDashboardJsonFieldResolver.BuildEnvelope(sample), binding.SourcePath);
            }
            var raw = field.FixedText != null ? new JValue(field.FixedText) : ReadSource(field.Source, "x");
            runtime.RawJson = raw?.ToString(Formatting.None) ?? "null";
            var variables = field.Variables.ToDictionary(x => x.Key, x => ReadSource(x.Value, x.Key));
            runtime.SourceTimesJson = JsonConvert.SerializeObject(times);
            runtime.DataTime = times.Values.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(runtime.CalculatedAt).Max();
            var previousRuntime = old == null ? null : JsonConvert.DeserializeObject<ProcessFieldRuntime>(old.RuntimeJson);
            var inputIdentity = JsonConvert.SerializeObject(new { raw = runtime.RawJson, variables, runtime.SourceTimesJson });
            // 输入不变时复用已落库的计算结果；随机/时间相关公式可以选择每轮重算。
            var cacheKey = "inputs:" + inputIdentity;
            var cachedInput = previousRuntime?.InputIdentity;
            if (previousRuntime?.FieldIdentity == runtime.FieldIdentity && !field.RecalculateEverySample &&
                cachedInput == cacheKey && previousRuntime is { Error: null })
            {
                runtime.ValueJson = previousRuntime.ValueJson;
                runtime.State = previousRuntime.State;
            }
            else if (string.IsNullOrWhiteSpace(field.Formula))
            {
                runtime.ValueJson = runtime.RawJson;
                if (raw == null || raw.Type == JTokenType.Null) runtime.Error = "字段尚无有效值或已明确清空";
                runtime.State = LimitState(runtime.ValueJson, field);
            }
            else
            {
                var arguments = new Dictionary<string, double>();
                var all = new Dictionary<string, JToken?>(variables) { ["x"] = raw };
                var missing = new List<string>();
                foreach (var argument in all)
                {
                    if (TryNumber(argument.Value, out var number)) arguments[argument.Key] = number;
                    else if (argument.Key != "x") missing.Add(argument.Key);
                }
                if (missing.Count > 0) runtime.Error = "变量无有效数值：" + string.Join("、", missing);
                else
                {
                    var result = await formulas.EvaluateAsync(new ProcessFormulaRequest
                    {
                        Formula = field.Formula, Variables = arguments, Constants = field.Constants, Functions = field.Functions
                    }, cancellationToken);
                    runtime.Error = result.Error;
                    runtime.ValueJson = result.Success ? JsonConvert.SerializeObject(result.Value) : "null";
                    runtime.State = result.Success ? LimitState(runtime.ValueJson, field) : "Unknown";
                }
            }
            runtime.InputIdentity = cacheKey;
            runtime.RawNumber = TryNumber(raw, out var rawNumber) ? rawNumber : null;
            runtime.ValueNumber = TryNumber(JToken.Parse(runtime.ValueJson), out var computedNumber) ? computedNumber : null;
            await PersistFieldAsync(card, line, field, runtime, old);
        }
    }

    private async Task PersistFieldAsync(ProcessBoardDefinition card, ProcessBoardDefinition line,
        ProcessFieldDefinition field, ProcessFieldRuntime runtime, ProcessBoardValueEntity? old)
    {
        var current = field.TraceRawValue ? runtime.RawJson : runtime.ValueJson;
        var previousRuntime = old == null ? null : JsonConvert.DeserializeObject<ProcessFieldRuntime>(old.RuntimeJson);
        var initialized = previousRuntime?.FieldIdentity == runtime.FieldIdentity;
        var changed = initialized && runtime.Error == null && Changed(old!.BaselineJson, current, field.ChangeThreshold, field.ComparisonPrecision);
        var wasAlarm = old?.LimitState is "Upper" or "Lower";
        var alarm = runtime.State is "Upper" or "Lower";
        var limitChanged = runtime.Error == null && alarm && (!initialized || runtime.State != old!.LimitState);
        var recovered = initialized && runtime.Error == null && runtime.State == "Normal" && wasAlarm;
        var state = new ProcessBoardValueEntity
        {
            Id = old?.Id ?? 0, CardId = card.Id, FieldCode = field.Code, Version = card.Version,
            RuntimeJson = JsonConvert.SerializeObject(runtime), UpdateTime = runtime.CalculatedAt,
            BaselineJson = !initialized || changed || old!.BaselineJson == "null" ? current : old.BaselineJson,
            LimitState = runtime.State == "Unknown" && initialized ? old!.LimitState : runtime.State
        };
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            // 保存时配置版本已变化，则放弃这帧，避免按过期接收人或公式产生事件。
            var active = await Db.Ado.SqlQuerySingleAsync<ProcessBoardConfigEntity>(
                "SELECT * FROM ProcessBoardConfig WITH (UPDLOCK,HOLDLOCK) WHERE Id = @id",
                new SqlSugar.SugarParameter("@id", card.Id));
            if (active?.Version != card.Version || !active.Enabled) return;
            if (old == null) await Db.Insertable(state).ExecuteCommandAsync();
            else if (await Db.Updateable(state).Where(x => x.Id == old.Id && x.UpdateTime == old.UpdateTime).ExecuteCommandAsync() != 1)
                return; // 多实例竞争时只有成功更新基准者生成历史和事件。
            if (field.TrendEnabled)
                await Db.Insertable(new ProcessBoardSampleEntity
                {
                    CardId = card.Id, FieldCode = field.Code, Version = card.Version,
                    RawNumber = runtime.RawNumber, ValueNumber = runtime.ValueNumber,
                    RuntimeJson = state.RuntimeJson, CreateTime = runtime.CalculatedAt
                }).ExecuteCommandAsync();
            var events = new List<string>();
            if (changed) events.Add("Changed");
            if (limitChanged) events.Add(runtime.State == "Upper" ? "UpperExceeded" : "LowerExceeded");
            if (recovered) events.Add("Recovered");
            foreach (var eventType in events)
            {
                var shouldPush = field.PushEnabled && (eventType == "Changed" ? field.PushOnChange :
                    eventType == "Recovered" ? field.PushOnRecovery : field.PushOnLimit);
                if (!field.TraceEnabled && !shouldPush) continue;
                var record = new ProcessBoardEventEntity
                {
                    CardId = card.Id, FieldCode = field.Code, Version = card.Version, EventType = eventType,
                    PreviousJson = eventType == "Changed" ? old?.BaselineJson ?? "null" :
                        field.TraceRawValue ? previousRuntime?.RawJson ?? "null" : previousRuntime?.ValueJson ?? "null",
                    RuntimeJson = state.RuntimeJson, CreateTime = runtime.CalculatedAt
                };
                record.Id = await Db.Insertable(record).ExecuteReturnBigIdentityAsync();
                if (shouldPush) await ProcessBoardPushBridge.EnqueueAsync(Db, card, line, field, runtime, record);
            }
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException;
    }

    public static string LimitState(string valueJson, ProcessFieldDefinition field)
    {
        if (!TryNumber(JToken.Parse(valueJson), out var number) || !field.LowerLimit.HasValue && !field.UpperLimit.HasValue)
            return "Unknown";
        number = Math.Round(number, field.ComparisonPrecision, MidpointRounding.AwayFromZero);
        if (field.LowerLimit.HasValue && number < Math.Round(field.LowerLimit.Value, field.ComparisonPrecision, MidpointRounding.AwayFromZero)) return "Lower";
        if (field.UpperLimit.HasValue && number > Math.Round(field.UpperLimit.Value, field.ComparisonPrecision, MidpointRounding.AwayFromZero)) return "Upper";
        return "Normal";
    }

    public static bool Changed(string previous, string current, double threshold, int precision = 6)
    {
        if (previous == "null" || current == "null" || previous == current) return false;
        return TryNumber(JToken.Parse(previous), out var before) && TryNumber(JToken.Parse(current), out var after)
            ? before != after && (threshold == 0 || Math.Round(Math.Abs(after - before), precision, MidpointRounding.AwayFromZero) >= threshold)
            : previous != current;
    }

    private static bool TryNumber(JToken? token, out double number)
    {
        number = 0;
        return token != null && token.Type is JTokenType.Integer or JTokenType.Float or JTokenType.String &&
            double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }
}
