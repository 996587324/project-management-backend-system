using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoMapper;
using calmadmin.Config;
using calmadmin.Controllers;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.Dto;
using Model.Menu;
using Model.SystemApplications;
using Service;
using SqlSugar;
using 上传MES.Config;

var database = "CodexMenu_" + Guid.NewGuid().ToString("N");
var server = Environment.GetEnvironmentVariable("MENU_SMOKE_SQL") ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Encrypt=false;Connect Timeout=15";
await using var master = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
await new SqlCommand($"CREATE DATABASE [{database}] COLLATE Chinese_PRC_CI_AS", master).ExecuteNonQueryAsync();
SqlSugarScope? scope = null; WebApplication? app = null; var passed = 0;
var stopUi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
try
{
    scope = new SqlSugarScope(new ConnectionConfig { ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true, ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString });
    await new SystemDatabaseInitializer(scope, new LoggerFactory().CreateLogger<SystemDatabaseInitializer>()).InitializeAsync(default);
    var db = scope.GetConnectionScope("calmadmin");
    var mapper = new MapperConfiguration(cfg => cfg.AddProfile<AutoMapperConfigs>()).CreateMapper();
    var service = new MenuService(scope, mapper);
    Check(await db.Queryable<sys_menu>().AnyAsync(x => x.MenuId == "940000001"), "fixture reproduces existing child ID that previously collided with root creation");
    await service.AddMenuAsync(new BackMenuDto { MenuType = "menu", Name = "MenuFixture", Label = "隔离测试菜单", Path = "/fixture", Component = "/index/index" });
    var root = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "MenuFixture");
    Check(root != null && root.MenuId != "940000001" && int.TryParse(root.MenuId, out _) && root.MetaTitle == "隔离测试菜单", "root creation avoids existing IDs and preserves route name/title");
    Check(await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == root!.MenuId), "new route is assigned to system A");
    await service.AddMenuAsync(new BackMenuDto { Name = "MenuFixtureChild", Label = "测试子菜单", Path = "child", ActivePath = "/fixture", Component = "/custom/actual-page", Sort = 1 });
    await service.AddMenuAsync(new BackMenuDto { Name = "MenuFixtureSibling", Label = "同序号菜单", Path = "sibling", ActivePath = "/fixture", Component = "/custom/sibling", Sort = 1 });
    var child = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "MenuFixtureChild");
    var sibling = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "MenuFixtureSibling");
    Check(child != null && sibling != null && child.MenuId != sibling.MenuId && child.ParentId == root!.MenuId && child.Component == "/custom/actual-page" && child.Path == "child", "same sort order does not collide and submitted component/parent are preserved");
    await service.AddMenuAsync(new BackMenuDto { Name = "MenuFixtureNested", Label = "三级菜单", Path = "/fixture/child/detail", ActivePath = "/fixture/child", Component = "/custom/detail" });
    var nested = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "MenuFixtureNested");
    Check(nested.ParentId == child!.MenuId && nested.Path == "detail", "nested parent path and full child URL resolve correctly");
    var before = await db.Queryable<sys_menu>().CountAsync();
    await Reject(() => service.AddMenuAsync(new BackMenuDto { Name = "MenuFixtureChild", Label = "重复标识", Path = "/different" }), "duplicate route name rejected");
    await Reject(() => service.AddMenuAsync(new BackMenuDto { Name = "DuplicatePath", Label = "重复地址", Path = "child", ActivePath = "/fixture" }), "duplicate full route rejected");
    await Reject(() => service.AddMenuAsync(new BackMenuDto { Name = "MissingParent", Label = "不存在的父级", Path = "child", ActivePath = "/missing" }), "missing parent produces a clear validation error");
    Check(await db.Queryable<sys_menu>().CountAsync() == before, "rejected creation does not leave partial menu records");
    await Task.WhenAll(Enumerable.Range(0, 5).Select(i => service.AddMenuAsync(new BackMenuDto { Name = "ParallelMenu" + i, Label = "并发菜单" + i,
        Path = "parallel-" + i, ActivePath = "/fixture", Component = "/custom/page" })));
    var parallel = await db.Queryable<sys_menu>().Where(x => x.MenuName.StartsWith("ParallelMenu")).ToListAsync();
    Check(parallel.Count == 5 && parallel.Select(x => x.MenuId).Distinct().Count() == 5, "concurrent requests receive distinct IDs");

    var applications = DispatchProxy.Create<ISystemApplicationService, ApplicationProbe>();
    var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
    builder.Services.AddSingleton(scope); builder.Services.AddSingleton(mapper);
    builder.Services.AddSingleton<IMenuService>(service); builder.Services.AddSingleton(applications);
    builder.Services.AddScoped<ISystemApplicationMenuService, SystemApplicationMenuService>();
    builder.Services.AddScoped<SystemApplicationAccessFilter>();
    builder.Services.AddControllers(options => options.Filters.AddService<SystemApplicationAccessFilter>()).AddApplicationPart(typeof(MenuController).Assembly);
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { }); builder.Services.AddAuthorization();
    app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
    if (Environment.GetEnvironmentVariable("MENU_UI_HOLD") == "1") app.MapPost("/__test/stop", () => { stopUi.TrySetResult(); return Results.Ok(); });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    Check((await client.PostAsJsonAsync("/api/Menu/AddMenu", new BackMenuDto { Name = "Anonymous", Label = "无权限", Path = "/anonymous" })).StatusCode == HttpStatusCode.Unauthorized, "anonymous menu creation denied by actual HTTP route");
    client.DefaultRequestHeaders.Add("X-Test-User", "admin");
    var httpResult = await client.PostAsJsonAsync("/api/Menu/AddMenu", new BackMenuDto { Name = "HttpMenu", Label = "接口新增菜单", Path = "/http-menu", Component = "/index/index" });
    Check(httpResult.IsSuccessStatusCode && JsonDocument.Parse(await httpResult.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetInt32() == 200, "actual A menu HTTP endpoint creates a route");
    var tree = await client.GetStringAsync("/api/Menu/GetMenuTree");
    Check(tree.Contains("HttpMenu") && tree.Contains("MenuFixtureChild"), "new A routes appear in the authorized menu tree");
    var invalid = await client.PostAsJsonAsync("/api/Menu/AddMenu", new BackMenuDto { Name = "HttpMenu", Label = "重复", Path = "/different" });
    Check(invalid.StatusCode == HttpStatusCode.BadRequest && (await invalid.Content.ReadAsStringAsync()).Contains("菜单标识已存在"), "duplicate response provides the actual validation reason");
    client.DefaultRequestHeaders.Add("X-Test-App", "B");
    var bResult = await client.PostAsJsonAsync("/api/Menu/AddMenu", new BackMenuDto { Name = "BOnlyMenu", Label = "B 菜单", Path = "/b-only", Component = "/index/index", ParentId = 0 });
    Check(bResult.IsSuccessStatusCode && await db.Queryable<SysApplicationMenuEntryEntity>().AnyAsync(x => x.AppCode == "B" && x.DefinitionJson.Contains("BOnlyMenu")) &&
        !await db.Queryable<sys_menu>().AnyAsync(x => x.MenuName == "BOnlyMenu"), "B system creation keeps its independent menu storage");
    Console.WriteLine($"PASS {passed} menu-creation checks; isolated SQL and actual HTTP controller/filter.");
    if (Environment.GetEnvironmentVariable("MENU_UI_HOLD") == "1")
    {
        Console.WriteLine("MENU_UI_API=" + address);
        await Task.WhenAny(stopUi.Task, Task.Delay(TimeSpan.FromMinutes(30)));
    }

    void Check(bool success, string description) { if (!success) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
    async Task Reject(Func<Task> action, string description) { try { await action(); } catch (ArgumentException) { Check(true, description); return; } throw new Exception("FAIL: " + description); }
}
finally
{
    if (app != null) await app.DisposeAsync();
    scope?.Dispose(); SqlConnection.ClearAllPools();
    await new SqlCommand($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]", master).ExecuteNonQueryAsync();
}

public class ApplicationProbe : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "GetAccessAsync") throw new NotSupportedException(targetMethod?.Name);
        var principal = (ClaimsPrincipal)args![0]!;
        return Task.FromResult(new ApplicationAccessContext { UserId = "admin", UserName = "admin", AppCode = principal.FindFirstValue("app") ?? "A",
            PlatformAdministrator = true, AllResources = true, Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" } });
    }
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var app = Request.Headers["X-Test-App"].ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim("app", app == "B" ? "B" : "A")], "test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
    }
}
