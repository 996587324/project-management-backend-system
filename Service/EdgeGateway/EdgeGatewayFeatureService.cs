using System.Text.Json;
using AutoMapper;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>使用项目 SQL Server 和 SqlSugar 保存边缘计算中心全局功能开关。</summary>
public sealed class EdgeGatewayFeatureService : IEdgeGatewayFeatureService
{
    private const int SettingId = 1;
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>通过 IOC 注入项目单例 SqlSugarScope 和 AutoMapper。</summary>
    public EdgeGatewayFeatureService(SqlSugarScope dbScope, IMapper mapper)
    {
        _dbScope = dbScope;
        _mapper = mapper;
    }

    /// <summary>读取单例设置；旧项目没有记录时默认开启，保持升级前运行行为。</summary>
    public async Task<EdgeGatewayFeatureSettingOutputDto> GetSettingAsync() =>
        _mapper.Map<EdgeGatewayFeatureSettingOutputDto>(await GetOrCreateSettingAsync());

    /// <summary>持久化全局开关和操作人，并写入 EdgeGateway 操作日志。</summary>
    public async Task<EdgeGatewayFeatureSettingOutputDto> SaveSettingAsync(
        EdgeGatewayFeatureSettingSaveDto dto)
    {
        var entity = await GetOrCreateSettingAsync();
        _mapper.Map(dto, entity);
        entity.UpdateTime = DateTime.Now;
        await Db.Updateable(entity)
            .UpdateColumns(x => new { x.Enabled, x.UpdatedBy, x.UpdateTime })
            .ExecuteCommandAsync();
        await Db.Insertable(new EdgeOperationLogEntity
        {
            OperationType = "FeatureSetting.Update",
            Target = "EdgeGateway",
            DetailJson = JsonSerializer.Serialize(new { entity.Enabled, entity.UpdatedBy }),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
        return _mapper.Map<EdgeGatewayFeatureSettingOutputDto>(entity);
    }

    /// <summary>读取数据库开关，后台循环每次检查时都能获得最新值。</summary>
    public async Task<bool> IsEnabledAsync() => (await GetOrCreateSettingAsync()).Enabled;

    /// <summary>阻止关闭状态下继续接收新数据或向 B 系统转发。</summary>
    public async Task EnsureEnabledAsync()
    {
        if (!await IsEnabledAsync())
            throw new InvalidOperationException("边缘计算中心功能已关闭");
    }

    /// <summary>并发安全地创建主键为 1 的默认设置记录。</summary>
    private async Task<EdgeGatewayFeatureSettingEntity> GetOrCreateSettingAsync()
    {
        var setting = await Db.Queryable<EdgeGatewayFeatureSettingEntity>()
            .FirstAsync(x => x.Id == SettingId);
        if (setting != null) return setting;

        await CreateGate.WaitAsync();
        try
        {
            setting = await Db.Queryable<EdgeGatewayFeatureSettingEntity>()
                .FirstAsync(x => x.Id == SettingId);
            if (setting != null) return setting;

            setting = new EdgeGatewayFeatureSettingEntity
            {
                Id = SettingId,
                Enabled = true,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
            await Db.Insertable(setting).ExecuteCommandAsync();
            return setting;
        }
        finally
        {
            CreateGate.Release();
        }
    }
}
