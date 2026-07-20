using Model.Agent;

namespace Interface;

/// <summary>
/// Agent 模型客户端接口，封装大模型调用、工具选择和最终回复生成。
/// </summary>
public interface IAgentModelClient
{
    /// <summary>
    /// 让模型从可用工具中选择需要调用的工具，并生成工具参数。
    /// </summary>
    Task<AgentToolSelectionReply> SelectToolsAsync(AgentToolSelectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据用户消息、历史记录和工具结果生成最终回复。
    /// </summary>
    Task<AgentModelReply> GenerateReplyAsync(AgentModelRequest request, CancellationToken cancellationToken = default);
}
