using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using calmadmin.Config;
using calmadmin.Controllers;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.Agent;
using Model.EdgeGateway;
using Model.EmailPush;
using Model.ExternalTools;
using Model.HourlyProduction;
using Model.Menu;
using Model.SystemApplications;
using Service;
using SqlSugar;
using ModelContextProtocol.Protocol;
using Autofac;

var database = "CodexHourly_" + Guid.NewGuid().ToString("N");
var connection = Environment.GetEnvironmentVariable("HOURLY_SMOKE_SQL") ??
    @"Server=(localdb)\CodexHourly20261022;Integrated Security=true;Encrypt=false;Connect Timeout=10";
await using var master = new SqlConnection(new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
await new SqlCommand($"CREATE DATABASE [{database}] COLLATE Chinese_PRC_CI_AS", master).ExecuteNonQueryAsync();
SqlSugarScope? scope = null;
WebApplication? app = null;
var mailCount = 0;
var wxCount = 0;
var modelFails = false;
var ownerRevoked = false;
var stopUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
        ConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = database }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    await new SystemDatabaseInitializer(scope, new LoggerFactory().CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
    db.CodeFirst.InitTables<EdgeMessageQueueEntity>();
    var catalog = new HourlyProductionCatalog();
    var initializer = new HourlyProductionInitializer(scope, catalog);
    await initializer.InitializeAsync(default);
    await initializer.InitializeAsync(default);
    Check(await db.Queryable<sys_menu>().CountAsync(x => x.MenuName == "HourlyProduction") == 1, "dynamic menu initialized exactly once");
    Check(await db.Queryable<SysApplicationMenuEntryEntity>().AnyAsync(x => x.AppCode == "B" && x.PermissionKey == "HourlyProduction"), "B independent route initialized");
    var applications = Mock<ISystemApplicationService>((method, args) => method.Name switch
    {
        "GetAccessAsync" => Task.FromResult(Access(((ClaimsPrincipal)args![0]!).FindFirstValue(ClaimTypes.NameIdentifier) ?? "owner")),
        "GetChannelAccessAsync" => ownerRevoked ? throw new UnauthorizedAccessException("revoked") : Task.FromResult(Access((string)args![0]!)),
        _ => throw new NotSupportedException(method.Name)
    });
    var email = Mock<IEmailPushService>((method, _) =>
    {
        if (method.Name != "EnqueueAsync") throw new NotSupportedException(method.Name);
        mailCount++;
        return Task.FromResult(new EmailEnqueueResult { TaskId = mailCount });
    });
    var wx = Mock<IWeChatPushQueueService>((method, _) =>
    {
        if (method.Name != "EnqueueAsync") throw new NotSupportedException(method.Name);
        wxCount++;
        return Task.FromResult<object>(new { taskIds = new[] { wxCount }, status = "Pending" });
    });
    var model = Mock<IAgentModelClient>((method, _) => method.Name == "GenerateReplyAsync"
        ? Task.FromResult(new AgentModelReply { Success = !modelFails, Content = modelFails ? "" : "关注两线气密工序产出差异。" })
        : throw new NotSupportedException(method.Name));
    var service = new HourlyProductionService(scope, catalog, applications, email, wx, model);
    var ingress = new HourlyProductionIngress(scope, catalog);
    var components = new ContainerBuilder();
    components.RegisterModule(new 上传MES.Config.AutofacModuleRegister());
    components.RegisterInstance(scope).ExternallyOwned();
    components.RegisterInstance(catalog);
    components.RegisterInstance(initializer).As<IHourlyProductionInitializer>();
    components.RegisterInstance(applications);
    components.RegisterInstance(email);
    components.RegisterInstance(wx);
    components.RegisterInstance(model);
    using (var container = components.Build())
    {
        Check(ReferenceEquals(container.Resolve<HourlyProductionCatalog>(), catalog) &&
            ReferenceEquals(container.Resolve<IHourlyProductionInitializer>(), initializer), "Autofac preserves singleton readiness and initializer");
        Check(container.Resolve<IHourlyProductionService>() is HourlyProductionService &&
            container.Resolve<IHourlyProductionIngress>() is HourlyProductionIngress, "production Autofac resolves hourly services without constructor DDL or network");
    }
    var board = NewBoard("test");
    board = await service.SaveAsync(Access("owner"), board);
    var date = HourlyProductionRules.Now.Date;
    async Task Send(string id, string sn, DateTime time, string? extra = null)
    {
        var data = new Dictionary<string, object?> { ["SN"] = sn, ["passTime"] = time.ToString("yyyy-MM-dd HH:mm:ss") };
        if (extra != null) data["rework"] = extra;
        await ingress.CaptureRealtimeAsync("tenant", "machine", id, time, [JsonSerializer.Serialize(data)], default);
    }
    // Arrival order differs from event time. The early event must migrate the output to 08:00,
    // while 09:10 becomes a rework; transport retries and <=30m repeats cannot count twice.
    await Send("later", "SN-一", date.AddHours(9).AddMinutes(10));
    await service.TickAsync(default);
    await Send("earlier", "SN-一", date.AddHours(8));
    await Send("earlier", "SN-一", date.AddHours(8));
    await Send("noise", "SN-一", date.AddHours(8).AddMinutes(20));
    await Send("new-sn", "SN-二", date.AddHours(8).AddMinutes(50));
    await Send("cross-hour-noise", "SN-二", date.AddHours(9).AddMinutes(10));
    await ingress.CaptureRealtimeAsync("tenant", "machine", "only-temperature", date.AddHours(9), ["{\"temperature\":25}"], default);
    await service.TickAsync(default);
    var buckets = await db.Queryable<HourlyProductionBucketEntity>().Where(x => x.BoardCode == "test").ToListAsync();
    Check(buckets.Sum(x => x.Output) == 2 && buckets.Sum(x => x.Rework) == 1, "out-of-order replay and 30m suppression");
    Check(buckets.Single(x => x.SlotStart == date.AddHours(8)).Output == 2, "backfilled events go to occurrence hour");
    Check(await db.Queryable<HourlyProductionPassEntity>().CountAsync() == 5, "transport retry and missing-SN incremental frame do not become passes");
    await service.TickAsync(default);
    Check((await db.Queryable<HourlyProductionBucketEntity>().ToListAsync()).Sum(x => x.Output) == 2, "worker repeat idempotency");
    await Denied(() => service.SaveAsync(Access("reader"), NewBoard("denied")), "read-only cannot save");
    await Denied(() => service.RuntimeAsync(Access("other-app"), "test"), "A/B boundary");
    await Denied(() => service.RuntimeAsync(Access("limited"), "test"), "resource boundary");
    await Denied(() => service.RuntimeAsync(null, "test", true), "unpublished board not public");
    var changed = JsonSerializer.Deserialize<HourlyBoardDefinition>(JsonSerializer.Serialize(board))!;
    changed.Lines[0].Processes[0].ReworkMinutes = 10;
    await Denied(() => service.SaveAsync(Access("owner"), changed), "existing statistics rule cannot silently change");
    board.Published = true;
    board.AiEnabled = true;
    board.Lines[0].Targets = Enumerable.Repeat<decimal?>(2, 12).ToList();
    board = await service.SaveAsync(Access("owner"), board);
    var runtime = await service.RuntimeAsync(null, "test", true);
    Check(runtime.Lines.Count == 2 && runtime.Lines[0].TotalOutput == 2, "public runtime and final-process summary");
    Check(runtime.Lines[0].SummaryPoints[0].Target == 2 && runtime.Lines[0].Processes[0].Points[0].Target == 1,
        "line target can differ from process target without mutating process chart");
    Check(!JsonSerializer.Serialize(runtime).Contains("SN-一"), "public output does not leak SN");
    await service.RefreshAdviceAsync(default);
    Check((await service.RuntimeAsync(null, "test", true)).Advice != null, "AI cached separately");
    modelFails = true;
    await db.Updateable<HourlyProductionAdviceEntity>().SetColumns(x => x.AttemptedAt == date.AddDays(-1)).Where(x => x.BoardCode == "test").ExecuteCommandAsync();
    await service.RefreshAdviceAsync(default);
    Check((await service.RuntimeAsync(null, "test", true)).Advice == null, "model failure hides suggestion");

    db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity>();
    var snapshotTime = new DateTimeOffset(date.AddHours(12), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    await db.Insertable(new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "tenant", DeviceCode = "machine", SourceTimestamp = snapshotTime,
        DataTime = date.AddHours(12), DataListJson = "[{\"data\":{\"SN\":\"newest-snapshot\"}}]"
    }).ExecuteCommandAsync();
    var mapper = new AutoMapper.MapperConfiguration(config => config.AddProfile<上传MES.Config.AutoMapperConfigs>()).CreateMapper();
    var receiver = new EdgeRealtimeDataService(scope, mapper,
        Mock<IEdgeGatewayFeatureService>((_, _) => Task.CompletedTask), Mock<IEdgeDashboardTelemetryProcessor>(),
        Mock<IEdgeDashboardService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<EdgeRealtimeDataService>.Instance, ingress);
    var oldSnapshotResult = await receiver.ReceiveAsync(new EdgeRealtimeDataReceiveDto
    {
        TenantMark = "tenant", DeviceMark = "machine", MessageId = "older-snapshot-two-passes",
        Time = new DateTimeOffset(date.AddHours(8), TimeSpan.FromHours(8)).ToUnixTimeMilliseconds(),
        DataList =
        [
            new EdgeRealtimeDataItemDto { Data = new() { ["SN"] = "batch-1", ["passTime"] = date.AddHours(8).ToString("O") } },
            new EdgeRealtimeDataItemDto { Data = new() { ["SN"] = "batch-2", ["passTime"] = date.AddHours(8).ToString("O") } }
        ]
    }, default);
    await service.TickAsync(default);
    Check(oldSnapshotResult.IgnoredOlderCount == 1 &&
        await db.Queryable<HourlyProductionPassEntity>().CountAsync(x => x.Sn == "batch-1" || x.Sn == "batch-2") == 2,
        "real realtime receiver persists ALL batch passes before ignoring old snapshot");
    Check((await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync()).DataListJson.Contains("newest-snapshot"),
        "late pass does not roll back latest device values");

    var sumBoard = NewBoard("sum");
    var sumProcess = sumBoard.Lines[0].Processes[0];
    sumProcess.Mode = "Sum";
    var sumBinding = sumProcess.Bindings[0];
    sumBinding.SourceKey = "calculations";
    sumBinding.DeviceKey = "sum-device";
    sumBinding.TimePath = "";
    sumBinding.QuantityPath = "/data/quantity";
    sumBinding.ReworkPath = "/data/rework";
    await service.SaveAsync(Access("owner"), sumBoard);
    await ingress.CaptureRealtimeAsync("calculations", "sum-device", "sum1", date.AddHours(8), ["{\"quantity\":3.123456}"], default);
    await ingress.CaptureRealtimeAsync("calculations", "sum-device", "sum1", date.AddHours(8), ["{\"quantity\":3.123456}"], default);
    await ingress.CaptureRealtimeAsync("calculations", "sum-device", "sum2", date.AddHours(8), ["{\"quantity\":2,\"rework\":1}"], default);
    var countBoard = NewBoard("count");
    var countProcess = countBoard.Lines[0].Processes[0];
    countProcess.Mode = "Count";
    var countBinding = countProcess.Bindings[0];
    countBinding.SourceKey = "calculations";
    countBinding.DeviceKey = "count-device";
    countBinding.TimePath = "";
    countBinding.FilterPath = "/data/result";
    countBinding.FilterValue = "OK";
    await service.SaveAsync(Access("owner"), countBoard);
    await ingress.CaptureRealtimeAsync("calculations", "count-device", "count1", date.AddHours(8), ["{\"result\":\"OK\"}", "{\"result\":\"NG\"}"], default);
    await service.TickAsync(default);
    var sumBucket = await db.Queryable<HourlyProductionBucketEntity>().FirstAsync(x => x.BoardCode == "sum");
    Check(sumBucket.Output == 3.123456m && sumBucket.Rework == 2m, "six-decimal sum, explicit rework and transport idempotency");
    Check((await db.Queryable<HourlyProductionBucketEntity>().FirstAsync(x => x.BoardCode == "count")).Output == 1,
        "conditional event count uses actual matching frames");
    var caseBoard = NewBoard("case-sensitive");
    caseBoard.Lines[0].Processes[0].Bindings[0].DeviceKey = "case-device";
    await service.SaveAsync(Access("owner"), caseBoard);
    foreach (var sn in new[] { "SN-Aa", "SN-aa" })
        await ingress.CaptureRealtimeAsync("tenant", "case-device", sn, date.AddHours(8),
            [JsonSerializer.Serialize(new { SN = sn, passTime = date.AddHours(8).ToString("O") })], default);
    await service.TickAsync(default);
    Check((await db.Queryable<HourlyProductionBucketEntity>().FirstAsync(x => x.BoardCode == "case-sensitive")).Output == 2,
        "case-insensitive SQL collation does not merge distinct SN identifiers");

    var night = NewBoard("night");
    night.StartTime = "20:00";
    night.EndTime = "08:00";
    var window = HourlyProductionRules.Window(night, date.AddDays(1).AddHours(1));
    Check(window.Start == date.AddHours(20) && window.End == date.AddDays(1).AddHours(8), "overnight production day");
    Check(HourlyProductionRules.Slots(night, date.AddHours(21)).Count == 12, "night has twelve hourly slots");
    night.Lines[0].Processes[0].Bindings[0].DeviceKey = "night-machine";
    await service.SaveAsync(Access("owner"), night);
    foreach (var time in new[] { date.AddHours(21), date.AddDays(1).AddHours(1) })
    {
        var utc = new DateTimeOffset(time, TimeSpan.FromHours(8)).UtcDateTime.ToString("O");
        await ingress.CaptureRealtimeAsync("tenant", "night-machine", "night-" + time.ToString("O"), time,
            [JsonSerializer.Serialize(new { SN = "night-sn", passTime = utc })], default);
    }
    await service.TickAsync(default);
    var nightPasses = await db.Queryable<HourlyProductionPassEntity>().Where(x => x.BoardCode == "night").ToListAsync();
    Check(nightPasses.Count == 2 && nightPasses.All(x => x.ProductionDate == date) && nightPasses.Sum(x => x.Rework) == 1,
        "real SQL night passes use China time, one production day and correct rework");

    // Real center queue projection with subscribed-device filtering; same gateway is not the same machine.
    var edgeBoard = NewBoard("edge");
    var binding = edgeBoard.Lines[0].Processes[0].Bindings[0];
    binding.Kind = "Edge";
    binding.SourceKey = "ws";
    binding.DeviceKey = "gateway";
    binding.SourceDeviceCode = "machine-one";
    await service.SaveAsync(Access("owner"), edgeBoard);
    foreach (var device in new[] { "machine-one", "machine-two" })
    {
        var message = new EdgeStandardMessageDto
        {
            MessageId = Guid.NewGuid().ToString("N"), DeviceId = "gateway", SourceId = "ws",
            SourceDeviceCode = device, Timestamp = date.AddHours(8),
            Values = new() { ["SN"] = device, ["passTime"] = date.AddHours(8).ToString("O") }
        };
        await db.Insertable(new EdgeMessageQueueEntity
        {
            MessageId = message.MessageId, DeviceCode = "gateway", SourceId = "ws", DataType = "Telemetry",
            StandardPayloadJson = JsonSerializer.Serialize(message), CreateTime = HourlyProductionRules.Now
        }).ExecuteCommandAsync();
    }
    await service.TickAsync(default);
    Check(await db.Queryable<HourlyProductionPassEntity>().CountAsync(x => x.BoardCode == "edge") == 1, "edge subscribed device filtering");

    // Existing non-empty tables missing new nullable columns must upgrade without data loss.
    await db.Ado.ExecuteCommandAsync("ALTER TABLE HourlyProductionInbox DROP COLUMN SourceDeviceCode, Error;");
    await db.Ado.ExecuteCommandAsync("ALTER TABLE HourlyProductionInbox ALTER COLUMN DeviceKey varchar(128) NOT NULL;");
    await new HourlyProductionInitializer(scope, new HourlyProductionCatalog()).InitializeAsync(default);
    await new HourlyProductionInitializer(scope, new HourlyProductionCatalog()).InitializeAsync(default);
    Check(await db.Queryable<HourlyProductionInboxEntity>().CountAsync() >= 1, "populated legacy table retained and repeat initialization succeeds");
    var unicode = new HourlyProductionInboxEntity
    {
        EventKey = HourlyProductionRules.Hash("unicode-boundary"), SourceKey = new string('源', 128),
        DeviceKey = new string('设', 128), SourceDeviceCode = new string('机', 128),
        OccurredAt = HourlyProductionRules.Now, ReceivedAt = HourlyProductionRules.Now, ProcessedAt = HourlyProductionRules.Now
    };
    unicode.Id = await db.Insertable(unicode).ExecuteReturnBigIdentityAsync();
    Check((await db.Queryable<HourlyProductionInboxEntity>().InSingleAsync(unicode.Id)).DeviceKey.Length == 128,
        "128 Chinese-character source identifiers survive schema upgrade and real insert without truncation");
    board.Push.Enabled = true;
    board.Push.EmailUserIds = ["receiver"];
    board.Push.WeChatUserIds = ["receiver"];
    board = await service.SaveAsync(Access("owner"), board);
    var freshDispatch = new HourlyProductionDispatchEntity
    {
        JobKey = HourlyProductionRules.Hash("nullable-insert"), BoardCode = "test", ReportId = 1,
        Channel = "Email", RecipientId = "receiver", OwnerId = "owner", AppCode = "A", Content = "test",
        CreatedAt = HourlyProductionRules.Now
    };
    freshDispatch.Id = await db.Insertable(freshDispatch).ExecuteReturnBigIdentityAsync();
    Check((await db.Queryable<HourlyProductionDispatchEntity>().InSingleAsync(freshDispatch.Id)).Error == null,
        "real insert respects nullable error/result and generated primary key");
    await service.TickAsync(default);
    Check(mailCount == 1 && (await db.Queryable<HourlyProductionDispatchEntity>().InSingleAsync(freshDispatch.Id)).Status == "Queued",
        "report dispatch uses existing queue only");
    await service.TickAsync(default);
    Check(mailCount == 1, "queued report not sent twice");
    await Denied(() => service.RetryDispatchAsync(Access("owner"), freshDispatch.Id, true), "queued report cannot be retried as failed");
    await db.Updateable<HourlyProductionDispatchEntity>().SetColumns(x => x.Status == "Failed")
        .Where(x => x.Id == freshDispatch.Id).ExecuteCommandAsync();
    await Denied(() => service.RetryDispatchAsync(Access("owner"), freshDispatch.Id, false), "retry requires explicit confirmation");
    await service.RetryDispatchAsync(Access("owner"), freshDispatch.Id, true);
    Check((await db.Queryable<HourlyProductionDispatchEntity>().InSingleAsync(freshDispatch.Id)).Status == "Pending", "explicit failed retry restores pending state");
    // Test-only restore: the fake channel has no idempotency store; actual queues enforce the original JobKey.
    await db.Updateable<HourlyProductionDispatchEntity>().SetColumns(x => x.Status == "Queued")
        .Where(x => x.Id == freshDispatch.Id).ExecuteCommandAsync();
    ownerRevoked = true;
    await db.Insertable(new HourlyProductionDispatchEntity
    {
        JobKey = HourlyProductionRules.Hash("revoked"), BoardCode = "test", ReportId = 1, Channel = "WeChat",
        RecipientId = "receiver", OwnerId = "owner", AppCode = "A", Content = "test", CreatedAt = HourlyProductionRules.Now
    }).ExecuteCommandAsync();
    await service.TickAsync(default);
    Check(wxCount == 0 && await db.Queryable<HourlyProductionDispatchEntity>().AnyAsync(x => x.Status == "Cancelled"), "revoked sender cannot push");

    ownerRevoked = false;
    var scheduled = NewBoard("scheduled");
    scheduled.StartTime = "00:00";
    scheduled.EndTime = "00:00";
    scheduled.Push.Enabled = true;
    scheduled.Push.EmailUserIds = ["receiver"];
    scheduled.Push.WeChatUserIds = ["receiver"];
    scheduled = await service.SaveAsync(Access("owner"), scheduled);
    var activated = HourlyProductionRules.Now.AddHours(-3);
    await db.Updateable<HourlyProductionBoardEntity>().SetColumns(x => x.UpdateTime == activated)
        .Where(x => x.Code == "scheduled").ExecuteCommandAsync();
    await service.TickAsync(default);
    var scheduledReports = await db.Queryable<HourlyProductionReportEntity>().Where(x => x.BoardCode == "scheduled").ToListAsync();
    Check(scheduledReports.Count > 0 && scheduledReports.All(x => x.SlotStart.AddHours(1) < HourlyProductionRules.Now), "scheduler freezes completed hours only");
    var scheduledJobs = await db.Queryable<HourlyProductionDispatchEntity>().Where(x => x.BoardCode == "scheduled").ToListAsync();
    Check(scheduledJobs.Count == scheduledReports.Count * 2 && scheduledJobs.All(x => x.Status == "Queued"), "hour scheduler creates channel-specific idempotent jobs");
    var sentBefore = mailCount + wxCount;
    await service.TickAsync(default);
    Check(mailCount + wxCount == sentBefore, "scheduler restart scan does not duplicate notifications");

    var external = new ExternalToolService(scope, Mock<IEdgeDashboardService>(), Mock<IEdgeGatewayService>(),
        Mock<IProjectManageService>(), Mock<IUserActivityService>(), Mock<IWeChatClawBotService>(),
        new ConfigurationBuilder().Build(), email, wx, service);
    var caller = new ExternalToolCallerContext { IsDirectAccess = true, AllResources = true, ClientId = "anonymous-mcp" };
    Check(external.GetDefinitions(caller).Count(x => x.Name.StartsWith("hourly.")) == 3, "MCP tool discovery contains hourly tools");
    var mcp = await external.ExecuteAsync("hourly.runtime", JsonSerializer.SerializeToElement(new { boardCode = "test" }), caller, "hourly-test");
    Check(mcp.Success, "MCP shared registry actual execution and audit");
    var privateMcp = await external.ExecuteAsync("hourly.runtime", JsonSerializer.SerializeToElement(new { boardCode = "edge" }), caller, "hourly-private");
    Check(!privateMcp.Success, "anonymous MCP cannot see unpublished hourly board");

    // Actual MVC routes with the project's DefaultContractResolver verify camelCase compatibility and anonymous isolation.
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton<IHourlyProductionService>(service);
    builder.Services.AddSingleton(applications);
    builder.Services.AddSingleton(scope);
    builder.Services.AddSingleton(Mock<IEdgeDashboardService>());
    builder.Services.AddSingleton(Mock<ISharedModelDictionaryService>());
    builder.Services.AddSingleton(wx);
    builder.Services.AddSingleton<IExternalToolService>(external);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<SystemApplicationAccessFilter>();
    builder.Services.AddControllers(options => options.Filters.AddService<SystemApplicationAccessFilter>())
        .AddApplicationPart(typeof(HourlyProductionController).Assembly)
        .AddNewtonsoftJson(options => options.SerializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver());
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
    builder.Services.AddAuthorization();
    var handlers = typeof(HourlyProductionController).Assembly.GetType("calmadmin.Controllers.ExternalTools.ExternalMcpV2Handlers")!;
    var listHandler = handlers.GetMethod("ListToolsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    var callHandler = handlers.GetMethod("CallToolAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
        .WithListToolsHandler((request, cancellation) => (ValueTask<ListToolsResult>)listHandler.Invoke(null, [request, cancellation])!)
        .WithCallToolHandler((request, cancellation) => (ValueTask<CallToolResult>)callHandler.Invoke(null, [request, cancellation])!);
    app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    if (Environment.GetEnvironmentVariable("HOURLY_UI_HOLD") == "1") app.MapPost("/__test/stop", () => { stopUi.TrySetResult(); return Results.Ok(); });
    app.MapMcp("/mcp/v2").AllowAnonymous();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    var publicResponse = await client.GetAsync("/api/hourly-production/public/runtime?code=test");
    Check(publicResponse.IsSuccessStatusCode && (await publicResponse.Content.ReadAsStringAsync()).Contains("\"totalOutput\""), "public MVC is anonymous with camelCase nested DTO");
    Check((await client.GetAsync("/api/hourly-production/definitions")).StatusCode == HttpStatusCode.Unauthorized, "anonymous configuration is denied");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    var rpc = await client.PostAsJsonAsync("/mcp/v2", new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } });
    Check(rpc.IsSuccessStatusCode && (await rpc.Content.ReadAsStringAsync()).Contains("hourly.runtime"), "standard MCP V2 tools/list discovers hourly tools");
    var rpcCall = await client.PostAsJsonAsync("/mcp/v2", new
    {
        jsonrpc = "2.0", id = 2, method = "tools/call",
        @params = new { name = "hourly.runtime", arguments = new { boardCode = "test" } }
    });
    var rpcBody = await rpcCall.Content.ReadAsStringAsync();
    Check(rpcCall.IsSuccessStatusCode && !rpcBody.Contains("\"isError\":true") && rpcBody.Contains("TotalOutput"), "standard MCP V2 calls real hourly service");
    client.DefaultRequestHeaders.Add("X-Test-User", "reader");
    Check((await client.PostAsJsonAsync("/api/hourly-production/definitions", NewBoard("blocked"))).StatusCode == HttpStatusCode.Forbidden,
        "read-only JWT cannot write through actual controller/filter");
    await HourlyDeletionSmoke.RunAsync(service, db, Access("owner"));
    await HourlyReaddSmoke.RunAsync(service, ingress, scope, Access("owner"));
    client.DefaultRequestHeaders.Remove("X-Test-User");
    Check((await client.GetAsync("/api/hourly-production/definitions/readd-regression/removed-processes?lineCode=L1")).StatusCode == HttpStatusCode.Unauthorized, "anonymous archive access denied");
    client.DefaultRequestHeaders.Add("X-Test-User", "reader");
    Check((await client.GetAsync("/api/hourly-production/definitions/readd-regression/removed-processes?lineCode=L1")).StatusCode == HttpStatusCode.Forbidden, "read-only account cannot browse configuration archive");
    client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", "owner");
    Check((await client.GetStringAsync("/api/hourly-production/definitions/readd-regression/removed-processes?lineCode=L1")).Contains("\"canRestore\":true"), "archive HTTP response exposes authorized restore metadata with camelCase contract");
    Console.WriteLine("PASS: isolated SQL initialization, legacy nullable migration, real inserts, 30m rework, late events, idempotency, overnight, edge projection, A/B/resource/public permissions, AI failure, report queue, MCP and HTTP.");
    if (Environment.GetEnvironmentVariable("HOURLY_UI_HOLD") == "1")
    {
        var preview = (await service.ListAsync(Access("owner"))).Single(x => x.Code == "readd-regression");
        preview.Name = "工序恢复隔离验证"; preview.Lines[0].Processes.RemoveAll(x => x.Code == "P1");
        await service.SaveAsync(Access("owner"), preview);
        Console.WriteLine("HOURLY_UI_API=" + address);
        await Task.WhenAny(stopUi.Task, Task.Delay(TimeSpan.FromMinutes(60)));
    }
}
finally
{
    if (app != null) await app.DisposeAsync();
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    await new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];", master).ExecuteNonQueryAsync();
}

static HourlyBoardDefinition NewBoard(string code) => new()
{
    Code = code,
    Lines = new[] { "L1", "L2" }.Select(line => new HourlyLineDefinition
    {
        Code = line, Name = line + "生产线", SummaryProcessCode = "P1", Processes =
        [new HourlyProcessDefinition
        {
            Code = "P1", Name = "商检", RowCode = "final", Targets = Enumerable.Repeat<decimal?>(1, 12).ToList(),
            Bindings = line == "L1" ? [new HourlyBinding { Code = "binding", SourceKey = "tenant", DeviceKey = "machine", SnPath = "/data/SN", TimePath = "/data/passTime" }] : []
        }]
    }).ToList()
};

static ApplicationAccessContext Access(string user) => new()
{
    UserId = user, UserName = user, AppCode = user == "other-app" ? "B" : "A", AllResources = user != "limited",
    Permissions = user == "reader" ? new() { "HourlyProduction" } : new() { "*" }
};

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static async Task Denied(Func<Task> action, string label)
{
    try { await action(); }
    catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or ArgumentException) { Check(true, label); return; }
    throw new InvalidOperationException("FAIL: " + label);
}

static T Mock<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
{
    var proxy = DispatchProxy.Create<T, Proxy>();
    ((Proxy)(object)proxy).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name));
    return proxy;
}

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
    }
}
