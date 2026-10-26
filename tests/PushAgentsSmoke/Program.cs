using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using calmadmin.HostedServices;
using Interface;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.Agent;
using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using Service;
using SqlSugar;
using 上传MES.Config;

// Explicit isolated SQL Server only. All model, SMTP and WeChat transports are probes, never network sends.
var server = Environment.GetEnvironmentVariable("PUSH_SMOKE_SQL") ??
    @"Server=(localdb)\CodexPushAgents20261022;Integrated Security=true;Encrypt=false;Connect Timeout=10";
var name = "CodexPushAgents_" + Guid.NewGuid().ToString("N");
await using var master = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
using (var create = new SqlCommand($"CREATE DATABASE [{name}] COLLATE Chinese_PRC_CI_AS", master))
    await create.ExecuteNonQueryAsync();
SqlSugarScope? scope = null;
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true,
        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    db.CodeFirst.InitTables<SysUserInfo>();
    WeChatClawBotDatabaseInitializer.Initialize(db);
    var initializer = new WeChatPushQueueInitializer(scope);
    await initializer.InitializeAsync(default);
    WeChatPushQueueInitializer.InitializeSchema(db);
    await db.Insertable(new[] { User("u1"), User("u2"), User("u3"), User("admin", "R_ADMIN") }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotAccountEntity
    {
        UserId = "u2", UserName = "接收人", BotId = "fake-bot", ILinkUserId = "fake-peer", Status = "Bound", BotToken = "not-real-token"
    }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotConversationEntity { UserId = "u2", ContextToken = "not-real-context" }).ExecuteCommandAsync();
    var access = Access("u1");
    var accessB = Access("u1", "B");
    var revoke = false;
    var apps = DispatchProxy.Create<ISystemApplicationService, Probe>();
    ((Probe)(object)apps).Handler = (method, args) => method.Name switch
    {
        "GetChannelAccessAsync" => revoke ? throw new UnauthorizedAccessException("revoked") :
            Task.FromResult(Access((string)args![0]!, (string)args[1]!)),
        "GetAccessAsync" => Task.FromResult(access),
        _ => throw new NotSupportedException(method.Name)
    };
    var sends = 0;
    var nextStatus = "Sent";
    var weChat = DispatchProxy.Create<IWeChatClawBotService, Probe>();
    ((Probe)(object)weChat).Handler = (method, args) =>
    {
        if (method.Name != "SendQueuedMessageAsync") throw new NotSupportedException(method.Name);
        Interlocked.Increment(ref sends);
        var status = nextStatus;
        nextStatus = "Sent";
        return Task.FromResult(new WeChatClawBotDeliveryDto { Id = sends, Status = status, SentTime = status == "Sent" ? DateTime.Now : null });
    };
    var queue = new WeChatPushQueueService(scope, initializer, apps, weChat);
    Require((await queue.GetRecipientsAsync(access, "u2", 1, 20)).Total == 1, "recipient search uses real accounts");
    Require(!(await queue.GetRecipientsAsync(access, "no-such-name", 1, 20)).Records.Any(), "no invented recipient");
    Require(!await queue.CanConfigureAsync(access) && await queue.CanConfigureAsync(Access("admin")), "configuration requires actual admin role");
    await Reject(() => queue.SetIntervalAsync(access, 1), "ordinary user interval write denied");
    await queue.SetIntervalAsync(Access("admin"), 5);
    await Reject(() => queue.EnqueueAsync(access, Message("no-confirm", false)), "send needs confirmation");
    await queue.EnqueueAsync(access, Message("first"));
    await Task.WhenAll(queue.EnqueueAsync(access, Message("first")), queue.EnqueueAsync(access, Message("first")));
    Require(await db.Queryable<WeChatPushQueueEntity>().CountAsync() == 1, "repeated submission is idempotent");
    var changed = Message("first");
    changed.Content = "different";
    await Reject(() => queue.EnqueueAsync(access, changed), "same key cannot change payload");
    var first = await db.Queryable<WeChatPushQueueEntity>().FirstAsync();
    Require(first.SentTime == null && first.DeliveryId == null && first.Error == null, "unoccurred SQL values really nullable");
    Require(first.Content == "测试微信正文", "Chinese text persisted");
    Require((await queue.GetRecordsAsync(accessB, null, 1, 20)).Total == 0, "cross application isolation");
    Require((await queue.GetRecordsAsync(Access("u3"), null, 1, 20)).Total == 0, "cross user isolation");
    Require((await queue.GetRecordsAsync(access, first.Id, 1, 20)).Total == 1, "taskId filter including nullable absent path works");

    // Legacy populated queue missing a newly defaulted field; preserve content, PK and unique indexes.
    var constraint = db.Ado.GetString("SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'WeChatPushQueue') AND c.name=N'Attempt'");
    if (!string.IsNullOrEmpty(constraint)) db.Ado.ExecuteCommand($"ALTER TABLE [WeChatPushQueue] DROP CONSTRAINT [{constraint}]");
    db.Ado.ExecuteCommand("ALTER TABLE [WeChatPushQueue] DROP COLUMN [Attempt]");
    WeChatPushQueueInitializer.InitializeSchema(db);
    WeChatPushQueueInitializer.InitializeSchema(db);
    Require((await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(first.Id)).Attempt == 0, "populated old table default backfilled");
    Require(await db.Queryable<WeChatPushQueueEntity>().CountAsync() == 1, "repeat initialization preserves data");

    await Task.WhenAll(queue.DispatchAsync(default), new WeChatPushQueueService(scope, initializer, apps, weChat).DispatchAsync(default));
    Require(sends == 1 && (await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(first.Id)).Status == "Sent", "single dispatch lease across workers");
    await queue.EnqueueAsync(access, Message("second"));
    await queue.DispatchAsync(default);
    Require(sends == 1, "global interval respected");
    await ClearThrottle();
    nextStatus = "Unknown";
    await queue.DispatchAsync(default);
    var unknown = await db.Queryable<WeChatPushQueueEntity>().Where(x => x.Status == "Unknown").FirstAsync();
    await Reject(() => queue.RetryAsync(access, unknown.Id), "unknown not retried");
    await queue.EnqueueAsync(access, Message("failed"));
    await ClearThrottle();
    nextStatus = "Failed";
    await queue.DispatchAsync(default);
    var failed = await db.Queryable<WeChatPushQueueEntity>().Where(x => x.Status == "Failed").FirstAsync();
    await Reject(() => queue.RetryAsync(accessB, failed.Id), "cross-app retry denied");
    await queue.RetryAsync(access, failed.Id);
    await ClearThrottle();
    await queue.DispatchAsync(default);
    Require((await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(failed.Id)).Attempt == 2, "confirmed failed retry increments attempt");
    await queue.EnqueueAsync(access, Message("revoked"));
    await ClearThrottle();
    var before = sends;
    revoke = true;
    await queue.DispatchAsync(default);
    revoke = false;
    Require(sends == before && await db.Queryable<WeChatPushQueueEntity>().AnyAsync(x => x.Status == "Cancelled"), "revoked actor cancelled before network");
    await queue.EnqueueAsync(access, Message("crashed"));
    var crashed = await db.Queryable<WeChatPushQueueEntity>().Where(x => x.Status == "Pending").FirstAsync();
    await db.Updateable<WeChatPushQueueEntity>().SetColumns(x => new WeChatPushQueueEntity
    {
        Status = "Sending", LeaseExpireTime = DateTime.Now.AddMinutes(-1)
    }).Where(x => x.Id == crashed.Id).ExecuteCommandAsync();
    await ClearThrottle();
    await queue.DispatchAsync(default);
    Require(sends == before && (await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(crashed.Id)).Status == "Unknown", "expired sending never replayed");

    // Real registry + confirmation dispatcher + actual email queue, fake transports only.
    var mapper = new MapperConfiguration(c =>
    {
        c.CreateMap<AgentModuleSeed, AgentDefinitionEntity>();
        c.CreateMap<AgentToolSeed, AgentToolRegistryEntity>();
        c.CreateMap<AgentToolRegistryEntity, AgentToolDefinitionDto>();
        c.CreateMap<AgentExecutionPlanEntity, AgentToolCallDto>();
    }).CreateMapper();
    var modules = new IAgentModuleProvider[]
    {
        new WeChatPushAgentModuleProvider(), new EmailPushAgentModuleProvider(),
        new QueryAssistantAgentModuleProvider(), new ProjectManagementAgentModuleProvider()
    };
    var registry = new AgentToolRegistryService(scope, mapper, modules, []);
    await registry.GetAllAgentsAsync();
    var aggregated = await registry.GetAllToolsAsync(AgentCodes.QueryAssistant);
    Require(aggregated.Select(x => x.ToolName).Distinct().Count() == aggregated.Count, "shared read tools deduplicated");
    Require((await registry.GetAllToolsAsync(AgentCodes.EmailPush)).Any(x => x.ToolName == "email.send"), "email agent registered");
    var mailInit = new EmailPushDatabaseInitializer(scope);
    await mailInit.InitializeAsync(default);
    var mailTransport = new MailProbe();
    var mail = new EmailPushService(scope, mailInit, new SecretProbe(), mailTransport, apps);
    await mail.SaveSettingAsync(Access("admin"), new EmailSettingSaveDto
    {
        Enabled = true, Host = "smtp.example.test", UserName = "sender@example.test", FromAddress = "sender@example.test",
        Password = "fake-only", Version = 1
    });
    var executor = new PushAgentToolExecutor(apps, new HttpContextAccessor(), queue, mail);
    var dispatcher = new AgentToolDispatcher(scope, mapper, registry, [executor], []);
    var conversation = new AgentConversationEntity { UserId = "u1", AgentCode = AgentCodes.EmailPush, Title = "test" };
    conversation.Id = await db.Insertable(conversation).ExecuteReturnIdentityAsync();
    AgentToolCallDto plan;
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, false, () => Task.FromResult(access)))
    {
        plan = await dispatcher.DispatchAsync(new AgentToolDispatchRequest
        {
            AgentCode = AgentCodes.EmailPush, ToolName = "email.send", ConversationId = conversation.Id, UserId = "u1",
            ArgumentsJson = "{\"recipientUserIds\":[\"u2\"],\"subject\":\"邮件测试\",\"body\":\"真实队列，模拟SMTP\"}"
        });
        Require(plan.Status == "PendingConfirmation" && await db.Queryable<EmailPushTaskEntity>().CountAsync() == 0, "no send before confirmation");
        Require(await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = plan.Id, UserId = "u1" }) == null, "channel cannot forge confirmation");
    }
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, true, () => Task.FromResult(access)))
    {
        var done = await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = plan.Id, UserId = "u1" });
        Require(done?.Status == "Succeeded", "confirmed email enqueued: " + done?.ResponseJson);
        await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = plan.Id, UserId = "u1" });
    }
    Require(await db.Queryable<EmailPushTaskEntity>().CountAsync() == 1, "confirmation replay cannot duplicate mail");
    await mail.DispatchAsync(default);
    Require(mailTransport.Sends == 1, "actual mail queue dispatches through simulated transport");
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, true, () => Task.FromResult(access)))
    {
        var wxPlan = await dispatcher.DispatchAsync(new AgentToolDispatchRequest
        {
            AgentCode = AgentCodes.WeChatPush, ToolName = "wechat.send", ConversationId = conversation.Id, UserId = "u1",
            ArgumentsJson = "{\"recipientUserIds\":[\"u2\"],\"content\":\"微信Agent确认计划\"}"
        });
        Require(wxPlan.Status == "PendingConfirmation", "WeChat send also requires actual confirmation");
        Require((await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = wxPlan.Id, UserId = "u3" })) == null,
            "other user cannot confirm sender plan");
        var done = await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = wxPlan.Id, UserId = "u1" });
        Require(done?.Status == "Succeeded", "confirmed WeChat plan entered real queue");
        await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = wxPlan.Id, UserId = "u1" });
    }
    Require(await db.Queryable<WeChatPushQueueEntity>().CountAsync(x => x.Content == "微信Agent确认计划") == 1,
        "WeChat confirmation replay cannot duplicate queue");
    var queryContext = new AgentBusinessToolContext { UserId = "u1", ToolName = "email.records" };
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, true, () => Task.FromResult(access)))
    {
        await executor.ExecuteAsync("records", JsonSerializer.SerializeToElement(new { }), queryContext);
        await executor.ExecuteAsync("channel-status", JsonSerializer.SerializeToElement(new { }), new AgentBusinessToolContext { UserId = "u1", ToolName = "email.channel-status" });
    }

    // Persisted business result draft: existing rows migrate with SQL NULL, same user/version/resource hash only.
    var job = new WeChatClawBotAiJobEntity
    {
        UserId = "u1", InboundId = 987, Version = "version-one", AgentCode = AgentCodes.ProcessBoard,
        BindingIdentity = "binding", Day = "20261022", Status = "Sent", Reply = "温度超限，请检查", ResultAccessHash = PushResultAccessFingerprint.Create(access)
    };
    job.Id = await db.Insertable(job).ExecuteReturnBigIdentityAsync();
    db.Ado.ExecuteCommand("ALTER TABLE [WeChatClawBotAiJob] DROP COLUMN [ResultAccessHash]");
    WeChatClawBotAiSchema.Initialize(db);
    WeChatClawBotAiSchema.Initialize(db);
    Require((await db.Queryable<WeChatClawBotAiJobEntity>().InSingleAsync(job.Id)).ResultAccessHash == null, "legacy job hash nullable migration preserves row");
    await db.Updateable<WeChatClawBotAiJobEntity>().SetColumns(x => x.ResultAccessHash == job.ResultAccessHash).Where(x => x.Id == job.Id).ExecuteCommandAsync();
    var ai = new WeChatClawBotAiService(scope, apps, registry);
    var config = new WeChatClawBotAiConnectionEntity
    {
        UserId = "u1", AppCode = "A", Mode = "Work", Version = "version-one",
        AllowedAgentCodesJson = "[\"process-board\",\"email-push\",\"wechat-push\"]"
    };
    var method = typeof(WeChatClawBotAiService).GetMethod("GetPreviousResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var draft = await (Task<AgentChannelResultDraft?>)method.Invoke(ai, [config, job.Id + 100])!;
    Require(draft?.SourceJobId == job.Id, "same user system result transferable");
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, true,
        () => Task.FromResult(access), draft))
    {
        var result = await executor.ExecuteAsync("previous-result", JsonSerializer.SerializeToElement(new { }),
            new AgentBusinessToolContext { UserId = "u1", ToolName = "push.previous-result" });
        Require(JsonSerializer.Serialize(result.Data).Contains("\\u6E29"), "authorized draft returned as plain data");
    }
    config.AppCode = "B";
    Require(await (Task<AgentChannelResultDraft?>)method.Invoke(ai, [config, job.Id + 100])! == null, "different application draft rejected");
    config.AppCode = "A";
    config.UserId = "u3";
    Require(await (Task<AgentChannelResultDraft?>)method.Invoke(ai, [config, job.Id + 100])! == null, "different user draft rejected");
    config.UserId = "u1";
    config.Version = "changed";
    Require(await (Task<AgentChannelResultDraft?>)method.Invoke(ai, [config, job.Id + 100])! == null, "new connection version cannot reuse old result");
    using (var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default, true,
        () => Task.FromResult(accessB), draft))
    {
        var result = await executor.ExecuteAsync("previous-result", JsonSerializer.SerializeToElement(new { }),
            new AgentBusinessToolContext { UserId = "u1", ToolName = "push.previous-result" });
        Require(!JsonSerializer.Serialize(result.Data).Contains("温度超限"), "tool rechecks current resource fingerprint");
    }
    Require(await WeChatClawBotAiAgentRouting.SelectAsync("u1", "@wechat-push 发送消息",
        [new AgentDefinitionEntity { AgentCode = AgentCodes.WeChatPush, Name = "微信推送助手" }], null, null, default) == AgentCodes.WeChatPush,
        "explicit WeChat routing recognized");
    await db.Updateable<WeChatClawBotAiJobEntity>().SetColumns(x => x.UpdateTimeUtc == DateTime.UtcNow.AddMinutes(-31))
        .Where(x => x.Id == job.Id).ExecuteCommandAsync();
    config.Version = "version-one";
    Require(await (Task<AgentChannelResultDraft?>)method.Invoke(ai, [config, job.Id + 100])! == null, "draft expires after30minutes");

    // Actual WeChat adapter, but its HTTP transport is in-memory and cannot access the network.
    var protection = new EphemeralDataProtectionProvider();
    var token = protection.CreateProtector("CalmAdmin.WeChatClawBot.BotToken.v1").Protect("fake-only-token");
    await db.Updateable<WeChatClawBotAccountEntity>().SetColumns(x => x.BotToken == token).Where(x => x.UserId == "u2").ExecuteCommandAsync();
    var handler = new WeChatHttpProbe();
    var factory = DispatchProxy.Create<IHttpClientFactory, Probe>();
    ((Probe)(object)factory).Handler = (_, _) => new HttpClient(handler, disposeHandler: false);
    var adapter = new WeChatClawBotService(scope, factory, Options.Create(new WeChatClawBotOptions()), protection);
    var account = await db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == "u2");
    var binding = WeChatClawBotBindingIdentity.Create(account);
    var delivered = await adapter.SendQueuedMessageAsync("u2", "模拟通道正文", "adapter-once", "fixture", binding, default);
    Require(delivered.Status == "Sent" && delivered.Source == "AgentPush", "real adapter uses AgentPush source");
    Require((await adapter.SendQueuedMessageAsync("u2", "模拟通道正文", "adapter-once", "fixture", binding, default)).Id == delivered.Id && handler.Sends == 1,
        "real adapter idempotency skips HTTP on repeated task");
    handler.ThrowNetworkError = true;
    var ambiguous = await adapter.SendQueuedMessageAsync("u2", "超时结果", "adapter-unknown", "fixture", binding, default);
    Require(ambiguous.Status == "Unknown" && ambiguous.SentTime == null, "real adapter classifies uncertain HTTP outcome");
    await adapter.SendQueuedMessageAsync("u2", "超时结果", "adapter-unknown", "fixture", binding, default);
    Require(handler.Sends == 2, "unknown adapter result not resent");
    await Reject(() => adapter.SendQueuedMessageAsync("u2", "binding changed", "adapter-changed", "fixture", "wrong-binding", default),
        "adapter enforces expected recipient binding before HTTP");

    // Assembly scanning must preserve initializer readiness as a singleton.
    var registrations = new ServiceCollection();
    registrations.AddSingleton(scope);
    registrations.AddSingleton<IWeChatPushQueueInitializer>(initializer);
    var containerBuilder = new ContainerBuilder();
    containerBuilder.Populate(registrations);
    containerBuilder.RegisterModule(new AutofacModuleRegister());
    using (var container = containerBuilder.Build())
    {
        Require(ReferenceEquals(initializer, container.Resolve<IWeChatPushQueueInitializer>()), "Autofac preserves queue initializer singleton");
    }
    var hostServices = new ServiceCollection().AddSingleton<IWeChatPushQueueService>(queue).BuildServiceProvider();
    using (hostServices)
    using (var background = new WeChatPushQueueHostedService(hostServices.GetRequiredService<IServiceScopeFactory>(), initializer,
               Microsoft.Extensions.Logging.Abstractions.NullLogger<WeChatPushQueueHostedService>.Instance))
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await background.StartAsync(default);
        Require(clock.Elapsed < TimeSpan.FromSeconds(1), "queue hosted service does not block startup");
        await background.StopAsync(default);
    }
    Console.WriteLine("PASS: SQL fresh/populated-legacy/repeat/insert; both agents, actual confirmation gate, queues, leases, throttle, idempotency, per-user/app records, revoked actor, unknown no retry, draft isolation. No external messages sent.");

    async Task ClearThrottle() => await db.Updateable<WeChatPushQueueSettingEntity>()
        .SetColumns(x => x.NextSendTime == (DateTime?)null).Where(x => x.Id == 1).ExecuteCommandAsync();
}
finally
{
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!name.StartsWith("CodexPushAgents_", StringComparison.Ordinal) || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_'))
        throw new InvalidOperationException("Unsafe test cleanup target");
    using var cleanup = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", master);
    await cleanup.ExecuteNonQueryAsync();
}

static SysUserInfo User(string id, string role = "R_USER") => new()
{
    UserId = id, UserName = id, NickName = id, Email = id + "@example.test", Password = "fake", Roles = role,
    PermissionKeys = "WeChatClawBot,EmailPush,ProcessBoardAgent", Buttons = "", CreateTime = DateTime.Now
};
static ApplicationAccessContext Access(string user, string app = "A") => new()
{
    UserId = user, AppCode = app, Permissions = new(StringComparer.OrdinalIgnoreCase) { "WeChatClawBot", "EmailPush", "ProcessBoardAgent" }
};
static WeChatPushEnqueueDto Message(string key, bool confirm = true) => new()
{
    RecipientUserIds = ["u2"], Content = "测试微信正文", IdempotencyKey = key, Confirm = confirm
};
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
}
static async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected rejection: " + message);
}
public class Probe : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public sealed class SecretProbe : IEmailSecretProtector
{
    public string Protect(string value) => "fake-encrypted";
    public string Unprotect(string value) => "fake-only";
}
public sealed class MailProbe : IEmailTransport
{
    public int Sends;
    public Task TestAsync(EmailPushSettingEntity setting, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting, EmailPushTaskEntity task, EmailPushDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        Sends++;
        return Task.FromResult(new EmailTransportResult { Status = "Sent" });
    }
}

public sealed class WeChatHttpProbe : HttpMessageHandler
{
    public int Sends;
    public bool ThrowNetworkError;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sends++;
        if (request.RequestUri?.AbsolutePath != "/ilink/bot/sendmessage") throw new InvalidOperationException("Unexpected mock endpoint");
        if (ThrowNetworkError) throw new HttpRequestException("simulated connection failure");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ret\":0,\"message_id\":\"fake-message\"}")
        });
    }
}
