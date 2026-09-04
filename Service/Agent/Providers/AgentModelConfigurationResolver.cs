using Interface;
using Model.Agent;
using SqlSugar;

namespace Service;

/// <summary>
/// 从数据库解析系统统一的模型配置。普通用户只作为调用审计归属，不能覆盖模型平台、密钥或绑定关系。
/// </summary>
public sealed class AgentModelConfigurationResolver : IAgentModelConfigurationResolver
{
    private readonly SqlSugarScope _dbScope;
    private readonly IAgentSecretProtector _secretProtector;
    private readonly IAgentModelDatabaseInitializer _initializer;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    public AgentModelConfigurationResolver(
        SqlSugarScope dbScope,
        IAgentSecretProtector secretProtector,
        IAgentModelDatabaseInitializer initializer)
    {
        _dbScope = dbScope;
        _secretProtector = secretProtector;
        _initializer = initializer;
    }

    /// <summary>
    /// 按当前 Agent 的全局绑定优先级读取数据库配置；未配置专属绑定时，按平台列表顺序使用全部启用平台。
    /// </summary>
    public async Task<IReadOnlyList<RuntimeAgentOptions>> ResolveCandidatesAsync(
        string agentCode,
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ownerUserId = RequireOwnerUserId(ownerUserId);
        await _initializer.EnsureSystemProviderPresetsAsync(cancellationToken);

        var normalizedAgentCode = AgentToolRegistryService.NormalizeAgentCode(agentCode);
        var providers = await Db.Queryable<AgentModelProviderEntity>()
            .Where(x => x.OwnerUserId == AgentModelConfigurationScope.SystemOwnerUserId &&
                        x.IsDelete == 0 && x.Enabled)
            .OrderBy(x => x.ProviderName)
            .ToListAsync();
        if (providers.Count == 0) return Array.Empty<RuntimeAgentOptions>();

        var bindings = await Db.Queryable<AgentModelBindingEntity>()
            .Where(x => x.OwnerUserId == AgentModelConfigurationScope.SystemOwnerUserId &&
                        x.AgentCode == normalizedAgentCode && x.IsDelete == 0 && x.Enabled)
            .OrderByDescending(x => x.IsPrimary)
            .OrderBy(x => x.Priority)
            .ToListAsync();

        var providerMap = providers.ToDictionary(x => x.Id);
        var selectedProviders = bindings
            .Select(x => providerMap.GetValueOrDefault(x.ProviderId))
            .Where(x => x != null)
            .Cast<AgentModelProviderEntity>()
            .DistinctBy(x => x.Id)
            .ToList();
        if (selectedProviders.Count == 0) selectedProviders = providers;

        return selectedProviders
            .Select(x => ToRuntime(x, normalizedAgentCode, ownerUserId))
            .Where(x => x != null)
            .Cast<RuntimeAgentOptions>()
            .ToList();
    }

    /// <summary>读取系统级单个平台以执行管理员发起的连通性测试。</summary>
    public async Task<RuntimeAgentOptions?> ResolveProviderAsync(
        int providerId,
        string ownerUserId,
        string agentCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ownerUserId = RequireOwnerUserId(ownerUserId);
        await _initializer.EnsureSystemProviderPresetsAsync(cancellationToken);

        var provider = await Db.Queryable<AgentModelProviderEntity>()
            .FirstAsync(x => x.Id == providerId &&
                             x.OwnerUserId == AgentModelConfigurationScope.SystemOwnerUserId &&
                             x.IsDelete == 0);
        return provider == null
            ? null
            : ToRuntime(provider, AgentToolRegistryService.NormalizeAgentCode(agentCode), ownerUserId);
    }

    private RuntimeAgentOptions? ToRuntime(
        AgentModelProviderEntity entity,
        string agentCode,
        string invocationUserId)
    {
        if (string.IsNullOrWhiteSpace(entity.ApiKeyCipherText)) return null;

        try
        {
            return new RuntimeAgentOptions
            {
                ProviderId = entity.Id,
                OwnerUserId = invocationUserId,
                AgentCode = agentCode,
                Provider = entity.ProviderCode,
                BaseUrl = entity.BaseUrl,
                EndpointPath = entity.EndpointPath,
                ApiKey = _secretProtector.Unprotect(entity.ApiKeyCipherText),
                ApiKeyHeader = entity.ApiKeyHeader,
                Model = entity.Model,
                Temperature = entity.Temperature,
                MaxTokens = entity.MaxTokens,
                TimeoutSeconds = entity.ClientTimeoutSeconds,
                PlatformTimeoutSeconds = entity.PlatformTimeoutSeconds,
                MaxRetries = entity.MaxRetries,
                FallbacksJson = entity.FallbacksJson,
                IsDatabaseConfiguration = true
            };
        }
        catch
        {
            // 密钥保护密钥发生变更或密文损坏时，该平台不可调用；不把密钥相关细节暴露给用户。
            return null;
        }
    }

    private static string RequireOwnerUserId(string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new InvalidOperationException("无法识别当前登录用户，不能调用 Agent。");

        return ownerUserId.Trim();
    }
}
