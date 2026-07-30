using Model.Agent;
using Model.ProjectManage;

namespace Interface;

/// <summary>
/// Agent 应用服务接口，面向控制器提供会话、消息、工具确认和邮件能力。
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// 分页查询 Agent 会话。
    /// </summary>
    Task<PagedResultDto<AgentConversationDto>> GetConversationPageAsync(string? userId, int current = 1, int size = 20);

    /// <summary>
    /// 查询单个会话详情，包括消息和工具调用记录。
    /// </summary>
    Task<AgentConversationDetailDto?> GetConversationAsync(int id, string? userId);

    /// <summary>
    /// 创建一个新的 Agent 会话。
    /// </summary>
    Task<AgentConversationDto> CreateConversationAsync(AgentStartRequest request);

    /// <summary>
    /// 发送用户消息，驱动模型选工具、执行工具并生成回复。
    /// </summary>
    Task<AgentResponseDto> SendMessageAsync(
        AgentSendRequest request,
        Func<AgentProgressDto, Task>? progress = null);

    /// <summary>
    /// 查询 Agent 可用工具目录。
    /// </summary>
    Task<List<AgentToolDefinitionDto>> GetToolCatalogAsync(string? agentCode = null);

    /// <summary>
    /// 确认执行一个待确认的写操作工具调用。
    /// </summary>
    Task<AgentToolCallDto?> ConfirmToolCallAsync(AgentConfirmRequest request);

    /// <summary>
    /// 删除指定会话。
    /// </summary>
    Task<bool> DeleteConversationAsync(AgentDeleteConversationRequest request);

    /// <summary>
    /// 让模型生成邮件草稿。
    /// </summary>
    Task<AgentEmailDraftDto?> CreateEmailDraftAsync(AgentEmailDraftRequest request);

    /// <summary>
    /// 发送邮件。
    /// </summary>
    Task<AgentEmailSendResultDto> SendEmailAsync(AgentEmailSendRequest request);
}
