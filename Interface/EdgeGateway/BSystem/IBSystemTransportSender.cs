using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// B 系统非 HTTP 出站协议发送器。新增协议时实现此接口即可接入队列转发流程。
/// </summary>
public interface IBSystemTransportSender
{
    /// <summary>发送器支持的协议编码，例如 MQTT 或 WebSocket。</summary>
    string Protocol { get; }

    /// <summary>按映射配置发送转换后的 JSON，并返回可审计的发送结果。</summary>
    Task<EdgeBTransportResult> SendAsync(
        EdgeBMappingRuleEntity mapping,
        string payload,
        CancellationToken cancellationToken = default);
}
