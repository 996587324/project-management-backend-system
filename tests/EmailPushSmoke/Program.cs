using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using calmadmin.Config;
using calmadmin.Controllers;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Model.EmailPush;
using Model.Agent;
using Model.Menu;
using Model.SystemApplications;
using Model.User;
using Newtonsoft.Json;
using Service;
using SqlSugar;
using calmadmin.HostedServices;
using 上传MES.Config;

// Only random isolated databases on the explicit test server; no live SMTP, model or WeChat calls.
var serverConnection = Environment.GetEnvironmentVariable("EMAIL_SMOKE_SQL") ??
    @"Server=(localdb)\CodexEmailPush20261020;Integrated Security=true;Encrypt=false;Connect Timeout=10";
var name = "CodexEmailPush_" + Guid.NewGuid().ToString("N");
var legacyName = name + "_Legacy";
var root = Path.Combine(Path.GetTempPath(), name);
Directory.CreateDirectory(root);
var names = new List<string>();
await using var master = new SqlConnection(new SqlConnectionStringBuilder(serverConnection) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
SqlSugarScope? scope = null;
WebApplication? app = null;
try
{
    foreach (var database in new[] { name, legacyName })
    {
        // Existing system menu entities use varchar, matching the deployed Chinese SQL Server collation.
        // The independent legacy-schema database keeps the server default to test new Unicode mail columns too.
        var collation = database == name ? " COLLATE Chinese_PRC_CI_AS" : "";
        using var create = new SqlCommand($"CREATE DATABASE [{database}]{collation}", master);
        await create.ExecuteNonQueryAsync();
        names.Add(database);
    }
    scope = Open(name);
    var db = scope.GetConnectionScope("calmadmin");
    await new SystemDatabaseInitializer(scope, new LoggerFactory().CreateLogger<SystemDatabaseInitializer>())
        .InitializeAsync(CancellationToken.None);
    var actualRoot = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "WeChatClawBotRoot");
    Require(actualRoot.MetaTitle == "推送中心", "dynamic menu root renamed: " + actualRoot.MetaTitle + "; column=" +
        db.DbMaintenance.GetColumnInfosByTableName("sys_menu", false).Single(x => x.DbColumnName == "meta_title").DataType);
    Require(await db.Queryable<sys_menu>().CountAsync(x => x.MenuName == "WeChatClawBotManagement") == 1, "management menu not duplicated");
    var managerMenu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "WeChatClawBotManagement");
    Require(managerMenu.Path == "/agent/clawbot-management", "old management URL preserved");
    await PushCenterMenus.SeedAsync(db);
    Require(await db.Queryable<sys_menu>().CountAsync(x => x.MenuName == "EmailPush") == 1, "menu migration is repeatable");
    var emailMenu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "EmailPush");
    Require(await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == emailMenu.MenuId), "A dynamic route registered");
    var bRoot = await db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.AppCode == "B" && x.PermissionKey == "WeChatClawBotRoot");
    var bManager = await db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.AppCode == "B" && x.PermissionKey == "WeChatClawBotManagement");
    Require(bRoot != null && bManager?.ParentId == bRoot.Id &&
        await db.Queryable<SysApplicationMenuEntryEntity>().AnyAsync(x => x.AppCode == "B" && x.PermissionKey == "EmailPush"),
        "B receives independent push-center routes with the same permission keys");
    var bAgent = await db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.AppCode == "B" && x.PermissionKey == "Agent");
    var custom = JsonConvert.DeserializeObject<sys_menu>(bManager!.DefinitionJson)!;
    custom.Path = "custom-claw";
    custom.MetaTitle = "自定义微信管理";
    custom.Status = false;
    bManager.ParentId = bAgent.Id;
    bManager.DefinitionJson = JsonConvert.SerializeObject(custom);
    await db.Updateable(bManager).ExecuteCommandAsync();
    await db.Deleteable<SysApplicationMigrationEntity>().Where(x => x.Version == "push-center-email-v1").ExecuteCommandAsync();
    await PushCenterMenus.SeedAsync(db);
    var moved = await db.Queryable<SysApplicationMenuEntryEntity>().InSingleAsync(bManager.Id);
    var movedDefinition = JsonConvert.DeserializeObject<sys_menu>(moved.DefinitionJson)!;
    Require(moved.ParentId == bRoot!.Id && movedDefinition.Path == "/agent/custom-claw" &&
        movedDefinition.MetaTitle == "自定义微信管理" && movedDefinition.Status == false,
        "B menu migration preserves custom URL, title and disabled state");

    await db.Insertable(new[]
    {
        User("role-admin", "alice", "alice@example.test", "R_ADMIN"),
        User("u1", "operator", "operator@example.test", "R_USER"),
        User("u2", "receiver", "receiver@example.test", "R_USER"),
        User("no-mail", "empty-email", "", "R_USER")
    }).ExecuteCommandAsync();
    var initializer = new EmailPushDatabaseInitializer(scope);
    await initializer.InitializeAsync(CancellationToken.None);
    await initializer.InitializeAsync(CancellationToken.None);
    var environment = new TestEnvironment { ContentRootPath = root };
    var secrets = new EmailSecretProtector(environment);
    const string fakeSecret = "isolated-test-secret-not-a-real-mailbox-password";
    var encrypted = secrets.Protect(fakeSecret);
    Require(encrypted != fakeSecret && new EmailSecretProtector(environment).Unprotect(encrypted) == fakeSecret,
        "mail password encryption survives protector reconstruction with persisted keys");
    var smtp = new TransportProbe();
    var push = new EmailPushService(scope, initializer, secrets, smtp);
    var registrations = new ServiceCollection();
    registrations.AddSingleton(scope);
    registrations.AddSingleton<IEmailPushDatabaseInitializer>(initializer);
    registrations.AddSingleton<IEmailSecretProtector>(secrets);
    var containerBuilder = new ContainerBuilder();
    containerBuilder.Populate(registrations);
    containerBuilder.RegisterModule(new AutofacModuleRegister());
    using (var container = containerBuilder.Build())
    using (var lifetime = container.BeginLifetimeScope())
    {
        Require(ReferenceEquals(initializer, lifetime.Resolve<IEmailPushDatabaseInitializer>()) &&
            ReferenceEquals(secrets, lifetime.Resolve<IEmailSecretProtector>()), "assembly scanning cannot replace mail initialization/key singletons");
    }
    var admin = Access("role-admin");
    var user = Access("u1");
    var other = Access("u2");
    Require(await push.IsAdministratorAsync(admin), "R_ADMIN user need not be named admin");
    await RejectAsync(() => push.GetSettingAsync(user), "ordinary role cannot read SMTP configuration");
    await RejectAsync(() => push.SaveSettingAsync(user, Setting(1)), "ordinary role cannot save SMTP configuration");
    await RejectAsync(() => push.GetSettingAsync(Access("u1", "*")), "wildcard permission alone is not an administrator role");
    await RejectAsync(() => push.EnqueueAsync(Access("u1", "AgentAssistant"), Submission("readonly-assistant")),
        "read-only query-assistant permission cannot grant email side effects");
    var saved = await push.SaveSettingAsync(admin, Setting(1));
    Require(db.DbMaintenance.GetColumnInfosByTableName("EmailPushTask", false).Single(x => x.DbColumnName == "Subject").DataType == "nvarchar",
        "new mail subjects use explicit Unicode columns");
    Require(saved.HasPassword && !JsonConvert.SerializeObject(saved).Contains(fakeSecret), "config DTO never returns password");
    var keepPassword = Setting(saved.Version);
    keepPassword.Password = "";
    await push.SaveSettingAsync(admin, keepPassword);
    Require(secrets.Unprotect((await db.Queryable<EmailPushSettingEntity>().InSingleAsync(1)).ProtectedPassword!) == fakeSecret,
        "blank password preserves encrypted credentials");
    await RejectAsync(() => push.SaveSettingAsync(admin, Setting(1)), "stale config version rejected");
    var changedAccount = Setting((await push.GetSettingAsync(admin)).Version);
    changedAccount.Password = "";
    changedAccount.UserName = "different@example.test";
    await RejectAsync(() => push.SaveSettingAsync(admin, changedAccount), "account change cannot reuse old password");
    var recipients = await push.GetRecipientsAsync(user, "empty-email", 1, 20);
    Require(recipients.Records.Single().Available == false, "missing email is selectable only as a disabled option");
    var noConfirm = Submission("no-confirm");
    noConfirm.Confirm = false;
    await RejectAsync(() => push.EnqueueAsync(user, noConfirm), "confirmation required");
    var missing = Submission("missing");
    missing.RecipientUserIds = ["no-mail"];
    await RejectAsync(() => push.EnqueueAsync(user, missing), "invalid recipient email rejected");
    var queued = await push.EnqueueAsync(user, Submission("same-key"));
    var duplicate = await push.EnqueueAsync(user, Submission("same-key"));
    Require(queued.TaskId == duplicate.TaskId && duplicate.Duplicate, "same request does not enqueue twice");
    var conflicting = Submission("same-key");
    conflicting.Subject = "different";
    await RejectAsync(() => push.EnqueueAsync(user, conflicting), "same idempotency key cannot identify different content");
    Require(smtp.Sends == 0, "queue submission performs no SMTP calls");
    var stored = await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == queued.TaskId);
    Require(stored.SentTime == null && stored.LeaseExpireTime == null, "unsent SQL timestamps are genuinely NULL");
    await push.DispatchAsync(CancellationToken.None);
    Require((await db.Queryable<EmailPushDeliveryEntity>().InSingleAsync(stored.Id)).Status == "Sent" && smtp.Sends == 1,
        "consumer updates server-accepted state");
    Require((await push.GetRecordsAsync(other, new())).Total == 0 && (await push.GetRecordsAsync(user, new())).Total == 1,
        "ordinary records restricted to creator");
    Require((await push.GetRecordsAsync(admin, new())).Total == 1, "administrator can inspect all records");

    var unknown = await push.EnqueueAsync(user, Submission("unknown"));
    await ClearThrottleAsync();
    smtp.NextStatus = "Unknown";
    await push.DispatchAsync(CancellationToken.None);
    var unknownRow = await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == unknown.TaskId);
    Require(unknownRow.Status == "Unknown", "uncertain SMTP outcome marked Unknown");
    await ClearThrottleAsync();
    var sendsBefore = smtp.Sends;
    await push.DispatchAsync(CancellationToken.None);
    Require(smtp.Sends == sendsBefore, "unknown delivery never automatically repeats");
    await RejectAsync(() => push.RetryAsync(admin, unknownRow.Id, new() { Confirm = true }), "manual Unknown retry needs duplicate-risk acceptance");
    await push.RetryAsync(admin, unknownRow.Id, new() { Confirm = true, AcknowledgeDuplicateRisk = true });
    await ClearThrottleAsync();
    await push.DispatchAsync(CancellationToken.None);
    Require((await db.Queryable<EmailPushDeliveryEntity>().InSingleAsync(unknownRow.Id)).Status == "Sent", "explicit manual retry supported");
    var changedMailbox = await push.EnqueueAsync(user, Submission("changed-mailbox"));
    await db.Updateable<SysUserInfo>().SetColumns(x => x.Email == "changed@example.test").Where(x => x.UserId == "u2").ExecuteCommandAsync();
    await ClearThrottleAsync();
    await push.DispatchAsync(CancellationToken.None);
    Require((await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == changedMailbox.TaskId)).Status == "Cancelled",
        "recipient email changes cannot silently redirect queued mail");
    await db.Updateable<SysUserInfo>().SetColumns(x => x.Email == "receiver@example.test").Where(x => x.UserId == "u2").ExecuteCommandAsync();
    var parallel1 = await push.EnqueueAsync(user, Submission("parallel-1"));
    var parallel2 = await push.EnqueueAsync(user, Submission("parallel-2"));
    await ClearThrottleAsync();
    sendsBefore = smtp.Sends;
    await Task.WhenAll(push.DispatchAsync(CancellationToken.None), push.DispatchAsync(CancellationToken.None));
    Require(smtp.Sends == sendsBefore + 1, "database lease and throttle protect parallel workers");
    await push.DispatchAsync(CancellationToken.None);
    Require(smtp.Sends == sendsBefore + 1, "interval blocks immediate next send");
    await db.Updateable<EmailPushSettingEntity>().SetColumns(x => x.Enabled == false).Where(x => x.Id == 1).ExecuteCommandAsync();
    await ClearThrottleAsync();
    await push.DispatchAsync(CancellationToken.None);
    Require(smtp.Sends == sendsBefore + 1, "disabled service pauses existing tasks");
    await RejectAsync(() => push.EnqueueAsync(user, Submission("disabled-service")), "disabled service does not accept new mail");
    await db.Updateable<EmailPushSettingEntity>().SetColumns(x => x.Enabled == true).Where(x => x.Id == 1).ExecuteCommandAsync();
    await ClearThrottleAsync();
    await push.DispatchAsync(CancellationToken.None); // drain second concurrent request
    var retryable = await push.EnqueueAsync(user, Submission("retryable"));
    for (var attempt = 1; attempt <= 3; attempt++)
    {
        await ClearThrottleAsync();
        var past = DateTime.Now.AddMinutes(-1);
        await db.Updateable<EmailPushDeliveryEntity>().SetColumns(x => x.NextAttemptTime == past)
            .Where(x => x.TaskId == retryable.TaskId).ExecuteCommandAsync();
        smtp.NextStatus = "Retryable";
        await push.DispatchAsync(CancellationToken.None);
    }
    var exhausted = await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == retryable.TaskId);
    Require(exhausted.Status == "Failed" && exhausted.AttemptCount == 3, "temporary failures stop at configured maximum attempts");
    var interrupted = await push.EnqueueAsync(user, Submission("interrupted"));
    var expired = DateTime.Now.AddMinutes(-1);
    await db.Updateable<EmailPushDeliveryEntity>()
        .SetColumns(x => new EmailPushDeliveryEntity { Status = "Processing", LeaseExpireTime = expired })
        .Where(x => x.TaskId == interrupted.TaskId).ExecuteCommandAsync();
    sendsBefore = smtp.Sends;
    await ClearThrottleAsync();
    await push.DispatchAsync(CancellationToken.None);
    Require((await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == interrupted.TaskId)).Status == "Unknown" &&
        smtp.Sends == sendsBefore, "interrupted processing is not automatically resent after restart");
    EmailPushSchema.Initialize(db);
    Require(await db.Queryable<EmailPushTaskEntity>().AnyAsync(x => x.Id == queued.TaskId), "repeat schema preserves tasks and keys");

    // Simulate an earlier populated table, including an incorrectly NOT NULL timestamp.
    using (var legacy = Open(legacyName))
    {
        var old = legacy.GetConnectionScope("calmadmin");
        old.Ado.ExecuteCommand("""
            CREATE TABLE EmailPushSetting (Id int NOT NULL PRIMARY KEY, Enabled bit NOT NULL,
                Host nvarchar(256) NOT NULL, ProtectedPassword nvarchar(4000) NOT NULL);
            INSERT INTO EmailPushSetting VALUES (1,0,N'legacy.smtp.example.test',N'old-ciphertext');
            CREATE TABLE EmailPushDelivery (Id bigint IDENTITY(1,1) PRIMARY KEY, TaskId bigint NOT NULL,
                SentTime datetime NOT NULL);
            INSERT INTO EmailPushDelivery(TaskId,SentTime) VALUES (123,'2026-01-01');
            """);
        EmailPushSchema.Initialize(old);
        EmailPushSchema.Initialize(old);
        Require(old.Queryable<EmailPushSettingEntity>().InSingle(1).Host == "legacy.smtp.example.test", "populated legacy config retained");
        var legacyDelivery = old.Queryable<EmailPushDeliveryEntity>().InSingle(1);
        Require(legacyDelivery.TaskId == 123 && legacyDelivery.SentTime.HasValue, "legacy timestamp values preserved");
        old.Insertable(new EmailPushDeliveryEntity { TaskId = 124, RecipientUserId = "u2", Address = "receiver@example.test" }).ExecuteCommand();
        Require(old.DbMaintenance.GetColumnInfosByTableName("EmailPushDelivery", false).Single(x => x.DbColumnName == "SentTime").IsNullable,
            "legacy timestamp constraint repaired and new NULL insert succeeds");
    }

    var applicationService = DispatchProxy.Create<ISystemApplicationService, ApplicationProbe>();
    ApplicationProbe.Accesses = new() { ["role-admin"] = admin, ["u1"] = user };
    var http = new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "test"))
        }
    };
    var adapter = new EmailService(push, applicationService, http, scope);
    var executor = new AgentEmailBusinessToolExecutor(adapter, push, applicationService, http);
    var context = new AgentBusinessToolContext { UserId = "u1", AgentCode = "project-management", ConversationId = 1, CorrelationId = "test-plan-1" };
    var arguments = JsonSerializer.SerializeToElement(new { recipientUserIds = new[] { "u2" }, subject = "Agent test", body = "queue only" });
    var executed = await executor.ExecuteAsync("SendEmail", arguments, context);
    var agentResult = (AgentEmailSendResultDto)executed.Data!;
    Require(agentResult.TaskId > 0 && agentResult.SendTime == null, "Agent adapter returns queued task without claiming it has already sent");
    var repeated = await executor.ExecuteAsync("SendEmail", arguments, context);
    Require(((AgentEmailSendResultDto)repeated.Data!).TaskId == agentResult.TaskId, "same Agent execution correlation is idempotent");
    var lookup = await executor.ExecuteAsync("GetEmailRecipients", JsonSerializer.SerializeToElement(new { keyword = "receiver" }), context);
    Require(((EmailPage<EmailRecipientDto>)lookup.Data!).Records.Single().UserId == "u2", "Agent can resolve actual user IDs before sending");
    await RejectAsync(() => adapter.SendAsync(new AgentEmailSendRequest
    {
        UserId = "u1", ToEmails = ["not-a-user@example.test"], Subject = "test", Body = "test", Confirm = true, IdempotencyKey = "legacy-invalid"
    }), "legacy parameters cannot become an arbitrary external-mail relay");
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(scope);
    builder.Services.AddSingleton<IEmailPushService>(push);
    builder.Services.AddSingleton(applicationService);
    builder.Services.AddControllers(options => options.Filters.Add<SystemApplicationAccessFilter>())
        .AddApplicationPart(typeof(EmailPushController).Assembly).AddNewtonsoftJson();
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
    builder.Services.AddAuthorization();
    app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    await app.StartAsync();
    var startupClock = System.Diagnostics.Stopwatch.StartNew();
    using (var background = new EmailPushHostedService(app.Services.GetRequiredService<IServiceScopeFactory>(), initializer,
               new LoggerFactory().CreateLogger<EmailPushHostedService>()))
    {
        await background.StartAsync(CancellationToken.None);
        Require(startupClock.Elapsed < TimeSpan.FromSeconds(1), "background service does not wait for SMTP or SQL initialization in StartAsync");
        await background.StopAsync(CancellationToken.None);
    }
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    Require((await client.GetAsync("/api/email-push/setting")).StatusCode == HttpStatusCode.Unauthorized, "email API remains JWT-protected");
    client.DefaultRequestHeaders.Authorization = new("Bearer", "u1");
    Require((await client.GetAsync("/api/email-push/setting")).StatusCode == HttpStatusCode.Forbidden, "HTTP ordinary user cannot read configuration");
    Require((await client.PostAsJsonAsync("/api/email-push/setting", Setting(3))).StatusCode == HttpStatusCode.Forbidden,
        "HTTP ordinary user cannot save configuration");
    Require((await client.GetAsync("/api/email-push/recipients?size=10")).IsSuccessStatusCode, "dedicated user selector endpoint usable without user-management permission");
    var sendResponse = await client.PostAsJsonAsync("/api/email-push/send", Submission("http-send"));
    Require(sendResponse.IsSuccessStatusCode, "HTTP send enqueues through real controller and service: " + await sendResponse.Content.ReadAsStringAsync());
    client.DefaultRequestHeaders.Authorization = new("Bearer", "role-admin");
    Require((await client.GetAsync("/api/email-push/setting")).IsSuccessStatusCode, "HTTP administrator role config access");
    var configText = await client.GetStringAsync("/api/email-push/setting");
    Require(!configText.Contains(fakeSecret) && !configText.Contains(encrypted), "HTTP config does not leak plaintext or protected password");
    Console.WriteLine("PASS: isolated SQL Server fresh/legacy/repeat schemas, administrator-role permissions, encrypted settings, queue/idempotency, per-user records, uncertain outcomes, throttle, menu migration and real HTTP boundaries. No external mail sent.");

    async Task ClearThrottleAsync()
    {
        await db.Updateable<EmailPushSettingEntity>().SetColumns(x => x.NextSendTime == (DateTime?)null).Where(x => x.Id == 1).ExecuteCommandAsync();
    }
}
finally
{
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    foreach (var database in names)
    {
        if (!database.StartsWith("CodexEmailPush_", StringComparison.Ordinal) || database.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '_'))
            throw new InvalidOperationException("Unsafe test database cleanup target");
        using var cleanup = new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", master);
        await cleanup.ExecuteNonQueryAsync();
    }
    if (Path.GetFileName(root) == name) Directory.Delete(root, true);
}

SqlSugarScope Open(string database) => new(new ConnectionConfig
{
    ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true,
    ConnectionString = new SqlConnectionStringBuilder(serverConnection) { InitialCatalog = database }.ConnectionString
});

static SysUserInfo User(string id, string name, string email, string role) => new()
{
    UserId = id, UserName = name, NickName = name, Password = "test-only", Email = email,
    Roles = role, PermissionKeys = "EmailPush", Buttons = "", Enabled = true, CreateTime = DateTime.Now
};
static ApplicationAccessContext Access(string id, string permission = "EmailPush") => new()
{
    UserId = id, AppCode = "A", Permissions = new(StringComparer.OrdinalIgnoreCase) { permission }
};
static EmailSettingSaveDto Setting(int version) => new()
{
    Enabled = true, Host = "smtp.example.test", UserName = "sender@example.test", FromAddress = "sender@example.test",
    Password = "isolated-test-secret-not-a-real-mailbox-password", FromName = "Test", Version = version
};
static EmailEnqueueDto Submission(string key) => new()
{
    RecipientUserIds = ["u2"], Subject = "isolated-test", Body = "No real SMTP delivery", IdempotencyKey = key, Confirm = true
};
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task RejectAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return; }
    throw new InvalidOperationException("Expected rejection: " + message);
}

public sealed class TransportProbe : IEmailTransport
{
    public int Sends;
    public string NextStatus = "Sent";
    public Task TestAsync(EmailPushSettingEntity setting, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting, EmailPushTaskEntity task,
        EmailPushDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Sends);
        var result = NextStatus;
        NextStatus = "Sent";
        return Task.FromResult(new EmailTransportResult { Status = result });
    }
}
public sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "EmailPushSmoke";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
public class ApplicationProbe : DispatchProxy
{
    public static Dictionary<string, ApplicationAccessContext> Accesses = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "GetAccessAsync")
        {
            var id = ((ClaimsPrincipal)args![0]!).FindFirstValue(ClaimTypes.NameIdentifier)!;
            return Task.FromResult(Accesses[id]);
        }
        throw new NotSupportedException(method?.Name);
    }
}
public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, value[7..])], "test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
    }
}
