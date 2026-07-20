using Model.Agent;

namespace Interface;

/// <summary>
/// Agent 工具调度器接口，负责创建执行计划、确认写操作并调用业务执行器。
/// </summary>
public interface IAgentToolDispatcher
{
    /// <summary>
    /// 根据工具名和参数创建工具调用；写操作会先进入待确认状态。
    /// </summary>
    Task<AgentToolCallDto> DispatchAsync(AgentToolDispatchRequest request);

    /// <summary>
    /// 确认并执行待确认的工具调用。
    /// </summary>
    Task<AgentToolCallDto?> ConfirmAsync(AgentConfirmRequest request);

    /// <summary>
    /// 查询一个会话下已经产生的工具调用记录。
    /// </summary>
    Task<List<AgentToolCallDto>> GetConversationExecutionsAsync(int conversationId);
}
