using System.Reflection;
using System.Text.Json;
using AutoMapper;
using calmadmin.Config;
using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Text;
using Model.Agent;
using Model.Other;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using Service;
using SqlSugar;
using 上传MES.Config;

if (args.Length != 1) throw new ArgumentException("请提供后端根目录；测试仅创建随机隔离数据库，不发送真实微信或模型请求");
await ReplyFeedbackSmoke.RunAsync();
using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(args[0]), "calmadmin", "appsettings.json")));
var configured = configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var database = "CodexWeChatAi_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
async Task Master(string command)
{
    await using var sql = master.CreateCommand();
    sql.CommandText = command;
    await sql.ExecuteNonQueryAsync();
}
await Master($"CREATE DATABASE [{database}]");
SqlSugarScope? scope = null;
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true,
        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = database }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    WeChatClawBotDatabaseInitializer.Initialize(db);
    await db.Insertable(new WeChatClawBotAccountEntity { UserId = "u1", UserName = "user1" }).ExecuteCommandAsync();
    WeChatClawBotDatabaseInitializer.Initialize(db);
    Check((await db.Queryable<WeChatClawBotAccountEntity>().FirstAsync()).LastOnlineTime == null,
        "fresh insert and repeat initialization preserve null not-yet-online timestamps");
    // 模拟已上线的旧表/已有行：新功能表独立建立，旧业务数据及主键不改变。
    db.DbMaintenance.DropTable<WeChatClawBotAiConnectionEntity, WeChatClawBotAiDayEntity, WeChatClawBotAiJobEntity, WeChatClawBotSendBudgetEntity>();
    WeChatClawBotDatabaseInitializer.Initialize(db);
    WeChatClawBotDatabaseInitializer.Initialize(db);
    Check(await db.Queryable<WeChatClawBotAccountEntity>().CountAsync() == 1, "populated legacy installation upgrades twice without changing account data");
    await db.Insertable(new WeChatClawBotAiConnectionEntity { UserId = "nullable-fixture" }).ExecuteCommandAsync();
    Check((await db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync("nullable-fixture")).AuthorizedUntilUtc == null,
        "real SQL insert accepts nullable secret, authorization, lock and conversation fields");
    await db.Insertable(new WeChatClawBotAiConnectionEntity
    { UserId = "legacy-work", Mode = "Work", AgentCode = "test", ConversationId = 6064 }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotAiJobEntity
    { UserId = "legacy-work", InboundId = -17, Version = "fixture", BindingIdentity = "fixture", Day = "20000101", Status = "Sent" }).ExecuteCommandAsync();
    // 仅在随机隔离数据库回退为旧结构，验证非空表新增 nullable 列及重复升级。
    db.Ado.ExecuteCommand("ALTER TABLE WeChatClawBotAiConnection DROP COLUMN AllowedAgentCodesJson,AgentConversationsJson,ActiveAgentCode");
    db.Ado.ExecuteCommand("ALTER TABLE WeChatClawBotAiJob DROP COLUMN AgentCode");
    WeChatClawBotDatabaseInitializer.Initialize(db);
    WeChatClawBotDatabaseInitializer.Initialize(db);
    var legacyWork = await db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync("legacy-work");
    Check(legacyWork.AllowedAgentCodesJson == null && WeChatClawBotAiAgentRouting.GetAllowed(legacyWork).SequenceEqual(["test"]) &&
        WeChatClawBotAiAgentRouting.GetConversations(legacyWork)["test"] == 6064,
        "populated single-agent configuration upgrades twice and retains original permissions and conversation");
    Check(db.DbMaintenance.GetColumnInfosByTableName("WeChatClawBotAiConnection", false)
        .Where(x => x.DbColumnName is "AllowedAgentCodesJson" or "AgentConversationsJson" or "ActiveAgentCode").All(x => x.IsNullable) &&
        (await db.Queryable<WeChatClawBotAiJobEntity>().FirstAsync(x => x.InboundId == -17)).AgentCode == null,
        "new optional configuration and audit fields support existing SQL rows without fake defaults");
    db.CodeFirst.InitTables<SysUserInfo, SysRole, SysApplicationEntity, SysUserApplicationEntity>();
    db.CodeFirst.InitTables<SysPlatformAdministratorEntity>();
    await db.Insertable(new[] { "u1", "u2" }.Select(user => new SysUserInfo
    {
        UserId = user, UserName = user, Password = "fixture", Enabled = true, CreateTime = DateTime.Now,
        PermissionKeys = "*"
    }).ToList()).ExecuteCommandAsync();
    await db.Insertable(new[] { "A", "B" }.Select(app => new SysApplicationEntity
    {
        AppCode = app, DisplayName = app == "B" ? "工艺智能管理系统" : "智能制造管理系统",
        HomePath = "/", UpdateTime = DateTime.Now
    }).ToList()).ExecuteCommandAsync();
    await db.Insertable(new[] { "u1", "u2" }.SelectMany(user => new[] { "A", "B" }.Select(app => new SysUserApplicationEntity
    {
        UserId = user, AppCode = app, PermissionsJson = "[\"WeChatClawBot\",\"Agent\",\"tool.read\"]",
        AllResources = app == "A", UpdateTime = DateTime.Now
    })).ToList()).ExecuteCommandAsync();
    var applications = new SystemApplicationService(scope, new ConstantOptions<JWTTokenOptions>(new()));
    var channelAccess = await applications.GetChannelAccessAsync("u1", "B");
    Check(!channelAccess.AllResources && !channelAccess.HasPermission("*") && channelAccess.HasPermission("Agent"),
        "background access reads selected system grant, not global user wildcard and not a forged web JWT");
    var registry = Proxy.Make<IAgentToolRegistryService>((method, values) => method.Name switch
    {
        "GetAgentAsync" => Task.FromResult<AgentDefinitionEntity?>(new() { AgentCode = (string)values[0]!, Name = "测试 Agent" }),
        "GetAllAgentsAsync" => Task.FromResult(new List<AgentDefinitionEntity> { new() { AgentCode = "chat", Name = "聊天" }, new() { AgentCode = "test", Name = "测试" } }),
        _ => throw new NotSupportedException(method.Name)
    });
    var service = new WeChatClawBotAiService(scope, applications, registry);
    var account = await db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == "u1");
    account.Status = "Bound";
    account.BotId = "fixture-bot";
    account.ILinkUserId = "fixture-sender";
    account.BotToken = "fixture-token-never-sent";
    await db.Updateable(account).ExecuteCommandAsync();
    var second = new WeChatClawBotAccountEntity { UserId = "u2", UserName = "u2", Status = "Bound", BotId = "fixture-bot2", ILinkUserId = "sender2" };
    second.Id = await db.Insertable(second).ExecuteReturnBigIdentityAsync();
    var settings = new WeChatClawBotAiSaveDto { Enabled = true, DailyLimit = 2 };
    await service.SaveAsync("u1", settings);
    var secret = await service.ResetSecretAsync("u1");
    Check(secret.Length == 69 && WeChatClawBotAiPolicy.Verify(secret,
        (await db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync("u1")).SecretHash), "generated high-entropy key verifies against hash only");
    Check(!JsonSerializer.Serialize(await service.GetStateAsync("u1")).Contains(secret), "state endpoint never returns secret or stored hash");
    async Task Ingest(string text, string type = "Text", string user = "u1", string? key = null)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var connection = DatabaseOperationScope.GetConnection(scope, "calmadmin");
        var transaction = await connection.Ado.UseTranAsync(async () => await service.HandleInboundAsync(
            user == "u1" ? account : second, new WeChatClawBotInboundMessageEntity
            {
                UserId = user, SenderId = user == "u1" ? account.ILinkUserId! : second.ILinkUserId!,
                MessageKey = key ?? Guid.NewGuid().ToString("N"), Content = text, MessageType = type
            }));
        if (!transaction.IsSuccess) throw transaction.ErrorException;
    }
    await Ingest("查询产量");
    Check((await service.GetJobsAsync("u1", 1, 20)).All(x => x.Kind == "Control") && (await service.GetStateAsync("u1")).Used == 0,
        "unverified text never reaches model or consumes conversation quota");
    for (var i = 0; i < 5; i++) await Ingest("连接 wxai_" + new string('A', 64));
    Check((await service.GetStateAsync("u1")).State == "Locked", "five invalid keys lock verification for fifteen minutes");
    await Ingest("连接 " + secret);
    Check((await service.GetStateAsync("u1")).State == "Locked", "correct key cannot bypass active lock");
    secret = await service.ResetSecretAsync("u1");
    await Ingest("连接 " + secret);
    Check((await service.GetStateAsync("u1")).State == "Authorized", "matching sender and key authorize connection");
    Check((await db.Queryable<WeChatClawBotInboundMessageEntity>().ToListAsync()).All(x => !x.Content.Contains("wxai_")),
        "all valid and invalid key messages are redacted before persistence");
    var keys = (await db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync("u1")).SecretHash;
    await service.SaveAsync("u2", settings);
    await service.ResetSecretAsync("u2");
    await Ingest("连接 " + secret, user: "u2");
    Check((await service.GetStateAsync("u2")).State != "Authorized", "another user's key cannot authorize a different binding");
    await Ingest("图片", "Image");
    Check((await service.GetStateAsync("u1")).Used == 0, "unsupported media never invokes agent or consumes dialogue quota");
    var before = await db.Queryable<WeChatClawBotInboundMessageEntity>().CountAsync();
    await Ingest("first", key: "dedup-fixture");
    await ExpectRejected(() => Ingest("duplicate", key: "dedup-fixture"), "unique inbound key rolls back duplicate work and counters");
    Check(await db.Queryable<WeChatClawBotInboundMessageEntity>().CountAsync() == before + 1, "duplicate transaction inserts no second inbound row");
    await Ingest("second");
    Check((await service.GetStateAsync("u1")).Used == 2, "quota is reserved while enqueueing, before model execution");
    await Ingest("third");
    await Ingest("fourth");
    Check((await service.GetJobsAsync("u1", 1, 100)).Count(x => x.Kind == "Limit") == 1,
        "over-limit input queues only one daily notice then remains silent");
    var restart = new WeChatClawBotAiService(scope, applications, registry);
    var pendingUsers = await restart.GetPendingUsersAsync();
    Check(pendingUsers.Contains("u1") && pendingUsers.Contains("u2"), "background discovery groups pending jobs by user without large-user backlog hiding other accounts");
    Check((await restart.GetStateAsync("u1")).Used == 2, "new service instance cannot reset persisted daily quota");
    var modelCalls = 0;
    var sent = 0;
    var agent = Proxy.Make<IAgentService>((method, values) =>
    {
        if (method.Name == "CreateConversationAsync") return Task.FromResult(new AgentConversationDto { Id = 101 });
        if (method.Name == "SendMessageAsync")
        {
            Check(AgentChannelExecutionScope.Current != null, "background model invocation has explicit channel execution context");
            modelCalls++;
            return Task.FromResult(new AgentResponseDto { ConversationId = 101, Reply = "测试结果" });
        }
        throw new NotSupportedException(method.Name);
    });
    var sender = Proxy.Make<IWeChatClawBotService>((method, values) =>
    {
        if (method.Name != "SendAiMessageAsync") throw new NotSupportedException(method.Name);
        sent++;
        return Task.FromResult(new WeChatClawBotDeliveryDto { Id = sent, Status = "Sent" });
    });
    for (var i = 0; i < 12; i++) await restart.ProcessUserAsync("u1", agent, sender, default);
    Check(modelCalls == 2 && (await restart.GetJobsAsync("u1", 1, 100)).Where(x => x.Kind == "Agent").All(x => x.Status == "Sent"),
        "queued text drives agent and reply, controls and limit notices never invoke model");
    Check((await restart.GetStateAsync("u2")).Used == 0 && (await restart.GetJobsAsync("u2", 1, 100)).All(x => x.UserId == "u2"),
        "usage, conversation and jobs are isolated per backend user");
    Check(WeChatClawBotAiPolicy.Day(new DateTime(2026, 10, 16, 15, 59, 59, DateTimeKind.Utc)) == "20261016" &&
        WeChatClawBotAiPolicy.Day(new DateTime(2026, 10, 16, 16, 0, 0, DateTimeKind.Utc)) == "20261017", "daily partition boundary is Beijing midnight independent of server timezone");
    var today = WeChatClawBotAiPolicy.Day(DateTime.UtcNow);
    await db.Ado.ExecuteCommandAsync("UPDATE WeChatClawBotAiDay SET Day=@previous WHERE UserId=@user AND Day=@today",
        new { previous = "20000101", user = "u1", today });
    Check((await restart.GetStateAsync("u1")).Used == 0, "a new Beijing date has fresh quota without deleting history");
    await Ingest("new-day");
    await restart.DisconnectAsync("u1");
    await restart.ProcessUserAsync("u1", agent, sender, default);
    Check((await restart.GetJobsAsync("u1", 1, 1))[0].Status == "Cancelled" && (await restart.GetStateAsync("u1")).Used == 0,
        "disconnect cancels queued work before execution and releases reserved quota");
    secret = await restart.ResetSecretAsync("u1");
    await Ingest("连接 " + secret);
    for (var i = 0; i < 3; i++) await restart.ProcessUserAsync("u1", agent, sender, default);
    await Ingest("crash-fixture");
    var last = (await restart.GetJobsAsync("u1", 1, 1))[0];
    last.Status = "Running";
    await db.Updateable(last).ExecuteCommandAsync();
    var callsBeforeCrash = modelCalls;
    await restart.ProcessUserAsync("u1", agent, sender, default);
    Check((await restart.GetJobsAsync("u1", 1, 1))[0].Status == "Unknown" && modelCalls == callsBeforeCrash,
        "crashed running task is marked uncertain rather than re-executing potential writes");
    await Ingest("after-expiry");
    await db.Updateable<WeChatClawBotAiConnectionEntity>().SetColumns(x => x.AuthorizedUntilUtc == DateTime.UtcNow.AddMinutes(-1))
        .Where(x => x.UserId == "u1").ExecuteCommandAsync();
    await restart.ProcessUserAsync("u1", agent, sender, default);
    Check(modelCalls == callsBeforeCrash, "authorization expiry blocks already queued requests");
    // 工作模式显式系统权限：A中可用，B中受限；用户全局 * 不能绕过 B 数据范围。
    settings.AppCode = "B"; settings.Mode = "Work"; settings.AgentCode = "test"; settings.DailyLimit = 10;
    await restart.SaveAsync("u1", settings);
    secret = await restart.ResetSecretAsync("u1");
    await Ingest("连接 " + secret);
    await restart.ProcessUserAsync("u1", agent, sender, default);
    await Ingest("work query");
    string? permissionError = null;
    var workAgent = Proxy.Make<IAgentService>(asyncHandler);
    object? asyncHandler(MethodInfo method, object?[] values)
    {
        if (method.Name == "CreateConversationAsync") return Task.FromResult(new AgentConversationDto { Id = 202 });
        if (method.Name == "SendMessageAsync") return Work();
        throw new NotSupportedException(method.Name);
    }
    async Task<AgentResponseDto> Work()
    {
        permissionError = await AgentChannelExecutionScope.Current!.AuthorizeToolAsync(new AgentToolRegistryEntity
        { AgentCode = "test", ToolName = "test.read", PermissionKey = "tool.read" });
        return new AgentResponseDto { Reply = "已拒绝越权查询", ConversationId = 202 };
    }
    await restart.ProcessUserAsync("u1", workAgent, sender, default);
    Check(permissionError?.Contains("受限数据范围") == true, "legacy full-data tools fail closed for resource-scoped system users");
    await Ingest("revoked query");
    await db.Updateable<SysUserApplicationEntity>().SetColumns(x => x.Enabled == false)
        .Where(x => x.UserId == "u1" && x.AppCode == "B").ExecuteCommandAsync();
    await restart.ProcessUserAsync("u1", workAgent, sender, default);
    Check((await restart.GetJobsAsync("u1", 1, 1))[0].Status == "Cancelled", "application grant revocation is rechecked before queued execution");
    await db.Updateable<SysUserApplicationEntity>().SetColumns(x => x.Enabled == true)
        .Where(x => x.UserId == "u1" && x.AppCode == "B").ExecuteCommandAsync();
    settings.AppCode = "A"; settings.BotDailyLimit = 3; settings.SendIntervalSeconds = 1;
    await restart.SaveAsync("u1", settings);
    var botKey = WeChatClawBotAiPolicy.Hash(account.BotId!);
    await restart.ReserveSendAsync(account, true, default);
    await db.Updateable<WeChatClawBotSendBudgetEntity>().SetColumns(x => x.NextSendUtc == DateTime.UtcNow.AddSeconds(-1)).Where(x => x.BotKey == botKey).ExecuteCommandAsync();
    await restart.ReserveSendAsync(account, true, default);
    await ExpectRejected(() => restart.ReserveSendAsync(account, true, default), "normal messages cannot consume the final reserved robot notice slot");
    await db.Updateable<WeChatClawBotSendBudgetEntity>().SetColumns(x => x.NextSendUtc == DateTime.UtcNow.AddSeconds(-1)).Where(x => x.BotKey == botKey).ExecuteCommandAsync();
    await restart.ReserveSendAsync(account, false, default);
    await ExpectRejected(() => restart.ReserveSendAsync(account, false, default), "robot total budget includes controls, replies and notice and cannot exceed limit");
    await Ingest("连接 " + secret);
    // 密钥已在前面配置更新时撤销，本段只验证机器人预算耗尽不能引起额外模型调用。
    var callsBeforeBudget = modelCalls;
    for (var i = 0; i < 3; i++) await restart.ProcessUserAsync("u1", agent, sender, default);
    Check(modelCalls == callsBeforeBudget, "budget and connection controls never invoke model");
    await using (var lease = await WeChatClawBotAiLock.TryEnterAsync(db, "ClawBotAi:runner:fixture"))
        Check(await WeChatClawBotAiLock.TryEnterAsync(db, "ClawBotAi:runner:fixture") == null,
            "independent SQL sessions cannot concurrently process the same user");
    // 实际工具调度器：即使注册表关闭确认，微信渠道也必须强制写操作确认及十分钟过期。
    db.CodeFirst.InitTables<AgentExecutionPlanEntity, AgentInvocationLogEntity>();
    var tool = new AgentToolRegistryEntity { Id = 1, AgentCode = "test", ToolName = "test.save", Title = "测试写入",
        ServiceName = "fixture", ActionName = "Save", IsWriteAction = true, RequiresConfirmation = false };
    var toolRegistry = Proxy.Make<IAgentToolRegistryService>((method, values) => method.Name == "GetToolAsync" ?
        Task.FromResult<AgentToolRegistryEntity?>(tool) : throw new NotSupportedException(method.Name));
    var mapper = new MapperConfiguration(config => config.AddProfile<AutoMapperConfigs>()).CreateMapper();
    var dispatcher = new AgentToolDispatcher(scope, mapper, toolRegistry, [], []);
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default))
    {
        var pending = await dispatcher.DispatchAsync(new AgentToolDispatchRequest
        { UserId = "u1", ConversationId = 99, AgentCode = "test", ToolName = "test.save", ArgumentsJson = "{}" });
        Check(pending.RequiresConfirmation, "channel enforces write confirmation even if tool registry disables it");
        await db.Updateable<AgentExecutionPlanEntity>().SetColumns(x => x.CreateTime == DateTime.Now.AddMinutes(-11))
            .Where(x => x.Id == pending.Id).ExecuteCommandAsync();
        var expired = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = "u1", ToolCallId = pending.Id });
        Check(expired != null && expired.Status == "Expired", "old confirmation plans cannot execute through WeChat");
    }
    Check(AgentChannelExecutionScope.Current == null, "channel identity is removed after invocation and cannot leak into another request");
    // 微信确认只承接上一轮已成功送达的计划，不接受裸确认或重复确认。
    var workSettings = new WeChatClawBotAiSaveDto { Enabled = true, Mode = "Work", AgentCode = "test", DailyLimit = 100 };
    await restart.SaveAsync("u2", workSettings);
    var secret2 = await restart.ResetSecretAsync("u2");
    await Ingest("连接 " + secret2, user: "u2");
    for (var i = 0; i < 5; i++) await restart.ProcessUserAsync("u2", agent, sender, default);
    var workCalls = 0;
    var confirmationFlags = new List<bool>();
    var confirmingAgent = Proxy.Make<IAgentService>((method, values) =>
    {
        if (method.Name == "CreateConversationAsync") return Task.FromResult(new AgentConversationDto { Id = 303 });
        if (method.Name != "SendMessageAsync") throw new NotSupportedException(method.Name);
        workCalls++;
        var isConfirmation = WeChatClawBotAiPolicy.IsConfirmation(((AgentSendRequest)values[0]!).Message);
        confirmationFlags.Add(AgentChannelExecutionScope.Current!.AllowConfirmation);
        return Task.FromResult(new AgentResponseDto { ConversationId = 303,
            Reply = isConfirmation ? "操作结果" : "待确认计划", RequiresConfirmation = !isConfirmation });
    });
    await Ingest("确认", user: "u2");
    await restart.ProcessUserAsync("u2", confirmingAgent, sender, default);
    Check(workCalls == 0, "bare confirmation without a delivered plan never enters Agent");
    await Ingest("新增设备", user: "u2");
    await restart.ProcessUserAsync("u2", confirmingAgent, sender, default);
    await Ingest("确认", user: "u2");
    await restart.ProcessUserAsync("u2", confirmingAgent, sender, default);
    await Ingest("确认", user: "u2");
    await restart.ProcessUserAsync("u2", confirmingAgent, sender, default);
    Check(workCalls == 2 && confirmationFlags.SequenceEqual([false, true]),
        "delivered latest plan allows one confirmation, duplicate confirmation cannot re-execute");
    await Ingest("查询数据", user: "u2");
    var usageBeforeFailure = (await restart.GetStateAsync("u2")).Used;
    var failingAgent = Proxy.Make<IAgentService>((method, values) =>
        method.Name == "SendMessageAsync" ? Task.FromException<AgentResponseDto>(new AgentChannelExecutionException(false)) :
            throw new NotSupportedException(method.Name));
    await restart.ProcessUserAsync("u2", failingAgent, sender, default);
    Check((await restart.GetStateAsync("u2")).Used == usageBeforeFailure - 1 &&
        (await restart.GetJobsAsync("u2", 1, 1))[0].Kind == "Error", "known model failure before any tool refunds quota and queues a sanitized failure reply");
    await restart.ProcessUserAsync("u2", failingAgent, sender, default);
    // 回复失败提示只进入发送队列；恢复处理绝不重新执行业务工具。
    foreach (var hasWriteEffects in new[] { false, true })
    {
        await Ingest("回复截断测试", user: "u2");
        var invocations = 0;
        var feedback = AgentReplyFailureFeedback.Build(AgentReplyFailureKind.OutputLimit,
            [new AgentToolCallDto { ToolName = "fixture", Status = "Succeeded" }], hasWriteEffects);
        var truncatedAgent = Proxy.Make<IAgentService>((method, values) =>
        {
            if (method.Name != "SendMessageAsync") throw new NotSupportedException(method.Name);
            invocations++;
            return Task.FromException<AgentResponseDto>(new AgentChannelExecutionException(hasWriteEffects, feedback, true));
        });
        var notifications = new List<string>();
        var feedbackSender = Proxy.Make<IWeChatClawBotService>((method, values) =>
        {
            if (method.Name != "SendAiMessageAsync") throw new NotSupportedException(method.Name);
            restart.ValidateReplyAsync("u2", (string)values[2]!).GetAwaiter().GetResult();
            notifications.Add((string)values[1]!);
            return Task.FromResult(new WeChatClawBotDeliveryDto { Id = 9000, Status = "Sent" });
        });
        await restart.ProcessUserAsync("u2", truncatedAgent, feedbackSender, default);
        var queuedNotice = (await restart.GetJobsAsync("u2", 1, 1))[0];
        Check(queuedNotice.Status == "ReplyPending" && !queuedNotice.RequiresConfirmation,
            "truncated reply queues one safe user notice, never an actionable confirmation");
        var recoveredService = new WeChatClawBotAiService(scope, applications, registry);
        await recoveredService.ProcessUserAsync("u2", truncatedAgent, feedbackSender, default);
        await recoveredService.ProcessUserAsync("u2", truncatedAgent, feedbackSender, default);
        var deliveredNotice = (await restart.GetJobsAsync("u2", 1, 1))[0];
        Check(invocations == 1 && notifications.Count == 1 && notifications[0].Contains("输出长度限制"),
            "restart sends failure notice once without calling the Agent or tools again");
        Check(hasWriteEffects
                ? deliveredNotice.Status == "Unknown" && deliveredNotice.Kind == "ExecutionUnknown" &&
                  notifications[0].Contains("不要重复提交") && deliveredNotice.DeliveryId == 9000
                : deliveredNotice.Status == "Sent" && deliveredNotice.Kind == "Error" && notifications[0].Contains("缩小查询范围"),
            "read-only failure suggests narrower query; writes retain review status even after notice delivery");
    }
    var beforeConcurrent = (await restart.GetStateAsync("u2")).Used;
    await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Ingest("parallel " + index, user: "u2")));
    Check((await restart.GetStateAsync("u2")).Used == beforeConcurrent + 8,
        "parallel incoming transactions reserve distinct quota slots without lost updates");
    // 完整生产适配链路：真实 getupdates 解析、入站事务、密钥脱敏、上下文、发送审计；仅替换网络传输。
    settings.Mode = "Chat"; settings.AgentCode = "chat"; settings.BotDailyLimit = 0;
    await restart.SaveAsync("u1", settings);
    secret = await restart.ResetSecretAsync("u1");
    for (var i = 0; i < 10; i++) await restart.ProcessUserAsync("u1", agent, sender, default);
    var protection = new EphemeralDataProtectionProvider();
    account.BotToken = protection.CreateProtector("CalmAdmin.WeChatClawBot.BotToken.v1").Protect("offline-fixture-token");
    await db.Updateable(account).ExecuteCommandAsync();
    var payload = Updates("bind-message", "连接 " + secret, "fixture-sender", "context-1");
    var transmitted = new List<string>();
    var factory = new OfflineHttpFactory(async request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("getupdates")) return payload;
        if (request.RequestUri.AbsolutePath.EndsWith("sendmessage"))
        {
            transmitted.Add(await request.Content!.ReadAsStringAsync());
            return "{\"ret\":0,\"message_id\":\"offline-reply\"}";
        }
        throw new InvalidOperationException("Unexpected offline endpoint");
    });
    var transport = new WeChatClawBotService(scope, factory, Options.Create(new WeChatClawBotOptions()), protection, restart);
    await transport.MaintainConversationsAsync();
    Check((await restart.GetStateAsync("u1")).State == "Authorized", "real receive parser routes matching sender into key verification");
    var rawSecretRows = await db.Queryable<WeChatClawBotInboundMessageEntity>().Where(x => x.Content.Contains(secret)).CountAsync();
    Check(rawSecretRows == 0, "real receive transaction never stores plaintext connection key");
    for (var i = 0; i < 3; i++) await restart.ProcessUserAsync("u1", agent, transport, default);
    Check(transmitted.Count == 1 && transmitted[0].Contains("context-1") && transmitted[0].Contains("fixture-sender"),
        "verification reply uses matched sender, real context token and offline send adapter");
    payload = Updates("chat-message", "查询当前数据", "fixture-sender", "context-2");
    await transport.MaintainConversationsAsync();
    await restart.ProcessUserAsync("u1", agent, transport, default);
    Check(transmitted.Count == 2 && transmitted[1].Contains("context-2"), "real adapter round-trip sends Agent result to refreshed WeChat conversation");
    var usageBeforeDuplicate = (await restart.GetStateAsync("u1")).Used;
    await transport.MaintainConversationsAsync();
    Check((await restart.GetStateAsync("u1")).Used == usageBeforeDuplicate,
        "duplicate remote message ID does not enqueue another Agent job or consume quota");
    payload = Updates("unknown-sender", "查询所有项目", "not-the-bound-user", "wrong-context");
    await transport.MaintainConversationsAsync();
    Check(!await db.Queryable<WeChatClawBotInboundMessageEntity>().AnyAsync(x => x.WeChatMessageId == "unknown-sender"),
        "unrecognized sender cannot enter the bound user's channel or replace reply context");
    Check((await db.Queryable<WeChatClawBotDeliveryEntity>().Where(x => x.Source == "AI").ToListAsync()).All(x => x.Status == "Sent"),
        "real sending audit stores AI source, task idempotency and delivery status");
    // 多选允许列表：真实队列与会话映射，路由模型及业务 Agent 均为离线替身。
    var routeChoice = "edge-gateway";
    var routingCalls = 0;
    var routingModel = Proxy.Make<IAgentModelClient>((method, values) =>
    {
        if (method.Name != "GenerateReplyAsync") throw new NotSupportedException(method.Name);
        var request = (AgentModelRequest)values[0]!;
        Check(request.AgentCode == AgentCodes.Chat && request.UserId == "u2", "routing uses a no-tool chat model and the real channel owner");
        routingCalls++;
        return Task.FromResult(new AgentModelReply { Success = true, Content = JsonSerializer.Serialize(new { agentCode = routeChoice }) });
    });
    var multiService = new WeChatClawBotAiService(scope, applications, registry, routingModel);
    var multiSettings = new WeChatClawBotAiSaveDto
    {
        Enabled = true, Mode = "Work", AgentCodes = ["test", "edge-gateway", "test"], DailyLimit = 100
    };
    await multiService.SaveAsync("u2", multiSettings);
    Check((await multiService.GetStateAsync("u2")).Configuration.AgentCodes!.SequenceEqual(["test", "edge-gateway"]),
        "multiple selection is persisted, deduplicated and returned as a full list");
    await ExpectRejected(() => multiService.SaveAsync("u2", new WeChatClawBotAiSaveDto
    { Mode = "Work", AgentCodes = [] }), "explicit empty work allowlist cannot silently fall back to a default Agent");
    await ExpectRejected(() => multiService.SaveAsync("u2", new WeChatClawBotAiSaveDto
    { Mode = "Work", AgentCodes = ["chat"] }), "chat Agent cannot be mixed into the work allowlist");
    await db.Updateable<SysUserApplicationEntity>().SetColumns(x => x.PermissionsJson == "[\"WeChatClawBot\",\"ProductionDashboardAgent\"]")
        .Where(x => x.UserId == "u2" && x.AppCode == "A").ExecuteCommandAsync();
    await ExpectRejected(() => multiService.SaveAsync("u2", new WeChatClawBotAiSaveDto
    { Mode = "Work", AgentCodes = ["production-dashboard", "edge-gateway"] }), "every selected Agent must have selected-system permission, not just the first item");
    await db.Updateable<SysUserApplicationEntity>().SetColumns(x => x.PermissionsJson == "[\"WeChatClawBot\",\"Agent\",\"tool.read\"]")
        .Where(x => x.UserId == "u2" && x.AppCode == "A").ExecuteCommandAsync();
    secret2 = await multiService.ResetSecretAsync("u2");
    await Ingest("连接 " + secret2, user: "u2");
    for (var index = 0; index < 20; index++) await multiService.ProcessUserAsync("u2", agent, sender, default);
    var selectedRequests = new List<AgentSendRequest>();
    var createdConversations = new Dictionary<string, int>();
    var nextConversation = 500;
    var planNext = false;
    var allowConfirmationFlags = new List<bool>();
    var selectedAgent = Proxy.Make<IAgentService>((method, values) =>
    {
        if (method.Name == "CreateConversationAsync")
        {
            var start = (AgentStartRequest)values[0]!;
            var id = ++nextConversation;
            createdConversations[start.AgentCode!] = id;
            return Task.FromResult(new AgentConversationDto { Id = id });
        }
        if (method.Name == "SendMessageAsync") return SelectedWork((AgentSendRequest)values[0]!);
        throw new NotSupportedException(method.Name);
    });
    async Task<AgentResponseDto> SelectedWork(AgentSendRequest request)
    {
        selectedRequests.Add(request);
        allowConfirmationFlags.Add(AgentChannelExecutionScope.Current!.AllowConfirmation);
        var crossAgent = request.AgentCode == "test" ? "edge-gateway" : "test";
        var denied = await AgentChannelExecutionScope.Current.AuthorizeToolAsync(new AgentToolRegistryEntity
        { AgentCode = crossAgent, ToolName = "cross.read", PermissionKey = "tool.read" });
        Check(denied != null, "selected turn cannot invoke another allowed Agent's tools");
        return new AgentResponseDto { ConversationId = request.ConversationId!.Value, Reply = "离线多 Agent 结果", RequiresConfirmation = planNext };
    }
    var beforeMultiUsage = (await multiService.GetStateAsync("u2")).Used;
    await Ingest("查询边缘设备信息", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(selectedRequests[^1].AgentCode == "edge-gateway" && routingCalls == 1,
        "multi-agent message uses model-selected allowlisted Agent rather than always the first item");
    routeChoice = "test";
    await Ingest("查询项目", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    routeChoice = "edge-gateway";
    await Ingest("继续查询边缘设备", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(createdConversations.Count == 2 && selectedRequests[0].ConversationId == selectedRequests[2].ConversationId &&
        selectedRequests[0].ConversationId != selectedRequests[1].ConversationId,
        "one WeChat channel retains separate Agent contexts and restores each on return");
    Check((await multiService.GetStateAsync("u2")).Used == beforeMultiUsage + 3,
        "routing and business model calls together consume only one dialogue quota per inbound message");
    var callsBeforeExplicit = routingCalls;
    await Ingest("@test 显式选择查询", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(routingCalls == callsBeforeExplicit && selectedRequests[^1].AgentCode == "test",
        "explicit @AgentCode bypasses routing model but still checks the allowlist");
    var beforeRejectedRoute = selectedRequests.Count;
    await Ingest("@not-allowed 查询", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(selectedRequests.Count == beforeRejectedRoute && (await multiService.GetJobsAsync("u2", 1, 1))[0].Kind == "Error",
        "explicit unselected Agent produces clarification without business execution");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    routeChoice = "not-allowed";
    await Ingest("模型误选测试", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(selectedRequests.Count == beforeRejectedRoute, "routing model cannot invent or select an unchecked Agent");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    routeChoice = "edge-gateway";
    planNext = true;
    await Ingest("新增边缘配置", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    var callsBeforeConfirmation = routingCalls;
    routeChoice = "test";
    planNext = false;
    await Ingest("确认", user: "u2");
    await multiService.ProcessUserAsync("u2", selectedAgent, sender, default);
    Check(routingCalls == callsBeforeConfirmation && selectedRequests[^1].AgentCode == "edge-gateway" && allowConfirmationFlags[^1],
        "confirmation is pinned to delivered plan's Agent and cannot be rerouted by the model");
    var storedMap = await db.Queryable<WeChatClawBotAiConnectionEntity>().InSingleAsync("u2");
    Check(WeChatClawBotAiAgentRouting.GetConversations(storedMap).Count == 2 && storedMap.ActiveAgentCode == "edge-gateway" &&
        (await multiService.GetJobsAsync("u2", 1, 1))[0].AgentCode == "edge-gateway",
        "per-Agent conversations, active Agent and task attribution persist for restart and audit");
    await multiService.SaveAsync("u2", new WeChatClawBotAiSaveDto { Mode = "Work", AgentCode = "test" });
    var legacySave = await multiService.GetStateAsync("u2");
    Check(legacySave.Configuration.AgentCodes!.SequenceEqual(["test"]) && legacySave.ConversationId == null,
        "old single-agent API callers remain compatible and configuration changes clear old authorization and contexts");
    Console.WriteLine("PASS: isolated SQL + simulated model/WeChat and multi-agent routing complete; no real message or model request sent.");
}
finally
{
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!database.StartsWith("CodexWeChatAi_") || !Guid.TryParseExact(database[14..], "N", out _))
        throw new InvalidOperationException("拒绝清理非本次创建的隔离数据库");
    await Master($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static string Updates(string id, string content, string sender, string context) => JsonSerializer.Serialize(new
{
    ret = 0, get_updates_buf = "cursor-" + id,
    msgs = new[] { new { message_type = 1, message_id = id, from_user_id = sender,
        context_token = context, item_list = new[] { new { type = 1, text_item = new { text = content } } } } }
});

static async Task ExpectRejected(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SqlException or UnauthorizedAccessException)
    { Console.WriteLine("PASS: " + message); return; }
    throw new InvalidOperationException("FAIL: " + message);
}

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    public static T Make<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var instance = Create<T, Proxy>();
        ((Proxy)(object)instance).Handler = handler;
        return instance;
    }
}

internal sealed class ConstantOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class OfflineHttpFactory(Func<HttpRequestMessage, Task<string>> handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new OfflineHandler(handler));
    private sealed class OfflineHandler(Func<HttpRequestMessage, Task<string>> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await handler(request), Encoding.UTF8, "application/json") };
    }
}
