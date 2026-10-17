using System.Runtime.CompilerServices;
using Interface;
using Microsoft.Extensions.AI;
using Model.Agent;

namespace Service;

/// <summary>
/// 将现有 Turing / DeepSeek 模型平台适配为 Microsoft.Extensions.AI.IChatClient。
/// 保留现有模型配置、DSML 兼容、故障回退及调用日志，避免替换运行框架后丢失平台兼容能力。
/// </summary>
internal sealed class AgentFrameworkModelChatClient : IChatClient
{
    private readonly IAgentModelClient _modelClient;
    private readonly AgentFrameworkExecutionContext _context;

    public AgentFrameworkModelChatClient(
        IAgentModelClient modelClient,
        AgentFrameworkExecutionContext context)
    {
        _modelClient = modelClient;
        _context = context;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _context.HasCompletedToolExecution
            ? await GenerateFinalReplyAsync(cancellationToken)
            : await SelectToolsOrReplyAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                MessageId = message.MessageId
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
        // 模型 HttpClient 由 DI 容器管理；每次调用创建的适配器不持有需要释放的资源。
    }

    private async Task<ChatResponse> SelectToolsOrReplyAsync(CancellationToken cancellationToken)
    {
        if (_context.Request.Tools.Count == 0)
            return await GenerateFinalReplyAsync(cancellationToken);

        var selection = await _modelClient.SelectToolsAsync(new AgentToolSelectionRequest
        {
            AgentCode = _context.Request.AgentCode,
            UserId = _context.Request.UserId,
            SystemPrompt = _context.Request.SystemPrompt,
            UserMessage = _context.Request.UserMessage,
            ScopeProjectName = _context.Request.ScopeProjectName,
            History = _context.Request.History,
            Tools = _context.Request.Tools,
            MaxToolCalls = _context.Request.MaxToolCalls,
            MaxOutputTokens = _context.Request.MaxOutputTokens
        }, cancellationToken);

        if (!selection.Success)
        {
            if (AgentChannelExecutionScope.Current != null)
                throw new InvalidOperationException("模型暂时无法选择工具");
            return CreateTextResponse(selection.ErrorMessage ?? "模型暂时无法选择可用工具");
        }

        if (selection.ToolCalls.Count == 0)
        {
            return await GenerateFinalReplyAsync(cancellationToken);
        }

        var calls = selection.ToolCalls
            .Take(Math.Clamp(_context.Request.MaxToolCalls, 1, 8))
            .Select(call => new FunctionCallContent(
                string.IsNullOrWhiteSpace(call.CallId) ? Guid.NewGuid().ToString("N") : call.CallId,
                call.ToolName,
                ParseArguments(call.ArgumentsJson)))
            .Cast<AIContent>()
            .ToList();

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, calls));
    }

    private async Task<ChatResponse> GenerateFinalReplyAsync(CancellationToken cancellationToken)
    {
        var reply = await _modelClient.GenerateReplyAsync(new AgentModelRequest
        {
            AgentCode = _context.Request.AgentCode,
            UserId = _context.Request.UserId,
            UserMessage = _context.Request.UserMessage,
            SystemPrompt = _context.Request.SystemPrompt,
            ScopeProjectName = _context.Request.ScopeProjectName,
            History = _context.Request.History,
            ToolResults = _context.BuildToolResults()
        }, cancellationToken);

        if (!reply.Success || string.IsNullOrWhiteSpace(reply.Content))
            throw new AgentReplyGenerationException(reply.FailureKind == AgentReplyFailureKind.None
                ? AgentReplyFailureKind.EmptyResponse
                : reply.FailureKind);
        _context.FinalReply = reply.Success && !string.IsNullOrWhiteSpace(reply.Content)
            ? reply.Content.Trim()
            : reply.ErrorMessage ?? "暂时无法生成回答";
        return CreateTextResponse(_context.FinalReply);
    }

    private static ChatResponse CreateTextResponse(string content) =>
        new(new ChatMessage(ChatRole.Assistant, content));

    private static IDictionary<string, object?> ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new Dictionary<string, object?>();

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new Dictionary<string, object?>();

            return document.RootElement.EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }
}
