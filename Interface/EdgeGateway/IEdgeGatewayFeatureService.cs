using Model.EdgeGateway;

namespace Interface;

/// <summary>边缘计算中心全局功能开关服务，负责数据库设置和运行链路校验。</summary>
public interface IEdgeGatewayFeatureService
{
    /// <summary>读取全局功能设置；首次使用时创建默认开启的单例记录。</summary>
    Task<EdgeGatewayFeatureSettingOutputDto> GetSettingAsync();

    /// <summary>保存全局启停状态并返回最新设置。</summary>
    Task<EdgeGatewayFeatureSettingOutputDto> SaveSettingAsync(EdgeGatewayFeatureSettingSaveDto dto);

    /// <summary>快速判断边缘计算中心当前是否启用。</summary>
    Task<bool> IsEnabledAsync();

    /// <summary>功能关闭时抛出统一业务异常，供数据接收和转发入口调用。</summary>
    Task EnsureEnabledAsync();
}
