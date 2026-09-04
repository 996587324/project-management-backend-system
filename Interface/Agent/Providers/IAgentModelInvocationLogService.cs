using Model.Agent;

namespace Interface;

/// <summary>写入模型平台调用观测日志，日志失败不能影响 Agent 主流程。</summary>
public interface IAgentModelInvocationLogService
{
    Task RecordAsync(AgentModelInvocationLogWriteDto dto, CancellationToken cancellationToken = default);
}
