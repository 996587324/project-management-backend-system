using Model.Agent;

namespace Interface;

/// <summary>
/// Microsoft Agent Framework 运行时。
/// 对上层隐藏模型协议、函数工具循环与框架会话实现细节。
/// </summary>
public interface IAgentFrameworkRuntime
{
    Task<AgentFrameworkRunResult> RunAsync(
        AgentFrameworkRunRequest request,
        CancellationToken cancellationToken = default);
}
