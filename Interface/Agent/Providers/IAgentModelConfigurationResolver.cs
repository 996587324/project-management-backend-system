using Model.Agent;

namespace Interface;

/// <summary>从后端统一配置解析 Agent 的模型调用参数。</summary>
public interface IAgentModelConfigurationResolver
{
    Task<IReadOnlyList<RuntimeAgentOptions>> ResolveCandidatesAsync(
        string agentCode,
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<RuntimeAgentOptions?> ResolveProviderAsync(
        int providerId,
        string ownerUserId,
        string agentCode,
        CancellationToken cancellationToken = default);
}
