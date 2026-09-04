using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 边缘节点访问中心端 EdgeGateway API 的客户端。
/// </summary>
public interface ICenterApiClient
{
    /// <summary>
    /// 将本地队列中的标准消息批量上传到中心端。
    /// </summary>
    Task<(bool Success, int StatusCode, string? Error)> UploadAsync(
        IReadOnlyCollection<QueueMessage> messages, CancellationToken cancellationToken);

    /// <summary>
    /// 上报设备心跳，并取得中心端当前的连接、采集配置和规则版本。
    /// </summary>
    Task<CenterVersions> SendHeartbeatAsync(object heartbeat, CancellationToken cancellationToken);

    /// <summary>
    /// 获取当前设备在中心端已生效的连接、字段映射和规则快照。
    /// </summary>
    Task<EffectiveEdgeConfiguration> GetEffectiveConfigurationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 将边缘节点运行事件上报到中心端。
    /// </summary>
    Task ReportEventAsync(string level, string eventType, string message, object? detail,
        CancellationToken cancellationToken);

    /// <summary>上报一次向上游 WebSocket 发送订阅或请求报文的结果。</summary>
    Task ReportWebSocketSendRecordAsync(EdgeWebSocketSendRecordReportDto record,
        CancellationToken cancellationToken);
}
