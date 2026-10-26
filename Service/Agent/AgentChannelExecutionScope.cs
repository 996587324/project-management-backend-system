using Model.Agent;
using Model.SystemApplications;

namespace Service;

/// <summary>后台渠道的显式授权边界；不伪造网页 JWT，不改变现有网页调用。</summary>
public sealed class AgentChannelExecutionScope : IDisposable
{
    private static readonly AsyncLocal<AgentChannelExecutionScope?> Slot = new();
    private readonly AgentChannelExecutionScope? _previous;
    public static AgentChannelExecutionScope? Current => Slot.Value;
    public CancellationToken CancellationToken { get; }
    public bool AllowConfirmation { get; }
    public Func<AgentToolRegistryEntity, Task<string?>> AuthorizeToolAsync { get; }
    public Func<Task<ApplicationAccessContext>>? ResolveApplicationAccessAsync { get; }
    public AgentChannelResultDraft? PreviousResult { get; }

    public AgentChannelExecutionScope(Func<AgentToolRegistryEntity, Task<string?>> authorizeToolAsync,
        CancellationToken cancellationToken, bool allowConfirmation = true,
        Func<Task<ApplicationAccessContext>>? resolveApplicationAccessAsync = null,
        AgentChannelResultDraft? previousResult = null)
    {
        _previous = Slot.Value;
        AuthorizeToolAsync = authorizeToolAsync;
        CancellationToken = cancellationToken;
        AllowConfirmation = allowConfirmation;
        ResolveApplicationAccessAsync = resolveApplicationAccessAsync;
        PreviousResult = previousResult;
        Slot.Value = this;
    }

    public void Dispose() => Slot.Value = _previous;
}

/// <summary>仅由渠道在授权边界内构造，正文属于数据，不是模型指令。</summary>
public sealed record AgentChannelResultDraft(string AgentCode, long SourceJobId, string Content, string AccessHash);

public sealed class AgentChannelExecutionException(
    bool hasToolSideEffects,
    string? userFeedback = null,
    bool hasToolCalls = false) : Exception("微信 Agent 模型调用失败")
{
    public bool HasToolSideEffects { get; } = hasToolSideEffects;
    public bool HasToolCalls { get; } = hasToolCalls || hasToolSideEffects;
    public string UserFeedback { get; } = userFeedback ?? (hasToolSideEffects
        ? "AI 未能生成完整回答，已有操作可能执行。请先到后台核对执行记录，不要重复提交操作。"
        : "AI 暂时无法完成本次请求，请稍后重试，或缩小查询范围。本次未扣除对话额度。");
}

internal sealed class AgentReplyGenerationException(AgentReplyFailureKind failureKind)
    : Exception("模型未生成有效的完整回复")
{
    public AgentReplyFailureKind FailureKind { get; } = failureKind;
}
