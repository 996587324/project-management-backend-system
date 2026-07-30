using Model.Agent;

namespace Interface;

/// <summary>
/// Agent 工具注册表服务接口，负责初始化、查询和维护 Agent 可用工具。
/// </summary>
public interface IAgentToolRegistryService
{
    /// <summary>
    /// 确保指定 Agent 的定义和工具清单已经写入数据库。
    /// </summary>
    Task EnsureInitializedAsync(string agentCode);

    /// <summary>
    /// 查询 Agent 定义信息。
    /// </summary>
    Task<AgentDefinitionEntity?> GetAgentAsync(string agentCode);

    /// <summary>
    /// 查询指定 Agent 当前启用的工具列表。
    /// </summary>
    Task<List<AgentToolRegistryEntity>> GetEnabledToolsAsync(string agentCode);

    /// <summary>
    /// 查询指定 Agent 的全部工具，包括未启用的工具。
    /// </summary>
    Task<List<AgentToolRegistryEntity>> GetAllToolsAsync(string agentCode);

    /// <summary>
    /// 按工具名称查询单个工具定义。
    /// </summary>
    Task<AgentToolRegistryEntity?> GetToolAsync(string agentCode, string toolName);
}
