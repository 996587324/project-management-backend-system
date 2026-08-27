using System.Text.Json;
using Interface;
using Model.Agent;

namespace Service;

/// <summary>
/// 强类型 Agent 工具动作基类，统一完成 JSON 参数反序列化，业务动作只处理已经校验后的参数模型。
/// </summary>
public abstract class AgentToolActionBase<TArguments> : IAgentToolAction
    where TArguments : class, new()
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public abstract AgentToolSeed Definition { get; }

    /// <summary>把通用 JsonElement 转换为强类型参数后调用具体业务动作。</summary>
    public Task<AgentBusinessToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<TArguments>(arguments.GetRawText(), JsonOptions) ?? new TArguments();
        return ExecuteAsync(payload, context, cancellationToken);
    }

    /// <summary>由具体工具实现的强类型执行方法。</summary>
    protected abstract Task<AgentBusinessToolResult> ExecuteAsync(
        TArguments arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken);
}
