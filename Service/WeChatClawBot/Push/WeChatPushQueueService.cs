using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interface;
using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

public sealed partial class WeChatPushQueueService(
    SqlSugarScope scope,
    IWeChatPushQueueInitializer initializer,
    ISystemApplicationService applications,
    IWeChatClawBotService weChat) : IWeChatPushQueueService
{
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    private async Task RequireSenderAsync(ApplicationAccessContext access)
    {
        if (access.AppCode is not ("A" or "B") || !access.HasPermission("WeChatClawBot"))
            throw new UnauthorizedAccessException("缺少当前系统微信推送权限");
        if (!await Db.Queryable<SysUserInfo>().AnyAsync(x => x.UserId == access.UserId && x.Enabled))
            throw new UnauthorizedAccessException("发起账号不存在或已停用");
        if (!initializer.IsReady) throw new InvalidOperationException("微信发送队列正在初始化，请稍后重试");
    }

    public async Task<EmailPage<WeChatClawBotRecipientDto>> GetRecipientsAsync(
        ApplicationAccessContext access, string? keyword, int current, int size)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        keyword = keyword?.Trim();
        if (keyword?.Length > 128) throw new ArgumentException("搜索内容过长");
        current = Math.Max(1, current);
        size = Math.Clamp(size, 1, 100);
        var query = Db.Queryable<WeChatClawBotAccountEntity, SysUserInfo, WeChatClawBotConversationEntity>(
            (account, user, conversation) => new JoinQueryInfos(
                JoinType.Inner, account.UserId == user.UserId,
                JoinType.Inner, account.UserId == conversation.UserId))
            .Where((account, user, conversation) => user.Enabled && account.Status == "Bound" &&
                account.McpPushEnabled && !account.AdminDisabled && account.BotToken != null &&
                conversation.ContextToken != null && conversation.ContextToken != "")
            .WhereIF(!string.IsNullOrEmpty(keyword), (account, user, conversation) =>
                user.UserName.Contains(keyword!) || user.NickName.Contains(keyword!))
            .OrderBy((account, user, conversation) => user.UserName)
            .Select((account, user, conversation) => new WeChatClawBotRecipientDto
            {
                RecipientCode = user.UserId, UserName = user.UserName, Status = account.Status
            });
        RefAsync<int> total = 0;
        var rows = await query.ToPageListAsync(current, size, total);
        return new EmailPage<WeChatClawBotRecipientDto> { Current = current, Size = size, Total = total, Records = rows };
    }

    public async Task<object> EnqueueAsync(ApplicationAccessContext access, WeChatPushEnqueueDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        return await EnqueueCoreAsync(access.UserId, access.AppCode, dto);
    }

    private async Task<object> EnqueueCoreAsync(string createdBy, string appCode, WeChatPushEnqueueDto dto)
    {
        if (!initializer.IsReady) throw new InvalidOperationException("微信发送队列正在初始化，请稍后重试");
        if (!dto.Confirm) throw new ArgumentException("推送必须明确确认");
        if (string.IsNullOrWhiteSpace(dto.Content) || dto.Content.Length > 10000)
            throw new ArgumentException("正文不能为空且不能超过10000字");
        if (string.IsNullOrWhiteSpace(dto.IdempotencyKey) || dto.IdempotencyKey.Length > 128)
            throw new ArgumentException("缺少合法幂等标识");
        if (dto.ExternalTaskId?.Length > 128 || dto.SourceLabel?.Length > 32)
            throw new ArgumentException("外部任务编号或来源标签过长");
        if (string.Equals(dto.SourceLabel, "AI", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AI为系统保留消息来源");
        if (string.Equals(dto.SourceLabel, "WorkTask", StringComparison.OrdinalIgnoreCase)
            && !createdBy.StartsWith(WorkTaskChannelPolicy.Prefix, StringComparison.Ordinal))
            throw new ArgumentException("工作任务为系统保留消息来源");
        var ids = dto.RecipientUserIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length is 0 or > 100 || ids.Any(x => x.Length > 64)) throw new ArgumentException("单次需选择1至100位真实接收用户");
        var key = Hash($"{appCode}:{createdBy}:{dto.IdempotencyKey}");
        var payload = dto.ExternalTaskId == null && dto.SourceLabel == null
            ? Hash(JsonSerializer.Serialize(new { ids, content = dto.Content.Trim() }))
            : Hash(JsonSerializer.Serialize(new { ids, content = dto.Content.Trim(), dto.ExternalTaskId, dto.SourceLabel }));
        var old = await Db.Queryable<WeChatPushQueueEntity>().Where(x => x.RequestKey == key).ToListAsync();
        if (old.Count > 0) return Existing(old, payload);
        var accounts = await Db.Queryable<WeChatClawBotAccountEntity>().Where(x => ids.Contains(x.UserId)).ToListAsync();
        var users = await Db.Queryable<SysUserInfo>().Where(x => ids.Contains(x.UserId) && x.Enabled).ToListAsync();
        var conversations = await Db.Queryable<WeChatClawBotConversationEntity>().Where(x => ids.Contains(x.UserId)).ToListAsync();
        if (users.Count != ids.Length || accounts.Count != ids.Length || accounts.Any(x =>
                x.Status != "Bound" || !x.McpPushEnabled || x.AdminDisabled || string.IsNullOrWhiteSpace(x.BotToken)) ||
            ids.Any(id => !conversations.Any(x => x.UserId == id && !string.IsNullOrWhiteSpace(x.ContextToken))))
            throw new ArgumentException("接收用户不存在、已停用、未绑定、未建立会话或已关闭推送授权");
        var rows = ids.Select(id => new WeChatPushQueueEntity
        {
            RequestKey = key, PayloadHash = payload, CreatedBy = createdBy, AppCode = appCode,
            RecipientUserId = id, Content = dto.Content.Trim(),
            ExternalTaskId = dto.ExternalTaskId, SourceLabel = dto.SourceLabel,
            BindingIdentity = WeChatClawBotBindingIdentity.Create(accounts.Single(x => x.UserId == id))
        }).ToArray();
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            foreach (var row in rows) row.Id = await Db.Insertable(row).ExecuteReturnBigIdentityAsync();
        });
        if (!transaction.IsSuccess)
        {
            old = await Db.Queryable<WeChatPushQueueEntity>().Where(x => x.RequestKey == key).ToListAsync();
            if (old.Count > 0) return Existing(old, payload);
            throw transaction.ErrorException ?? new InvalidOperationException("微信消息入队失败");
        }
        return Existing(rows, payload);
    }

    public async Task<object> GetChannelStatusAsync(ApplicationAccessContext access, string recipientUserId)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        if (string.IsNullOrWhiteSpace(recipientUserId) || recipientUserId.Length > 64)
            throw new ArgumentException("请指定真实接收用户编码");
        var user = await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == recipientUserId);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == recipientUserId);
        var conversation = await Db.Queryable<WeChatClawBotConversationEntity>().FirstAsync(x => x.UserId == recipientUserId);
        var available = user?.Enabled == true && account?.Status == "Bound" && !account.AdminDisabled &&
            account.McpPushEnabled && !string.IsNullOrWhiteSpace(account.BotToken) && !string.IsNullOrWhiteSpace(conversation?.ContextToken);
        return new
        {
            recipientUserId, available, userEnabled = user?.Enabled == true,
            bound = account?.Status == "Bound", pushEnabled = account?.McpPushEnabled == true,
            adminDisabled = account?.AdminDisabled == true, hasConversation = !string.IsNullOrWhiteSpace(conversation?.ContextToken)
        };
    }

    private static object Existing(IEnumerable<WeChatPushQueueEntity> rows, string payload)
    {
        var records = rows.ToArray();
        if (records.Any(x => x.PayloadHash != payload)) throw new ArgumentException("该幂等标识已用于其他消息，不能替换人员或正文");
        return new { message = "消息已提交后台队列，请按任务编号查询结果；不代表已送达", tasks = records.Select(x => new { taskId = x.Id, x.RecipientUserId, x.Status }) };
    }

    public async Task<EmailPage<WeChatPushQueueRecordDto>> GetRecordsAsync(ApplicationAccessContext access, long? taskId, int current, int size)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        return await QueryRecordsAsync(access.UserId, access.AppCode, new WeChatPushQueueQuery
        {
            TaskId = taskId, Current = current, Size = size
        });
    }

    private async Task<EmailPage<WeChatPushQueueRecordDto>> QueryRecordsAsync(string owner, string appCode, WeChatPushQueueQuery input)
    {
        if (!initializer.IsReady) throw new InvalidOperationException("微信发送队列正在初始化，请稍后重试");
        var current = Math.Max(1, input.Current);
        var size = Math.Clamp(input.Size, 1, 100);
        RefAsync<int> total = 0;
        var query = Db.Queryable<WeChatPushQueueEntity>()
            .Where(x => x.CreatedBy == owner && x.AppCode == appCode)
            .OrderByDescending(x => x.Id);
        if (input.TaskId is { } id) query = query.Where(x => x.Id == id);
        if (input.DeliveryId is { } deliveryId) query = query.Where(x => x.DeliveryId == deliveryId);
        if (!string.IsNullOrWhiteSpace(input.RecipientCode)) query = query.Where(x => x.RecipientUserId == input.RecipientCode);
        if (!string.IsNullOrWhiteSpace(input.Status)) query = query.Where(x => x.Status == input.Status);
        if (!string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            var key = Hash($"{appCode}:{owner}:{input.IdempotencyKey}");
            query = query.Where(x => x.RequestKey == key);
        }
        var records = await query.Select(x => new WeChatPushQueueRecordDto
        {
            Id = x.Id, RecipientUserId = x.RecipientUserId, Content = x.Content, Status = x.Status,
            DeliveryId = x.DeliveryId, Error = x.Error, Attempt = x.Attempt, CreateTime = x.CreateTime, SentTime = x.SentTime,
            ExternalTaskId = x.ExternalTaskId, SourceLabel = x.SourceLabel
        }).ToPageListAsync(current, size, total);
        return new EmailPage<WeChatPushQueueRecordDto> { Records = records, Current = current, Size = size, Total = total };
    }

    public async Task RetryAsync(ApplicationAccessContext access, long id)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        var count = await Db.Updateable<WeChatPushQueueEntity>()
            .SetColumns(x => new WeChatPushQueueEntity { Status = "Pending", Error = null, UpdateTime = DateTime.Now })
            .Where(x => x.Id == id && x.CreatedBy == access.UserId && x.AppCode == access.AppCode && x.Status == "Failed")
            .ExecuteCommandAsync();
        if (count != 1) throw new InvalidOperationException("仅能重试本人当前系统明确失败的任务，待核实结果禁止重试");
    }

    public async Task<int> GetIntervalAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        return (await Db.Queryable<WeChatPushQueueSettingEntity>().InSingleAsync(1)).IntervalSeconds;
    }

    public async Task SetIntervalAsync(ApplicationAccessContext access, int seconds)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        if (!await CanConfigureAsync(access)) throw new UnauthorizedAccessException("仅管理员角色可以调整发送间隔");
        if (seconds is < 1 or > 3600) throw new ArgumentException("发送间隔范围1至3600秒");
        await Db.Updateable<WeChatPushQueueSettingEntity>().SetColumns(x => x.IntervalSeconds == seconds).Where(x => x.Id == 1).ExecuteCommandAsync();
    }

    public async Task<bool> CanConfigureAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        var user = await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == access.UserId);
        return user.RoleList.Any(x => x.Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
            x.Equals("R_SUPER", StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
