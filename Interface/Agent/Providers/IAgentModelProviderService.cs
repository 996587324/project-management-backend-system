using Model.Agent;

namespace Interface;

/// <summary>模型平台配置、Agent 绑定和连通性管理服务。</summary>
public interface IAgentModelProviderService
{
    Task<AgentModelProviderPageDto> GetPageAsync(string ownerUserId, AgentModelProviderQueryDto query);
    Task<List<AgentModelProviderDto>> GetOptionsAsync(string ownerUserId);
    Task<AgentModelProviderDto?> GetAsync(string ownerUserId, int id);
    Task<AgentModelProviderDto> SaveAsync(string ownerUserId, AgentModelProviderSaveDto dto);
    Task<AgentModelProviderDto> SetEnabledAsync(string ownerUserId, int id, bool enabled);
    Task<bool> DeleteAsync(string ownerUserId, int id);
    Task<List<AgentModelBindingDto>> GetBindingsAsync(string ownerUserId);
    Task<AgentModelBindingDto> SaveBindingAsync(string ownerUserId, AgentModelBindingSaveDto dto);
}
