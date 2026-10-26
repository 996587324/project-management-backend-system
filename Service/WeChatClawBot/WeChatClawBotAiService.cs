using Interface;
using Model.Agent;
using Model.SystemApplications;
using Model.WeChatClawBot;
using SqlSugar;
using System.Text.Json;

namespace Service;

/// <summary>微信 AI 渠道：程序验密、持久化额度、入站任务和异步回复；不调用公开 MCP。</summary>
public sealed class WeChatClawBotAiService(
    SqlSugarScope scope,
    ISystemApplicationService applications,
    IAgentToolRegistryService registry,
    IAgentModelClient? routingModel = null) : IWeChatClawBotAiService
{
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly HashSet<string> Initialized = new();
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    private async Task EnsureAsync()
    {
        var key = Db.CurrentConnectionConfig.ConnectionString;
        await SchemaGate.WaitAsync();
        try
        {
            if (Initialized.Contains(key)) return;
            await using var schemaLock = await WeChatClawBotAiLock.TryEnterAsync(Db, "ClawBotAi:schema", 10000)
                ?? throw new InvalidOperationException("微信数据表正在初始化，请稍后重试");
            using var initializationDb = Db.CopyNew();
            WeChatClawBotDatabaseInitializer.Initialize(initializationDb);
            Initialized.Add(key);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private async Task<WeChatClawBotAiConnectionEntity> GetConnectionAsync(string userId)
    {
        return await Db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync(userId)
            ?? new WeChatClawBotAiConnectionEntity { UserId = userId };
    }

    private async Task PersistAsync(WeChatClawBotAiConnectionEntity connection)
    {
        connection.UpdateTimeUtc = DateTime.UtcNow;
        if (await Db.Queryable<WeChatClawBotAiConnectionEntity>().AnyAsync(x => x.UserId == connection.UserId))
            await Db.Updateable(connection).ExecuteCommandAsync();
        else
            await Db.Insertable(connection).ExecuteCommandAsync();
    }

    public async Task<WeChatClawBotAiStateDto> GetStateAsync(string userId)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        var config = await GetConnectionAsync(userId);
        var day = WeChatClawBotAiPolicy.Day(DateTime.UtcNow);
        var usage = await Db.Queryable<WeChatClawBotAiDayEntity>().FirstAsync(x => x.UserId == userId && x.Day == day);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
        var authorized = IsAuthorized(config, account);
        return new WeChatClawBotAiStateDto
        {
            Configuration = ToConfiguration(config), HasSecret = config.SecretHash != null,
            State = !config.Enabled ? "Disabled" : account?.Status != "Bound" || account.AdminDisabled ? "Unbound" :
                config.LockedUntilUtc > DateTime.UtcNow ? "Locked" : authorized ? "Authorized" :
                config.AuthorizedUntilUtc != null ? "Expired" : "Unverified",
            AuthorizedUntilUtc = config.AuthorizedUntilUtc, LockedUntilUtc = config.LockedUntilUtc,
            Used = usage?.Used ?? 0,
            Remaining = usage?.LimitNoticeQueued == true ? 0 : Math.Max(0, config.DailyLimit - (usage?.Used ?? 0)),
            DailyLimited = usage?.LimitNoticeQueued == true,
            Day = day, ConversationId = config.ConversationId, ActiveAgentCode = config.ActiveAgentCode ??
                (config.ConversationId.HasValue ? config.AgentCode : null)
        };
    }

    public async Task SaveAsync(string userId, WeChatClawBotAiSaveDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        if (dto.AppCode is not ("A" or "B") || dto.Mode is not ("Chat" or "Work") ||
            dto.ValidHours is < 1 or > 720 || dto.DailyLimit is < 1 or > 10000 ||
            dto.BotDailyLimit is < 0 or > 100000 || dto.SendIntervalSeconds is < 1 or > 3600 ||
            dto.TimeoutSeconds is < 10 or > 300 || string.IsNullOrWhiteSpace(dto.LimitMessage) || dto.LimitMessage.Length > 500)
            throw new ArgumentException("连接配置无效，请检查系统、模式、有效期、额度、间隔和提示内容");
        if (WeChatClawBotAiPolicy.IsConnectionCommand(dto.LimitMessage))
            throw new ArgumentException("超限提示不能包含连接命令或专属密钥");
        var access = await applications.GetChannelAccessAsync(userId, dto.AppCode);
        if (!CanUseChannel(access)) throw new UnauthorizedAccessException("没有微信 ClawBot 功能权限");
        var requested = dto.Mode == "Chat" ? new List<string> { AgentCodes.Chat } :
            dto.AgentCodes ?? new List<string> { dto.AgentCode };
        if (requested.Count is < 1 or > 32 || requested.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64))
            throw new ArgumentException("工作模式请选择 1～32 个有效 Agent");
        var agentCodes = new List<string>();
        foreach (var code in requested.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var definition = await registry.GetAgentAsync(code);
            if (definition == null || !definition.Enabled)
                throw new ArgumentException("所选 Agent 不存在或已停用");
            if (dto.Mode == "Work" && (definition.AgentCode == AgentCodes.Chat || !CanUseAgent(access, definition.AgentCode)))
                throw new UnauthorizedAccessException("没有所选工作 Agent 权限");
            agentCodes.Add(definition.AgentCode);
        }
        await using var gate = await StateLockAsync(userId);
        var config = await GetConnectionAsync(userId);
        // 所有配置修改均撤销旧授权，防止换系统/Agent后沿用之前的验证和确认计划。
        config.Enabled = dto.Enabled;
        config.AppCode = dto.AppCode;
        config.Mode = dto.Mode;
        config.AgentCode = agentCodes[0];
        config.AllowedAgentCodesJson = JsonSerializer.Serialize(agentCodes);
        config.ValidHours = dto.ValidHours;
        config.DailyLimit = dto.DailyLimit;
        config.BotDailyLimit = dto.BotDailyLimit;
        config.SendIntervalSeconds = dto.SendIntervalSeconds;
        config.TimeoutSeconds = dto.TimeoutSeconds;
        config.LimitMessage = dto.LimitMessage.Trim();
        Revoke(config);
        await PersistAsync(config);
    }

    public async Task<string> ResetSecretAsync(string userId)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        await using var gate = await StateLockAsync(userId);
        var config = await GetConnectionAsync(userId);
        var secret = WeChatClawBotAiPolicy.NewSecret();
        config.SecretHash = WeChatClawBotAiPolicy.Hash(secret);
        config.FailedAttempts = 0;
        config.LockedUntilUtc = null;
        Revoke(config);
        await PersistAsync(config);
        return secret;
    }

    public async Task DisconnectAsync(string userId)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        await using var gate = await StateLockAsync(userId);
        var config = await GetConnectionAsync(userId);
        Revoke(config);
        await PersistAsync(config);
    }

    public async Task ClearContextAsync(string userId) => await DisconnectAsync(userId);

    public async Task<List<object>> GetOptionsAsync(string userId)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        var result = new List<object>();
        var apps = await Db.Queryable<SysApplicationEntity>().Where(x => x.Enabled).OrderBy(x => x.Sort).ToListAsync();
        foreach (var app in apps)
        {
            ApplicationAccessContext access;
            try { access = await applications.GetChannelAccessAsync(userId, app.AppCode); }
            catch (UnauthorizedAccessException) { continue; }
            if (!CanUseChannel(access)) continue;
            var agents = (await registry.GetAllAgentsAsync()).Where(x => x.Enabled &&
                (x.AgentCode == "chat" || CanUseAgent(access, x.AgentCode)))
                .Select(x => new { x.AgentCode, x.Name }).ToList();
            result.Add(new { app.AppCode, app.DisplayName, access.AllResources, agents });
        }
        return result;
    }

    public async Task<List<WeChatClawBotAiJobEntity>> GetJobsAsync(string userId, int current, int size)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        return await Db.Queryable<WeChatClawBotAiJobEntity>().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Id).Skip((Math.Clamp(current, 1, 10000) - 1) * Math.Clamp(size, 1, 100))
            .Take(Math.Clamp(size, 1, 100)).ToListAsync();
    }

    public async Task HandleInboundAsync(WeChatClawBotAccountEntity account, WeChatClawBotInboundMessageEntity message)
    {
        // 调用者已开启消息+游标事务；必须使用同一个 DatabaseOperationScope，失败整体回滚。
        await EnsureAsync();
        await using var gate = await StateLockAsync(account.UserId);
        if (message.UserId != account.UserId || message.SenderId != account.ILinkUserId ||
            account.Status != "Bound" || account.AdminDisabled)
            throw new UnauthorizedAccessException("微信消息发送者与绑定账号不一致");
        var raw = message.Content;
        var command = WeChatClawBotAiPolicy.IsConnectionCommand(raw);
        if (command) message.Content = WeChatClawBotAiPolicy.Redact(raw);
        message.Id = await Db.Insertable(message).ExecuteReturnBigIdentityAsync();
        var config = await GetConnectionAsync(account.UserId);
        if (!config.Enabled || config.SecretHash == null) return;
        var today = WeChatClawBotAiPolicy.Day(DateTime.UtcNow);
        var day = await Db.Queryable<WeChatClawBotAiDayEntity>()
            .FirstAsync(x => x.UserId == account.UserId && x.Day == today);
        if (day == null)
        {
            day = new WeChatClawBotAiDayEntity { UserId = account.UserId, Day = today };
            await Db.Insertable(day).ExecuteCommandAsync();
        }
        if (day.LimitNoticeQueued) return;
        // 验证和工作请求都必须绑定有效的本地用户及系统授权。
        try
        {
            var access = await applications.GetChannelAccessAsync(account.UserId, config.AppCode);
            if (!CanUseChannel(access)) return;
        }
        catch (UnauthorizedAccessException) { return; }
        if (day.Used >= config.DailyLimit)
        {
            day.LimitNoticeQueued = true;
            await Db.Updateable(day).ExecuteCommandAsync();
            await EnqueueAsync(account, message, config, today, "Limit", config.LimitMessage);
            return;
        }
        if (command)
        {
            if (config.LockedUntilUtc > DateTime.UtcNow) return;
            var wasAuthorized = IsAuthorized(config, account);
            var secret = WeChatClawBotAiPolicy.ReadSecret(raw);
            var success = message.MessageType == "Text" && secret != null && WeChatClawBotAiPolicy.Verify(secret, config.SecretHash);
            if (success)
            {
                config.BindingIdentity = WeChatClawBotBindingIdentity.Create(account);
                config.AuthorizedUntilUtc = DateTime.UtcNow.AddHours(config.ValidHours);
                config.FailedAttempts = 0;
                config.LockedUntilUtc = null;
            }
            else
            {
                config.FailedAttempts++;
                if (config.FailedAttempts >= 5)
                {
                    config.LockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
                    config.FailedAttempts = 0;
                }
            }
            await PersistAsync(config);
            await ControlReplyAsync(account, message, config, day, success ? "连接验证成功，可以开始对话。" :
                config.LockedUntilUtc > DateTime.UtcNow ? "验证失败次数过多，请十五分钟后重试。" : "连接密钥无效，请检查后重试。", success && !wasAuthorized);
            return;
        }
        if (!IsAuthorized(config, account))
        {
            await ControlReplyAsync(account, message, config, day, "请先发送“连接 专属密钥”完成验证。", false);
            return;
        }
        if (message.MessageType != "Text")
        {
            await ControlReplyAsync(account, message, config, day, "当前仅支持文字消息。", false);
            return;
        }
        if (raw.Length > 8000)
        {
            await ControlReplyAsync(account, message, config, day, "消息过长，请缩短后重试。", false);
            return;
        }
        // 预占额度与任务入队、消息游标在同一事务，重启和并发接收都不能绕过额度。
        day.Used++;
        await Db.Updateable(day).ExecuteCommandAsync();
        await EnqueueAsync(account, message, config, today, "Agent", null);
    }

    private async Task ControlReplyAsync(WeChatClawBotAccountEntity account, WeChatClawBotInboundMessageEntity inbound,
        WeChatClawBotAiConnectionEntity config, WeChatClawBotAiDayEntity day, string text, bool important)
    {
        if (!important && day.LastControlReplyUtc > DateTime.UtcNow.AddMinutes(-1)) return;
        day.LastControlReplyUtc = DateTime.UtcNow;
        await Db.Updateable(day).ExecuteCommandAsync();
        await EnqueueAsync(account, inbound, config, day.Day, "Control", text);
    }

    private async Task EnqueueAsync(WeChatClawBotAccountEntity account, WeChatClawBotInboundMessageEntity inbound,
        WeChatClawBotAiConnectionEntity config, string day, string kind, string? reply)
    {
        await Db.Insertable(new WeChatClawBotAiJobEntity
        {
            UserId = account.UserId, InboundId = inbound.Id, Version = config.Version,
            BindingIdentity = WeChatClawBotBindingIdentity.Create(account), Day = day,
            Kind = kind, Reply = reply, Status = kind == "Agent" ? "Pending" : "ReplyPending",
            QuotaReserved = kind == "Agent"
        }).ExecuteCommandAsync();
    }

    public async Task<List<string>> GetPendingUsersAsync()
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        var users = await Db.Queryable<WeChatClawBotAiJobEntity>()
            .Where(x => x.Status == "Pending" || x.Status == "ReplyPending" || x.Status == "Running" || x.Status == "Sending")
            .GroupBy(x => x.UserId)
            .Select(x => new { UserId = x.UserId, Updated = SqlFunc.AggregateMin(x.UpdateTimeUtc) })
            .OrderBy(x => x.Updated).Take(30).ToListAsync();
        return users.Select(x => x.UserId).ToList();
    }

    public async Task ProcessUserAsync(string userId, IAgentService agent, IWeChatClawBotService sender,
        CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        await using var runner = await WeChatClawBotAiLock.TryEnterAsync(Db, "ClawBotAi:runner:" + userId);
        if (runner == null) return;
        var job = await Db.Queryable<WeChatClawBotAiJobEntity>().Where(x => x.UserId == userId &&
            (x.Status == "Pending" || x.Status == "ReplyPending" || x.Status == "Running" || x.Status == "Sending"))
            .OrderBy(x => x.Id).FirstAsync();
        if (job == null) return;
        // 已取得独占 runner，遗留 Running/Sending 只能来自崩溃，绝不自动重执行写工具或重复发微信。
        if (job.Status is "Running" or "Sending")
        {
            await FinishAsync(job, "Unknown", "处理进程曾中断，执行或发送结果不确定；未自动重试，请核对审计记录。");
            return;
        }
        try
        {
            var config = await RequireCurrentAsync(job, job.Kind is "Agent" or "Error" or "ExecutionUnknown");
            if (job.Status == "Pending" && await BotBudgetFullAsync(job.UserId))
            {
                await ReleaseQuotaAsync(job);
                var noticeExists = await Db.Queryable<WeChatClawBotAiJobEntity>()
                    .AnyAsync(x => x.UserId == userId && x.Day == job.Day && x.Kind == "Limit");
                if (noticeExists)
                {
                    await FinishAsync(job, "Cancelled", "机器人每日总发送预算已用完，当天保持静默。");
                    return;
                }
                job.Kind = "Limit";
                job.Reply = config.LimitMessage;
                await Db.Updateable<WeChatClawBotAiDayEntity>().SetColumns(x => x.LimitNoticeQueued == true)
                    .Where(x => x.UserId == userId && x.Day == job.Day).ExecuteCommandAsync();
                await SetStatusAsync(job, "ReplyPending");
            }
            if (job.Status == "Pending")
            {
                await SetStatusAsync(job, "Running");
                var inbound = await Db.Queryable<WeChatClawBotInboundMessageEntity>().InSingleAsync(job.InboundId)
                    ?? throw new InvalidOperationException("接收消息不存在");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                var confirmation = config.Mode == "Work" && WeChatClawBotAiPolicy.IsConfirmation(inbound.Content);
                var previous = await Db.Queryable<WeChatClawBotAiJobEntity>().Where(x => x.UserId == userId &&
                        x.Version == job.Version && (x.Kind == "Agent" || x.Kind == "Error" || x.Kind == "ExecutionUnknown") && x.Id < job.Id)
                    .OrderByDescending(x => x.Id).FirstAsync();
                var candidates = config.Mode == "Work" ? await GetWorkCandidatesAsync(config) : [];
                var selectedAgent = config.Mode == "Chat" ? AgentCodes.Chat : confirmation
                    ? previous?.AgentCode ?? config.ActiveAgentCode ?? candidates[0].AgentCode
                    : await WeChatClawBotAiAgentRouting.SelectAsync(userId, inbound.Content, candidates,
                        config.ActiveAgentCode, routingModel, timeout.Token);
                if (selectedAgent == null)
                    throw new WeChatClawBotAgentClarificationException("请明确要使用的 Agent，或将跨业务问题分步提出。可发送 @Agent编码 问题：\n" +
                        string.Join("\n", candidates.Select(x => $"@{x.AgentCode}（{x.Name}）")));
                job.AgentCode = selectedAgent;
                var resultAccessHash = PushResultAccessFingerprint.Create(
                    await applications.GetChannelAccessAsync(userId, config.AppCode));
                var conversations = WeChatClawBotAiAgentRouting.GetConversations(config);
                var allowConfirmation = confirmation && previous?.Status == "Sent" && previous.RequiresConfirmation &&
                    previous.UpdateTimeUtc > DateTime.UtcNow.AddMinutes(-10) &&
                    conversations.TryGetValue(selectedAgent, out var previousConversation) && previous.ConversationId == previousConversation;
                if (!confirmation && config.Mode == "Work" && conversations.Count > 0 &&
                    Db.DbMaintenance.IsAnyTable("AgentExecutionPlan", false))
                {
                    var conversationIds = conversations.Values.Distinct().ToList();
                    await Db.Updateable<AgentExecutionPlanEntity>().SetColumns(x => x.Status == "Expired")
                        .Where(x => conversationIds.Contains(x.ConversationId) && x.UserId == userId && x.Status == "PendingConfirmation")
                        .ExecuteCommandAsync();
                }
                var previousResult = PushAgentCatalog.IsPushAgent(selectedAgent)
                    ? await GetPreviousResultAsync(config, job.Id)
                    : null;
                using var channel = new AgentChannelExecutionScope(async tool =>
                {
                    var current = await RequireCurrentAsync(job, true);
                    var access = await applications.GetChannelAccessAsync(userId, current.AppCode);
                    if (!WeChatClawBotAiAgentRouting.GetAllowed(current).Contains(selectedAgent, StringComparer.OrdinalIgnoreCase) ||
                        !CanUseAgent(access, selectedAgent)) return "没有当前系统 Agent 权限";
                    // 旧工具尚未实现资源级过滤；不能仅凭参数限定就返回全量数据。
                    if (!access.AllResources && tool.ServiceName is not ("ProcessBoard" or "WeChatPush" or "EmailPush"))
                        return "该业务工具尚未适配受限数据范围，已拒绝执行";
                    if (!string.IsNullOrWhiteSpace(tool.PermissionKey) && !access.HasPermission(tool.PermissionKey))
                        return "缺少当前系统工具权限：" + tool.PermissionKey;
                    if (!tool.AgentCode.Equals(selectedAgent, StringComparison.OrdinalIgnoreCase)) return "工具不属于本轮选定的 Agent";
                    if (tool.IsWriteAction && (tool.ToolName.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                                              tool.ToolName.Contains("remove", StringComparison.OrdinalIgnoreCase)))
                        return "微信渠道不开放删除工具";
                    return null;
                }, timeout.Token, allowConfirmation, async () =>
                {
                    var current = await RequireCurrentAsync(job, true);
                    return await applications.GetChannelAccessAsync(userId, current.AppCode);
                }, previousResult);
                if (!conversations.TryGetValue(selectedAgent, out var conversationId))
                {
                    var conversation = await agent.CreateConversationAsync(new AgentStartRequest
                    {
                        UserId = userId, AgentCode = selectedAgent, Mode = config.Mode,
                        Title = "微信 · " + config.AppCode + " · " + selectedAgent
                    });
                    conversationId = conversation.Id;
                    conversations[selectedAgent] = conversationId;
                }
                var conversationsJson = JsonSerializer.Serialize(conversations);
                var changed = await Db.Updateable<WeChatClawBotAiConnectionEntity>()
                    .SetColumns(x => new WeChatClawBotAiConnectionEntity
                    {
                        ConversationId = conversationId, ActiveAgentCode = selectedAgent,
                        AgentConversationsJson = conversationsJson
                    }).Where(x => x.UserId == userId && x.Version == job.Version).ExecuteCommandAsync();
                if (changed == 0) throw new UnauthorizedAccessException("连接配置已变更");
                job.ConversationId = conversationId;
                await SaveJobAsync(job);
                var response = confirmation && !allowConfirmation ? new AgentResponseDto
                {
                    ConversationId = conversationId,
                    Reply = "当前没有已送达且仍有效的待确认计划，请重新提出操作请求。"
                } : await agent.SendMessageAsync(new AgentSendRequest
                {
                    UserId = userId, ConversationId = conversationId,
                    AgentCode = selectedAgent, Mode = config.Mode, Message = inbound.Content
                });
                timeout.Token.ThrowIfCancellationRequested();
                await RequireCurrentAsync(job, true);
                job.Reply = string.IsNullOrWhiteSpace(response.Reply) ? "本次未生成有效回复，请稍后重试。" : response.Reply;
                job.RequiresConfirmation = response.RequiresConfirmation;
                var latestAccessHash = PushResultAccessFingerprint.Create(
                    await applications.GetChannelAccessAsync(userId, config.AppCode));
                job.ResultAccessHash = resultAccessHash == latestAccessHash ? resultAccessHash : null;
                if (response.RequiresConfirmation)
                    job.Reply += "\n请在十分钟内回复“确认”执行以上操作。";
                if (job.Reply.Length > 10000) job.Reply = job.Reply[..9900] + "\n内容过长，完整结果请在后台 Agent 会话查看。";
                await SetStatusAsync(job, "ReplyPending");
            }
            await RequireCurrentAsync(job, job.Kind is "Agent" or "Error" or "ExecutionUnknown");
            await SetStatusAsync(job, "Sending");
            var delivery = await sender.SendAiMessageAsync(userId, job.Reply!, "wxai:" + job.Id,
                job.BindingIdentity, cancellationToken);
            job.DeliveryId = delivery.Id;
            // 发送层在真正发出之前再次校验授权，不能在节流等待时撤权后仍发出。
            var requiresReview = job.Kind == "ExecutionUnknown";
            var finalStatus = requiresReview || delivery.Status == "Unknown"
                ? "Unknown"
                : delivery.Status == "Sent" ? "Sent" : "Failed";
            var finalError = requiresReview ? job.Error : null;
            if (delivery.Status != "Sent")
                finalError += "微信回复失败，请查看关联投递记录。";
            await FinishAsync(job, finalStatus, finalError);
        }
        catch (WeChatClawBotAgentClarificationException exception)
        {
            job.Kind = "Error";
            job.Reply = exception.Message;
            job.Error = "未能在允许列表中明确选择 Agent，未执行业务工具。";
            await SetStatusAsync(job, "ReplyPending");
        }
        catch (AgentChannelExecutionException exception)
        {
            job.Reply = exception.UserFeedback;
            job.RequiresConfirmation = false;
            if (!exception.HasToolSideEffects)
            {
                if (!exception.HasToolCalls) await ReleaseQuotaAsync(job);
                job.Kind = "Error";
                job.Error = exception.HasToolCalls
                    ? "业务查询已尝试，模型未生成完整回复，已安排失败提示。"
                    : "模型调用失败，未执行业务工具。";
            }
            else
            {
                // 只排队发送告知，不重跑 Agent；提示送达后仍保留“结果待核对”。
                job.Kind = "ExecutionUnknown";
                job.Error = "模型未生成完整回复，写操作结果需核对，不自动重试。";
            }
            await SetStatusAsync(job, "ReplyPending");
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(job, "Unknown", "处理已取消或超时，请核对工具和发送记录；未自动重试。");
        }
        catch (UnauthorizedAccessException)
        {
            await ReleaseQuotaAsync(job);
            await FinishAsync(job, "Cancelled", "连接授权、绑定或用户权限已失效。");
        }
        catch (Exception)
        {
            // 模型/协议异常正文可能含敏感信息，不复制到微信或普通任务日志。
            if (job.Status == "Pending") await ReleaseQuotaAsync(job);
            await FinishAsync(job, job.Status is "Running" or "Sending" ? "Unknown" : "Failed",
                "处理失败，请查看模型调用、工具执行或微信投递记录；不自动重执行业务操作。");
        }
    }

    private async Task<WeChatClawBotAiConnectionEntity> RequireCurrentAsync(WeChatClawBotAiJobEntity job, bool requireAuthorized)
    {
        var config = await GetConnectionAsync(job.UserId);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == job.UserId);
        if (!config.Enabled || config.Version != job.Version || account?.Status != "Bound" || account.AdminDisabled ||
            WeChatClawBotBindingIdentity.Create(account) != job.BindingIdentity ||
            job.Day != WeChatClawBotAiPolicy.Day(DateTime.UtcNow) || requireAuthorized && !IsAuthorized(config, account))
            throw new UnauthorizedAccessException("微信连接授权已失效");
        var access = await applications.GetChannelAccessAsync(job.UserId, config.AppCode);
        var allowed = WeChatClawBotAiAgentRouting.GetAllowed(config);
        if (!CanUseChannel(access) || config.Mode == "Work" &&
            (job.AgentCode == null ? !allowed.Any(code => CanUseAgent(access, code)) :
                !allowed.Contains(job.AgentCode, StringComparer.OrdinalIgnoreCase) || !CanUseAgent(access, job.AgentCode)))
            throw new UnauthorizedAccessException("当前系统权限已撤销");
        if (config.Mode == "Work" && job.AgentCode != null && (await registry.GetAgentAsync(job.AgentCode))?.Enabled != true)
            throw new UnauthorizedAccessException("本轮 Agent 已停用");
        return config;
    }

    private async Task<List<AgentDefinitionEntity>> GetWorkCandidatesAsync(WeChatClawBotAiConnectionEntity config)
    {
        var access = await applications.GetChannelAccessAsync(config.UserId, config.AppCode);
        var result = new List<AgentDefinitionEntity>();
        foreach (var code in WeChatClawBotAiAgentRouting.GetAllowed(config))
        {
            if (!CanUseAgent(access, code)) continue;
            var definition = await registry.GetAgentAsync(code);
            if (definition?.Enabled == true) result.Add(definition);
        }
        if (result.Count == 0) throw new UnauthorizedAccessException("没有可用的工作 Agent");
        return result;
    }

    public async Task ReserveSendAsync(WeChatClawBotAccountEntity account, bool reserveNotice, CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await EnsureAsync();
        var botId = account.BotId;
        var owners = await Db.Queryable<WeChatClawBotAccountEntity>().Where(x => x.BotId == botId).Select(x => x.UserId).ToListAsync();
        var configurations = await Db.Queryable<WeChatClawBotAiConnectionEntity>()
            .Where(x => x.Enabled && owners.Contains(x.UserId)).ToListAsync();
        if (configurations.Count == 0) return;
        var limit = configurations.Where(x => x.BotDailyLimit > 0).Select(x => x.BotDailyLimit).DefaultIfEmpty(0).Min();
        var interval = configurations.Max(x => x.SendIntervalSeconds);
        var key = WeChatClawBotAiPolicy.Hash(botId ?? account.UserId);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = TimeSpan.FromSeconds(1);
            await using (var gate = await WeChatClawBotAiLock.TryEnterAsync(Db, "ClawBotAi:send:" + key, 1000))
            {
                if (gate != null)
                {
                    var now = DateTime.UtcNow;
                    var day = WeChatClawBotAiPolicy.Day(now);
                    var budget = await Db.Queryable<WeChatClawBotSendBudgetEntity>().InSingleAsync(key);
                    if (budget == null)
                    {
                        budget = new WeChatClawBotSendBudgetEntity { BotKey = key, Day = day };
                        await Db.Insertable(budget).ExecuteCommandAsync();
                    }
                    if (budget.Day != day) { budget.Day = day; budget.Used = 0; }
                    // 普通消息预留一次机器人级限额提示预算；所有来源都参与此预算。
                    if (limit > 0 && budget.Used >= limit - (reserveNotice ? 1 : 0))
                        throw new InvalidOperationException("机器人今日发送预算已用完");
                    if (budget.NextSendUtc <= now)
                    {
                        budget.Used++;
                        budget.NextSendUtc = now.AddSeconds(interval);
                        await Db.Updateable(budget).ExecuteCommandAsync();
                        return;
                    }
                    wait = budget.NextSendUtc - now;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 100, 1000)), cancellationToken);
        }
    }

    private async Task<bool> BotBudgetFullAsync(string userId)
    {
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
        if (account == null) return true;
        var botId = account.BotId;
        var owners = await Db.Queryable<WeChatClawBotAccountEntity>().Where(x => x.BotId == botId).Select(x => x.UserId).ToListAsync();
        var limits = await Db.Queryable<WeChatClawBotAiConnectionEntity>()
            .Where(x => x.Enabled && x.BotDailyLimit > 0 && owners.Contains(x.UserId)).Select(x => x.BotDailyLimit).ToListAsync();
        if (limits.Count == 0) return false;
        var key = WeChatClawBotAiPolicy.Hash(botId ?? userId);
        var budget = await Db.Queryable<WeChatClawBotSendBudgetEntity>().InSingleAsync(key);
        return budget?.Day == WeChatClawBotAiPolicy.Day(DateTime.UtcNow) && budget.Used >= limits.Min() - 1;
    }

    private async Task ReleaseQuotaAsync(WeChatClawBotAiJobEntity job)
    {
        if (!job.QuotaReserved) return;
        await using var gate = await StateLockAsync(job.UserId);
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            var changed = await Db.Updateable<WeChatClawBotAiJobEntity>()
                .SetColumns(x => x.QuotaReserved == false).Where(x => x.Id == job.Id && x.QuotaReserved).ExecuteCommandAsync();
            if (changed == 1)
                await Db.Updateable<WeChatClawBotAiDayEntity>().SetColumns(x => x.Used == x.Used - 1)
                    .Where(x => x.UserId == job.UserId && x.Day == job.Day && x.Used > 0).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("释放对话额度失败");
        job.QuotaReserved = false;
    }

    private async Task SetStatusAsync(WeChatClawBotAiJobEntity job, string status)
    {
        job.Status = status;
        await SaveJobAsync(job);
    }

    private async Task FinishAsync(WeChatClawBotAiJobEntity job, string status, string? error)
    {
        job.Error = error;
        await SetStatusAsync(job, status);
    }

    private Task<int> SaveJobAsync(WeChatClawBotAiJobEntity job)
    {
        job.UpdateTimeUtc = DateTime.UtcNow;
        return Db.Updateable(job).ExecuteCommandAsync();
    }

    private async Task<IAsyncDisposable> StateLockAsync(string userId) =>
        await WeChatClawBotAiLock.TryEnterAsync(Db, "ClawBotAi:state:" + userId, 5000)
        ?? throw new InvalidOperationException("微信连接配置正在处理，请稍后重试");

    public async Task<bool> ValidateReplyAsync(string userId, string idempotencyKey)
    {
        var id = long.Parse(idempotencyKey[5..], System.Globalization.CultureInfo.InvariantCulture);
        var job = await Db.Queryable<WeChatClawBotAiJobEntity>().InSingleAsync(id);
        if (job == null || job.UserId != userId || job.Status != "Sending")
            throw new UnauthorizedAccessException("回复任务不存在或已终止");
        await RequireCurrentAsync(job, job.Kind is "Agent" or "Error" or "ExecutionUnknown");
        return job.Kind == "Limit";
    }

    private static bool IsAuthorized(WeChatClawBotAiConnectionEntity config, WeChatClawBotAccountEntity? account) =>
        account?.Status == "Bound" && !account.AdminDisabled && config.Enabled &&
        config.AuthorizedUntilUtc > DateTime.UtcNow && (config.LockedUntilUtc == null || config.LockedUntilUtc <= DateTime.UtcNow) &&
        config.BindingIdentity == WeChatClawBotBindingIdentity.Create(account);

    private static bool CanUseChannel(ApplicationAccessContext access) =>
        access.HasPermission("WeChatClawBot") || access.HasPermission("WeChatClawBotManagement");

    internal static bool CanUseAgent(ApplicationAccessContext access, string agentCode)
    {
        if (agentCode == AgentCodes.WeChatPush) return access.HasPermission("WeChatClawBot");
        if (agentCode == AgentCodes.EmailPush) return access.HasPermission("EmailPush");
        if (access.HasPermission("Agent") || access.HasPermission("AgentExecutor")) return true;
        return agentCode switch
        {
            AgentCodes.QueryAssistant => access.HasPermission("AgentAssistant"),
            AgentCodes.EdgeGateway => access.HasPermission("EdgeGatewayAgent"),
            AgentCodes.ProductionDashboard => access.HasPermission("ProductionDashboardAgent"),
            AgentCodes.ProcessBoard => access.HasPermission("ProcessBoardAgent"),
            _ => false
        };
    }

    private async Task<AgentChannelResultDraft?> GetPreviousResultAsync(WeChatClawBotAiConnectionEntity config, long currentJobId)
    {
        var earliest = DateTime.UtcNow.AddMinutes(-30);
        var row = await Db.Queryable<WeChatClawBotAiJobEntity>()
            .Where(x => x.UserId == config.UserId && x.Version == config.Version && x.Id < currentJobId &&
                x.Kind == "Agent" && x.Status == "Sent" && !x.RequiresConfirmation && x.UpdateTimeUtc >= earliest &&
                x.AgentCode != AgentCodes.Chat && x.AgentCode != AgentCodes.WeChatPush && x.AgentCode != AgentCodes.EmailPush)
            .OrderByDescending(x => x.Id).FirstAsync();
        if (row?.AgentCode == null || string.IsNullOrWhiteSpace(row.Reply)) return null;
        var access = await applications.GetChannelAccessAsync(config.UserId, config.AppCode);
        if (!CanUseAgent(access, row.AgentCode)) return null;
        if (!WeChatClawBotAiAgentRouting.GetAllowed(config).Contains(row.AgentCode, StringComparer.OrdinalIgnoreCase)) return null;
        if (!access.AllResources && row.AgentCode != AgentCodes.ProcessBoard) return null;
        var accessHash = PushResultAccessFingerprint.Create(access);
        if (row.ResultAccessHash != accessHash) return null;
        return new AgentChannelResultDraft(row.AgentCode, row.Id, row.Reply, accessHash);
    }

    private static void Revoke(WeChatClawBotAiConnectionEntity config)
    {
        config.Version = Guid.NewGuid().ToString("N");
        config.AuthorizedUntilUtc = null;
        config.BindingIdentity = null;
        config.ConversationId = null;
        config.AgentConversationsJson = null;
        config.ActiveAgentCode = null;
    }

    private static WeChatClawBotAiSaveDto ToConfiguration(WeChatClawBotAiConnectionEntity config) => new()
    {
        Enabled = config.Enabled, AppCode = config.AppCode, Mode = config.Mode, AgentCode = config.AgentCode,
        AgentCodes = config.Mode == "Work" ? WeChatClawBotAiAgentRouting.GetAllowed(config) : [],
        ValidHours = config.ValidHours, DailyLimit = config.DailyLimit, BotDailyLimit = config.BotDailyLimit,
        SendIntervalSeconds = config.SendIntervalSeconds, TimeoutSeconds = config.TimeoutSeconds, LimitMessage = config.LimitMessage
    };
}
