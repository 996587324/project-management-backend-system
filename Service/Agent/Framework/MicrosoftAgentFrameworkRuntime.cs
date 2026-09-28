using Interface;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Model.Agent;

namespace Service;

/// <summary>
/// 基于 Microsoft Agent Framework 的系统 Agent 运行时。
/// 领域工具、确认计划、权限校验和会话持久化仍由本项目业务层负责。
/// </summary>
public sealed class MicrosoftAgentFrameworkRuntime : IAgentFrameworkRuntime
{
    private readonly IAgentModelClient _modelClient;
    private readonly IAgentToolDispatcher _dispatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MicrosoftAgentFrameworkRuntime> _logger;

    public MicrosoftAgentFrameworkRuntime(
        IAgentModelClient modelClient,
        IAgentToolDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        ILogger<MicrosoftAgentFrameworkRuntime> logger)
    {
        _modelClient = modelClient;
        _dispatcher = dispatcher;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<AgentFrameworkRunResult> RunAsync(
        AgentFrameworkRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = new AgentFrameworkExecutionContext(request, _dispatcher);
        var chatClient = new AgentFrameworkModelChatClient(_modelClient, context);
        var functions = request.Tools
            .Select(tool => (AITool)new AgentFrameworkToolFunction(tool, context))
            .ToList();
        var agent = new ChatClientAgent(
            chatClient,
            request.SystemPrompt,
            string.IsNullOrWhiteSpace(request.AgentName) ? request.AgentCode : request.AgentName,
            $"{request.AgentCode} 业务 Agent",
            functions,
            _loggerFactory,
            null);

        try
        {
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, request.UserMessage)],
                cancellationToken: cancellationToken);

            return new AgentFrameworkRunResult
            {
                Success = true,
                Reply = context.FinalReply ?? "工具调用已完成",
                ToolCalls = context.ToolCalls
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Microsoft Agent Framework execution failed. AgentCode: {AgentCode}, ConversationId: {ConversationId}",
                request.AgentCode,
                request.ConversationId);
            return new AgentFrameworkRunResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ToolCalls = context.ToolCalls
            };
        }
    }
}
