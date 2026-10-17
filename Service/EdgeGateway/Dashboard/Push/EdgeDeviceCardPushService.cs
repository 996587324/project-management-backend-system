using Interface;
using Model.EdgeGateway;
using Model.User;
using Model.WeChatClawBot;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

/// <summary>系统字段消息的持久化发送队列；不生成定时业务计划，也不调用大模型。</summary>
public sealed partial class EdgeDeviceCardPushService : IEdgeDeviceCardPushService
{
    private readonly SqlSugarScope _dbScope;
    private readonly IWeChatClawBotService _weChat;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public EdgeDeviceCardPushService(SqlSugarScope dbScope, IWeChatClawBotService weChat)
    {
        _dbScope = dbScope;
        _weChat = weChat;
    }

    public async Task<List<EdgeDeviceCardPushRecipientDto>> GetRecipientsAsync()
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var accounts = await Db.Queryable<WeChatClawBotAccountEntity>()
            .Where(x => x.Status == "Bound").OrderBy(x => x.UserName).ToListAsync();
        var ids = accounts.Select(x => x.UserId).ToArray();
        if (ids.Length == 0) return [];
        var users = await Db.Queryable<SysUserInfo>().Where(x => ids.Contains(x.UserId))
            .Select(x => new { x.UserId, x.UserName, x.Enabled }).ToListAsync();
        var contexts = await Db.Queryable<WeChatClawBotConversationEntity>()
            .Where(x => ids.Contains(x.UserId) && x.ContextToken != null)
            .Select(x => new { x.UserId, x.ContextToken }).ToListAsync();
        return accounts.Select(account =>
        {
            var user = users.FirstOrDefault(x => x.UserId.Equals(account.UserId, StringComparison.OrdinalIgnoreCase));
            var selectable = user?.Enabled == true && !account.AdminDisabled && !string.IsNullOrWhiteSpace(account.BotToken);
            var ready = selectable && contexts.Any(x =>
                x.UserId.Equals(account.UserId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.ContextToken));
            return new EdgeDeviceCardPushRecipientDto
            {
                UserId = account.UserId, UserName = user?.UserName ?? account.UserName,
                Selectable = selectable, Ready = ready,
                Status = !selectable ? "用户已停用或推送被禁用" : ready ? "可发送" : "会话未就绪，请先在微信发送消息"
            };
        }).ToList();
    }

    public async Task<EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>> GetTasksAsync(EdgeDeviceCardPushTaskQueryDto query)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var current = Math.Max(1, query.Current);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.LineCode), x => x.LineCode == query.LineCode)
            .WhereIF(query.CardId.HasValue, x => x.CardId == query.CardId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status)
            .WhereIF(!string.IsNullOrWhiteSpace(query.UserId), x => x.UserId == query.UserId)
            .OrderByDescending(x => x.Id).ToPageListAsync(current, size, total);
        return new EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>
        {
            Records = records, Current = current, Size = size, Total = total
        };
    }

    public async Task<int> QueueTestAsync(EdgeDeviceCardPushTestDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var card = await Db.Queryable<EdgeDashboardDeviceCardEntity>()
            .FirstAsync(x => x.Id == dto.CardId && x.Enabled && x.IsDelete == 0)
            ?? throw new InvalidOperationException("请先保存并启用卡片，再测试推送");
        var field = await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
            .FirstAsync(x => x.CardId == card.Id && x.FieldCode == dto.FieldCode && x.IsDelete == 0 && x.Enabled)
            ?? throw new InvalidOperationException("请先保存该展示字段");
        var rule = await Db.Queryable<EdgeDeviceCardPushRuleEntity>()
            .FirstAsync(x => x.CardId == card.Id && x.FieldCode == field.FieldCode)
            ?? throw new InvalidOperationException("请先保存该字段的推送配置");
        if (rule.Channel != "WeChat") throw new InvalidOperationException("外部 MCP 模式不由本系统发送微信消息");
        var ids = (dto.RecipientUserIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0 || ids.Length > 100) throw new InvalidOperationException("测试请选择 1～100 位接收人");
        await EdgeDeviceCardPushConfiguration.ValidateRecipientsAsync(Db, ids);
        var snapshot = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == card.TenantMark && x.DeviceCode == card.DeviceMark)
            ?? throw new InvalidOperationException("设备尚未接收到数据");
        await EdgeRealtimeFieldLatestStore.HydrateAsync(Db, [snapshot]);
        var token = EdgeDashboardJsonFieldResolver.ResolveIncludingNull(
            EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot), field.SourcePath);
        if (token == null || token.Type == JTokenType.Null)
            throw new InvalidOperationException("该字段尚无有效实际值，不能发送占位测试数据");
        var values = await Db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>()
            .Where(x => x.TenantMark == card.TenantMark && x.DeviceCode == card.DeviceMark).ToListAsync();
        var latest = values.Where(x => x.SourcePath == field.SourcePath ||
                (x.ValueType != "Object" && field.SourcePath.StartsWith(x.SourcePath + "/", StringComparison.Ordinal)))
            .OrderByDescending(x => x.SourcePath.Length).FirstOrDefault();
        var time = latest?.DataTime ?? snapshot.DataTime;
        if (dto.MessageTemplate?.Length > 2000) throw new InvalidOperationException("推送模板最多 2000 字");
        rule.MessageTemplate = string.IsNullOrWhiteSpace(dto.MessageTemplate) ? null : dto.MessageTemplate.Trim();
        var content = BuildContent(card, field, rule, "null", token, "Test", time);
        var eventKey = Guid.NewGuid().ToString("N");
        await InsertTasksAsync(Db, card, field, rule.Id, ids, eventKey, "Test", content, time);
        return ids.Length;
    }

    public async Task RetryAsync(long taskId)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var affected = await Db.Updateable<EdgeDeviceCardPushTaskEntity>()
            .SetColumns(x => new EdgeDeviceCardPushTaskEntity
            {
                Status = "Pending", ErrorMessage = null, NextAttemptTime = DateTime.Now, UpdateTime = DateTime.Now
            })
            .Where(x => x.Id == taskId && (x.Status == "Failed" || x.Status == "Blocked"))
            .ExecuteCommandAsync();
        if (affected == 0)
            throw new InvalidOperationException("仅明确失败或会话未就绪的任务可重试；结果未知时请先核实微信收件情况");
    }

    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var now = DateTime.Now;
        // 已开始发送但进程中断，无法确认微信是否收件：不退回 Pending，避免重启重复推送。
        await Db.Updateable<EdgeDeviceCardPushTaskEntity>()
            .SetColumns(x => new EdgeDeviceCardPushTaskEntity
            {
                Status = "Unknown", ErrorMessage = "发送处理中断，结果未知，请核实微信收件情况", UpdateTime = now
            })
            .Where(x => x.Status == "Processing" && x.LeaseExpireTime != null && x.LeaseExpireTime <= now)
            .ExecuteCommandAsync();
        await DispatchQueueBatchAsync(cancellationToken);
    }

    private async Task<bool> PrepareTaskAsync(EdgeDeviceCardPushTaskEntity task)
    {
        var now = DateTime.Now;
        var attempts = task.AttemptCount + 1;
        var lease = now.AddMinutes(10);
        var claimed = await Db.Updateable<EdgeDeviceCardPushTaskEntity>()
            .SetColumns(x => new EdgeDeviceCardPushTaskEntity
            {
                Status = "Processing", AttemptCount = attempts, LeaseExpireTime = lease, UpdateTime = now
            })
            .Where(x => x.Id == task.Id && (x.Status == "Pending" || x.Status == "Retrying"))
            .ExecuteCommandAsync();
        if (claimed == 0) return false;
        task.AttemptCount = attempts;
        task.Status = "Processing";
        try
        {
            if (task.CardId < 0 && task.LineCode.StartsWith("process:", StringComparison.Ordinal))
            {
                if (await ProcessBoardPushBridge.IsCurrentAsync(Db, task))
                {
                    await EdgeDeviceCardPushConfiguration.ValidateRecipientsAsync(Db, [task.UserId]);
                    var binding = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == task.UserId);
                    if (binding != null && task.BindingIdentity != null &&
                        task.BindingIdentity == WeChatClawBotBindingIdentity.Create(binding)) return true;
                }
                task.Status = "Cancelled";
                task.ErrorMessage = "工艺卡片、配置版本、接收人或微信绑定已变更/停用";
                await CompleteTaskAsync(task);
                return false;
            }
            var rule = await Db.Queryable<EdgeDeviceCardPushRuleEntity>().InSingleAsync(task.RuleId);
            var card = await Db.Queryable<EdgeDashboardDeviceCardEntity>().InSingleAsync(task.CardId);
            var field = await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>()
                .FirstAsync(x => x.CardId == task.CardId && x.FieldCode == task.FieldCode && x.Enabled && x.IsDelete == 0);
            var selected = task.EventType == "Test" || await Db.Queryable<EdgeDeviceCardPushRecipientEntity>()
                .AnyAsync(x => x.RuleId == task.RuleId && x.UserId == task.UserId);
            if (rule == null || rule.Channel != "WeChat" || (task.EventType != "Test" && !rule.Enabled) ||
                card == null || !card.Enabled || card.IsDelete != 0 || field == null || !selected)
            {
                task.Status = "Cancelled";
                task.ErrorMessage = "卡片、字段、接收人或推送规则已变更/停用";
            }
            else
            {
                await EdgeDeviceCardPushConfiguration.ValidateRecipientsAsync(Db, [task.UserId]);
                var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == task.UserId);
                if (account == null || task.BindingIdentity == null || task.BindingIdentity != WeChatClawBotBindingIdentity.Create(account))
                {
                    task.Status = "Cancelled";
                    task.ErrorMessage = "微信绑定已变更或原绑定无法确认，请重新触发推送";
                }
                else
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            task.Status = "Blocked";
            task.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            task.Status = "Unknown";
            task.ErrorMessage = ex.Message;
        }
        await CompleteTaskAsync(task);
        return false;
    }

    private async Task CompleteTaskAsync(EdgeDeviceCardPushTaskEntity task)
    {
        var attempts = task.AttemptCount;
        task.ErrorMessage = task.ErrorMessage?.Length > 2000 ? task.ErrorMessage[..2000] : task.ErrorMessage;
        task.LeaseExpireTime = null;
        task.UpdateTime = DateTime.Now;
        // 条件更新防止长请求返回后覆盖已经被其他处理器标记为 Unknown 的任务。
        await Db.Updateable(task).Where(x => x.Id == task.Id && x.Status == "Processing" && x.AttemptCount == attempts)
            .ExecuteCommandAsync();
    }
}
