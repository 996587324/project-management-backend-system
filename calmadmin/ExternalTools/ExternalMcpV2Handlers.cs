using System.Text.Json;
using Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Model.ExternalTools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace calmadmin.Controllers.ExternalTools;

/// <summary>
/// 标准 MCP SDK 的动态工具适配层。
/// 工具定义、参数校验、显式确认和审计均继续由 ExternalToolService 负责，
/// 此处只处理 MCP 协议对象与内部工具模型之间的转换。
/// </summary>
internal static class ExternalMcpV2Handlers
{
    internal static ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var services = request.Services ?? throw new InvalidOperationException("MCP 请求服务上下文不可用");
        var caller = GetCaller(services);
        var service = services.GetRequiredService<IExternalToolService>();
        var tools = service.GetDefinitions(caller)
            .Select(definition => new Tool
            {
                Name = definition.Name,
                Title = definition.Title,
                Description = definition.Description,
                InputSchema = definition.InputSchema,
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = !definition.RequiredScope.EndsWith(".write", StringComparison.OrdinalIgnoreCase),
                    IdempotentHint = !definition.RequiredScope.EndsWith(".write", StringComparison.OrdinalIgnoreCase),
                    OpenWorldHint = false
                }
            })
            .ToList();

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    internal static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("MCP 请求服务上下文不可用");
        var service = services.GetRequiredService<IExternalToolService>();
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var result = await service.ExecuteAsync(
            request.Params.Name ?? string.Empty,
            ToJsonElement(request.Params.Arguments),
            GetCaller(services),
            httpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"),
            cancellationToken);

        var payload = result.Success ? result.Data : new { error = result.Message };
        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = JsonSerializer.Serialize(payload) }
            },
            StructuredContent = result.Success && result.Data != null
                ? JsonSerializer.SerializeToElement(result.Data)
                : null,
            IsError = !result.Success
        };
    }

    private static ExternalToolCallerContext GetCaller(IServiceProvider services)
    {
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        return httpContext == null
            ? new ExternalToolCallerContext
            {
                IsDirectAccess = true,
                ClientId = "internal-mcp"
            }
            : ExternalToolCallerContextFactory.CreateDirect(httpContext);
    }

    private static JsonElement ToJsonElement(IDictionary<string, JsonElement>? arguments) =>
        JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>());
}
