using Model.EdgeGateway;

namespace Interface;

/// <summary>B 系统中心端自动转发服务，负责数据库设置和单轮到期消息处理。</summary>
public interface IEdgeBAutoForwardService
{
    /// <summary>读取数据库中的自动转发设置和最近执行状态。</summary>
    Task<EdgeBAutoForwardSettingOutputDto> GetSettingAsync();

    /// <summary>保存自动转发启停、扫描间隔和单批数量。</summary>
    Task<bool> SaveSettingAsync(EdgeBAutoForwardSettingSaveDto dto);

    /// <summary>根据当前数据库设置执行一轮到期消息转发。</summary>
    Task<EdgeBAutoForwardSettingOutputDto> RunOnceAsync(CancellationToken cancellationToken);
}
