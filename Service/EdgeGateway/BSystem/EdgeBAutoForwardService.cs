using System.Text.Json;
using AutoMapper;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// 中心端 B 系统自动转发服务，设置和运行状态全部保存在 SQL Server。
/// </summary>
public class EdgeBAutoForwardService : IEdgeBAutoForwardService
{
    private const int SettingId = 1;
    private readonly SqlSugarScope _dbScope;
    private readonly IEdgeGatewayService _edgeGatewayService;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>注入 SqlSugar、转发业务和 AutoMapper，表结构由启动初始化服务统一维护。</summary>
    public EdgeBAutoForwardService(
        SqlSugarScope dbScope,
        IEdgeGatewayService edgeGatewayService,
        IMapper mapper)
    {
        _dbScope = dbScope;
        _edgeGatewayService = edgeGatewayService;
        _mapper = mapper;
    }

    /// <summary>读取设置；首次运行时创建默认关闭的单例记录。</summary>
    public async Task<EdgeBAutoForwardSettingOutputDto> GetSettingAsync()
    {
        var entity = await GetOrCreateSettingAsync();
        return _mapper.Map<EdgeBAutoForwardSettingOutputDto>(entity);
    }

    /// <summary>校验并保存自动转发参数，最近运行状态保持不变。</summary>
    public async Task<bool> SaveSettingAsync(EdgeBAutoForwardSettingSaveDto dto)
    {
        Validate(dto);
        var entity = await GetOrCreateSettingAsync();
        _mapper.Map(dto, entity);
        var updateTime = DateTime.Now;
        await Db.Updateable<EdgeBAutoForwardSettingEntity>()
            .SetColumns(x => new EdgeBAutoForwardSettingEntity
            {
                Enabled = entity.Enabled,
                ScanIntervalSeconds = entity.ScanIntervalSeconds,
                BatchSize = entity.BatchSize,
                UpdateTime = updateTime
            })
            .Where(x => x.Id == SettingId)
            .ExecuteCommandAsync();
        await AddOperationAsync("BAutoForwardSetting.Update", entity.Id.ToString(), dto);
        return true;
    }

    /// <summary>启用时处理一批到期消息，并更新最近执行时间、数量和错误摘要。</summary>
    public async Task<EdgeBAutoForwardSettingOutputDto> RunOnceAsync(
        CancellationToken cancellationToken)
    {
        var setting = await GetOrCreateSettingAsync();
        if (!setting.Enabled) return _mapper.Map<EdgeBAutoForwardSettingOutputDto>(setting);

        try
        {
            var result = await _edgeGatewayService.ForwardDueQueueAsync(
                setting.BatchSize,
                cancellationToken);
            var lastRunTime = DateTime.Now;
            var lastError = result.FailedCount > 0
                ? $"本轮有 {result.FailedCount} 条消息转发失败，请查看消息队列"
                : null;
            var lastSuccessTime = result.SuccessCount > 0
                ? lastRunTime
                : setting.LastSuccessTime;
            await Db.Updateable<EdgeBAutoForwardSettingEntity>()
                .SetColumns(x => new EdgeBAutoForwardSettingEntity
                {
                    LastRunTime = lastRunTime,
                    LastSuccessTime = lastSuccessTime,
                    LastError = lastError,
                    LastRequestedCount = result.RequestedCount,
                    LastSuccessCount = result.SuccessCount,
                    LastFailedCount = result.FailedCount
                })
                .Where(x => x.Id == SettingId)
                .ExecuteCommandAsync();
            return await GetSettingAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var lastRunTime = DateTime.Now;
            var lastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await Db.Updateable<EdgeBAutoForwardSettingEntity>()
                .SetColumns(x => new EdgeBAutoForwardSettingEntity
                {
                    LastRunTime = lastRunTime,
                    LastError = lastError
                })
                .Where(x => x.Id == SettingId)
                .ExecuteCommandAsync();
            throw;
        }
    }

    /// <summary>获取主键为 1 的设置记录，避免按环境配置文件控制运行状态。</summary>
    private async Task<EdgeBAutoForwardSettingEntity> GetOrCreateSettingAsync()
    {
        var setting = await Db.Queryable<EdgeBAutoForwardSettingEntity>()
            .FirstAsync(x => x.Id == SettingId);
        if (setting != null) return setting;

        setting = new EdgeBAutoForwardSettingEntity
        {
            Id = SettingId,
            Enabled = false,
            ScanIntervalSeconds = 5,
            BatchSize = 100,
            UpdateTime = DateTime.Now
        };
        try
        {
            await Db.Insertable(setting).ExecuteCommandAsync();
            return setting;
        }
        catch (SqlSugarException) when (Db.Queryable<EdgeBAutoForwardSettingEntity>()
                   .Any(x => x.Id == SettingId))
        {
            return await Db.Queryable<EdgeBAutoForwardSettingEntity>()
                .FirstAsync(x => x.Id == SettingId);
        }
    }

    /// <summary>限制后台扫描频率和批量，避免错误配置占满中心端资源。</summary>
    private static void Validate(EdgeBAutoForwardSettingSaveDto dto)
    {
        if (dto.ScanIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("自动转发扫描间隔必须在 1 到 300 秒之间");
        if (dto.BatchSize is < 1 or > 500)
            throw new InvalidOperationException("自动转发单批数量必须在 1 到 500 条之间");
    }

    /// <summary>记录自动转发设置变更。</summary>
    private Task AddOperationAsync(string operationType, string target, object detail) =>
        Db.Insertable(new EdgeOperationLogEntity
        {
            OperationType = operationType,
            Target = target,
            DetailJson = JsonSerializer.Serialize(detail),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
}
