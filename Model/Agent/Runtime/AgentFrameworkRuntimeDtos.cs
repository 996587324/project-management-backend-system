namespace Model.Agent;

/// <summary>
/// 交给 Microsoft Agent Framework 运行时的一次 Agent 调用上下文。
/// 会话、权限和审计仍由业务系统持久化，框架仅负责编排模型与工具循环。
/// </summary>
public sealed class AgentFrameworkRunRequest
{
    public int ConversationId { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string? ScopeProjectName { get; set; }
    public List<AgentMessageDto> History { get; set; } = [];
    public List<AgentToolDefinitionDto> Tools { get; set; } = [];
    public int MaxToolCalls { get; set; } = 4;
    public int? MaxOutputTokens { get; set; }
    /// <summary>
    /// 工具即将执行时的通知回调，仅用于当前请求的前端进度推送，不参与模型请求或持久化。
    /// </summary>
    public Func<AgentToolDefinitionDto, Task>? OnToolInvokingAsync { get; set; }
}

/// <summary>Microsoft Agent Framework 运行完成后返回给应用服务的业务结果。</summary>
public sealed class AgentFrameworkRunResult
{
    public bool Success { get; set; }
    public string Reply { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<AgentToolCallDto> ToolCalls { get; set; } = [];
}
