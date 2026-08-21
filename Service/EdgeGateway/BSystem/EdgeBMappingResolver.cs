using Interface;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// B 系统映射选路实现，通过订阅子设备名称关联目标连接，并兼容节点和全局默认映射。
/// </summary>
public sealed class EdgeBMappingResolver : IEdgeBMappingResolver
{
    /// <inheritdoc />
    public EdgeBMappingRuleEntity? Resolve(
        IReadOnlyCollection<EdgeBMappingRuleEntity> mappings,
        IReadOnlyCollection<EdgeWebSocketSourceEntity> sources,
        IReadOnlyCollection<EdgeWebSocketSubscriptionDeviceEntity> subscriptionDevices,
        int? deviceId,
        string sourceId,
        string? sourceDeviceCode)
    {
        var enabledMappings = mappings
            .Where(x => x.IsDelete == 0 && x.Enabled)
            .OrderByDescending(x => x.UpdateTime ?? x.CreateTime)
            .ThenByDescending(x => x.Id)
            .ToList();

        if (deviceId.HasValue &&
            !string.IsNullOrWhiteSpace(sourceId) &&
            !string.IsNullOrWhiteSpace(sourceDeviceCode))
        {
            var originSource = sources
                .Where(x => x.IsDelete == 0 &&
                            x.DeviceId == deviceId.Value &&
                            TextEquals(x.SourceId, sourceId))
                .OrderByDescending(x => x.UpdateTime ?? x.CreateTime)
                .ThenByDescending(x => x.Id)
                .FirstOrDefault();

            if (originSource != null)
            {
                // 子设备必须同时匹配所属连接和设备编码，避免同名设备跨连接串用配置。
                var subscriptionDevice = subscriptionDevices
                    .Where(x => x.IsDelete == 0 &&
                                x.WebSocketSourceId == originSource.Id &&
                                TextEquals(x.DeviceCode, sourceDeviceCode))
                    .OrderByDescending(x => x.UpdateTime ?? x.CreateTime)
                    .ThenByDescending(x => x.Id)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(subscriptionDevice?.DeviceName))
                {
                    // DeviceName 对应业务上传配置名称，在同一节点内精确关联目标数据源。
                    var targetSource = sources
                        .Where(x => x.IsDelete == 0 &&
                                    x.DeviceId == deviceId.Value &&
                                    TextEquals(x.SourceName, subscriptionDevice.DeviceName))
                        .OrderByDescending(x => x.UpdateTime ?? x.CreateTime)
                        .ThenByDescending(x => x.Id)
                        .FirstOrDefault();

                    if (targetSource != null)
                    {
                        // B 映射通过目标数据源标识保存上传地址、协议、请求头和报文模板等信息。
                        var routedMapping = enabledMappings.Find(x =>
                            x.DeviceId == deviceId &&
                            TextEquals(x.SourceId, targetSource.SourceId));
                        if (routedMapping != null) return routedMapping;
                    }
                }

            }
        }

        if (deviceId.HasValue)
        {
            // 没有子设备专属映射时，使用当前边缘节点的默认映射。
            var deviceDefault = enabledMappings.Find(x =>
                x.DeviceId == deviceId &&
                string.IsNullOrWhiteSpace(x.SourceId) &&
                string.IsNullOrWhiteSpace(x.SourceDeviceCode));
            if (deviceDefault != null) return deviceDefault;
        }

        // 子设备和节点默认映射均未命中时，最后回退到全局默认映射。
        return enabledMappings.Find(x =>
            x.DeviceId == null &&
            string.IsNullOrWhiteSpace(x.SourceId) &&
            string.IsNullOrWhiteSpace(x.SourceDeviceCode));
    }

    /// <summary>以不区分大小写的方式比较数据库配置和采集报文中的标识。</summary>
    private static bool TextEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
