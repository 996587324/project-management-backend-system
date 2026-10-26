using System.Security.Claims;
using System.Text.Json;
using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.ExternalTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Service;

namespace calmadmin.Controllers.ExternalTools;

/// <summary>为不支持 MCP 的模型平台提供等价的 OpenAPI 工具目录和调用入口。</summary>
/// <remarks>内网免认证直连；仍保留已登记工具、业务参数校验、写入确认和审计。</remarks>
[AllowAnonymous]
[ApiController]
[Route("api/external/v1")]
public sealed class ExternalToolController : ControllerBase
{
    private readonly IExternalToolService _service;

    public ExternalToolController(IExternalToolService service)
    {
        _service = service;
    }

    [HttpGet("tools")]
    public IActionResult GetTools() => Ok(new JObject
    {
        ["data"] = ExternalToolJsonMapper.ToTools(_service.GetDefinitions(ExternalToolCallerContextFactory.CreateDirect(HttpContext)))
    });

    [HttpPost("tools/{toolName}")]
    public async Task<IActionResult> Execute(string toolName, [FromBody] JObject arguments, CancellationToken cancellationToken)
    {
        var result = await _service.ExecuteAsync(
            toolName,
            ToJsonElement(arguments),
            ExternalToolCallerContextFactory.CreateDirect(HttpContext),
            GetRequestId(),
            cancellationToken);
        return result.Success ? Ok(new { data = result.Data }) : BadRequest(new { message = result.Message });
    }

    private string GetRequestId() => HttpContext.TraceIdentifier;

    private static JsonElement ToJsonElement(JToken? token) =>
        JsonDocument.Parse(token?.ToString(Formatting.None) ?? "{}").RootElement.Clone();
}

/// <summary>兼容旧版 MCP 协议入口；与标准 V2 一样使用内网免认证直连。</summary>
/// <remarks>仅开放已登记的业务工具；写操作仍要求 confirm: true 并记录审计。</remarks>
[AllowAnonymous]
[ApiController]
[Route("mcp")]
public sealed class ExternalMcpController : ControllerBase
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly IExternalToolService _service;

    public ExternalMcpController(IExternalToolService service)
    {
        _service = service;
    }

    [HttpPost]
    public async Task<IActionResult> Handle([FromBody] JObject request, CancellationToken cancellationToken)
    {
        if (request == null) return JsonRpcError(null, -32600, "Invalid Request");
        var id = request["id"]?.DeepClone();
        var method = request.Value<string>("method");
        var parameters = request["params"] as JObject ?? new JObject();
        var caller = ExternalToolCallerContextFactory.CreateDirect(HttpContext);

        if (string.Equals(method, "initialize", StringComparison.Ordinal))
            return await InitializeAsync(id);

        return method switch
        {
            "notifications/initialized" => NoContent(),
            "tools/list" => JsonRpcResult(id, new JObject
            {
                ["tools"] = ExternalToolJsonMapper.ToTools(_service.GetDefinitions(caller))
            }),
            "tools/call" => await CallToolAsync(id, parameters, caller, cancellationToken),
            _ => JsonRpcError(id, -32601, "Method not found")
        };
    }

    private async Task<IActionResult> InitializeAsync(JToken? id)
    {
        var setting = await _service.GetServerSettingAsync();
        return JsonRpcResult(id, new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { } },
            serverInfo = new { name = setting.ServerName, version = setting.Version }
        });
    }

    private async Task<IActionResult> CallToolAsync(
        JToken? id,
        JObject parameters,
        ExternalToolCallerContext caller,
        CancellationToken cancellationToken)
    {
        var toolName = parameters.Value<string>("name");
        if (string.IsNullOrWhiteSpace(toolName)) return JsonRpcError(id, -32602, "工具名称不能为空");

        var arguments = parameters["arguments"] ?? new JObject();
        var result = await _service.ExecuteAsync(
            toolName,
            ToJsonElement(arguments),
            caller,
            HttpContext.TraceIdentifier,
            cancellationToken);
        return JsonRpcResult(id, new
        {
            content = new[] { new { type = "text", text = System.Text.Json.JsonSerializer.Serialize(result.Success ? result.Data : new { error = result.Message }) } },
            isError = !result.Success
        });
    }

    private static IActionResult JsonRpcResult(JToken? id, object result) =>
        new JsonResult(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = JToken.FromObject(result)
        });

    private static IActionResult JsonRpcError(JToken? id, int code, string message) =>
        new JsonResult(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        });

    private static JsonElement ToJsonElement(JToken token) =>
        JsonDocument.Parse(token.ToString(Formatting.None)).RootElement.Clone();
}

internal static class ExternalToolCallerContextFactory
{
    internal static async Task<ExternalToolCallerContext> CreateAuthenticatedAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("MCP 需要授权令牌");
        if (context.User.FindFirst("TokenPurpose")?.Value != "mcp")
        {
            var caller = Create(context.User, context);
            var clients = await context.RequestServices.GetRequiredService<IExternalToolService>().GetClientsAsync();
            var client = clients.FirstOrDefault(x => x.ClientId == caller.ClientId && x.Enabled &&
                (x.ExpireTime == null || x.ExpireTime > DateTime.Now));
            if (client == null) throw new UnauthorizedAccessException("MCP 客户端已停用或过期");
            caller.Scopes.IntersectWith(Split(client.Scopes));
            caller.AllowedLineCodes.IntersectWith(Split(client.AllowedLineCodes));
            caller.AllowedProjectCodes.IntersectWith(Split(client.AllowedProjectCodes));
            caller.AllowedDeviceCodes.IntersectWith(Split(client.AllowedDeviceCodes));
            return caller;
        }
        var access = await context.RequestServices.GetRequiredService<ISystemApplicationService>()
            .GetAccessAsync(context.User, true);
        var service = context.RequestServices.GetRequiredService<IExternalToolService>();
        var scopes = access.McpScopes.Contains("*")
            ? service.GetDefinitions(new ExternalToolCallerContext { IsDirectAccess = true })
                .Select(x => x.RequiredScope).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : access.McpScopes;
        return new ExternalToolCallerContext
        {
            ClientId = $"user-{access.AppCode}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(access.UserId)))[..24]}",
            UserId = access.UserId,
            AppCode = access.AppCode,
            Scopes = scopes,
            AllResources = access.AllResources,
            AllowedLineCodes = access.LineCodes,
            AllowedProjectCodes = access.ProjectCodes,
            AllowedDeviceCodes = access.DeviceCodes,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString()
        };
    }
    /// <summary>
    /// 内网开放入口统一创建匿名直连来源，不伪造后台用户，也不按传入令牌提升或收窄权限。
    /// </summary>
    internal static ExternalToolCallerContext CreateDirect(HttpContext context) => new()
    {
        IsDirectAccess = true,
        AllResources = true,
        ClientId = "anonymous-mcp",
        RemoteIp = context.Connection.RemoteIpAddress?.ToString()
    };

    internal static ExternalToolCallerContext Create(ClaimsPrincipal principal, HttpContext context) => new()
    {
        ClientId = principal.FindFirstValue("client_id") ?? string.Empty,
        Scopes = Split(principal.FindFirstValue("scope")),
        AllowedLineCodes = Split(principal.FindFirstValue("external_lines")),
        AllowedProjectCodes = Split(principal.FindFirstValue("external_projects")),
        AllowedDeviceCodes = Split(principal.FindFirstValue("external_devices")),
        RemoteIp = context.Connection.RemoteIpAddress?.ToString()
    };

    private static HashSet<string> Split(string? value) => (value ?? string.Empty)
        .Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>把 System.Text.Json 的参数 Schema 转换为 Newtonsoft JSON，兼容本项目全局序列化配置。</summary>
internal static class ExternalToolJsonMapper
{
    internal static JArray ToTools(IEnumerable<ExternalToolDefinitionDto> definitions) => new(
        definitions.Select(tool => new JObject
        {
            ["name"] = tool.Name,
            ["title"] = tool.Title,
            ["description"] = tool.Description,
            ["inputSchema"] = JToken.Parse(tool.InputSchema.GetRawText())
        }));
}
