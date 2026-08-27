namespace Model.Agent;

/// <summary>提供给边缘 Agent 的 WebSocket 连接摘要，不包含请求头等敏感连接信息。</summary>
public sealed class EdgeAgentWebSocketSourceSummaryDto
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool DataOnline { get; set; }
    public string DataStatus { get; set; } = string.Empty;
    public DateTime? LastDataTime { get; set; }
    public int SubscriptionDeviceCount { get; set; }
    public int SubscriptionParameterCount { get; set; }
}

/// <summary>提供给边缘 Agent 的 B 系统映射摘要，不包含 Token、密码和请求头。</summary>
public sealed class EdgeAgentBMappingSummaryDto
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public string? SourceId { get; set; }
    public string? SourceDeviceCode { get; set; }
    public string MappingName { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string? MqttTopic { get; set; }
    public bool Enabled { get; set; }
}
