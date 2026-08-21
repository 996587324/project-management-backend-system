using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 根据 WebSocket 订阅子设备和连接名称关系解析唯一的 B 系统映射。
/// </summary>
public interface IEdgeBMappingResolver
{
    /// <summary>
    /// 先通过订阅子设备名称解析目标连接，再按“子设备映射、节点默认、全局默认”选择映射。
    /// </summary>
    EdgeBMappingRuleEntity? Resolve(
        IReadOnlyCollection<EdgeBMappingRuleEntity> mappings,
        IReadOnlyCollection<EdgeWebSocketSourceEntity> sources,
        IReadOnlyCollection<EdgeWebSocketSubscriptionDeviceEntity> subscriptionDevices,
        int? deviceId,
        string sourceId,
        string? sourceDeviceCode);
}
