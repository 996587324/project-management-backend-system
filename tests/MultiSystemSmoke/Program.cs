using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoMapper;
using calmadmin.Config;
using calmadmin.Controllers;
using calmadmin.Controllers.ExternalTools;
using Interface;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Model.EdgeGateway;
using Model.ExternalTools;
using Model.Menu;
using Model.Other;
using Model.ProjectManage;
using Model.SystemApplications;
using Model.User;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Service;
using SkiaSharp;
using SqlSugar;
using 上传MES.Config;

// 隔离数据库验证；不启动采集、AI、微信发送或任何生产清理任务。
const string Verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
var root = Path.GetFullPath(args[0]);
using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "calmadmin", "appsettings.json")));
var connection = configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var databaseName = "CodexMultiSystem_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
await MasterAsync($"CREATE DATABASE [{databaseName}]");
WebApplication? app = null;
SqlSugarScope? scope = null;
var avatarRoot = Path.Combine(Path.GetTempPath(), "CodexAvatar_" + Guid.NewGuid().ToString("N"));
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin",
        ConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = databaseName }.ConnectionString,
        DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute
    });
    var db = scope.GetConnectionScope("calmadmin");
    db.CodeFirst.InitTables<SysUserInfo, SysRole, sys_menu>();
    await db.Insertable(new[]
    {
        User("admin-id", "administrator", "R_ADMIN"), User("u1", "operator", "R_USER"),
        User("default-admin", "admin", "R_ADMIN")
    }).ExecuteCommandAsync();
    Require(db.DbMaintenance.GetColumnInfosByTableName("sys_user_info", false)
        .Single(x => x.DbColumnName == "avatar_file_key").IsNullable, "fresh avatar column explicitly allows SQL NULL");
    // 仅在随机隔离库模拟生产旧表：保留用户记录，删除新列再执行迁移。
    db.DbMaintenance.DropColumn("sys_user_info", "avatar_file_key");
    db.CodeFirst.InitTables<SysUserInfo>();
    db.CodeFirst.InitTables<SysUserInfo>();
    Require(await db.Queryable<SysUserInfo>().CountAsync() == 3 &&
        (await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1")).AvatarFileKey == null,
        "populated old user table upgrades repeatedly without losing users or forcing fake avatars");
    await db.Insertable(new SysRole { roleCode = "R_ADMIN", roleName = "admin", description = "test", permissionKeys = "*", enabled = true, createTime = DateTime.Now }).ExecuteCommandAsync();
    await db.Insertable(new sys_menu
    {
        MenuId = "1", ParentId = "0", MenuName = "LegacyMenu", Path = "/legacy", Component = "/legacy/index",
        MetaTitle = "旧菜单", MetaIcon = "test", MetaAuthList = "[]", MetaRoles = "", Status = true
    }).ExecuteCommandAsync();
    await SystemApplicationSchema.InitializeAsync(db);
    Require(await db.Queryable<SysUserApplicationEntity>().CountAsync() == 4, "only named admin receives default A+B grants");
    Require(!await db.Queryable<SysUserApplicationEntity>().AnyAsync(x => x.UserId == "admin-id" && x.AppCode == "B"), "other old administrators are not auto-granted B");
    await db.Insertable(User("new-user", "newcomer", "R_USER")).ExecuteCommandAsync();
    await SystemApplicationSchema.InitializeAsync(db);
    Require(!await db.Queryable<SysUserApplicationEntity>().AnyAsync(x => x.UserId == "new-user"), "new users are not auto-granted on restart");
    Require(await db.Queryable<SysApplicationMenuEntity>().CountAsync() == 5, "repeat schema initialization does not duplicate menu assignments");
    var jwt = new JWTTokenOptions { Issuer = "multi-system-test", Audience = "admin-test", SecurityKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
    var options = new ConstantOptions<JWTTokenOptions>(jwt);
    var mapper = new MapperConfiguration(config => config.AddProfile<AutoMapperConfigs>()).CreateMapper();
    var menuService = new SystemApplicationMenuService(scope, mapper);
    var service = new SystemApplicationService(scope, options, menus: menuService);
    await service.RegisterSessionAsync("admin", "default-admin-session");
    var defaultAdmin = Principal("default-admin", "admin", "default-admin-session");
    Require((await service.GetApplicationsAsync(defaultAdmin)).Select(x => x.AppCode).SequenceEqual(["A", "B"]), "admin can enter both systems by default");
    var adminTicket = JsonSerializer.SerializeToElement(await service.CreateTicketAsync(defaultAdmin, ProofRequest("B")));
    var adminToken = JsonSerializer.SerializeToElement(await service.RedeemTicketAsync(new ApplicationTicketRedeemRequest
    {
        AppCode = "B", Ticket = adminTicket.GetProperty("ticket").GetString()!, State = "state-for-smoke", CodeVerifier = Verifier
    })).GetProperty("token").GetString()!;
    var adminBPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "test"));
    var adminBAccess = await service.GetAccessAsync(adminBPrincipal, true);
    Require(adminBAccess.PlatformAdministrator && adminBAccess.AllResources && adminBAccess.HasPermission("*") && adminBAccess.McpScopes.Contains("*"), "admin B has all business and MCP capabilities");
    var originalBMenu = await db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.AppCode == "B" && x.SourceMenuId == "1");
    await menuService.SaveMenuAsync(adminBAccess, new Model.Dto.BackMenuDto
    {
        Id = originalBMenu.Id, Name = "CustomLegacy", Path = "/custom-legacy", Label = "B 独立菜单", Component = "/legacy/index", ParentId = 0
    });
    Require((await db.Queryable<sys_menu>().FirstAsync(x => x.MenuId == "1")).Path == "/legacy", "B menu edits leave A source intact");
    await menuService.SaveMenuAsync(adminBAccess, new Model.Dto.BackMenuDto
    {
        Name = "AdditionalLegacy", Path = "/additional-legacy", Label = "新复用组件", Component = "/legacy/index", ParentId = 0
    });
    Require(await db.Queryable<SysApplicationMenuEntryEntity>().AnyAsync(x => x.AppCode == "B" && x.SourceMenuId == null), "new custom menu inserts with nullable source ID and identity primary key");
    await SystemApplicationSchema.InitializeAsync(db);
    Require((await menuService.GetMenuAsync(adminBAccess, originalBMenu.Id))!.MenuName == "CustomLegacy", "restart does not overwrite B menu customizations");
    await service.SaveMenusAsync(defaultAdmin, "B", (await db.Queryable<sys_menu>().ToListAsync()).Where(x => x.MenuName != "SystemBSharedModelDictionary").Select(x => x.MenuId).ToList());
    Require((await menuService.GetMenuAsync(adminBAccess, originalBMenu.Id))!.Path == "/custom-legacy", "resaving component selection preserves customized B routes");
    var customTree = await menuService.GetTreeAsync(adminBAccess);
    Require(customTree.Single(x => x.name == "CustomLegacy").meta.permissionKey == "LegacyMenu", "custom route retains original component permission identity");
    await menuService.SaveMenuAsync(adminBAccess, new Model.Dto.BackMenuDto
    {
        Id = originalBMenu.Id, Name = "CustomLegacy", Path = "/custom-legacy", Label = "B 独立菜单", Component = "/legacy/index", ParentId = 0, IsEnable = false
    });
    Require(!(await menuService.GetTreeAsync(adminBAccess)).Any(x => x.name == "CustomLegacy") &&
        (await menuService.GetTreeAsync(adminBAccess, true)).Any(x => x.name == "CustomLegacy" && !x.meta.isEnable), "disabled menus remain editable but do not appear in navigation");
    await menuService.SaveMenuAsync(adminBAccess, new Model.Dto.BackMenuDto
    {
        Id = originalBMenu.Id, Name = "CustomLegacy", Path = "/custom-legacy", Label = "B 独立菜单", Component = "/legacy/index", ParentId = 0
    });
    await menuService.SaveMenuAsync(adminBAccess, new Model.Dto.BackMenuDto
    {
        Id = originalBMenu.Id, MenuType = "button", AuthName = "新增", AuthLabel = "add"
    });
    Require((await menuService.GetMenuAsync(adminBAccess, originalBMenu.Id))!.GetMetaAuthList().Any(x => x.AuthMark == "add"), "B menu button metadata saves independently");
    await menuService.DeleteButtonAsync(adminBAccess, originalBMenu.Id, "add");
    Require(!(await menuService.GetMenuAsync(adminBAccess, originalBMenu.Id))!.GetMetaAuthList().Any(), "B menu button deletion persists");
    await service.RegisterSessionAsync("administrator", "admin-session");
    await service.RegisterSessionAsync("operator", "operator-session");
    var admin = Principal("admin-id", "administrator", "admin-session");
    var user = Principal("u1", "operator", "operator-session");
    Require((await service.GetApplicationsAsync(user)).Select(x => x.AppCode).SequenceEqual(["A"]), "portal intersects system grants");
    await Denied(async () => await service.CreateTicketAsync(user, ProofRequest("B")), "B entry denied before explicit grant");
    await service.SaveGrantAsync(admin, new ApplicationGrantDto
    {
        UserId = "u1", AppCode = "B", Enabled = true,
        Permissions = ["SystemBWorkbench", "shared-dictionary.read"],
        McpScopes = ["dashboard.read", "project.read"], LineCodes = ["L1"], ProjectCodes = ["P1"], SourceKeys = ["T1\u001fD1"]
    });
    var ticketRequest = ProofRequest("B");
    var ticketData = JsonSerializer.SerializeToElement(await service.CreateTicketAsync(user, ticketRequest));
    var redeem = new ApplicationTicketRedeemRequest
    {
        AppCode = "B", Ticket = ticketData.GetProperty("ticket").GetString()!,
        State = ticketRequest.State, CodeVerifier = Verifier
    };
    await Denied(async () => await service.RedeemTicketAsync(new ApplicationTicketRedeemRequest
    {
        AppCode = "A", Ticket = redeem.Ticket, State = redeem.State, CodeVerifier = redeem.CodeVerifier
    }), "ticket cannot be used by another application");
    await Denied(async () => await service.RedeemTicketAsync(new ApplicationTicketRedeemRequest
    {
        AppCode = "B", Ticket = redeem.Ticket, State = "wrong-state", CodeVerifier = redeem.CodeVerifier
    }), "wrong state does not consume the valid ticket");
    var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
    {
        try { return JsonSerializer.SerializeToElement(await service.RedeemTicketAsync(redeem)); }
        catch (UnauthorizedAccessException) { return (JsonElement?)null; }
    }));
    Require(attempts.Count(x => x.HasValue) == 1, "concurrent ticket redemption succeeds exactly once");
    var bToken = attempts.Single(x => x.HasValue)!.Value.GetProperty("token").GetString()!;
    var bPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(bToken).Claims, "test"));
    var bAccess = await service.GetAccessAsync(bPrincipal, true);
    Require(bAccess.AppCode == "B" && !bAccess.HasPermission("*") && !bAccess.PlatformAdministrator, "B does not inherit global account role permissions");
    Require(!bAccess.AllResources && bAccess.SourceKeys.SetEquals(["T1\u001fD1"]), "resource grants are per application");
    await Denied(() => menuService.SaveMenuAsync(bAccess, new Model.Dto.BackMenuDto()), "ordinary B users cannot escalate permissions through menu editing");
    await Denied(async () => await service.SaveApplicationAsync(bPrincipal, new SysApplicationEntity { AppCode = "B", DisplayName = "attack" }), "application users cannot administer platform configuration");

    var dashboard = new EdgeDashboardService(scope, mapper);
    db.CodeFirst.InitTables<EdgeDashboardModelDisplayMappingEntity, EdgeDashboardModelFieldDisplayMappingEntity, EdgeDashboardModelFieldCatalogEntity>();
    await db.Insertable(new EdgeDashboardModelFieldCatalogEntity { ModelMark = "MODEL", SourcePath = "/data/temperature", FieldName = "temperature", ValueType = "Number" }).ExecuteCommandAsync();
    await dashboard.SaveModelDisplayMappingAsync(new EdgeDashboardModelDisplayMappingSaveDto { DashboardCode = "B", ModelMark = "MODEL", DisplayName = "通用中文模型" });
    await dashboard.SaveModelFieldDisplayMappingsAsync(new EdgeDashboardModelFieldDisplayMappingListSaveDto
    {
        DashboardCode = "B", ModelMark = "MODEL", Mappings = [new() { SourcePath = "/data/temperature", DisplayName = "温度", ValueType = "Number", Unit = "℃", Decimals = 2 }]
    });
    Require((await dashboard.GetModelDisplayMappingsAsync("A")).Single().DisplayName == "通用中文模型", "A/B use the same model mapping");
    Require((await dashboard.GetModelFieldDisplayMappingsAsync("another-dashboard", "MODEL")).Single().DisplayName == "温度", "field mapping is no longer keyed by caller dashboard");
    var columns = db.DbMaintenance.GetColumnInfosByTableName("SysApplicationLoginTicket", false);
    Require(columns.Single(x => x.DbColumnName == "ConsumedAtUtc").IsNullable, "unconsumed SQL ticket timestamp permits NULL");

    var dashboardProxy = DispatchProxy.Create<IEdgeDashboardService, BusinessProbe>();
    var projectsProxy = DispatchProxy.Create<IProjectManageService, BusinessProbe>();
    var external = new ExternalToolService(scope, dashboardProxy,
        DispatchProxy.Create<IEdgeGatewayService, BusinessProbe>(), projectsProxy,
        DispatchProxy.Create<IUserActivityService, BusinessProbe>(), DispatchProxy.Create<IWeChatClawBotService, BusinessProbe>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWTTokenOptions:SecurityKey"] = jwt.SecurityKey, ["JWTTokenOptions:Issuer"] = jwt.Issuer
        }).Build());
    // 模拟生产非空旧日志表；新增归属列必须允许 NULL，不能靠删除旧记录完成升级。
    db.CodeFirst.InitTables<LegacyExternalLog>();
    await db.Insertable(new LegacyExternalLog { ClientId = "legacy-client", ToolName = "legacy.tool", RequestId = "legacy-request", ArgumentHash = "old" }).ExecuteCommandAsync();
    var caller = new ExternalToolCallerContext
    {
        UserId = "u1", AppCode = "B", ClientId = "test-user-B", Scopes = ["dashboard.read", "project.read"],
        AllowedLineCodes = ["L1"], AllowedProjectCodes = ["P1"]
    };
    var result = await external.ExecuteAsync("production.line-runtime", JsonSerializer.SerializeToElement(new { }), caller, "scope-test");
    Require(result.Success && BusinessProbe.Calls.SequenceEqual(["line:L1"]), "missing line code aggregates only authorized lines");
    result = await external.ExecuteAsync("production.line-runtime", JsonSerializer.SerializeToElement(new { lineCode = "L2" }), caller, "denial-test");
    Require(!result.Success && BusinessProbe.Calls.Count == 1, "unauthorized resource is rejected before business query");
    Require(external.GetDefinitions(caller).All(x => x.RequiredScope is "dashboard.read" or "project.read"), "tools/list filters scopes");
    Require(await db.Queryable<ExternalToolAccessLogEntity>().AnyAsync(x => x.UserId == "u1" && x.AppCode == "B"), "MCP audit contains user and application attribution");
    Require(await db.Queryable<ExternalToolAccessLogEntity>().AnyAsync(x => x.ClientId == "legacy-client" && x.UserId == null), "populated old MCP audit table upgrades without losing records");
    var newClient = await external.SaveClientAsync(new ExternalToolClientSaveDto { ClientName = "no-expiry", Scopes = "project.read", AllowedProjectCodes = "P1", Enabled = true });
    Require(newClient.Client.ExpireTime == null, "external client SQL timestamp allows NULL");
    db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity, EdgeRealtimeDeviceFieldLatestEntity>();
    await db.Insertable(new[]
    {
        new EdgeRealtimeDeviceSnapshotEntity { TenantMark = "T1", DeviceCode = "D1", ModelMark = "MODEL", DataListJson = "[{\"data\":{\"temperature\":25.678}}]" },
        new EdgeRealtimeDeviceSnapshotEntity { TenantMark = "T2", DeviceCode = "D1", ModelMark = "MODEL" }
    }).ExecuteCommandAsync();

    // 真实 MVC 请求验证后端菜单、旧接口隔离和共享配置权限。
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.AddSingleton(scope);
    builder.Services.AddSingleton<IOptionsMonitor<JWTTokenOptions>>(options);
    builder.Services.AddScoped<ISystemApplicationService, SystemApplicationService>();
    builder.Services.AddScoped<ISystemApplicationMenuService, SystemApplicationMenuService>();
    builder.Services.AddSingleton<IEdgeDashboardService>(dashboard);
    builder.Services.AddScoped<ISharedModelDictionaryService, SharedModelDictionaryService>();
    builder.Services.AddScoped<IEdgeModelFieldCatalogService, EdgeModelFieldCatalogService>();
    builder.Services.AddAutoMapper(typeof(AutoMapperConfigs));
    builder.Services.AddScoped<IMenuService, MenuService>();
    builder.Services.AddScoped<IUserInfoService, UserInfoService>();
    builder.Services.Configure<UserAvatarOptions>(options => options.StorageRoot = avatarRoot);
    builder.Services.AddScoped<IUserAvatarService, UserAvatarService>();
    builder.Services.AddScoped<IRoleService, RoleService>();
    builder.Services.AddScoped<IUserActivityService, UserActivityService>();
    builder.Services.AddSingleton<IExternalToolService>(external);
    builder.Services.AddHttpContextAccessor();
    var handlers = typeof(ExternalToolController).Assembly.GetType("calmadmin.Controllers.ExternalTools.ExternalMcpV2Handlers")!;
    var listHandler = handlers.GetMethod("ListToolsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    var callHandler = handlers.GetMethod("CallToolAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
        .WithListToolsHandler((request, cancellation) => (ValueTask<ListToolsResult>)listHandler.Invoke(null, [request, cancellation])!)
        .WithCallToolHandler((request, cancellation) => (ValueTask<CallToolResult>)callHandler.Invoke(null, [request, cancellation])!);
    builder.Services.AddControllers(options => options.Filters.Add<SystemApplicationAccessFilter>())
        .AddApplicationPart(typeof(SystemApplicationsController).Assembly).AddNewtonsoftJson();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecurityKey)), ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents { OnAuthenticationFailed = context =>
        {
            Console.WriteLine("JWT validation failed: " + context.Exception.GetType().Name + ": " + context.Exception.Message);
            return Task.CompletedTask;
        }, OnTokenValidated = async context =>
        {
            try { await service.GetAccessAsync(context.Principal!); }
            catch (UnauthorizedAccessException exception) { Console.WriteLine("Session validation failed: " + exception.Message); context.Fail(exception.Message); }
        } };
    }).AddJwtBearer(ExternalToolAuthenticationDefaults.Scheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = SystemApplicationService.McpAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecurityKey)), ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents { OnTokenValidated = async context =>
        {
            try { await service.GetAccessAsync(context.Principal!, true); }
            catch (UnauthorizedAccessException exception) { context.Fail(exception.Message); }
        } };
    });
    builder.Services.AddAuthorization(options => options.AddPolicy("McpAccess", policy =>
        policy.AddAuthenticationSchemes(ExternalToolAuthenticationDefaults.Scheme).RequireAuthenticatedUser()));
    app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapMcp("/mcp/v2").RequireAuthorization("McpAccess");
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bToken);
    var menuRequest = await client.GetAsync("/api/Menu/GetMenuTree");
    var menuResponse = await menuRequest.Content.ReadAsStringAsync();
    Require(menuRequest.IsSuccessStatusCode, "menu HTTP access: " + menuResponse);
    Require(menuResponse.Contains("SystemBWorkbench") && !menuResponse.Contains("LegacyMenu") && !menuResponse.Contains("ApplicationAdministration"), "server returns B menus only");
    using var adminClient = new HttpClient { BaseAddress = client.BaseAddress };
    adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
    var fullMenus = await adminClient.GetStringAsync("/api/Menu/GetMenuTree");
    Require(fullMenus.Contains("CustomLegacy") && fullMenus.Contains("ApplicationAdministration") && fullMenus.Contains("AdditionalLegacy"), "B admin receives full independent menu tree");
    var profile = await adminClient.GetStringAsync("/api/UserInfo/GetUserInfo");
    Require(profile.Contains("R_ADMIN") && profile.Contains("homePath") && profile.Contains("\"B\""), "B admin profile supports existing backend UI and its own homepage");
    Require((await client.GetAsync("/api/Menu/GetManagementMenuTree")).StatusCode == HttpStatusCode.Forbidden, "partial users cannot obtain management menu definitions");
    Require((await adminClient.GetAsync("/api/Menu/GetManagementMenuTree")).IsSuccessStatusCode, "B admin can manage menu definitions with the same API framework");
    Require((await adminClient.GetAsync($"/api/Menu/GetMenuById?menuId={originalBMenu.Id}")).IsSuccessStatusCode &&
        (await client.GetAsync($"/api/Menu/GetMenuById?menuId={originalBMenu.Id}")).StatusCode == HttpStatusCode.Forbidden,
        "old business authorization accepts B full-resource admin and rejects partial-resource users");
    var write = await client.PostAsync("/api/shared-data/models", new StringContent("{\"ModelMark\":\"X\",\"DisplayName\":\"not allowed\"}", Encoding.UTF8, "application/json"));
    Require(write.StatusCode == HttpStatusCode.Forbidden, "dictionary read permission does not allow write");
    var sources = await client.GetStringAsync("/api/shared-data/sources");
    Require(sources.Contains("T1") && !sources.Contains("T2"), "shared sources restrict tenant/device pair before pagination");
    var sourceFieldsResponse = await client.GetStringAsync("/api/shared-data/source-fields?tenantMark=T1&deviceMark=D1");
    using var sourceFieldsJson = JsonDocument.Parse(sourceFieldsResponse);
    var fieldValue = sourceFieldsJson.RootElement.GetProperty("data").EnumerateArray()
        .Single(field => Property(field, "SourcePath").GetString() == "/data/temperature");
    Require(Property(fieldValue, "CurrentValue").GetDouble() == 25.678 &&
        Property(fieldValue, "DisplayValue").GetDouble() == 25.68 && Property(fieldValue, "FieldName").GetString() == "温度",
        "B reuses shared Chinese/precision mapping while preserving raw value");
    Require((await client.GetAsync("/api/shared-data/source-fields?tenantMark=T2&deviceMark=D1")).StatusCode == HttpStatusCode.Forbidden,
        "same device code in another tenant cannot read field values");
    var operationResponse = await client.PostAsync("/api/user-activity/operation", JsonBody("{\"module\":\"系统 B\",\"action\":\"保存配置\",\"pagePath\":\"/shared-data\",\"operationType\":\"ButtonClick\"}"));
    Require(operationResponse.IsSuccessStatusCode && await db.Queryable<SysUserOperationLogEntity>().AnyAsync(x => x.UserId == "u1" && x.AppCode == "B"), "UI operation audit is attributed to the server-verified application");
    using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
    await service.SaveApplicationAsync(defaultAdmin, new SysApplicationEntity
    {
        AppCode = "A", DisplayName = "智能制造管理系统", EntryUrl = "http://localhost:3006/#/portal", HomePath = "/", Sort = 1
    });
    await service.SaveApplicationAsync(defaultAdmin, new SysApplicationEntity
    {
        AppCode = "B", DisplayName = "工艺智能管理系统", EntryUrl = "http://localhost:3007/#/auth/entry", HomePath = "/workbench", Sort = 2
    });
    using var brandResponse = await anonymous.GetAsync("/api/system-applications/entry-information?appCode=B");
    using var brandJson = JsonDocument.Parse(await brandResponse.Content.ReadAsStringAsync());
    var brand = brandJson.RootElement.GetProperty("data");
    Require(brandResponse.IsSuccessStatusCode && brand.GetProperty("displayName").GetString() == "工艺智能管理系统" &&
        brand.GetProperty("appCode").GetString() == "B" && brand.GetProperty("portalUrl").GetString() == "http://localhost:3006/#/portal",
        "anonymous B branding loads configured name while retaining A portal URL");
    Require(brandResponse.Headers.CacheControl?.NoStore == true, "branding metadata must not cache stale configured names");
    using var defaultBrandJson = JsonDocument.Parse(await anonymous.GetStringAsync("/api/system-applications/entry-information"));
    Require(defaultBrandJson.RootElement.GetProperty("data").GetProperty("appCode").GetString() == "A", "old A entry clients remain compatible");
    Require((await anonymous.GetAsync("/api/system-applications/entry-information?appCode=C")).StatusCode == HttpStatusCode.BadRequest,
        "invalid public system codes are rejected without exposing account configuration");
    var initializeJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"smoke\",\"version\":\"1\"}}}";
    Require((await anonymous.PostAsync("/mcp/v2", JsonBody(initializeJson))).StatusCode == HttpStatusCode.Unauthorized, "anonymous V2 access denied");
    Require((await anonymous.GetAsync("/api/external/v1/tools")).StatusCode == HttpStatusCode.Unauthorized, "legacy OpenAPI cannot bypass MCP authentication");
    Require((await client.PostAsync("/mcp/v2", JsonBody(initializeJson))).StatusCode == HttpStatusCode.Unauthorized, "application audience token cannot access MCP");
    var originalUser = await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1");
    Require((await anonymous.GetAsync("/api/user-profile/avatar")).StatusCode == HttpStatusCode.Unauthorized,
        "anonymous users cannot read private avatars");
    using (var anonymousUpload = AvatarBody(ImageBytes(SKEncodedImageFormat.Png)))
        Require((await anonymous.PostAsync("/api/user-profile/avatar", anonymousUpload)).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous users cannot upload avatars");
    Require((await client.GetAsync("/api/user-profile/avatar")).StatusCode == HttpStatusCode.NoContent,
        "users without custom avatars receive 204 for frontend default fallback");
    using var portalClient = new HttpClient { BaseAddress = client.BaseAddress };
    portalClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(jwt.Issuer, jwt.Audience, user.Claims,
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecurityKey)), SecurityAlgorithms.HmacSha256))));
    using (var portalUpload = AvatarBody(ImageBytes(SKEncodedImageFormat.Png)))
        Require((await portalClient.PostAsync("/api/user-profile/avatar", portalUpload)).StatusCode == HttpStatusCode.Forbidden,
            "portal-purpose JWT must enter an authorized application before uploading an avatar");
    string? lastAvatarKey = null;
    foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
    {
        using var upload = AvatarBody(ImageBytes(format));
        upload.Add(new StringContent("default-admin"), "userId"); // 伪造归属不能修改别人的账号。
        using var uploaded = await client.PostAsync("/api/user-profile/avatar", upload);
        var body = await uploaded.Content.ReadAsStringAsync();
        Require(uploaded.IsSuccessStatusCode, "ordinary B user avatar upload without user-management permission: " + body);
        var updated = await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1");
        Require(updated.AvatarFileKey != null && updated.AvatarFileKey != lastAvatarKey &&
            updated.Password == originalUser.Password && updated.Roles == originalUser.Roles && updated.PermissionKeys == originalUser.PermissionKeys,
            "avatar update changes only current user's avatar and update timestamp");
        Require(File.Exists(Path.Combine(avatarRoot, updated.AvatarFileKey!)) &&
            (lastAvatarKey == null || !File.Exists(Path.Combine(avatarRoot, lastAvatarKey))),
            "successful replacement retains new file and cleans the old managed file");
        lastAvatarKey = updated.AvatarFileKey;
        var image = await client.GetByteArrayAsync("/api/user-profile/avatar");
        using var imageData = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(imageData);
        Require(codec != null && codec.Info.Width == 512 && codec.Info.Height == 512 && codec.EncodedFormat == SKEncodedImageFormat.Png,
            "backend normalizes uploaded images to a real 512x512 PNG");
    }
    Require((await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "default-admin")).AvatarFileKey == null &&
        (await adminClient.GetAsync("/api/user-profile/avatar")).StatusCode == HttpStatusCode.NoContent,
        "forged userId cannot overwrite or read another account's avatar");
    var aTicket = JsonSerializer.SerializeToElement(await service.CreateTicketAsync(user, ProofRequest("A")));
    var aToken = JsonSerializer.SerializeToElement(await service.RedeemTicketAsync(new ApplicationTicketRedeemRequest
    {
        AppCode = "A", Ticket = aTicket.GetProperty("ticket").GetString()!, State = "state-for-smoke", CodeVerifier = Verifier
    })).GetProperty("token").GetString()!;
    using var aClient = new HttpClient { BaseAddress = client.BaseAddress };
    aClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aToken);
    Require((await aClient.GetByteArrayAsync("/api/user-profile/avatar")).SequenceEqual(await client.GetByteArrayAsync("/api/user-profile/avatar")),
        "same account shares avatar bytes across systems A and B");
    foreach (var invalid in new[] { Encoding.UTF8.GetBytes("<svg></svg>"), new byte[0], new byte[UserAvatarService.MaximumFileBytes + 1], ImageBytes(SKEncodedImageFormat.Png, 4097) })
    {
        using var upload = AvatarBody(invalid);
        var invalidResponse = await client.PostAsync("/api/user-profile/avatar", upload);
        Require(invalidResponse.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            "invalid, empty, oversized or excessive-resolution avatar is rejected without SQL/file changes");
        Require((await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1")).AvatarFileKey == lastAvatarKey,
            "failed validation preserves existing avatar");
    }
    db.CodeFirst.InitTables<SysUserInfo>();
    Require((await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1")).AvatarFileKey == lastAvatarKey,
        "repeat schema initialization preserves a saved avatar");
    var accountService = new UserInfoService(scope);
    await accountService.UpdateUserAsync(new AccountSaveDto
    {
        userId = "u1", userName = "operator", nickName = "operator", enabled = true, roleCodes = ["R_USER"]
    });
    await accountService.SaveUserPermissionAsync(new AccountSaveDto
    {
        userId = "u1", roleCodes = ["R_USER"], permissionKeys = []
    });
    Require((await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1")).AvatarFileKey == lastAvatarKey,
        "account management and permission updates preserve avatar column");
    using var avatarMetadataResponse = await client.GetAsync("/api/user-profile/avatar/info");
    Require(avatarMetadataResponse.Headers.CacheControl?.NoStore == true &&
        (await client.GetStringAsync("/api/UserInfo/GetUserInfo")).Contains(lastAvatarKey!),
        "profile returns avatar metadata with no-store synchronization");
    Require((await client.PostAsync("/api/user-profile/avatar/reset", null)).IsSuccessStatusCode &&
        (await client.PostAsync("/api/user-profile/avatar/reset", null)).IsSuccessStatusCode &&
        (await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == "u1")).AvatarFileKey == null &&
        !File.Exists(Path.Combine(avatarRoot, lastAvatarKey!)), "reset is idempotent and writes SQL NULL while cleaning managed file");
    var mcpToken = await service.CreateMcpTokenAsync(bPrincipal);
    using var mcpClient = new HttpClient { BaseAddress = client.BaseAddress };
    mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mcpToken);
    Require((await mcpClient.GetAsync("/api/user-profile/avatar/info")).StatusCode == HttpStatusCode.Unauthorized,
        "MCP audience token cannot access personal profile APIs");
    mcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    var initialize = await mcpClient.PostAsync("/mcp/v2", JsonBody(initializeJson));
    Require(initialize.IsSuccessStatusCode, "standard V2 initialize with dedicated user token");
    mcpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
    var toolsRequest = await mcpClient.PostAsync("/mcp/v2", JsonBody("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"));
    var toolsBody = await toolsRequest.Content.ReadAsStringAsync();
    Require(toolsRequest.IsSuccessStatusCode && toolsBody.Contains("production.line-runtime") && !toolsBody.Contains("clawbot.send"), "standard V2 lists only authorized tools");
    using var adminMcpClient = new HttpClient { BaseAddress = client.BaseAddress };
    adminMcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await service.CreateMcpTokenAsync(adminBPrincipal));
    adminMcpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    adminMcpClient.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
    var adminTools = await adminMcpClient.PostAsync("/mcp/v2", JsonBody("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"));
    var adminToolsBody = await adminTools.Content.ReadAsStringAsync();
    Require(adminTools.IsSuccessStatusCode && adminToolsBody.Contains("clawbot.send") && adminToolsBody.Contains("project."), "admin B wildcard exposes all authorized MCP tools without sending messages");
    var callRequest = await mcpClient.PostAsync("/mcp/v2", JsonBody("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"production.line-runtime\",\"arguments\":{\"lineCode\":\"L2\"}}}"));
    Require((await callRequest.Content.ReadAsStringAsync()).Contains("isError"), "standard V2 rejects unauthorized resource call");
    await service.RevokeSessionAsync(user);
    Require((await client.GetAsync("/api/Menu/GetMenuTree")).StatusCode == HttpStatusCode.Unauthorized, "logout revokes app token before menu access");
    Require((await client.GetAsync("/api/user-profile/avatar/info")).StatusCode == HttpStatusCode.Unauthorized,
        "revoked session cannot obtain avatar metadata");
    Require((await mcpClient.PostAsync("/mcp/v2", JsonBody(initializeJson))).StatusCode == HttpStatusCode.Unauthorized, "logout also revokes user MCP token");
    await db.Insertable(new SysPortalSessionEntity { SessionId = "old-session", UserId = "u1", ExpiresAtUtc = DateTime.UtcNow.AddDays(-2) }).ExecuteCommandAsync();
    await service.CleanupExpiredSessionsAsync(CancellationToken.None);
    Require(!await db.Queryable<SysPortalSessionEntity>().AnyAsync(x => x.SessionId == "old-session"), "expired sessions are cleaned in bounded batches");
    Console.WriteLine("PASS: avatar populated-table migration, real PNG/JPEG/WebP uploads, SQL NULL reset, per-user ownership, A/B sharing and validation.");
    Console.WriteLine("PASS: schema migration, SQL nullability, PKCE replay/concurrency, A/B grants, shared mapping, MCP scopes/audit, HTTP isolation and logout.");
}
finally
{
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^CodexMultiSystem_[0-9a-f]{32}$")) throw new InvalidOperationException("Unsafe cleanup target");
    await MasterAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
    Console.WriteLine("Temporary SQL database removed.");
    if (Path.GetDirectoryName(avatarRoot) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
        !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(avatarRoot), "^CodexAvatar_[0-9a-f]{32}$"))
        throw new InvalidOperationException("Unsafe avatar cleanup target");
    if (Directory.Exists(avatarRoot)) Directory.Delete(avatarRoot, recursive: true);
}

async Task MasterAsync(string sql)
{
    using var command = master.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

static SysUserInfo User(string id, string name, string role) => new()
{
    UserId = id, UserName = name, Roles = role, PermissionKeys = "", Buttons = "", Password = "test-password",
    NickName = name, Email = "", Enabled = true, CreateTime = DateTime.Now
};

static ClaimsPrincipal Principal(string id, string name, string sessionId) => new(new ClaimsIdentity(
    [new Claim("Id", id), new Claim("Name", name), new Claim("SessionId", sessionId), new Claim("TokenPurpose", "portal")], "test"));

static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

static byte[] ImageBytes(SKEncodedImageFormat format, int width = 64)
{
    using var bitmap = new SKBitmap(width, 64);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.CornflowerBlue);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(format, 90);
    return data.ToArray();
}

static MultipartFormDataContent AvatarBody(byte[] bytes)
{
    var content = new MultipartFormDataContent();
    var file = new ByteArrayContent(bytes);
    file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(file, "file", "avatar.png");
    return content;
}

static JsonElement Property(JsonElement element, string name) => element.EnumerateObject()
    .Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

static ApplicationTicketRequest ProofRequest(string code) => new()
{
    AppCode = code, State = "state-for-smoke", CodeChallenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)))
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
}

static async Task Denied(Func<Task> callback, string message)
{
    try { await callback(); } catch (UnauthorizedAccessException) { return; }
    throw new InvalidOperationException("FAILED: " + message);
}

sealed class ConstantOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class BusinessProbe : DispatchProxy
{
    public static List<string> Calls { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetLineRuntimeAsync")
        {
            Calls.Add("line:" + args![0]);
            return Task.FromResult(new EdgeDashboardLineRuntimeDto { LineCode = (string)args[0]! });
        }
        throw new InvalidOperationException("Unexpected business call: " + method.Name);
    }
}

[SugarTable("ExternalToolAccessLog")]
public sealed class LegacyExternalLog
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64)] public string ClientId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)] public string ToolName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)] public string RequestId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)] public string ArgumentHash { get; set; } = string.Empty;
    [SugarColumn(Length = 32)] public string Status { get; set; } = "Success";
    [SugarColumn(Length = 1000, IsNullable = true)] public string? Message { get; set; }
    public int ResultCount { get; set; }
    public long DurationMs { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? RemoteIp { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
