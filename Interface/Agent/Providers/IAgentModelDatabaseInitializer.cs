namespace Interface;

/// <summary>集中初始化 Agent 模型平台表结构和查询索引。</summary>
public interface IAgentModelDatabaseInitializer
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
    Task EnsureSystemProviderPresetsAsync(CancellationToken cancellationToken = default);
}
