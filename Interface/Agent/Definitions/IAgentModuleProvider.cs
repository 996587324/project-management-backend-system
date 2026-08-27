using Model.Agent;

namespace Interface;

/// <summary>
/// Agent 模块配置提供器。实现类由 IOC 自动发现，通用 Agent 核心不感知具体业务模块。
/// </summary>
public interface IAgentModuleProvider
{
    /// <summary>返回 Agent 自身定义、系统提示词和前端建议问题。</summary>
    AgentModuleSeed Module { get; }

    /// <summary>
    /// 返回由原有分组执行器处理的工具配置；独立工具动作可通过 IAgentToolAction 自行提供配置。
    /// </summary>
    IReadOnlyCollection<AgentToolSeed> Tools { get; }
}
