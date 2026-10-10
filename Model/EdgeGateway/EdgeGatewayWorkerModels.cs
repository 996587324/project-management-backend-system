using System.Text.Json.Nodes;

namespace Model.EdgeGateway;

/// <summary>
/// 协议适配器收到的一条原始数据源报文。
/// </summary>
public record SourceEnvelope(
    string SourceId,
    string DataType,
    string RawPayload,
    DateTime ReceivedAt,
    string? SourceDeviceCode = null);

/// <summary>
/// 完成字段映射和边缘规则处理后的统一标准消息。
/// </summary>
public class StandardMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string? SourceDeviceCode { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DataType { get; set; } = "Telemetry";
    public Dictionary<string, JsonNode?> Values { get; set; } = new();
    public string? RawPayload { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
}

/// <summary>
/// 单条原始报文的处理结果；Message 为空表示该报文被拒绝或规则丢弃。
/// </summary>
public record ProcessResult(StandardMessage? Message, string? DropReason)
{
    /// <summary>创建已接收的处理结果。</summary>
    public static ProcessResult Accepted(StandardMessage message) => new(message, null);
    /// <summary>创建已丢弃的处理结果并保留原因。</summary>
    public static ProcessResult Dropped(string reason) => new(null, reason);
}

/// <summary>
/// 从 SQLite 队列读取出的待上传消息。
/// </summary>
public class QueueMessage
{
    public int Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTime CreateTime { get; set; }
}

/// <summary>边缘节点本地队列状态统计。</summary>
public record QueueStatistics(long Pending, long Failed, long Uploaded);

/// <summary>中心端通过心跳返回的数据库启停状态和当前最高版本号。</summary>
public record CenterVersions(bool Enabled, int ConfigVersion, int RuleVersion, int SourceVersion);

/// <summary>中心端下发给边缘节点的完整生效配置。</summary>
public record EffectiveEdgeConfiguration(
    bool Enabled,
    int ConfigVersion,
    string ConfigJson,
    int RuleVersion,
    string RulesJson,
    int SourceVersion,
    IReadOnlyCollection<SourceOptions> Sources);
