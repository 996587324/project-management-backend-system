using System.Security.Cryptography;
using System.Text;
using Model.EdgeGateway;
using Model.User;
using Model.WeChatClawBot;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>卡片保存事务内同步推送配置。用卡片/字段编码关联，字段表重建不会丢失规则。</summary>
public static class EdgeDeviceCardPushConfiguration
{
    public static async Task ValidateAsync(ISqlSugarClient db, EdgeDashboardDeviceCardSaveDto card)
    {
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in card.Fields)
        {
            if (field.Push == null) continue; // 旧客户端未传此配置时保留原规则，不静默关闭推送。
            var push = field.Push;
            push.RecipientUserIds = (push.RecipientUserIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            push.MessageTemplate = string.IsNullOrWhiteSpace(push.MessageTemplate) ? null : push.MessageTemplate.Trim();
            if (push.Channel is not ("WeChat" or "ExternalMcp"))
                throw new InvalidOperationException("推送渠道只支持系统微信或外部 MCP");
            if (push.ChangeThreshold < 0 || push.ChangeThreshold > 999999999999m ||
                push.ChangeIntervalSeconds is < 0 or > 86400)
                throw new InvalidOperationException("推送变化阈值或间隔无效，间隔范围为 0～86400 秒");
            if (push.ChangeThreshold.HasValue && decimal.Round(push.ChangeThreshold.Value, 6) != push.ChangeThreshold.Value)
                throw new InvalidOperationException("推送变化阈值最多保留 6 位小数");
            if (push.MessageTemplate?.Length > 2000 || push.RecipientUserIds.Count > 100 ||
                push.RecipientUserIds.Any(x => x.Length > 64))
                throw new InvalidOperationException("推送模板最多 2000 字，接收人最多 100 位");
            if (!push.Enabled) continue;
            if (!push.OnChange && !push.OnLimit && !push.OnRecovery)
                throw new InvalidOperationException($"字段“{field.DisplayName}”至少选择一种推送触发方式");
            if ((push.OnLimit || push.OnRecovery) && field.AlarmLowerLimit == null && field.AlarmUpperLimit == null)
                throw new InvalidOperationException($"字段“{field.DisplayName}”请先配置上下限，或仅选择变化推送");
            if (push.Channel == "WeChat")
            {
                if (push.RecipientUserIds.Count == 0)
                    throw new InvalidOperationException($"字段“{field.DisplayName}”请选择微信接收人");
                recipients.UnionWith(push.RecipientUserIds);
            }
        }
        await ValidateRecipientsAsync(db, recipients.ToArray());
    }

    public static async Task ValidateRecipientsAsync(ISqlSugarClient db, string[] userIds)
    {
        if (userIds.Length == 0) return;
        var enabled = await db.Queryable<SysUserInfo>()
            .Where(x => userIds.Contains(x.UserId) && x.Enabled).Select(x => x.UserId).ToListAsync();
        var bound = await db.Queryable<WeChatClawBotAccountEntity>()
            .Where(x => userIds.Contains(x.UserId) && x.Status == "Bound" && !x.AdminDisabled && x.BotToken != null)
            .Select(x => x.UserId).ToListAsync();
        if (userIds.Any(id => !enabled.Contains(id, StringComparer.OrdinalIgnoreCase) ||
                              !bound.Contains(id, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("接收人必须是已启用、已绑定微信且未被管理员禁用的用户");
    }

    public static async Task SaveAsync(ISqlSugarClient db, EdgeDashboardDeviceCardSaveDto card, long cardId)
    {
        var rules = await db.Queryable<EdgeDeviceCardPushRuleEntity>().Where(x => x.CardId == cardId).ToListAsync();
        var ids = rules.Select(x => x.Id).ToArray();
        var recipients = ids.Length == 0 ? [] : await db.Queryable<EdgeDeviceCardPushRecipientEntity>()
            .Where(x => ids.Contains(x.RuleId)).ToListAsync();
        var removed = rules.Where(x => !card.Fields.Any(f => f.FieldCode.Equals(x.FieldCode,
            StringComparison.OrdinalIgnoreCase))).Select(x => x.Id).ToArray();
        if (removed.Length > 0)
        {
            await CancelPendingAsync(db, removed);
            await db.Deleteable<EdgeDeviceCardPushStateEntity>().Where(x => removed.Contains(x.RuleId)).ExecuteCommandAsync();
            await db.Deleteable<EdgeDeviceCardPushRecipientEntity>().Where(x => removed.Contains(x.RuleId)).ExecuteCommandAsync();
            await db.Deleteable<EdgeDeviceCardPushRuleEntity>().In(removed).ExecuteCommandAsync();
        }
        foreach (var field in card.Fields)
        {
            var rule = rules.FirstOrDefault(x => x.FieldCode.Equals(field.FieldCode, StringComparison.OrdinalIgnoreCase));
            var config = field.Push ?? (rule == null ? new EdgeDeviceCardPushConfigDto() : ToDto(rule, recipients));
            var sourceIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(new[] { card.TenantMark, card.DeviceMark, field.SourcePath }))));
            var changed = rule == null || rule.SourceIdentity != sourceIdentity ||
                JsonConvert.SerializeObject(ToDto(rule, recipients)) != JsonConvert.SerializeObject(config);
            rule ??= new EdgeDeviceCardPushRuleEntity { CardId = cardId, FieldCode = field.FieldCode };
            rule.SourceIdentity = sourceIdentity;
            rule.Enabled = config.Enabled;
            rule.Channel = config.Channel;
            rule.OnChange = config.OnChange;
            rule.OnLimit = config.OnLimit;
            rule.OnRecovery = config.OnRecovery;
            rule.ChangeThreshold = config.ChangeThreshold;
            rule.ChangeIntervalSeconds = config.ChangeIntervalSeconds;
            rule.MessageTemplate = config.MessageTemplate;
            rule.UpdateTime = DateTime.Now;
            if (rule.Id == 0)
                rule.Id = await db.Insertable(rule).ExecuteReturnBigIdentityAsync();
            else
                await db.Updateable(rule).ExecuteCommandAsync();
            if (changed)
            {
                await CancelPendingAsync(db, [rule.Id]);
                await db.Deleteable<EdgeDeviceCardPushStateEntity>().Where(x => x.RuleId == rule.Id).ExecuteCommandAsync();
            }
            await db.Deleteable<EdgeDeviceCardPushRecipientEntity>().Where(x => x.RuleId == rule.Id).ExecuteCommandAsync();
            if (config.RecipientUserIds.Count > 0)
                await db.Insertable(config.RecipientUserIds.Select(id => new EdgeDeviceCardPushRecipientEntity
                {
                    RuleId = rule.Id, UserId = id
                }).ToArray()).ExecuteCommandAsync();
        }
    }

    public static async Task AttachAsync(ISqlSugarClient db, List<EdgeDashboardDeviceCardOutputDto> cards)
    {
        var cardIds = cards.Select(x => x.Id).ToArray();
        if (cardIds.Length == 0) return;
        var rules = await db.Queryable<EdgeDeviceCardPushRuleEntity>().Where(x => cardIds.Contains(x.CardId)).ToListAsync();
        var ids = rules.Select(x => x.Id).ToArray();
        var recipients = ids.Length == 0 ? [] : await db.Queryable<EdgeDeviceCardPushRecipientEntity>()
            .Where(x => ids.Contains(x.RuleId)).ToListAsync();
        foreach (var card in cards)
        foreach (var field in card.Fields)
        {
            var rule = rules.FirstOrDefault(x => x.CardId == card.Id &&
                x.FieldCode.Equals(field.FieldCode, StringComparison.OrdinalIgnoreCase));
            field.Push = rule == null ? new EdgeDeviceCardPushConfigDto() : ToDto(rule, recipients);
        }
    }

    private static EdgeDeviceCardPushConfigDto ToDto(EdgeDeviceCardPushRuleEntity rule,
        List<EdgeDeviceCardPushRecipientEntity> recipients) => new()
    {
        Enabled = rule.Enabled, Channel = rule.Channel, OnChange = rule.OnChange,
        OnLimit = rule.OnLimit, OnRecovery = rule.OnRecovery, ChangeThreshold = rule.ChangeThreshold,
        ChangeIntervalSeconds = rule.ChangeIntervalSeconds, MessageTemplate = rule.MessageTemplate,
        RecipientUserIds = recipients.Where(x => x.RuleId == rule.Id).Select(x => x.UserId)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
    };

    private static Task<int> CancelPendingAsync(ISqlSugarClient db, long[] ruleIds) =>
        db.Updateable<EdgeDeviceCardPushTaskEntity>()
            .SetColumns(x => new EdgeDeviceCardPushTaskEntity
            {
                Status = "Cancelled", ErrorMessage = "推送配置已变更，请重新触发", UpdateTime = DateTime.Now
            })
            .Where(x => ruleIds.Contains(x.RuleId) && (x.Status == "Pending" || x.Status == "Retrying" ||
                x.Status == "Failed" || x.Status == "Blocked"))
            .ExecuteCommandAsync();
}
