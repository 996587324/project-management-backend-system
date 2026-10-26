using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using calmadmin.Config;
using calmadmin.Controllers.ExternalTools;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.EmailPush;
using Model.ExternalTools;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Service;
using SqlSugar;

// Only random isolated SQL databases. WeChat HTTP and SMTP are in-memory probes, never external sends.
var server = Environment.GetEnvironmentVariable("MCP_PUSH_SMOKE_SQL") ??
    @"Server=(localdb)\CodexMcpPush20261022;Integrated Security=true;Encrypt=false;Connect Timeout=10";
var name = "CodexMcpPush_" + Guid.NewGuid().ToString("N");
await using var master = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
using (var create = new SqlCommand($"CREATE DATABASE [{name}] COLLATE Chinese_PRC_CI_AS", master))
    await create.ExecuteNonQueryAsync();
SqlSugarScope? scope = null;
WebApplication? app = null;
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
    var mailInit = new EmailPushDatabaseInitializer(scope);
    var queueInit = new WeChatPushQueueInitializer(scope);
    await mailInit.InitializeAsync(default);
    await queueInit.InitializeAsync(default);
    foreach (var id in new[] { "admin", "receiver", "cc", "no-mail" })
    {
        await db.Insertable(new SysUserInfo
        {
            UserId = id, UserName = id, NickName = id, Password = "fixture", Roles = id == "admin" ? "R_ADMIN" : "R_USER",
            Email = id == "no-mail" ? "" : id + "@example.test", PermissionKeys = "EmailPush,WeChatClawBot", Buttons = "", CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
    }
    var protection = new EphemeralDataProtectionProvider();
    var token = protection.CreateProtector("CalmAdmin.WeChatClawBot.BotToken.v1").Protect("fake-token-not-real");
    await db.Insertable(new WeChatClawBotAccountEntity
    {
        UserId = "receiver", UserName = "receiver", Status = "Bound", BotId = "fixture-bot", ILinkUserId = "fixture-peer", BotToken = token
    }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotConversationEntity { UserId = "receiver", ContextToken = "fixture-context" }).ExecuteCommandAsync();
    var applicationCalls = 0;
    var applications = Proxy<ISystemApplicationService>((method, _) =>
    {
        applicationCalls++;
        throw new UnauthorizedAccessException("MCP must not impersonate a web user: " + method.Name);
    });
    var weChatHandler = new WeChatTransport();
    var httpFactory = Proxy<IHttpClientFactory>((_, _) => new HttpClient(weChatHandler, false));
    var weChat = new WeChatClawBotService(scope, httpFactory, Options.Create(new WeChatClawBotOptions()), protection);
    var queue = new WeChatPushQueueService(scope, queueInit, applications, weChat);
    var mailTransport = new MailTransport();
    var mail = new EmailPushService(scope, mailInit, new Secrets(), mailTransport, applications);
    await mail.SaveSettingAsync(Access("admin"), new EmailSettingSaveDto
    {
        Enabled = true, Host = "smtp.example.test", FromAddress = "sender@example.test", UserName = "sender@example.test",
        Password = "fake-password", Version = 1
    });
    var service = new ExternalToolService(scope, Never<IEdgeDashboardService>(), Never<IEdgeGatewayService>(),
        Never<IProjectManageService>(), Never<IUserActivityService>(), weChat, new ConfigurationBuilder().Build(), mail, queue);
    var caller = new ExternalToolCallerContext { IsDirectAccess = true, AllResources = true, ClientId = "anonymous-mcp", RemoteIp = "127.0.0.1" };
    var definitions = service.GetDefinitions(caller);
    Require(new[] { "email.recipients.list", "email.channel-status", "email.send", "email.delivery-status", "email.retry", "clawbot.queue-status" }
        .All(tool => definitions.Any(x => x.Name == tool)), "all push tools discovered in actual catalog");
    var limited = new ExternalToolCallerContext { ClientId = "limited", Scopes = new() { "email.read", "email.write" } };
    Require(!service.GetDefinitions(limited).Any(x => x.Name.StartsWith("email.")), "resource-limited token cannot inherit email-recipient access");
    Require(!(await Call("email.send", MailPayload("missing-confirm", false))).Success, "confirm false rejected");
    Require(!(await Call("email.send", new { recipientUserIds = new[] { "receiver" }, subject = "test", body = "body", confirm = true })).Success,
        "missing idempotency key rejected");
    Require(!(await Call("email.send", new { recipientUserIds = new[] { "receiver" }, subject = "test", body = "body", idempotencyKey = "spoof", confirm = true, createdBy = "admin" })).Success,
        "model cannot inject creator or admin identity");
    Require(!(await Call("email.send", new { recipientUserIds = Enumerable.Repeat("receiver", 101).ToArray(), subject = "test", body = "body", idempotencyKey = "too-many", confirm = true })).Success,
        "over-limit arrays are rejected rather than truncated");
    Require(await db.Queryable<EmailPushTaskEntity>().CountAsync() == 0, "invalid calls cause no mail enqueue");
    Require((await Call("email.recipients.list", new { keyword = "receiver" })).Success, "anonymous recipient search");
    var channel = await Call("email.channel-status", new { });
    Require(channel.Success && !JsonSerializer.Serialize(channel.Data).Contains("sender@example.test"), "channel status hides SMTP identity");
    var first = await Call("email.send", MailPayload("mail-one"));
    Require(first.Success, "anonymous mail enqueue: " + first.Message);
    var repeated = await Call("email.send", MailPayload("mail-one"));
    Require(repeated.Success && await db.Queryable<EmailPushTaskEntity>().CountAsync() == 1, "mail idempotency across request IDs");
    Require(!(await Call("email.send", new { recipientUserIds = new[] { "receiver" }, subject = "changed", body = "body", idempotencyKey = "mail-one", confirm = true })).Success,
        "mail key cannot change content");
    var task = await db.Queryable<EmailPushTaskEntity>().FirstAsync();
    Require(task.Source == "MCP" && task.AppCode == "MCP" && task.CreatedBy.StartsWith("mcp:") && task.CreatedBy.Length == 64,
        "MCP source persisted separately, no fake admin or AB membership");
    Require(await db.Queryable<ExternalToolAccessLogEntity>().AnyAsync(x => x.ToolName == "email.send" && x.Status == "Attempted" && x.UserId == null),
        "write-ahead audit with truthful anonymous identity");
    Require((await Call("email.delivery-status", new { taskId = task.Id })).Success, "MCP mail records readable before dispatch");
    await mail.DispatchAsync(default);
    Require(mailTransport.Sends == 1, "MCP mail uses actual shared worker without web authorization");
    await db.Updateable<EmailPushSettingEntity>().SetColumns(x => x.NextSendTime == (DateTime?)null).Where(x => x.Id == 1).ExecuteCommandAsync();
    await mail.DispatchAsync(default);
    Require(mailTransport.Sends == 2, "MCP cc processed separately");
    var delivery = await db.Queryable<EmailPushDeliveryEntity>().FirstAsync();
    await db.Updateable<EmailPushDeliveryEntity>().SetColumns(x => x.Status == "Unknown").Where(x => x.Id == delivery.Id).ExecuteCommandAsync();
    Require(!(await Call("email.retry", new { deliveryId = delivery.Id, confirm = true })).Success, "unknown mail cannot be retried by MCP");
    await db.Updateable<EmailPushDeliveryEntity>().SetColumns(x => x.Status == "Failed").Where(x => x.Id == delivery.Id).ExecuteCommandAsync();
    Require((await Call("email.retry", new { deliveryId = delivery.Id, confirm = true })).Success, "failed mail retry only enqueues");
    Require(mailTransport.Sends == 2, "retry does not synchronously send SMTP");
    var foreignMail = await mail.EnqueueAsync(Access("admin"), new EmailEnqueueDto
    {
        RecipientUserIds = ["receiver"], Subject = "private", Body = "not exposed", Confirm = true, IdempotencyKey = "admin-mail"
    });
    var privateDelivery = await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == foreignMail.TaskId);
    await db.Updateable<EmailPushDeliveryEntity>().SetColumns(x => x.Status == "Failed").Where(x => x.Id == privateDelivery.Id).ExecuteCommandAsync();
    Require(!(await Call("email.retry", new { deliveryId = privateDelivery.Id, confirm = true })).Success, "MCP cannot retry admin's private mail");
    var foreignView = await Call("email.delivery-status", new { taskId = foreignMail.TaskId });
    Require(foreignView.Success && JsonSerializer.SerializeToElement(foreignView.Data).GetProperty("Total").GetInt32() == 0, "MCP cannot read private mail task");
    var otherCaller = new ExternalToolCallerContext { ClientId = "other-verified-client", AllResources = true, Scopes = new() { "email.read" } };
    var otherView = await service.ExecuteAsync("email.delivery-status", JsonSerializer.SerializeToElement(new { taskId = task.Id }), otherCaller, Guid.NewGuid().ToString("N"));
    Require(otherView.Success && JsonSerializer.SerializeToElement(otherView.Data).GetProperty("Total").GetInt32() == 0, "verified client namespaces are distinct from anonymous namespace");

    var wx = await Call("clawbot.send", new { recipientCodes = new[] { "receiver" }, content = "测试推送", idempotencyKey = "wx-one", externalTaskId = "task-external", source = "external-model", confirm = true });
    Require(wx.Success && weChatHandler.Sends == 0, "clawbot.send now queues without network");
    var wxJob = await db.Queryable<WeChatPushQueueEntity>().FirstAsync();
    Require(wxJob.ExternalTaskId == "task-external" && wxJob.SourceLabel == "external-model", "external metadata persisted, not used as trusted origin");
    await Call("clawbot.send", new { recipientCodes = new[] { "receiver" }, content = "测试推送", idempotencyKey = "wx-one", externalTaskId = "task-external", source = "external-model", confirm = true });
    Require(await db.Queryable<WeChatPushQueueEntity>().CountAsync() == 1, "WeChat queue idempotency");
    // Upgrade existing populated queue: optional metadata columns may be absent on prior Agent deployment.
    db.Ado.ExecuteCommand("ALTER TABLE [WeChatPushQueue] DROP COLUMN [ExternalTaskId],[SourceLabel]");
    WeChatPushQueueInitializer.InitializeSchema(db);
    WeChatPushQueueInitializer.InitializeSchema(db);
    Require((await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(wxJob.Id)).ExternalTaskId == null,
        "populated legacy queue gets nullable metadata without changing existing rows");
    await queue.DispatchAsync(default);
    Require(weChatHandler.Sends == 1 && (await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(wxJob.Id)).Status == "Sent",
        "MCP WeChat worker actually dispatches through shared adapter");
    var physical = await db.Queryable<WeChatClawBotDeliveryEntity>().InSingleAsync((await db.Queryable<WeChatPushQueueEntity>().InSingleAsync(wxJob.Id)).DeliveryId!.Value);
    Require(physical.Source == "MCP", "MCP transport audit not mislabeled AgentPush or DeviceCard");
    Require((await Call("clawbot.queue-status", new { idempotencyKey = "wx-one" })).Success, "queue query by stable external key");
    await Call("clawbot.send", new { recipientCodes = new[] { "receiver" }, content = "second", idempotencyKey = "wx-two", confirm = true });
    await queue.DispatchAsync(default);
    Require(weChatHandler.Sends == 1, "MCP shares 5second throttle");
    await db.Updateable<WeChatPushQueueSettingEntity>().SetColumns(x => x.NextSendTime == (DateTime?)null).Where(x => x.Id == 1).ExecuteCommandAsync();
    weChatHandler.Uncertain = true;
    await queue.DispatchAsync(default);
    var unknown = await db.Queryable<WeChatPushQueueEntity>().Where(x => x.Status == "Unknown").FirstAsync();
    Require(!(await Call("clawbot.retry", new { taskId = unknown.Id, confirm = true })).Success, "unknown WeChat queue cannot be retried");
    await db.Updateable<WeChatPushQueueEntity>().SetColumns(x => x.Status == "Failed").Where(x => x.Id == unknown.Id).ExecuteCommandAsync();
    Require((await Call("clawbot.retry", new { deliveryId = unknown.DeliveryId, confirm = true })).Success, "legacy deliveryId alias resolves queued failed task");
    Require(!(await Call("clawbot.retry", new { taskId = unknown.Id, deliveryId = unknown.DeliveryId, confirm = true })).Success, "ambiguous retry IDs rejected");
    Require(applicationCalls == 0, "MCP never resolves a real user's channel access or assumes admin");
    var chineseSource = new string('源', 32);
    var chineseTask = new string('任', 128);
    Require((await Call("clawbot.send", new
    {
        recipientCodes = new[] { "receiver" }, content = "中文元数据", idempotencyKey = "unicode",
        source = chineseSource, externalTaskId = chineseTask, confirm = true
    })).Success, "maximum legal Unicode metadata inserts without SQL truncation");
    var unicode = await db.Queryable<WeChatPushQueueEntity>().FirstAsync(x => x.Content == "中文元数据");
    Require(unicode.SourceLabel == chineseSource && unicode.ExternalTaskId == chineseTask, "Unicode metadata round trips unchanged");

    // Real SDK V2, legacy JSON-RPC and OpenAPI all run the same production service.
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton<IExternalToolService>(service);
    builder.Services.AddSingleton(applications);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddControllers(options => options.Filters.Add<SystemApplicationAccessFilter>())
        .AddApplicationPart(typeof(ExternalToolController).Assembly).AddNewtonsoftJson();
    builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, AnonymousAuth>("fixture", _ => { });
    builder.Services.AddAuthorization();
    var handlers = typeof(ExternalToolController).Assembly.GetType("calmadmin.Controllers.ExternalTools.ExternalMcpV2Handlers")!;
    var list = handlers.GetMethod("ListToolsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    var call = handlers.GetMethod("CallToolAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
        .WithListToolsHandler((request, cancellation) => (ValueTask<ListToolsResult>)list.Invoke(null, [request, cancellation])!)
        .WithCallToolHandler((request, cancellation) => (ValueTask<CallToolResult>)call.Invoke(null, [request, cancellation])!);
    app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapMcp("/mcp/v2").AllowAnonymous();
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    foreach (var endpoint in new[] { "/mcp/v2", "/mcp" })
    {
        var catalog = await Rpc(endpoint, "tools/list", new { });
        Require(catalog.Contains("email.send") && catalog.Contains("clawbot.queue-status"), endpoint + " discovers production tools");
        var sent = await Rpc(endpoint, "tools/call", new { name = "email.send", arguments = MailPayload("http-" + endpoint.Replace('/', '-')) });
        Require(!sent.Contains("\"isError\":true") && sent.Contains("TaskId"), endpoint + " submits mail through real queue");
        var noConfirm = await Rpc(endpoint, "tools/call", new { name = "email.send", arguments = MailPayload("http-invalid", false) });
        Require(noConfirm.Contains("\"isError\":true"), endpoint + " rejects unconfirmed send");
    }
    Require((await client.GetStringAsync("/api/external/v1/tools")).Contains("email.recipients.list"), "OpenAPI also lists new mail tools");
    using (var response = await client.PostAsJsonAsync("/api/external/v1/tools/email.send", MailPayload("http-openapi")))
        Require(response.IsSuccessStatusCode, "OpenAPI also submits to same queue");
    Require((await client.GetAsync("/api/email-push/setting")).StatusCode == HttpStatusCode.Unauthorized, "SMTP settings remain protected");
    Require((await client.GetAsync("/api/wechat-clawbot/push-queue/records")).StatusCode == HttpStatusCode.Unauthorized, "internal queue remains protected");
    Require(!definitions.Any(x => x.Name.Contains("setting") || x.Name.Contains("password")), "no account configuration tools exposed");
    var beforeAuditFailure = await db.Queryable<EmailPushTaskEntity>().CountAsync();
    db.DbMaintenance.DropTable<ExternalToolAccessLogEntity>();
    Require(!(await Call("email.send", MailPayload("audit-unavailable"))).Success &&
        await db.Queryable<EmailPushTaskEntity>().CountAsync() == beforeAuditFailure,
        "required write-ahead audit failure prevents enqueue");
    Console.WriteLine("PASS: production MCP V2/legacy/OpenAPI catalogs and calls; SQL migration/nulls; email and WeChat queues; audit-before-write; confirm/schema/idempotency/throttle; no fake user; no unknown retry; private task isolation. No external messages sent.");

    Task<ExternalToolExecutionResultDto> Call(string tool, object payload) =>
        service.ExecuteAsync(tool, JsonSerializer.SerializeToElement(payload), caller, Guid.NewGuid().ToString("N"));
    async Task<string> Rpc(string endpoint, string method, object parameters)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
        var body = await response.Content.ReadAsStringAsync();
        Require(response.IsSuccessStatusCode, endpoint + " HTTP " + response.StatusCode + " " + body);
        return body;
    }
}
finally
{
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!name.StartsWith("CodexMcpPush_", StringComparison.Ordinal) || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_'))
        throw new InvalidOperationException("Unsafe test database cleanup target");
    using var cleanup = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", master);
    await cleanup.ExecuteNonQueryAsync();
}

static object MailPayload(string key, bool confirm = true) => new
{
    recipientUserIds = new[] { "receiver" }, ccUserIds = new[] { "cc" }, subject = "MCP邮件测试", body = "示例正文", idempotencyKey = key, confirm
};
static ApplicationAccessContext Access(string id) => new() { UserId = id, AppCode = "A", Permissions = new() { "EmailPush", "WeChatClawBot" } };
static T Never<T>() where T : class => Proxy<T>((method, _) => throw new NotSupportedException(method.Name));
static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
{
    var value = DispatchProxy.Create<T, InvocationProbe>();
    ((InvocationProbe)(object)value).Handler = handler;
    return value;
}
static void Require(bool success, string message)
{
    if (!success) throw new InvalidOperationException("FAILED: " + message);
}
public class InvocationProbe : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
public sealed class Secrets : IEmailSecretProtector
{
    public string Protect(string value) => "fake-protected";
    public string Unprotect(string value) => "fake-password";
}
public sealed class MailTransport : IEmailTransport
{
    public int Sends;
    public Task TestAsync(EmailPushSettingEntity setting, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting, EmailPushTaskEntity task, EmailPushDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        Sends++;
        return Task.FromResult(new EmailTransportResult { Status = "Sent" });
    }
}
public sealed class WeChatTransport : HttpMessageHandler
{
    public int Sends;
    public bool Uncertain;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sends++;
        if (request.RequestUri?.AbsolutePath != "/ilink/bot/sendmessage") throw new InvalidOperationException("Unexpected mock endpoint");
        if (Uncertain) throw new HttpRequestException("simulated uncertain network");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ret\":0,\"message_id\":\"fixture-id\"}") });
    }
}
public sealed class AnonymousAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
