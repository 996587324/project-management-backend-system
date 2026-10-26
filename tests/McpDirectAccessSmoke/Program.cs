using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using calmadmin.Config;
using calmadmin.Controllers.ExternalTools;
using Interface;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Model.ExternalTools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Service;

// Real HTTP controllers and MCP SDK handlers, with no database/model/WeChat calls.
// Business persistence and audit insertion are covered by MultiSystemSmoke separately.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
var service = DispatchProxy.Create<IExternalToolService, ToolServiceProbe>();
var probe = (ToolServiceProbe)(object)service;
builder.Services.AddSingleton(service);
builder.Services.AddSingleton(DispatchProxy.Create<ISystemApplicationService, RejectApplicationAccessProbe>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddControllers(options => options.Filters.Add<SystemApplicationAccessFilter>())
    .AddApplicationPart(typeof(ExternalToolController).Assembly)
    .AddNewtonsoftJson();
builder.Services.AddAuthentication("smoke")
    .AddScheme<AuthenticationSchemeOptions, AnonymousAuthenticationHandler>("smoke", _ => { });
builder.Services.AddAuthorization();
var handlers = typeof(ExternalToolController).Assembly
    .GetType("calmadmin.Controllers.ExternalTools.ExternalMcpV2Handlers")!;
var listHandler = handlers.GetMethod("ListToolsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
var callHandler = handlers.GetMethod("CallToolAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithListToolsHandler((request, cancellation) =>
        (ValueTask<ListToolsResult>)listHandler.Invoke(null, [request, cancellation])!)
    .WithCallToolHandler((request, cancellation) =>
        (ValueTask<CallToolResult>)callHandler.Invoke(null, [request, cancellation])!);

await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapMcp("/mcp/v2").AllowAnonymous();
app.MapGet("/protected", () => "private").RequireAuthorization();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");

Require((await client.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized,
    "normal authorized endpoints remain protected");
Require((await client.GetAsync("/api/wechat-clawbot/push-queue/records")).StatusCode == HttpStatusCode.Unauthorized,
    "new internal Agent push queue is not anonymously accessible through MCP mode");
Require((await client.GetAsync("/api/external-tool-clients")).StatusCode == HttpStatusCode.Unauthorized,
    "MCP administrative configuration remains protected");

foreach (var endpoint in new[] { "/mcp/v2", "/mcp" })
{
    await ExpectRpcAsync(endpoint, "initialize", new
    {
        protocolVersion = "2025-06-18",
        capabilities = new { },
        clientInfo = new { name = "anonymous-smoke", version = "1.0" }
    }, "serverInfo");
    await ExpectRpcAsync(endpoint, "tools/list", new { }, "smoke.read");
    await ExpectRpcAsync(endpoint, "tools/call", new
    {
        name = "smoke.read", arguments = new { }
    }, "direct-ok");
    await ExpectRpcAsync(endpoint, "tools/call", new
    {
        name = "smoke.write", arguments = new { confirm = true, lineCode = "L2" }
    }, "direct-ok");
}

Require((await client.GetStringAsync("/api/external/v1/tools")).Contains("smoke.write"),
    "anonymous OpenAPI lists both read and write tools");
using (var response = await client.PostAsJsonAsync("/api/external/v1/tools/smoke.read", new { }))
{
    Require(response.IsSuccessStatusCode, "anonymous OpenAPI can call a tool");
}

client.DefaultRequestHeaders.Authorization = new("Bearer", "stale-invalid-token");
await ExpectRpcAsync("/mcp/v2", "tools/list", new { }, "smoke.write");
Require((await client.GetAsync("/protected")).StatusCode == HttpStatusCode.Unauthorized,
    "invalid tokens do not grant access to normal endpoints");
Require(probe.Callers.Count >= 9 && probe.Callers.All(caller =>
    caller.IsDirectAccess && caller.AllResources && caller.ClientId == "anonymous-mcp" &&
    caller.UserId == null && caller.AppCode == null && !string.IsNullOrWhiteSpace(caller.RemoteIp)),
    "all external routes use anonymous source context with IP, without fabricated user/app or resource limits");

// Verify the actual production write-confirmation guard, not a mock implementation.
var confirmGuard = typeof(ExternalToolService)
    .GetMethod("EnsureWriteConfirmed", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var value in new[] { "{}", "{\"confirm\":false}", "{\"confirm\":\"true\"}" })
{
    var rejected = false;
    try
    {
        confirmGuard.Invoke(null, [JsonSerializer.Deserialize<JsonElement>(value)]);
    }
    catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
    {
        rejected = true;
    }

    Require(rejected, "production write guard rejects absent, false or non-boolean confirmation");
}
confirmGuard.Invoke(null, [JsonSerializer.SerializeToElement(new { confirm = true })]);
Console.WriteLine("PASS: anonymous V2/legacy/OpenAPI access, protected admin endpoints, direct caller context and production write confirmation guard.");
await app.StopAsync();

async Task ExpectRpcAsync(string endpoint, string method, object parameters, string expected)
{
    using var response = await client.PostAsJsonAsync(endpoint, new
    {
        jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters
    });
    var body = await response.Content.ReadAsStringAsync();
    Require(response.IsSuccessStatusCode && body.Contains(expected) && !body.Contains("\"error\":"),
        $"{endpoint} {method}: {response.StatusCode} {body}");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public class ToolServiceProbe : DispatchProxy
{
    public List<ExternalToolCallerContext> Callers { get; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IExternalToolService.GetServerSettingAsync))
        {
            return Task.FromResult(new ExternalToolServerSettingDto());
        }

        if (method?.Name == nameof(IExternalToolService.GetDefinitions))
        {
            Callers.Add((ExternalToolCallerContext)args![0]!);
            return new List<ExternalToolDefinitionDto>
            {
                Definition("smoke.read", "dashboard.read"),
                Definition("smoke.write", "dashboard.write")
            };
        }

        if (method?.Name == nameof(IExternalToolService.ExecuteAsync))
        {
            Callers.Add((ExternalToolCallerContext)args![2]!);
            if (string.IsNullOrWhiteSpace((string?)args[3]))
            {
                throw new InvalidOperationException("HTTP request identifier must be preserved for audit");
            }
            return Task.FromResult(new ExternalToolExecutionResultDto
            {
                Success = true, Data = new { result = "direct-ok" }
            });
        }

        throw new NotSupportedException(method?.Name);
    }

    private static ExternalToolDefinitionDto Definition(string name, string scope)
    {
        return new ExternalToolDefinitionDto
        {
            Name = name, Title = name, RequiredScope = scope,
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
        };
    }
}

public sealed class AnonymousAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public AnonymousAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

public class RejectApplicationAccessProbe : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        throw new InvalidOperationException("Anonymous MCP must not resolve A/B user permissions: " + method?.Name);
    }
}
