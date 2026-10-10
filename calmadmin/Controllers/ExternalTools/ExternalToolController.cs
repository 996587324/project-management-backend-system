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
/// <remarks>该入口按内网直连模式开放，不校验 ClientId、ClientSecret、Token 或来源 IP。</remarks>
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
        ["data"] = ExternalToolJsonMapper.ToTools(_service.GetDefinitions(BuildCaller()))
    });

    [HttpPost("tools/{toolName}")]
    public async Task<IActionResult> Execute(string toolName, [FromBody] JObject arguments, CancellationToken cancellationToken)
    {
        var result = await _service.ExecuteAsync(
            toolName,
            ToJsonElement(arguments),
            BuildCaller(),
            GetRequestId(),
            cancellationToken);
        return result.Success ? Ok(new { data = result.Data }) : BadRequest(new { message = result.Message });
    }

    private ExternalToolCallerContext BuildCaller() => ExternalToolCallerContextFactory.CreateDirect(HttpContext);

    private string GetRequestId() => HttpContext.TraceIdentifier;

    private static JsonElement ToJsonElement(JToken? token) =>
        JsonDocument.Parse(token?.ToString(Formatting.None) ?? "{}").RootElement.Clone();
}

/// <summary>远程 MCP 的 Streamable HTTP 最小端点；只声明并执行白名单只读工具。</summary>
/// <remarks>该入口按内网直连模式开放，不校验 ClientId、ClientSecret、Token 或来源 IP。</remarks>
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
    /// <summary>
    /// 内网 MCP 调用不依赖外部客户端凭据；调用 IP 与固定调用方标识仍会写入审计日志。
    /// </summary>
    internal static ExternalToolCallerContext CreateDirect(HttpContext context) => new()
    {
        IsDirectAccess = true,
        ClientId = "internal-mcp",
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
