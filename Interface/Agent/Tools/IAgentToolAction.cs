using System.Text.Json;
using Model.Agent;

namespace Interface;

/// <summary>
/// 可独立注册的 Agent 工具动作。新增动作类后 IOC 会自动发现，不需要修改调度器的 switch。
/// </summary>
public interface IAgentToolAction
{
    /// <summary>当前动作对应的工具配置。</summary>
    AgentToolSeed Definition { get; }

    /// <summary>执行已经通过权限、确认状态和 JSON Schema 校验的业务动作。</summary>
    Task<AgentBusinessToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default);
}
