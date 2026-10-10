namespace Model.EdgeGateway;

/// <summary>
/// 边缘节点连接中心前必须具备的启动引导参数，对应 appsettings.json 中的 EdgeGateway 节点。
/// 设备启停状态不属于引导参数，由中心数据库 EdgeGatewayDevice.Enabled 动态下发。
/// </summary>
public class EdgeGatewayBootstrapOptions
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string CenterBaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    /// <summary>
    /// 本地日数据库文件的基础路径。配置 data/edge-gateway.db 时，运行时实际使用
    /// data/edge-gateway-yyyyMMdd.db；旧同名单文件仅在首次升级时用于迁移待上传数据。
    /// </summary>
    public string DatabasePath { get; set; } = "data/edge-gateway.db";
    public int UploadBatchSize { get; set; } = 50;
    public int UploadIntervalSeconds { get; set; } = 3;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int RetryBaseSeconds { get; set; } = 5;
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// 单个 MQTT 或 WebSocket 数据源的运行配置。
/// </summary>
public class SourceOptions
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? Topic { get; set; }
    public string? ClientId { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? HeadersJson { get; set; }
    public string? SubscriptionPayload { get; set; }
    public List<WebSocketDeviceSubscriptionOptions> SubscriptionDevices { get; set; } = new();
    public string DataType { get; set; } = "Telemetry";
    public int ReconnectIntervalSeconds { get; set; } = 5;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 中心端连接运行版本。版本变化会让数据源管理器重建该连接。
    /// </summary>
    public int RuntimeVersion { get; set; }

    /// <summary>
    /// 订阅设备或参数的版本。仅该版本变化时，运行中的 WebSocket 直接重新发送订阅消息。
    /// </summary>
    public int SubscriptionVersion { get; set; }
}

/// <summary>Worker 运行时使用的单个业务设备 WebSocket 订阅。</summary>
public class WebSocketDeviceSubscriptionOptions
{
    public string DeviceCode { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
}

/// <summary>
/// 原始 JSON 字段到标准消息字段的映射和数值转换配置。
/// </summary>
public class FieldMappingOptions
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceField { get; set; } = string.Empty;
    public string TargetField { get; set; } = string.Empty;
    public decimal Factor { get; set; } = 1;
    public decimal Offset { get; set; }
    public Dictionary<string, string> EnumMap { get; set; } = new();
}

/// <summary>
/// 边缘节点内存中执行的规则配置。
/// </summary>
public class EdgeRuleOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Filter";
    public string Field { get; set; } = string.Empty;
    public string Operator { get; set; } = "Equal";
    public decimal? CompareValue { get; set; }
    public string? CompareText { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Threshold { get; set; }
    public int WindowSeconds { get; set; } = 30;
    public string Action { get; set; } = "Drop";
    public bool Enabled { get; set; } = true;
}
