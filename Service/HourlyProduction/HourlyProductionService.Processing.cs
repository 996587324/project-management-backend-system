using System.Globalization;
using System.Text.Json;
using Interface;
using Model.EdgeGateway;
using Model.HourlyProduction;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

public sealed partial class HourlyProductionService
{
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        Ready();
        using var operation = DatabaseOperationScope.Begin(scope);
        var now = HourlyProductionRules.Now;
        var owner = Guid.NewGuid().ToString("N");
        if (await Db.Updateable<HourlyProductionWorkerStateEntity>()
                .SetColumns(x => new HourlyProductionWorkerStateEntity { LeaseOwner = owner, LeaseUntil = now.AddMinutes(5) })
                .Where(x => x.Id == 1 && (x.LeaseUntil == null || x.LeaseUntil < now)).ExecuteCommandAsync() == 0) return;
        try
        {
            // 先冻结事件水位，再读配置：配置提交后才到达的事件不会被旧配置批次跳过。
            var edgeHighWater = Db.DbMaintenance.IsAnyTable("EdgeGatewayMessageQueue", false)
                ? await Db.Queryable<EdgeMessageQueueEntity>().MaxAsync(x => x.Id) : 0;
            var boards = await Db.Queryable<HourlyProductionBoardEntity>().ToListAsync();
            catalog.Boards = boards.Select(Definition).ToArray();
            var edgeCaughtUp = await CaptureEdgeAsync(boards, edgeHighWater, cancellationToken);
            var inboxHighWater = await Db.Queryable<HourlyProductionInboxEntity>().MaxAsync(x => x.Id);
            boards = await Db.Queryable<HourlyProductionBoardEntity>().ToListAsync();
            var pending = await Db.Queryable<HourlyProductionInboxEntity>().Where(x => x.ProcessedAt == null && x.Id <= inboxHighWater)
                .OrderBy(x => x.Id).Take(100).ToListAsync();
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessInboxAsync(item, boards, cancellationToken);
            }
            // 先追平接收暂存再冻结报表，不能把尚未处理的积压当作零产出发送。
            if (edgeCaughtUp && !await Db.Queryable<HourlyProductionInboxEntity>().AnyAsync(x => x.ProcessedAt == null))
                await ScheduleReportsAsync(boards, now, cancellationToken);
            await DispatchReportsAsync(cancellationToken);
            await CleanupInboxAsync(now);
        }
        finally
        {
            await Db.Updateable<HourlyProductionWorkerStateEntity>()
                .SetColumns(x => new HourlyProductionWorkerStateEntity { LeaseUntil = null, LeaseOwner = null })
                .Where(x => x.Id == 1 && x.LeaseOwner == owner).ExecuteCommandAsync();
        }
    }

    private async Task CleanupInboxAsync(DateTime now)
    {
        var state = await Db.Queryable<HourlyProductionWorkerStateEntity>().InSingleAsync(1);
        if (state.NextCleanupAt > now) return;
        var cutoff = now.AddDays(-7);
        var ids = await Db.Queryable<HourlyProductionInboxEntity>()
            .Where(x => x.ProcessedAt != null && x.ReceivedAt < cutoff)
            .OrderBy(x => x.Id).Take(1000).Select(x => x.Id).ToListAsync();
        if (ids.Count > 0) await Db.Deleteable<HourlyProductionInboxEntity>().In(ids).ExecuteCommandAsync();
        var next = ids.Count == 1000 ? now.AddSeconds(5) : now.AddHours(1);
        await Db.Updateable<HourlyProductionWorkerStateEntity>().SetColumns(x => x.NextCleanupAt == next)
            .Where(x => x.Id == 1).ExecuteCommandAsync();
    }

    private async Task<bool> CaptureEdgeAsync(List<HourlyProductionBoardEntity> boards, long highWater, CancellationToken cancellationToken)
    {
        if (!Db.DbMaintenance.IsAnyTable("EdgeGatewayMessageQueue", false)) return true;
        var state = await Db.Queryable<HourlyProductionWorkerStateEntity>().InSingleAsync(1);
        var cursor = state.EdgeCursor;
        if (highWater <= cursor) return true;
        var definitions = boards.Select(Definition).ToArray();
        var keys = definitions.SelectMany(board => board.Lines).SelectMany(line => line.Processes)
            .Where(process => process.Enabled).SelectMany(process => process.Bindings)
            .Where(binding => binding.Kind == "Edge")
            .Select(binding => binding.SourceKey + "\u001f" + binding.DeviceKey).Distinct().ToArray();
        var messages = keys.Length == 0 ? new List<EdgeMessageQueueEntity>() :
            await Db.Queryable<EdgeMessageQueueEntity>().Where(x => x.Id > cursor && x.Id <= highWater &&
                keys.Contains(x.SourceId + "\u001f" + x.DeviceCode))
            .OrderBy(x => x.Id).Take(200).ToListAsync();
        foreach (var row in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EdgeStandardMessageDto? message;
            try
            {
                message = JsonSerializer.Deserialize<EdgeStandardMessageDto>(row.StandardPayloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (message?.Values == null || message.Values.Count == 0) throw new JsonException("报文数据为空");
            }
            catch (JsonException)
            {
                var errorKey = HourlyProductionRules.Hash("edge\u001f" + row.MessageId);
                if (!await Db.Queryable<HourlyProductionInboxEntity>().AnyAsync(x => x.EventKey == errorKey))
                    await Db.Insertable(new HourlyProductionInboxEntity
                    {
                        Kind = "Edge", EventKey = errorKey, SourceKey = row.SourceId, DeviceKey = row.DeviceCode,
                        ValuesJson = "{}", OccurredAt = row.MessageTime, ReceivedAt = row.CreateTime,
                        ProcessedAt = HourlyProductionRules.Now, Error = "中心消息JSON格式无效，未计入产量，请核对源报文"
                    }).ExecuteCommandAsync();
                continue;
            }
            if (message != null && definitions.Any(board => board.Lines.Any(line => line.Processes.Any(process =>
                    process.Enabled && process.Bindings.Any(binding => binding.Kind == "Edge" &&
                        binding.SourceKey == message.SourceId && binding.DeviceKey == row.DeviceCode)))))
            {
                var inbox = new HourlyProductionInboxEntity
                {
                    Kind = "Edge", EventKey = HourlyProductionRules.Hash("edge\u001f" + row.MessageId),
                    SourceKey = message.SourceId, DeviceKey = row.DeviceCode,
                    SourceDeviceCode = message.SourceDeviceCode,
                    ValuesJson = JsonSerializer.Serialize(message.Values), OccurredAt = LocalTime(message.Timestamp),
                    ReceivedAt = row.CreateTime
                };
                if (!await Db.Queryable<HourlyProductionInboxEntity>().AnyAsync(x => x.EventKey == inbox.EventKey))
                    await Db.Insertable(inbox).ExecuteCommandAsync();
            }
        }
        // 每批推进一次水位；若入库后宕机，下轮按事件唯一键安全重放。无关来源不拖慢统计。
        var nextCursor = messages.Count < 200 ? highWater : messages[^1].Id;
        await Db.Updateable<HourlyProductionWorkerStateEntity>().SetColumns(x => x.EdgeCursor == nextCursor)
            .Where(x => x.Id == 1 && x.EdgeCursor < nextCursor).ExecuteCommandAsync();
        return messages.Count < 200;
    }

    private async Task ProcessInboxAsync(HourlyProductionInboxEntity inbox,
        List<HourlyProductionBoardEntity> boards, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        using var textReader = new StringReader(inbox.ValuesJson);
        using var jsonReader = new Newtonsoft.Json.JsonTextReader(textReader)
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None
        };
        var values = JObject.Load(jsonReader);
        foreach (var row in boards)
        {
            var board = Definition(row);
            foreach (var line in board.Lines)
            foreach (var process in line.Processes.Where(x => x.Enabled))
            foreach (var binding in process.Bindings.Where(binding => binding.Kind == inbox.Kind &&
                         binding.SourceKey == inbox.SourceKey && binding.DeviceKey == inbox.DeviceKey &&
                         (binding.SourceDeviceCode.Length == 0 || binding.SourceDeviceCode == inbox.SourceDeviceCode)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // 必须在这一帧包含计数触发字段。合并的历史SN不能成为新过站。
                    if (process.Mode == "DistinctSn" && Token(values, binding.SnPath) == null ||
                        process.Mode == "Sum" && Token(values, binding.QuantityPath) == null) continue;
                    if (binding.FilterPath.Length > 0 && Text(Token(values, binding.FilterPath)) != binding.FilterValue) continue;
                    var occurred = binding.TimePath.Length == 0 ? inbox.OccurredAt :
                        ParseTime(Token(values, binding.TimePath));
                    if (occurred.Year < 2000 || occurred.Year > 2100)
                        throw new ArgumentException("过站时间必须在2000至2100年范围内");
                    if (process.ActiveSince.HasValue && (inbox.ReceivedAt < process.ActiveSince.Value || occurred < process.ActiveSince.Value)) continue;
                    var window = HourlyProductionRules.Window(board, occurred);
                    if (occurred < window.Start || occurred >= window.End) continue;
                    var slot = HourlyProductionRules.Slots(board, occurred).Single(x => occurred >= x.Start && occurred < x.End);
                    var snToken = Token(values, binding.SnPath);
                    if (process.Mode == "DistinctSn" && snToken?.Type is not (JTokenType.String or JTokenType.Integer))
                        throw new ArgumentException("SN字段必须是字符串或整数，不能绑定对象、数组或布尔值");
                    var sn = Text(snToken) ?? "";
                    if (process.Mode == "DistinctSn" && (string.IsNullOrWhiteSpace(sn) || sn.Length > 256))
                        throw new ArgumentException("SN为空或超过256字符");
                    var quantity = process.Mode == "Sum" ? ParseQuantity(Token(values, binding.QuantityPath)) : 1;
                    var pass = new HourlyProductionPassEntity
                    {
                        EventKey = HourlyProductionRules.Hash(inbox.EventKey + "\u001f" + board.Code + "\u001f" +
                            ProcessKey(line, process) + "\u001f" + binding.Code),
                        BoardCode = board.Code, ProcessKey = ProcessKey(line, process), Sn = sn,
                        ProductionDate = HourlyProductionRules.Plan(board,occurred)==null ? window.Start.Date : window.Start, SlotStart = slot.Start, OccurredAt = occurred,
                        Quantity = quantity, ExplicitRework = binding.ReworkPath.Length > 0 &&
                            Text(Token(values, binding.ReworkPath)) == binding.ReworkValue, RuleVersion = board.Version
                    };
                    await ApplyPassAsync(process, pass);
                }
                catch (ArgumentException exception)
                {
                    errors.Add($"{line.Code}/{process.Code}: {exception.Message}");
                }
            }
        }
        inbox.ProcessedAt = HourlyProductionRules.Now;
        inbox.Error = errors.Count == 0 ? null : string.Join("; ", errors)[..Math.Min(1000, string.Join("; ", errors).Length)];
        await Db.Updateable(inbox).ExecuteCommandAsync();
    }

    private async Task ApplyPassAsync(HourlyProcessDefinition process, HourlyProductionPassEntity pass)
    {
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await HourlyProcessLifecycle.LockAsync(Db, "hourly-lifecycle-schema", "Shared");
            await HourlyProcessLifecycle.LockAsync(Db, "hourly-config:" + pass.BoardCode, "Shared");
            var current = await Db.Queryable<HourlyProductionBoardEntity>().InSingleAsync(pass.BoardCode);
            if (current == null || current.Version != pass.RuleVersion)
                throw new InvalidOperationException("小时看板配置已更新，保留待处理报文，下轮按新配置处理");
            // 数据库事务级锁覆盖重算与桶差量，支持多个实例、重启补偿和乱序报文。
            var resource = "hourly:" + HourlyProductionRules.Hash(pass.BoardCode + pass.ProcessKey + pass.ProductionDate.ToString("yyyyMMdd"));
            await Db.Ado.ExecuteCommandAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @r < 0 THROW 51000, 'hourly statistic lock timeout', 1;",
                new SugarParameter("@key", resource));
            if (await Db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.EventKey == pass.EventKey)) return;
            var history = process.Mode == "DistinctSn"
                ? await Db.Queryable<HourlyProductionPassEntity>().Where(x => x.BoardCode == pass.BoardCode &&
                    x.ProcessKey == pass.ProcessKey && x.ProductionDate == pass.ProductionDate && x.Sn == pass.Sn).ToListAsync()
                : new List<HourlyProductionPassEntity>();
            // 数据库可能使用CI排序规则。SN是业务标识，索引筛选后仍需精确匹配，不能合并大小写不同的条码。
            history = history.Where(x => string.Equals(x.Sn, pass.Sn, StringComparison.Ordinal)).ToList();
            var before = history.GroupBy(x => x.SlotStart).ToDictionary(x => x.Key,
                x => (Output: x.Sum(p => p.Output), Rework: x.Sum(p => p.Rework)));
            history.Add(pass);
            HourlyProductionRules.Classify(process, history);
            pass.Id = await Db.Insertable(pass).ExecuteReturnBigIdentityAsync();
            if (history.Count > 1) await Db.Updateable(history.Where(x => x.Id != pass.Id).ToArray())
                .UpdateColumns(x => new { x.Output, x.Rework }).ExecuteCommandAsync();
            foreach (var group in history.GroupBy(x => x.SlotStart))
            {
                var previous = before.GetValueOrDefault(group.Key);
                var outputDelta = group.Sum(x => x.Output) - previous.Output;
                var reworkDelta = group.Sum(x => x.Rework) - previous.Rework;
                var slotStart = group.Key;
                var bucket = await Db.Queryable<HourlyProductionBucketEntity>().FirstAsync(x =>
                    x.BoardCode == pass.BoardCode && x.ProcessKey == pass.ProcessKey && x.SlotStart == slotStart);
                if (bucket == null)
                    await Db.Insertable(new HourlyProductionBucketEntity
                    {
                        BoardCode = pass.BoardCode, ProcessKey = pass.ProcessKey, SlotStart = group.Key,
                        Output = outputDelta, Rework = reworkDelta, UpdatedAt = HourlyProductionRules.Now
                    }).ExecuteCommandAsync();
                else
                {
                    bucket.Output += outputDelta;
                    bucket.Rework += reworkDelta;
                    bucket.UpdatedAt = HourlyProductionRules.Now;
                    await Db.Updateable(bucket).ExecuteCommandAsync();
                }
            }
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("小时统计事务失败");
    }

    internal static JToken? Token(JObject values, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (values.TryGetValue(path, StringComparison.Ordinal, out var direct)) return direct.Type == JTokenType.Null ? null : direct;
        // 复用现有历史字段目录的JSON Pointer（/data/SN、/data/a~1b/0），不能当成JSONPath处理。
        if (path.StartsWith('/'))
        {
            var parts = path.Split('/').Skip(1).ToArray();
            var offset = parts.FirstOrDefault() == "data" ? 1 : 0;
            JToken? current = values;
            foreach (var part in parts.Skip(offset))
            {
                var key = part.Replace("~1", "/").Replace("~0", "~");
                current = current switch
                {
                    JObject obj => obj.GetValue(key, StringComparison.Ordinal),
                    JArray array when int.TryParse(key, out var index) && index >= 0 && index < array.Count => array[index],
                    _ => null
                };
                if (current == null) return null;
            }
            return current?.Type == JTokenType.Null ? null : current;
        }
        try { return values.SelectToken(path, false); }
        catch (Newtonsoft.Json.JsonException) { throw new ArgumentException("字段路径格式错误"); }
    }

    internal static string? Text(JToken? token) => token?.Type switch
    {
        null or JTokenType.Null => null,
        JTokenType.Boolean => token.Value<bool>() ? "true" : "false",
        JTokenType.String => token.Value<string>(),
        _ => token.ToString(Newtonsoft.Json.Formatting.None)
    };

    private static decimal ParseQuantity(JToken? token)
    {
        if (!decimal.TryParse(Text(token), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 1000000000)
            throw new ArgumentException("求和字段不是合法的0至十亿数值");
        if (decimal.Round(value, 6) != value) throw new ArgumentException("求和字段最多支持6位小数，请先统一数据精度");
        return value;
    }

    private static DateTime ParseTime(JToken? token)
    {
        var text = Text(token);
        if (long.TryParse(text, out var stamp))
        {
            try
            {
                var date = stamp > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(stamp) : DateTimeOffset.FromUnixTimeSeconds(stamp);
                return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(date.UtcDateTime, "China Standard Time");
            }
            catch (ArgumentOutOfRangeException) { throw new ArgumentException("时间戳超出有效范围"); }
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset) &&
            (text!.EndsWith('Z') || RegexOffset(text)))
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(offset.UtcDateTime, "China Standard Time");
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            return DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
        throw new ArgumentException("过站时间字段缺失或格式无效，不使用接收时间伪造过站时间");
    }

    private static bool RegexOffset(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, "[+-][0-9]{2}:[0-9]{2}$");

    private static DateTime LocalTime(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? value : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(value.ToUniversalTime(), "China Standard Time");
}
