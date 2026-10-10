using AutoMapper;
using Interface;
using Microsoft.Extensions.Logging;
using Model.Agent;
using SqlSugar;

namespace Service;

/// <summary>模型调用观测日志服务；数据库日志异常只写应用日志，不中断 Agent 回复。</summary>
public sealed class AgentModelInvocationLogService : IAgentModelInvocationLogService
{
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IAgentModelDatabaseInitializer _initializer;
    private readonly ILogger<AgentModelInvocationLogService> _logger;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public AgentModelInvocationLogService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IAgentModelDatabaseInitializer initializer,
        ILogger<AgentModelInvocationLogService> logger)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _initializer = initializer;
        _logger = logger;
    }

    public async Task RecordAsync(AgentModelInvocationLogWriteDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            await _initializer.EnsureInitializedAsync(cancellationToken);
            var entity = _mapper.Map<AgentModelInvocationLogEntity>(dto);
            entity.CreateTime = DateTime.Now;
            await Db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记录 Agent 模型调用日志失败。AgentCode: {AgentCode}, Provider: {Provider}",
                dto.AgentCode, dto.ProviderCode);
        }
    }
}
