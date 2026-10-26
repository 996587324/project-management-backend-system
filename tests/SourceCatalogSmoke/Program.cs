using System.Reflection;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Autofac;
using calmadmin.Config;
using calmadmin.Controllers;
using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using Model.EdgeGateway;
using Model.SystemApplications;
using Model.ProcessBoard;
using Newtonsoft.Json;
using Service;
using SqlSugar;

var database = "CodexSourceCatalog_" + Guid.NewGuid().ToString("N");
var connection = @"Server=(localdb)\CodexSourceCatalog20261022;Integrated Security=true;Encrypt=false";
await using var master = new SqlConnection(connection + ";Database=master");
await master.OpenAsync();
await new SqlCommand($"CREATE DATABASE [{database}] COLLATE Chinese_PRC_CI_AS", master).ExecuteNonQueryAsync();
SqlSugarScope? scope = null;
WebApplication? app = null;
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute, ConnectionString = connection + ";Database=" + database
    });
    var db = scope.GetConnectionScope("calmadmin");
    db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity, EdgeDeviceEntity, EdgeWebSocketSourceEntity, EdgeWebSocketSubscriptionDeviceEntity>();
    db.CodeFirst.InitTables<EdgeDashboardDeviceBindingEntity, EdgeDashboardRobotBindingEntity, EdgeDashboardDeviceCardEntity>();
    db.CodeFirst.InitTables<ProcessBoardConfigEntity>();
    var mappings = DispatchProxy.Create<ISharedModelDictionaryService, DictionaryStub>();
    var catalog = new SourceCatalogService(scope, mappings);
    var snapshots = Enumerable.Range(0, 520).Select(index => new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "tenant", DeviceCode = $"device-{index:D4}", ModelMark = index == 519 ? "SECOND" : "ROBOT",
        ReceiveTime = DateTime.Now.AddDays(-5), DataListJson = "[]"
    }).ToList();
    snapshots[1].ReceiveTime = DateTime.Now.AddMinutes(-3);
    snapshots.Add(new() { TenantMark = "other", DeviceCode = "device-0000", ModelMark = "SECRET" });
    snapshots.Add(new() { TenantMark = "tenant", DeviceCode = "DEVICE-0000", ModelMark = "CASESECRET" });
    snapshots.Add(new() { TenantMark = "tenant", DeviceCode = "unclassified", ModelMark = null });
    await db.Insertable(snapshots).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardDeviceBindingEntity { TenantMark = "tenant", SourceDeviceMark = "device-0519", DeviceName = "安规测试仪", LineCode = "line-a" }).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardDeviceBindingEntity { TenantMark = "tenant", SourceDeviceMark = "device-0000", DeviceName = "机密线体别名", LineCode = "hidden-line" }).ExecuteCommandAsync();
    var full = Access(true);
    var models = await catalog.ModelsAsync(full, new() { Purpose = "hourly" });
    Check(models.Total == 5 && models.Records.Single(x => x.ModelMark == "ROBOT").DeviceCount == 519, "authorized model grouping/count");
    Check(Math.Abs((models.Records.Single(x => x.ModelMark == "ROBOT").LatestReceiveTime!.Value - snapshots[1].ReceiveTime).TotalMilliseconds) < 10,
        "model discovery exposes latest actual reception time without reading report JSON");
    var search = await catalog.DevicesAsync(full, new() { Purpose = "hourly", Keyword = "机器人", Page = 6, Size = 100 });
    Check(search.Total == 519 && search.Records.Count == 19 && search.Records.All(x => x.ModelName == "穿片下线机器人"), "Chinese model search before SQL pagination, beyond 500");
    var exact = await catalog.ResolveAsync(full, new() { Purpose = "hourly", SourceKey = "tenant", DeviceKey = "device-0519" });
    Check(exact?.ModelMark == "SECOND", "exact saved selection beyond first 500");
    var byName = await catalog.DevicesAsync(full, new() { Purpose = "hourly", Keyword = "安规测试" });
    Check(byName.Total == 1 && byName.Records[0].DeviceName == "安规测试仪", "existing configured device aliases searchable before paging");
    var limited = Access(false);
    var restrictedModels = await catalog.ModelsAsync(limited, new() { Purpose = "hourly" });
    Check(restrictedModels.Total == 1 && restrictedModels.Records[0].DeviceCount == 1, "resource filtering before counts on case-insensitive SQL database");
    Check(Math.Abs((restrictedModels.Records[0].LatestReceiveTime!.Value - snapshots[0].ReceiveTime).TotalMilliseconds) < 10,
        "latest reception time excludes ungranted devices of the same model");
    Check((await catalog.DevicesAsync(limited, new() { Purpose = "hourly", Keyword = "机密线体别名" })).Total == 0, "ungranted line aliases are not searchable even for an authorized source");
    Check(await catalog.ResolveAsync(limited, new() { Purpose = "hourly", SourceKey = "other", DeviceKey = "device-0000" }) == null, "same-name device in another tenant denied");
    Check(await catalog.ResolveAsync(limited, new() { Purpose = "hourly", SourceKey = "tenant", DeviceKey = "DEVICE-0000" }) == null, "case-variant source denied");
    var gateway = await db.Insertable(new EdgeDeviceEntity { DeviceCode = "gateway", DeviceName = "测试网关" }).ExecuteReturnEntityAsync();
    var source = await db.Insertable(new EdgeWebSocketSourceEntity { DeviceId = gateway.Id, SourceId = "connection", SourceName = "连接一", Endpoint = "ws://not-used", UpdateTime = DateTime.Now }).ExecuteReturnEntityAsync();
    await db.Insertable(new EdgeWebSocketSubscriptionDeviceEntity { WebSocketSourceId = source.Id, DeviceCode = "business-1", DeviceName = "气密测试设备", UpdateTime = DateTime.Now }).ExecuteCommandAsync();
    var edge = await catalog.DevicesAsync(full, new() { Purpose = "trace", Kind = "Edge", Keyword = "气密" });
    Check(edge.Total == 1 && edge.Records[0].GatewayId == gateway.Id && edge.Records[0].SourceKey == "connection" && edge.Records[0].ModelMark == SourceCatalogService.Unclassified, "edge Chinese device search and full compound binding");
    var legacyEdge = await catalog.ResolveAsync(full, new() { Purpose = "b-mapping", Kind = "Edge", GatewayId = gateway.Id, SourceKey = "connection", SourceDeviceCode = "business-1" });
    Check(legacyEdge?.DeviceKey == "gateway", "legacy mapping resolves by persisted gateway ID without capped gateway option list");
    var noEdge = await catalog.DevicesAsync(limited, new() { Purpose = "hourly", Kind = "Edge" });
    Check(noEdge.Total == 0, "unauthorized edge resources absent");
    var edgeAccess = new ApplicationAccessContext
    {
        AppCode = "B", Permissions = new(StringComparer.OrdinalIgnoreCase) { "HourlyProductionConfig" },
        DeviceCodes = new() { "gateway" }, SourceKeys = new() { "connection\u001fgateway" }
    };
    Check((await catalog.ModelsAsync(edgeAccess, new() { Purpose = "hourly", Kind = "Edge" })).Records[0].DeviceCount == 1, "B scoped edge model counts");
    Check((await catalog.ModelsAsync(edgeAccess, new() { Purpose = "hourly", Kind = "Edge" })).Records[0].LatestReceiveTime == null,
        "edge subscription update time is not represented as a report receive time");
    try { await catalog.ModelsAsync(new() { AppCode = "B" }, new()); throw new Exception("permission bypass"); }
    catch (UnauthorizedAccessException) { }

    var lineId = await db.Insertable(new ProcessBoardConfigEntity
    {
        Kind = "Line", Code = "scope-line", Name = "范围测试线体", CreateTime = DateTime.Now, UpdateTime = DateTime.Now,
        DefinitionJson = JsonConvert.SerializeObject(new ProcessBoardDefinition { Kind = "Line", Name = "范围测试线体",
            Devices = [new() { TenantMark = "tenant", DeviceMark = "device-0000" }, new() { TenantMark = "other", DeviceMark = "device-0000" }] })
    }).ExecuteReturnBigIdentityAsync();
    var processAccess = new ApplicationAccessContext
    {
        AppCode = "B", Permissions = new() { "ProcessBoardConfig" }, LineCodes = new() { $"process:{lineId}" },
        SourceKeys = new(StringComparer.Ordinal) { "tenant\u001fdevice-0000", "tenant\u001fdevice-0519" }
    };
    var processModels = await catalog.ModelsAsync(processAccess, new() { Purpose = "process", ProcessLineId = lineId });
    Check(processModels.Total == 1 && processModels.Records[0].DeviceCount == 1, "process line devices intersect user grants before aggregation");
    Check((await catalog.DevicesAsync(processAccess, new() { Purpose = "process", ProcessLineId = lineId, Keyword = "device-0519" })).Total == 0,
        "search cannot return a granted device outside the process line");
    Check(await catalog.ResolveAsync(full, new() { Purpose = "process", ProcessLineId = lineId, SourceKey = "tenant", DeviceKey = "device-0519" }) == null,
        "exact old-selection resolution also obeys process line bounds");
    try { await catalog.ModelsAsync(new() { AppCode = "B", Permissions = new() { "ProcessBoardConfig" } }, new() { Purpose = "process", ProcessLineId = lineId }); throw new Exception("line access bypass"); }
    catch (UnauthorizedAccessException) { }
    var emptyLineId = await db.Insertable(new ProcessBoardConfigEntity { Kind = "Line", Code = "scope-empty", Name = "空线体",
        DefinitionJson = JsonConvert.SerializeObject(new ProcessBoardDefinition { Kind = "Line" }), CreateTime = DateTime.Now, UpdateTime = DateTime.Now }).ExecuteReturnBigIdentityAsync();
    Check((await catalog.ModelsAsync(full, new() { Purpose = "process", ProcessLineId = emptyLineId })).Total == 0,
        "empty line does not fall back to the global device catalog");
    ApplicationStub.ProcessLineId = lineId;

    await db.Insertable(Enumerable.Range(0, 205).Select(index => new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "other", DeviceCode = $"new-{index}", ModelMark = $"M-{index:D3}", ReceiveTime = DateTime.Now
    }).ToList()).ExecuteCommandAsync();
    var modelPages = new List<SourceCatalogModel>();
    for (var page = 1; page <= 3; page++)
        modelPages.AddRange((await catalog.ModelsAsync(full, new() { Purpose = "hourly", Page = page, Size = 100 })).Records);
    Check(modelPages.Count == 210 && modelPages.Select(x => x.ModelMark).Distinct().Count() == 210,
        "discovery pages include all models beyond first hundred");
    Check((await catalog.ModelsAsync(limited, new() { Purpose = "dashboard" })).Total == 1,
        "dashboard model discovery keeps A/B authorization before paging and aggregation");
    var container = new ContainerBuilder();
    container.RegisterModule<上传MES.Config.AutofacModuleRegister>();
    container.RegisterInstance(scope).As<SqlSugarScope>();
    container.RegisterInstance(mappings).As<ISharedModelDictionaryService>();
    using (var built = container.Build()) Check(built.Resolve<ISourceCatalogService>() is SourceCatalogService, "Autofac catalog registration");

    var applications = DispatchProxy.Create<ISystemApplicationService, ApplicationStub>();
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(applications);
    builder.Services.AddSingleton<ISourceCatalogService>(catalog);
    builder.Services.AddScoped<SystemApplicationAccessFilter>();
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
    builder.Services.AddAuthorization();
    builder.Services.AddControllers(options => options.Filters.Add<SystemApplicationAccessFilter>())
        .AddApplicationPart(typeof(SourceCatalogController).Assembly).AddNewtonsoftJson();
    app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var http = new HttpClient { BaseAddress = new Uri(address) };
    Check((await http.GetAsync("/api/source-catalog/models?purpose=hourly")).StatusCode == HttpStatusCode.Unauthorized, "anonymous HTTP denied");
    http.DefaultRequestHeaders.Add("X-Test-User", "restricted");
    var response = await http.GetAsync("/api/source-catalog/models?purpose=hourly");
    Check(response.StatusCode == HttpStatusCode.OK, "scoped B MVC endpoint permitted by global filter");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(json.RootElement.GetProperty("data").GetProperty("records")[0].GetProperty("latestReceiveTime").GetString()!.Length > 0,
        "model reception timestamp is included in camelCase HTTP response");
    Check(json.RootElement.GetProperty("data").GetProperty("total").GetInt32() == 1 &&
        json.RootElement.GetProperty("data").GetProperty("records")[0].GetProperty("deviceCount").GetInt32() == 1, "camelCase wire response and scoped counts");
    Check((await http.GetAsync("/api/source-catalog/devices?purpose=hourly&kind=Unknown")).StatusCode == HttpStatusCode.BadRequest, "invalid kind HTTP 400");
    Check((await http.GetAsync($"/api/source-catalog/models?purpose=process&processLineId={lineId}")).IsSuccessStatusCode,
        "process-scoped catalog works through real MVC/global access filter");
    Check((await http.GetAsync($"/api/source-catalog/models?purpose=hourly&processLineId={lineId}")).StatusCode == HttpStatusCode.BadRequest,
        "other catalog purposes cannot misuse process-line scope");
    http.DefaultRequestHeaders.Remove("X-Test-User");
    http.DefaultRequestHeaders.Add("X-Test-User", "none");
    Check((await http.GetAsync("/api/source-catalog/models?purpose=hourly")).StatusCode == HttpStatusCode.Forbidden, "business permission HTTP 403");
    Console.WriteLine("Source catalog SQL smoke passed: pagination, Chinese search, exact legacy selection, A/B scope, binary resource IDs and edge identity.");
}
finally
{
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    await new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", master).ExecuteNonQueryAsync();
}

static ApplicationAccessContext Access(bool all) => new()
{
    AppCode = "A", AllResources = all,
    Permissions = new(StringComparer.OrdinalIgnoreCase) { "HourlyProductionConfig", "EdgeDataTrace", "EdgeGatewayConsole", "ProductionDashboardConfig", "ProcessBoardConfig" },
    SourceKeys = new(StringComparer.Ordinal) { "tenant\u001fdevice-0000" }
};
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

public class DictionaryStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod!.Name == nameof(ISharedModelDictionaryService.GetModelsAsync)
            ? Task.FromResult(new List<EdgeDashboardModelDisplayMappingOutputDto>
            {
                new() { ModelMark = "ROBOT", DisplayName = "穿片下线机器人", Enabled = true }
            }) : throw new NotSupportedException(targetMethod.Name);
}

public class ApplicationStub : DispatchProxy
{
    public static long ProcessLineId { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != nameof(ISystemApplicationService.GetAccessAsync)) throw new NotSupportedException();
        var user = ((ClaimsPrincipal)args![0]!).Identity!.Name;
        return Task.FromResult(new ApplicationAccessContext
        {
            AppCode = "B", UserName = user ?? "",
            Permissions = user == "none" ? new() : new() { "HourlyProductionConfig", "ProcessBoardConfig" },
            LineCodes = new() { $"process:{ProcessLineId}" },
            SourceKeys = new(StringComparer.Ordinal) { "tenant\u001fdevice-0000" }
        });
    }
}

public class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")), "test")));
    }
}
