using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 中心端 WebSocket 连接配置服务。
/// </summary>
public interface IEdgeWebSocketSourceService
{
    /// <summary>查询指定设备下的全部未删除 WebSocket 配置。</summary>
    Task<List<EdgeWebSocketSourceOutputDto>> GetByDeviceAsync(int deviceId);
    /// <summary>新增或修改 WebSocket 配置，并递增设备连接版本供配置同步循环识别。</summary>
    Task<int> SaveAsync(int deviceId, EdgeWebSocketSourceSaveDto dto);
    /// <summary>查询指定 WebSocket 连接的订阅设备和参数。</summary>
    Task<List<EdgeWebSocketSubscriptionDeviceOutputDto>> GetSubscriptionsAsync(int sourceId);
    /// <summary>校验并保存完整订阅清单，仅递增订阅版本。</summary>
    Task<int> SaveSubscriptionsAsync(int sourceId, EdgeWebSocketSubscriptionSaveDto dto);
    /// <summary>启用或停用 WebSocket 配置，并通知边缘节点更新。</summary>
    Task<bool> SetEnabledAsync(int id, bool enabled);
    /// <summary>软删除 WebSocket 配置，并通知边缘节点停止对应连接。</summary>
    Task<bool> DeleteAsync(int id);
    /// <summary>提升指定设备全部已启用连接的运行版本，使 Worker 重新建立连接。</summary>
    Task<EdgeWebSocketReconnectResultDto> ReconnectDeviceAsync(int deviceId);
    /// <summary>提升所有设备已启用连接的运行版本，使在线 Worker 重新建立连接。</summary>
    Task<EdgeWebSocketReconnectResultDto> ReconnectAllAsync();
    /// <summary>取得指定设备当前最高连接版本，供配置同步循环比较使用。</summary>
    Task<int> GetVersionAsync(int deviceId);
    /// <summary>把中心实体映射为可直接下发给 Worker 的数据源配置。</summary>
    Task<List<SourceOptions>> GetEffectiveSourcesAsync(int deviceId);
}
