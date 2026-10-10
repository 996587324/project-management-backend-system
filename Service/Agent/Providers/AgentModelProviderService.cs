using System.Text.Json;
using System.Text.RegularExpressions;
using AutoMapper;
using Interface;
using Model.Agent;
using SqlSugar;

namespace Service;

/// <summary>管理系统统一的模型平台、加密密钥以及 Agent 与平台的优先级绑定。</summary>
public sealed class AgentModelProviderService : IAgentModelProviderService
{
    private static readonly Regex ProviderCodeRegex = new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.Compiled);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IAgentSecretProtector _secretProtector;
    private readonly IAgentModelDatabaseInitializer _initializer;
    private readonly IAgentToolRegistryService _toolRegistryService;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public AgentModelProviderService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IAgentSecretProtector secretProtector,
        IAgentModelDatabaseInitializer initializer,
        IAgentToolRegistryService toolRegistryService)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _secretProtector = secretProtector;
        _initializer = initializer;
        _toolRegistryService = toolRegistryService;
    }

    /// <summary>分页查询模型平台，返回掩码而不返回任何密钥文本。</summary>
    public async Task<AgentModelProviderPageDto> GetPageAsync(string ownerUserId, AgentModelProviderQueryDto query)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var current = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 100);
        var keyword = query.Keyword?.Trim();
        RefAsync<int> total = 0;
        var entities = await Db.Queryable<AgentModelProviderEntity>()
            .Where(x => x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            .WhereIF(query.Enabled.HasValue, x => x.Enabled == query.Enabled!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), x =>
                x.ProviderCode.Contains(keyword!) ||
                x.ProviderName.Contains(keyword!) ||
                x.Model.Contains(keyword!))
            .OrderByDescending(x => x.Enabled)
            .OrderBy(x => x.ProviderName)
            .ToPageListAsync(current, size, total);

        return new AgentModelProviderPageDto
        {
            Records = entities.Select(ToDto).ToList(),
            Current = current,
            Size = size,
            Total = total
        };
    }

    /// <summary>查询绑定下拉框使用的平台选项。</summary>
    public async Task<List<AgentModelProviderDto>> GetOptionsAsync(string ownerUserId)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var entities = await Db.Queryable<AgentModelProviderEntity>()
            .Where(x => x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            .OrderByDescending(x => x.Enabled)
            .OrderBy(x => x.ProviderName)
            .ToListAsync();
        return entities.Select(ToDto).ToList();
    }

    /// <summary>按主键读取单个平台。</summary>
    public async Task<AgentModelProviderDto?> GetAsync(string ownerUserId, int id)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var entity = await Db.Queryable<AgentModelProviderEntity>()
            .FirstAsync(x => x.Id == id && x.OwnerUserId == ownerUserId && x.IsDelete == 0);
        return entity == null ? null : ToDto(entity);
    }

    /// <summary>新增或更新平台；未填写 ApiKey 时保留数据库中原有密钥。</summary>
    public async Task<AgentModelProviderDto> SaveAsync(string ownerUserId, AgentModelProviderSaveDto dto)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        NormalizeAndValidate(dto);

        var duplicate = await Db.Queryable<AgentModelProviderEntity>().AnyAsync(x =>
            x.OwnerUserId == ownerUserId && x.ProviderCode == dto.ProviderCode && x.IsDelete == 0 && x.Id != (dto.Id ?? 0));
        if (duplicate) throw new InvalidOperationException($"平台编码 {dto.ProviderCode} 已存在");

        AgentModelProviderEntity entity;
        if (dto.Id.HasValue && dto.Id.Value > 0)
        {
            entity = await Db.Queryable<AgentModelProviderEntity>()
                .FirstAsync(x => x.Id == dto.Id.Value && x.OwnerUserId == ownerUserId && x.IsDelete == 0)
                ?? throw new InvalidOperationException("模型平台不存在");
            _mapper.Map(dto, entity);
            entity.UpdateTime = DateTime.Now;
        }
        else
        {
            entity = _mapper.Map<AgentModelProviderEntity>(dto);
            entity.OwnerUserId = ownerUserId;
            entity.CreateTime = DateTime.Now;
            entity.UpdateTime = DateTime.Now;
        }

        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
        {
            var apiKey = dto.ApiKey.Trim();
            entity.ApiKeyCipherText = _secretProtector.Protect(apiKey);
            entity.ApiKeyMask = BuildApiKeyMask(apiKey);
        }

        if (entity.Enabled && string.IsNullOrWhiteSpace(entity.ApiKeyCipherText))
            throw new InvalidOperationException("启用模型平台前必须配置 API Key");

        if (entity.Id == 0)
        {
            entity.Id = await Db.Insertable(entity).ExecuteReturnIdentityAsync();
        }
        else
        {
            await Db.Updateable(entity).ExecuteCommandAsync();
        }

        return ToDto(entity);
    }

    /// <summary>单独切换平台启停，启用前必须已保存 API Key。</summary>
    public async Task<AgentModelProviderDto> SetEnabledAsync(string ownerUserId, int id, bool enabled)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var entity = await Db.Queryable<AgentModelProviderEntity>()
            .FirstAsync(x => x.Id == id && x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            ?? throw new InvalidOperationException("模型平台不存在");
        if (enabled && string.IsNullOrWhiteSpace(entity.ApiKeyCipherText))
            throw new InvalidOperationException("启用模型平台前请先编辑并填写 API Key");

        entity.Enabled = enabled;
        entity.UpdateTime = DateTime.Now;
        await Db.Updateable(entity).ExecuteCommandAsync();
        return ToDto(entity);
    }

    /// <summary>软删除平台并同步停用它的全部 Agent 绑定。</summary>
    public async Task<bool> DeleteAsync(string ownerUserId, int id)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var now = DateTime.Now;
        var affected = 0;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            affected = await Db.Updateable<AgentModelProviderEntity>()
                .SetColumns(x => new AgentModelProviderEntity
                {
                    IsDelete = 1,
                    Enabled = false,
                    UpdateTime = now
                })
                .Where(x => x.Id == id && x.OwnerUserId == ownerUserId && x.IsDelete == 0)
                .ExecuteCommandAsync();

            await Db.Updateable<AgentModelBindingEntity>()
                .SetColumns(x => new AgentModelBindingEntity
                {
                    IsDelete = 1,
                    Enabled = false,
                    UpdateTime = now
                })
                .Where(x => x.ProviderId == id && x.OwnerUserId == ownerUserId && x.IsDelete == 0)
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException!;
        return affected > 0;
    }

    /// <summary>查询所有已注册 Agent 的平台绑定；未绑定时仍返回 Agent 供前端配置。</summary>
    public async Task<List<AgentModelBindingDto>> GetBindingsAsync(string ownerUserId)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        var agents = await _toolRegistryService.GetAllAgentsAsync();
        var bindings = await Db.Queryable<AgentModelBindingEntity>()
            .Where(x => x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            .OrderByDescending(x => x.IsPrimary)
            .OrderBy(x => x.Priority)
            .ToListAsync();
        var providers = await Db.Queryable<AgentModelProviderEntity>()
            .Where(x => x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            .ToListAsync();
        var providerMap = providers.ToDictionary(x => x.Id);
        var result = new List<AgentModelBindingDto>();

        foreach (var agent in agents)
        {
            var agentBindings = bindings.Where(x =>
                x.AgentCode.Equals(agent.AgentCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (agentBindings.Count == 0)
            {
                result.Add(new AgentModelBindingDto
                {
                    AgentCode = agent.AgentCode,
                    AgentName = agent.Name,
                    Priority = 100
                });
                continue;
            }

            foreach (var binding in agentBindings)
            {
                var dto = _mapper.Map<AgentModelBindingDto>(binding);
                dto.AgentName = agent.Name;
                if (providerMap.TryGetValue(binding.ProviderId, out var provider))
                    dto.ProviderName = provider.ProviderName;
                result.Add(dto);
            }
        }
        return result;
    }

    /// <summary>新增或更新 Agent 绑定；设置主平台时自动取消该 Agent 的其他主平台标记。</summary>
    public async Task<AgentModelBindingDto> SaveBindingAsync(string ownerUserId, AgentModelBindingSaveDto dto)
    {
        ownerUserId = await GetSystemOwnerUserIdAsync();
        dto.AgentCode = AgentToolRegistryService.NormalizeAgentCode(dto.AgentCode);
        dto.Priority = Math.Clamp(dto.Priority, 1, 9999);

        var agent = await _toolRegistryService.GetAgentAsync(dto.AgentCode)
                    ?? throw new InvalidOperationException("Agent 不存在或已停用");
        var provider = await Db.Queryable<AgentModelProviderEntity>()
            .FirstAsync(x => x.Id == dto.ProviderId && x.OwnerUserId == ownerUserId && x.IsDelete == 0)
            ?? throw new InvalidOperationException("模型平台不存在");

        AgentModelBindingEntity? saved = null;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            if (dto.IsPrimary)
            {
                await Db.Updateable<AgentModelBindingEntity>()
                    .SetColumns(x => new AgentModelBindingEntity { IsPrimary = false, UpdateTime = DateTime.Now })
                    .Where(x => x.OwnerUserId == ownerUserId && x.AgentCode == dto.AgentCode && x.IsDelete == 0)
                    .ExecuteCommandAsync();
            }

            saved = await Db.Queryable<AgentModelBindingEntity>().FirstAsync(x =>
                x.OwnerUserId == ownerUserId && x.AgentCode == dto.AgentCode && x.ProviderId == dto.ProviderId && x.IsDelete == 0);
            if (saved == null)
            {
                saved = _mapper.Map<AgentModelBindingEntity>(dto);
                saved.OwnerUserId = ownerUserId;
                saved.CreateTime = DateTime.Now;
                saved.UpdateTime = DateTime.Now;
                saved.Id = await Db.Insertable(saved).ExecuteReturnIdentityAsync();
            }
            else
            {
                var createTime = saved.CreateTime;
                _mapper.Map(dto, saved);
                saved.CreateTime = createTime;
                saved.UpdateTime = DateTime.Now;
                await Db.Updateable(saved).ExecuteCommandAsync();
            }
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException!;

        var result = _mapper.Map<AgentModelBindingDto>(saved!);
        result.AgentName = agent.Name;
        result.ProviderName = provider.ProviderName;
        return result;
    }

    private AgentModelProviderDto ToDto(AgentModelProviderEntity entity)
    {
        var dto = _mapper.Map<AgentModelProviderDto>(entity);
        dto.HasApiKey = !string.IsNullOrWhiteSpace(entity.ApiKeyCipherText);
        return dto;
    }

    /// <summary>统一格式化和校验平台配置，避免错误地址或参数进入运行链路。</summary>
    private static void NormalizeAndValidate(AgentModelProviderSaveDto dto)
    {
        dto.ProviderCode = dto.ProviderCode.Trim().ToLowerInvariant();
        dto.ProviderName = dto.ProviderName.Trim();
        dto.ProtocolType = dto.ProtocolType.Trim();
        dto.BaseUrl = dto.BaseUrl.Trim().TrimEnd('/');
        dto.EndpointPath = "/" + dto.EndpointPath.Trim().Trim('/');
        dto.ApiKeyHeader = dto.ApiKeyHeader.Trim();
        dto.Model = dto.Model.Trim();
        dto.FallbacksJson = string.IsNullOrWhiteSpace(dto.FallbacksJson) ? null : dto.FallbacksJson.Trim();
        dto.Remark = string.IsNullOrWhiteSpace(dto.Remark) ? null : dto.Remark.Trim();

        if (!ProviderCodeRegex.IsMatch(dto.ProviderCode))
            throw new InvalidOperationException("平台编码只能包含小写字母、数字、下划线和连字符");
        if (!AgentModelConfigurationScope.IsSupportedProvider(dto.ProviderCode))
            throw new InvalidOperationException("当前仅支持选择 Turing 或 DeepSeek");
        if (string.IsNullOrWhiteSpace(dto.ProviderName)) throw new InvalidOperationException("平台名称不能为空");
        if (!dto.ProtocolType.Equals("OpenAIChatCompletions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前版本仅支持 OpenAI Chat Completions 协议");
        if (!Uri.TryCreate(dto.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("BaseUrl 必须是有效的 http 或 https 地址");
        if (string.IsNullOrWhiteSpace(dto.EndpointPath) || dto.EndpointPath == "/")
            throw new InvalidOperationException("接口路径不能为空");
        if (string.IsNullOrWhiteSpace(dto.ApiKeyHeader)) throw new InvalidOperationException("密钥请求头不能为空");
        if (string.IsNullOrWhiteSpace(dto.Model)) throw new InvalidOperationException("模型编码不能为空");
        if (dto.Temperature is < 0 or > 2) throw new InvalidOperationException("Temperature 必须在 0 到 2 之间");
        if (dto.MaxTokens is < 1 or > 32768) throw new InvalidOperationException("MaxTokens 必须在 1 到 32768 之间");
        if (dto.PlatformTimeoutSeconds is < 1 or > 600) throw new InvalidOperationException("平台超时必须在 1 到 600 秒之间");
        if (dto.ClientTimeoutSeconds <= dto.PlatformTimeoutSeconds)
            throw new InvalidOperationException("客户端超时必须大于平台超时");
        if (dto.ClientTimeoutSeconds > 900) throw new InvalidOperationException("客户端超时不能超过 900 秒");
        if (dto.MaxRetries is < 0 or > 5) throw new InvalidOperationException("最大重试次数必须在 0 到 5 之间");

        if (!string.IsNullOrWhiteSpace(dto.FallbacksJson))
        {
            try
            {
                using var document = JsonDocument.Parse(dto.FallbacksJson);
                if (document.RootElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
                    throw new InvalidOperationException("Fallback 必须是 JSON 字符串、对象或数组");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Fallback JSON 格式错误：{ex.Message}");
            }
        }
    }

    private static string BuildApiKeyMask(string apiKey)
    {
        var suffixLength = Math.Min(4, apiKey.Length);
        return $"****{apiKey[^suffixLength..]}";
    }

    private async Task<string> GetSystemOwnerUserIdAsync()
    {
        await _initializer.EnsureSystemProviderPresetsAsync();
        return AgentModelConfigurationScope.SystemOwnerUserId;
    }
}
