using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 根据 WebSocket 订阅子设备和连接名称关系解析唯一的 B 系统映射。
/// </summary>
public interface IEdgeBMappingResolver
{
    /// <summary>
    /// 先按“节点 + 数据源 + 订阅设备”精确匹配；未命中时兼容历史名称跳转，
    /// 最后回退到节点默认或全局默认映射。
    /// </summary>
    EdgeBMappingRuleEntity? Resolve(
        IReadOnlyCollection<EdgeBMappingRuleEntity> mappings,
        IReadOnlyCollection<EdgeWebSocketSourceEntity> sources,
        IReadOnlyCollection<EdgeWebSocketSubscriptionDeviceEntity> subscriptionDevices,
        int? deviceId,
        string sourceId,
        string? sourceDeviceCode);
}
