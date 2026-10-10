using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Model.EdgeGateway;
using Model.User;
using Model.WeChatClawBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

public sealed partial class EdgeDeviceCardPushService
{
    /// <summary>由实时接收在同一事务、同一写锁内调用；只落库，不请求微信。</summary>
    public static async Task CaptureAsync(ISqlSugarClient db, EdgeDashboardTelemetrySampleDto sample,
        CancellationToken cancellationToken)
    {
        var cards = await db.Queryable<EdgeDashboardDeviceCardEntity>()
            .Where(x => x.Enabled && x.IsDelete == 0 && x.TenantMark == sample.TenantMark &&
                        x.DeviceMark == sample.DeviceMark).ToListAsync();
        if (cards.Count == 0) return;
        var cardIds = cards.Select(x => x.Id).ToArray();
        var rules = await db.Queryable<EdgeDeviceCardPushRuleEntity>()
            .Where(x => cardIds.Contains(x.CardId) && x.Enabled && x.Channel == "WeChat").ToListAsync();
        if (rules.Count == 0) return;
        var ids = rules.Select(x => x.Id).ToArray();
        var recipients = await db.Queryable<EdgeDeviceCardPushRecipientEntity>()
            .Where(x => ids.Contains(x.RuleId)).ToListAsync();
        var fields = await db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
            .Where(x => cardIds.Contains(x.CardId) && x.Enabled && x.IsDelete == 0).ToListAsync();
        var states = await db.Queryable<EdgeDeviceCardPushStateEntity>()
            .Where(x => ids.Contains(x.RuleId)).ToListAsync();
        var reported = EdgeDashboardJsonFieldResolver.BuildReportedEnvelope(sample);
        var envelope = EdgeDashboardJsonFieldResolver.BuildEnvelope(sample);
        var now = DateTime.Now;
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = fields.FirstOrDefault(x => x.CardId == rule.CardId &&
                x.FieldCode.Equals(rule.FieldCode, StringComparison.OrdinalIgnoreCase));
            if (field == null || EdgeDashboardJsonFieldResolver.ResolveIncludingNull(reported, field.SourcePath) == null)
                continue;
            var token = EdgeDashboardJsonFieldResolver.ResolveIncludingNull(envelope, field.SourcePath);
            if (token == null) continue;
            var json = token.ToString(Formatting.None);
            var limit = LimitState(token, field);
            var state = states.FirstOrDefault(x => x.RuleId == rule.Id);
            if (state == null)
            {
                await db.Insertable(new EdgeDeviceCardPushStateEntity
                {
                    RuleId = rule.Id, ModelMark = sample.ModelMark, LastValueJson = json,
                    ChangeBaselineJson = json, LimitState = limit, DataTime = sample.DataTime,
                    SourceTimestamp = sample.SourceTimestamp, UpdateTime = now
                }).ExecuteCommandAsync();
                continue; // 首次上报只建立基准，包含首次就越限的情况。
            }
            if (sample.SourceTimestamp.HasValue && state.SourceTimestamp.HasValue
                    ? sample.SourceTimestamp.Value < state.SourceTimestamp.Value
                    : sample.DataTime < state.DataTime)
                continue;
            var events = new List<string>();
            var modelChanged = !string.Equals(state.ModelMark, sample.ModelMark, StringComparison.OrdinalIgnoreCase);
            if (!modelChanged && token.Type != JTokenType.Null)
            {
                if (rule.OnLimit && (limit is "Upper" or "Lower") && limit != state.LimitState)
                    events.Add(limit == "Upper" ? "UpperExceeded" : "LowerExceeded");
                if (rule.OnRecovery && limit == "Normal" && (state.LimitState is "Upper" or "Lower"))
                    events.Add("Recovered");
                if (events.Count == 0 && rule.OnChange && Changed(state.ChangeBaselineJson, json, rule.ChangeThreshold, field.ValueType) &&
                    (!state.LastQueuedTime.HasValue || now >= state.LastQueuedTime.Value.AddSeconds(rule.ChangeIntervalSeconds)))
                    events.Add("Changed");
            }
            if (events.Count > 0)
            {
                var card = cards.Single(x => x.Id == rule.CardId);
                var userIds = recipients.Where(x => x.RuleId == rule.Id).Select(x => x.UserId).ToArray();
                foreach (var eventType in events)
                {
                    var eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        JsonConvert.SerializeObject(new object?[]
                        {
                            rule.Id, eventType, sample.MessageId, sample.SourceTimestamp, sample.DataTime, json
                        }))));
                    var oldJson = eventType == "Changed" ? state.ChangeBaselineJson : state.LastValueJson;
                    var content = BuildContent(card, field, rule, oldJson, token, eventType, sample.DataTime);
                    await InsertTasksAsync(db, card, field, rule.Id, userIds, eventKey, eventType, content, sample.DataTime);
                }
                state.LastQueuedTime = now;
                state.ChangeBaselineJson = json;
            }
            else if (modelChanged || token.Type == JTokenType.Null || state.ChangeBaselineJson == "null")
            {
                state.ChangeBaselineJson = json;
            }
            state.LastValueJson = json;
            // 明确空值不是“恢复正常”；保留越限状态直到重新收到有效数值。
            if (limit != "Unknown" || modelChanged) state.LimitState = limit;
            state.ModelMark = sample.ModelMark;
            state.DataTime = sample.DataTime;
            state.SourceTimestamp = sample.SourceTimestamp;
            state.UpdateTime = now;
            await db.Updateable(state).ExecuteCommandAsync();
        }
    }

    private static string LimitState(JToken token, EdgeDashboardDeviceCardFieldEntity field)
    {
        var numeric = token.Type is JTokenType.Integer or JTokenType.Float ||
            (token.Type == JTokenType.String && EdgeDashboardService.IsDeviceCardNumericValueType(field.ValueType));
        if (!numeric || !decimal.TryParse(Text(token), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            return "Unknown";
        if (field.AlarmLowerLimit.HasValue && number < field.AlarmLowerLimit.Value) return "Lower";
        if (field.AlarmUpperLimit.HasValue && number > field.AlarmUpperLimit.Value) return "Upper";
        return "Normal";
    }

    private static bool Changed(string previous, string current, decimal? threshold, string valueType)
    {
        if (previous == current || previous == "null") return false;
        var old = JToken.Parse(previous);
        var next = JToken.Parse(current);
        var numeric = (old.Type is JTokenType.Integer or JTokenType.Float) && (next.Type is JTokenType.Integer or JTokenType.Float) ||
            EdgeDashboardService.IsDeviceCardNumericValueType(valueType);
        if (numeric && decimal.TryParse(Text(old), NumberStyles.Any, CultureInfo.InvariantCulture, out var oldValue) &&
            decimal.TryParse(Text(next), NumberStyles.Any, CultureInfo.InvariantCulture, out var nextValue))
        {
            try
            {
                return nextValue != oldValue && Math.Abs(nextValue - oldValue) >= (threshold ?? 0);
            }
            catch (OverflowException)
            {
                return true; // 两个合法大数的差值超出 decimal 范围，仍是超过阈值的变化。
            }
        }
        return true;
    }

    private static async Task InsertTasksAsync(ISqlSugarClient db, EdgeDashboardDeviceCardEntity card,
        EdgeDashboardDeviceCardFieldEntity field, long ruleId, string[] userIds, string eventKey,
        string eventType, string content, DateTime dataTime)
    {
        if (userIds.Length == 0) return;
        var existing = await db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => x.EventKey == eventKey).Select(x => x.UserId).ToListAsync();
        var users = await db.Queryable<SysUserInfo>().Where(x => userIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.UserName }).ToListAsync();
        var bindings = await db.Queryable<WeChatClawBotAccountEntity>()
            .Where(x => userIds.Contains(x.UserId)).ToListAsync();
        var tasks = userIds.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !existing.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Select(id =>
            {
                var account = bindings.FirstOrDefault(x => x.UserId.Equals(id, StringComparison.OrdinalIgnoreCase));
                return new EdgeDeviceCardPushTaskEntity
                {
                    RuleId = ruleId, CardId = card.Id, LineCode = card.LineCode,
                    DeviceMark = card.DeviceMark, FieldCode = field.FieldCode, DisplayName = field.DisplayName,
                    UserId = id, EventKey = eventKey, EventType = eventType, Content = content, DataTime = dataTime,
                    UserName = users.FirstOrDefault(x => x.UserId.Equals(id, StringComparison.OrdinalIgnoreCase))?.UserName,
                    BindingIdentity = account == null ? null : WeChatClawBotBindingIdentity.Create(account)
                };
            }).ToArray();
        if (tasks.Length > 0) await db.Insertable(tasks).ExecuteCommandAsync();
    }

    private static string BuildContent(EdgeDashboardDeviceCardEntity card, EdgeDashboardDeviceCardFieldEntity field,
        EdgeDeviceCardPushRuleEntity rule, string previousJson, JToken current, string eventType, DateTime dataTime)
    {
        var label = eventType switch
        {
            "UpperExceeded" => "超过上限", "LowerExceeded" => "低于下限",
            "Recovered" => "恢复正常", "Test" => "测试推送", _ => "字段变化"
        };
        var template = rule.MessageTemplate ??
            "【设备字段通知】{event}\n线体：{line}\n设备：{device}\n字段：{field}\n旧值：{oldValue}\n当前值：{value}{unit}\n范围：{lower} ～ {upper}\n数据时间：{time}";
        var values = new Dictionary<string, string>
        {
            ["event"] = label, ["line"] = card.LineCode, ["device"] = card.CardName,
            ["field"] = field.DisplayName, ["oldValue"] = Text(JToken.Parse(previousJson)),
            ["value"] = Text(current), ["unit"] = field.Unit ?? string.Empty,
            ["lower"] = field.AlarmLowerLimit?.ToString(CultureInfo.InvariantCulture) ?? "—",
            ["upper"] = field.AlarmUpperLimit?.ToString(CultureInfo.InvariantCulture) ?? "—",
            ["time"] = dataTime.ToString("yyyy-MM-dd HH:mm:ss")
        };
        // 单次替换，不让设备值里包含的 {token} 再次变成模板表达式。
        var content = System.Text.RegularExpressions.Regex.Replace(template, @"\{(\w+)\}",
            match => values.GetValueOrDefault(match.Groups[1].Value, match.Value));
        return content.Length <= 10000 ? content : content[..9990] + "…（已截断）";
    }

    private static string Text(JToken token) => token.Type == JTokenType.Null ? "—" :
        token is JValue value ? Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "—" : token.ToString(Formatting.None);
}
