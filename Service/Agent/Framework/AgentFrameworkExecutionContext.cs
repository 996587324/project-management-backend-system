using System.Text.Json;
using Interface;
using Microsoft.Extensions.AI;
using Model.Agent;

namespace Service;

/// <summary>
/// 单次 Microsoft Agent Framework 调用的业务上下文。
/// 每个请求单独创建，确保不同用户、会话和工具结果不会互相串扰。
/// </summary>
internal sealed class AgentFrameworkExecutionContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IAgentToolDispatcher _dispatcher;

    public AgentFrameworkExecutionContext(
        AgentFrameworkRunRequest request,
        IAgentToolDispatcher dispatcher)
    {
        Request = request;
        _dispatcher = dispatcher;
    }

    public AgentFrameworkRunRequest Request { get; }
    public List<AgentToolCallDto> ToolCalls { get; } = [];
    public string? FinalReply { get; set; }

    public bool HasCompletedToolExecution => ToolCalls.Count > 0;

    public async ValueTask<object?> InvokeToolAsync(
        string toolName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (ToolCalls.Count >= Math.Clamp(Request.MaxToolCalls, 1, 8))
        {
            return new
            {
                success = false,
                message = $"单次请求最多允许调用 {Math.Clamp(Request.MaxToolCalls, 1, 8)} 个工具"
            };
        }

        var tool = Request.Tools.FirstOrDefault(item =>
            item.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool != null && Request.OnToolInvokingAsync != null)
            await Request.OnToolInvokingAsync(tool);

        var call = await _dispatcher.DispatchAsync(new AgentToolDispatchRequest
        {
            ConversationId = Request.ConversationId,
            AgentCode = Request.AgentCode,
            ToolName = toolName,
            ArgumentsJson = JsonSerializer.Serialize(arguments, JsonOptions),
            UserId = Request.UserId
        });
        ToolCalls.Add(call);

        return new
        {
            toolName = call.ToolName,
            status = call.Status,
            requiresConfirmation = call.RequiresConfirmation,
            confirmed = call.Confirmed,
            result = ParseResult(call.ResponseJson)
        };
    }

    public List<AgentToolResultMessageDto> BuildToolResults() => ToolCalls
        .Select(call => new AgentToolResultMessageDto
        {
            ToolName = call.ToolName,
            Status = call.Status,
            ResultJson = call.ResponseJson
        })
        .ToList();

    private static object? ParseResult(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return responseJson;
        }
    }
}
