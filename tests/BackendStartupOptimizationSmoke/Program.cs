using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using AutoMapper;
using calmadmin.Config;
using calmadmin.Controllers;
using calmadmin.HostedServices;
using calmadmin.Middleware;
using Interface;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Model.EdgeGateway;
using Model.Menu;
using Model.User;
using Newtonsoft.Json.Linq;
using Service;
using SqlSugar;
using 上传MES.Config;

// 仅操作随机命名的隔离数据库。绝不启动真实采集、AI 请求、微信轮询或清理任务。
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "../..");
using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "calmadmin", "appsettings.json")));
var connectionString = configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var testDatabase = "CodexStartupOptimization_" + Guid.NewGuid().ToString("N");
var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
var testBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = testDatabase };
using var master = new SqlConnection(masterBuilder.ConnectionString);
await master.OpenAsync();
await ExecuteMasterAsync($"CREATE DATABASE [{testDatabase}]");
WebApplication? app = null;
SqlSugarScope? scope = null;
var commands = new ConcurrentQueue<string>();

try
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin",
        ConnectionString = testBuilder.ConnectionString,
        DbType = SqlSugar.DbType.SqlServer,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute
    }, db => db.Aop.OnLogExecuting = (sql, parameters) => commands.Enqueue(sql));
    builder.Services.AddSingleton(scope);
    builder.Services.AddAutoMapper(typeof(上传MES.Config.AutoMapperConfigs));
    builder.Services.AddTransient<IMenuService, MenuService>();
    builder.Services.AddTransient<IUserInfoService, UserInfoService>();
    builder.Services.AddTransient<IRoleService, RoleService>();
    builder.Services.AddSingleton<SystemDatabaseInitializer>();
    builder.Services.AddHostedService<SystemDatabaseInitializationHostedService>();
    builder.Services.AddControllers().AddApplicationPart(typeof(MenuController).Assembly);
    var signingKey = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = signingKey
        };
    });
    builder.Services.AddAuthorization();
    app = builder.Build();
    app.UseMiddleware<RequestTimingMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    await app.StartAsync();
    var db = scope.GetConnection("calmadmin");
    Require(await db.Queryable<SysRole>().CountAsync() == 3, "default roles created once");
    var account = await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "1");
    Require(account != null && account.Enabled, "initial account insert satisfies SQL constraints");

    var menu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "AgentAssistant");
    menu.MetaRoles = "CUSTOM_ROLE";
    menu.MetaIcon = "custom-icon";
    menu.Sort = 777;
    menu.MetaIsHide = true;
    await db.Updateable(menu).ExecuteCommandAsync();
    var role = await db.Queryable<SysRole>().FirstAsync(x => x.roleCode == "R_ENGINEER");
    role.permissionKeys = "CUSTOM_PERMISSION";
    await db.Updateable(role).ExecuteCommandAsync();
    var menuCount = await db.Queryable<sys_menu>().CountAsync();
    await new SystemDatabaseInitializer(scope, NullLogger<SystemDatabaseInitializer>.Instance).InitializeAsync(default);
    var preserved = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuId == menu.MenuId);
    Require(preserved.MetaRoles == "CUSTOM_ROLE" && preserved.MetaIcon == "custom-icon" &&
            preserved.Sort == 777 && preserved.MetaIsHide == true, "repeat initialization preserves administrator menu customization");
    Require(await db.Queryable<sys_menu>().CountAsync() == menuCount, "repeat initialization does not duplicate menus");
    Require((await db.Queryable<SysRole>().FirstAsync(x => x.roleCode == "R_ENGINEER")).permissionKeys == "CUSTOM_PERMISSION",
        "repeat initialization preserves role permissions");

    // 真正的控制器、JWT 校验和 HTTP 请求，不绕过接口授权。
    using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    var token = new JwtSecurityToken(claims: new[] { new Claim("Id", "1") }, expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    commands.Clear();
    var timings = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
    {
        var watch = Stopwatch.StartNew();
        using var response = await http.GetAsync("/api/Menu/GetMenuTree");
        Require(response.StatusCode == HttpStatusCode.OK, "menu HTTP success");
        return watch.Elapsed.TotalMilliseconds;
    }));
    using (var response = await http.GetAsync("/api/UserInfo/GetUserInfo"))
    {
        Require(response.StatusCode == HttpStatusCode.OK, "authenticated user info HTTP success");
    }
    Require(!commands.Any(sql => sql.Contains("extended_properties", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) || sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)),
        "menu and user info read paths execute no schema maintenance");
    Require(!commands.Any(sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
        sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)), "read paths do not write default data");
    Console.WriteLine($"HTTP menu 12 concurrent requests: min={timings.Min():F1}ms max={timings.Max():F1}ms average={timings.Average():F1}ms");

    using (var operation = DatabaseOperationScope.Begin(scope))
    {
        var parentDb = DatabaseOperationScope.GetConnection(scope, "calmadmin");
        Require(ReferenceEquals(parentDb, DatabaseOperationScope.GetConnection(scope, "calmadmin")), "same operation reuses its connection");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var child = DatabaseOperationScope.Begin(scope);
            var childDb = DatabaseOperationScope.GetConnection(scope, "calmadmin");
            Require(!ReferenceEquals(parentDb, childDb), "parallel child has independent connection");
            var transaction = await childDb.Ado.UseTranAsync(async () =>
            {
                await childDb.Insertable(new SysRole
                {
                    roleCode = "ISOLATION_" + index, roleName = "probe", description = "probe",
                    permissionKeys = "", enabled = true, createTime = DateTime.Now
                }).ExecuteCommandAsync();
                await Task.Delay(15);
                Require(ReferenceEquals(childDb, DatabaseOperationScope.GetConnection(scope, "calmadmin")), "transaction survives await and shares child context");
                if (index % 2 == 0) throw new InvalidOperationException("Expected rollback");
            });
            Require(transaction.IsSuccess == (index % 2 == 1), "only this child's transaction commits or rolls back");
        }));
        Require(ReferenceEquals(parentDb, DatabaseOperationScope.GetConnection(scope, "calmadmin")), "parent context restored after children");
        Require(await parentDb.Queryable<SysRole>().Where(x => x.roleCode.StartsWith("ISOLATION_")).CountAsync() == 4,
            "parallel rollback does not affect other transactions");
    }

    // 验证实际字段目录方法；覆盖真实 SQL Server 批量更新、可空值、历史字段和累计次数。
    db.CodeFirst.InitTables<EdgeDashboardModelFieldCatalogEntity>();
    var processor = new EdgeDashboardTelemetryProcessor(scope, NullLogger<EdgeDashboardTelemetryProcessor>.Instance);
    var method = typeof(EdgeDashboardTelemetryProcessor).GetMethod("RecordModelFieldsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var first = DateTime.Now;
    var sample = new EdgeDashboardTelemetrySampleDto { ModelMark = "PROBE", TenantMark = "probe", DeviceMark = "设备1", ReceiveTime = first };
    await RecordAsync(JObject.Parse("{\"data\":{\"temperature\":25,\"status\":\"运行'正常\",\"nullable\":null}}"));
    sample.ReceiveTime = first.AddSeconds(1);
    await RecordAsync(JObject.Parse("{\"data\":{\"temperature\":26,\"nullable\":null,\"newField\":1}}"));
    var catalog = await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().ToListAsync();
    var temperature = catalog.Single(x => x.SourcePath == "/data/temperature");
    Require(temperature.SeenCount == 2 && temperature.LastValueJson == "26", "bulk update preserves current value and count");
    Require(catalog.Any(x => x.SourcePath == "/data/status"), "previously received field remains selectable");
    Require(catalog.Any(x => x.SourcePath == "/data/newField"), "new field immediately persisted");
    Require(Math.Abs((temperature.FirstSeenTime - first).TotalSeconds) < 0.02, "bulk update preserves first seen time");

    // 验证实际 Autofac 注册和 CopyNew 后的日志计时，且不启动真实业务后台任务。
    var diagnosticLogs = new ProbeLoggerProvider();
    var productionBuilder = WebApplication.CreateBuilder();
    productionBuilder.Configuration.AddJsonFile(Path.Combine(root, "calmadmin", "appsettings.json"));
    productionBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:calmadmin"] = testBuilder.ConnectionString,
        ["BackendDiagnostics:SlowSqlMilliseconds"] = "50"
    });
    productionBuilder.Logging.AddProvider(diagnosticLogs);
    productionBuilder.Register();
    await using (var registeredApp = productionBuilder.Build())
    {
        var registeredScope = registeredApp.Services.GetRequiredService<SqlSugarScope>();
        using var operation = DatabaseOperationScope.Begin(registeredScope, "SqlLoggingProbe");
        var copiedDb = DatabaseOperationScope.GetConnection(registeredScope, "calmadmin");
        const string secretSentinel = "PROBE_DO_NOT_LOG_SECRET";
        await copiedDb.Ado.ExecuteCommandAsync("WAITFOR DELAY '00:00:00.100'; SELECT @value;", new SugarParameter("@value", secretSentinel));
        Require(diagnosticLogs.Messages.Any(message => message.Contains("Slow SQL") && message.Contains("SqlLoggingProbe")),
            "production registration and copied SQL context emit correctly timed operation logs");
        Require(diagnosticLogs.Messages.All(message => !message.Contains(secretSentinel)), "SQL logs do not contain parameter secrets");
    }

    await EdgeRealtimeFieldLatestSmoke.RunAsync(scope);
    await EdgeDeviceCardPushSmoke.RunAsync(scope);
    Console.WriteLine("PASS: startup idempotence, SQL inserts, read-only HTTP routes, parallel transaction isolation, real field catalog bulk updates.");

    async Task RecordAsync(JObject payload)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await (Task)method.Invoke(processor, new object[] { "PROBE", payload, sample })!;
    }
}
finally
{
    if (app != null)
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    // 名称完全由本程序生成并校验；仅移除本次创建的临时数据库。
    if (!System.Text.RegularExpressions.Regex.IsMatch(testDatabase, "^CodexStartupOptimization_[0-9a-f]{32}$"))
        throw new InvalidOperationException("Unsafe database cleanup target");
    await ExecuteMasterAsync($"ALTER DATABASE [{testDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{testDatabase}];");
    Console.WriteLine("Temporary SQL database removed.");
}

async Task ExecuteMasterAsync(string sql)
{
    using var command = master.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 30;
    await command.ExecuteNonQueryAsync();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
}

sealed class ProbeLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ProbeLogger(Messages);

    public void Dispose() { }

    private sealed class ProbeLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
