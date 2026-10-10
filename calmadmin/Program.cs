using Microsoft.OpenApi.Models;
using Interface;
using Model;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Service;
using Swashbuckle.AspNetCore.SwaggerUI;
using calmadmin.Controllers.ExternalTools;
using calmadmin.WebSockets;
using 上传MES.Config;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null) //防止传过来JSON对象首字母小写所以置为null
    .AddNewtonsoftJson(op =>
    {
        //JSON 不处理null值，以及日期时间格式化,否则导致接受不到实体
        op.SerializerSettings.ContractResolver = new DefaultContractResolver();
        op.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        op.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
    });////builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
var mcpV2ServerInfoProvider = new ExternalMcpV2ServerInfoProvider();
builder.Services.AddSingleton(mcpV2ServerInfoProvider);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = mcpV2ServerInfoProvider.ServerInfo;
        options.ServerInstructions = "内网数据工具服务。读取工具可直接调用；越限告警的认领与确认必须显式传入 confirm: true。";
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithListToolsHandler(ExternalMcpV2Handlers.ListToolsAsync)
    .WithCallToolHandler(ExternalMcpV2Handlers.CallToolAsync);
// 添加配置文件
// WebApplication.CreateBuilder already loads base and environment-specific appsettings files.
builder.Register();
builder.Services.AddScoped<AgentWebSocketHandler>();

// 注册数据库工厂（单例）
builder.Services.AddSingleton<DbFactory>();

// 注册业务服务
builder.Services.AddTransient<DataService>();
// 读取 appsettings.json 中的 Urls 配置
var urls = builder.Configuration["Urls"];
if (!string.IsNullOrEmpty(urls))
{
    builder.WebHost.UseUrls(urls); // 应用自定义域名配置
}

var app = builder.Build();
app.UseMiddleware<calmadmin.Middleware.RequestTimingMiddleware>();
// Configure the HTTP request pipeline.
////if (app.Environment.IsDevelopment())//不是调试环境下一样可以启动swagger
{
        
    app.UseSwagger();
    //app.UseSwaggerUI();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MyMES V1");
        c.RoutePrefix = string.Empty;
        //c.DocExpansion(DocExpansion.None);//样式
    });
}

#region 鉴权授权
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});
app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp/v2"))
    {
        try
        {
            var provider = context.RequestServices.GetRequiredService<ExternalMcpV2ServerInfoProvider>();
            var externalToolService = context.RequestServices.GetRequiredService<IExternalToolService>();
            await provider.RefreshAsync(externalToolService);
        }
        catch (Exception exception)
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("ExternalMcpV2")
                .LogWarning(exception, "Unable to refresh MCP V2 server information; using the last known value.");
        }
    }

    await next();
});
#endregion
app.MapControllers();
app.MapMcp("/mcp/v2").AllowAnonymous();
app.MapGet("/ws/agent", async (HttpContext context, AgentWebSocketHandler handler) =>
{
    await handler.HandleAsync(context);
}).RequireAuthorization();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
