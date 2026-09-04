using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>中心 SQL Server 中的边缘设备主数据。</summary>
[SugarTable("EdgeGatewayDevice")]
public class EdgeDeviceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceName { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SiteName { get; set; }
    /// <summary>管理端人工维护的设备 IP；运行时心跳上报 IP 单独保存在状态表。</summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ConfiguredIpAddress { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? DeviceType { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? GroupName { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    [SugarColumn(IsNullable = true)]
    public DateTime? UpdateTime { get; set; }
}

/// <summary>中心 SQL Server 中的设备最新心跳状态。</summary>
[SugarTable("EdgeGatewayDeviceStatus")]
public class EdgeDeviceStatusEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public DateTime LastHeartbeat { get; set; } = DateTime.Now;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? IpAddress { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? AppVersion { get; set; }
    public decimal CpuUsage { get; set; }
    public decimal MemoryUsage { get; set; }
    public long QueueDepth { get; set; }
    public long CollectedCount { get; set; }
    public long UploadedCount { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? LastError { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中按设备维护的 WebSocket 连接配置。</summary>
[SugarTable("EdgeGatewayWebSocketSource")]
public class EdgeWebSocketSourceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int DeviceId { get; set; }
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string SourceName { get; set; } = string.Empty;
    [SugarColumn(Length = 1024)]
    public string Endpoint { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? SubscriptionPayload { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? HeadersJson { get; set; }
    [SugarColumn(Length = 64)]
    public string DataType { get; set; } = "Telemetry";
    public int ReconnectIntervalSeconds { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }
    /// <summary>订阅设备或参数最后一次修改的版本；只变化时无需重建 WebSocket。</summary>
    public int SubscriptionVersion { get; set; } = 1;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>边缘节点向上游 WebSocket 发送订阅或请求报文的中心端审计记录。</summary>
[SugarTable("EdgeGatewayWebSocketSendRecord")]
public class EdgeWebSocketSendRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceName { get; set; }
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? Endpoint { get; set; }
    /// <summary>InitialSubscription 或 SubscriptionUpdate。</summary>
    [SugarColumn(Length = 32)]
    public string SendType { get; set; } = "InitialSubscription";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string Payload { get; set; } = string.Empty;
    public bool Success { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? ErrorMessage { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// WebSocket 接收数据日表实体。SqlSugar 以 CreateTime 为分表字段，自动写入
/// EdgeGatewayWebSocketReceiveRecord_yyyyMMdd 日表；保留策略由日志清理服务执行。
/// </summary>
[SugarTable("EdgeGatewayWebSocketReceiveRecord_{year}{month}{day}")]
[SplitTable(SplitType.Day)]
[SugarIndex(
    "IX_EdgeGatewayWebSocketReceiveRecord_Filter",
    nameof(DeviceCode), OrderByType.Asc,
    nameof(SourceId), OrderByType.Asc,
    nameof(SourceDeviceCode), OrderByType.Asc,
    nameof(CreateTime), OrderByType.Desc,
    false)]
public class EdgeWebSocketReceiveRecordEntity
{
    /// <summary>日表主键。分表不支持数据库自增列，写入时由 SqlSugar 生成雪花 ID。</summary>
    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceDeviceCode { get; set; }
    [SugarColumn(Length = 64)]
    public string DataType { get; set; } = "Telemetry";
    /// <summary>经过 WebSocket 拆包后的原始业务数据报文。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RawPayload { get; set; } = "{}";
    /// <summary>上游报文中携带或边缘节点生成的业务时间。</summary>
    public DateTime MessageTime { get; set; }
    /// <summary>中心端实际接收并入库的时间，也是日表分表依据。</summary>
    [SplitField]
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中某个 WebSocket 连接订阅的业务设备。</summary>
[SugarTable("EdgeGatewayWebSocketSubscriptionDevice")]
public class EdgeWebSocketSubscriptionDeviceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int WebSocketSourceId { get; set; }
    [SugarColumn(Length = 128)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? DeviceName { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中某个订阅设备需要实时推送的参数。</summary>
[SugarTable("EdgeGatewayWebSocketSubscriptionParameter")]
public class EdgeWebSocketSubscriptionParameterEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int SubscriptionDeviceId { get; set; }
    [SugarColumn(Length = 128)]
    public string ParameterName { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ParameterNameCn { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? DataType { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中的设备采集配置草稿及发布状态。</summary>
[SugarTable("EdgeGatewayDeviceConfig")]
public class EdgeDeviceConfigEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int Version { get; set; }
    [SugarColumn(Length = 128)]
    public string ConfigName { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ConfigJson { get; set; } = "{}";
    public bool IsPublished { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreatedBy { get; set; }
    [SugarColumn(IsNullable = true)]
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? PublishTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中不可变的采集配置发布快照。</summary>
[SugarTable("EdgeGatewayConfigVersion")]
public class EdgeConfigVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int ConfigId { get; set; }
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string SnapshotJson { get; set; } = "{}";
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? PublishedBy { get; set; }
    public DateTime PublishTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中可编辑的边缘规则定义。</summary>
[SugarTable("EdgeGatewayRule")]
public class EdgeRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    [SugarColumn(Length = 64)]
    public string RuleCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RuleName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string RuleType { get; set; } = "Filter";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RuleJson { get; set; } = "{}";
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中的规则发布或回滚版本快照。</summary>
[SugarTable("EdgeGatewayRuleVersion")]
public class EdgeRuleVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string SnapshotJson { get; set; } = "[]";
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Remark { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? PublishedBy { get; set; }
    public DateTime PublishTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中等待转发到 B 系统的标准消息。</summary>
[SugarTable("EdgeGatewayMessageQueue")]
public class EdgeMessageQueueEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceDeviceCode { get; set; }
    [SugarColumn(Length = 64)]
    public string DataType { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string StandardPayloadJson { get; set; } = "{}";
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; } = DateTime.Now;
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? LastError { get; set; }
    public DateTime MessageTime { get; set; } = DateTime.Now;
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UploadTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中每次 B 系统转发的结果记录。</summary>
[SugarTable("EdgeGatewayUploadRecord")]
public class EdgeUploadRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long QueueId { get; set; }
    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    /// <summary>产生本次投递消息的数据源标识。</summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SourceId { get; set; }
    /// <summary>WebSocket 返回报文外层的订阅设备代码。</summary>
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceDeviceCode { get; set; }
    /// <summary>本次投递实际命中的 B 系统映射主键。</summary>
    [SugarColumn(IsNullable = true)]
    public int? MappingRuleId { get; set; }
    /// <summary>本次投递实际命中的 B 系统映射名称快照。</summary>
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MappingName { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? TargetUrl { get; set; }
    public bool Success { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? StatusCode { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? ErrorMessage { get; set; }
    /// <summary>B 系统返回的 HTTP 正文或 MQTT 发布确认摘要。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ResponseContent { get; set; }
    /// <summary>返回内容是否因超过中心保存上限而被截断。</summary>
    [SugarColumn(IsNullable = true)]
    public bool? ResponseContentTruncated { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中保存的边缘网关运行事件。</summary>
[SugarTable("EdgeGatewaySystemEvent")]
public class EdgeSystemEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Level { get; set; } = "Info";
    [SugarColumn(Length = 64)]
    public string EventType { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string Message { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? DetailJson { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中根据心跳错误产生的告警记录。</summary>
[SugarTable("EdgeGatewayAlarmRecord")]
public class EdgeAlarmRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string AlarmType { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Level { get; set; } = "Warning";
    [SugarColumn(Length = 512)]
    public string Message { get; set; } = string.Empty;
    public bool Handled { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? HandledTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中的 EdgeGateway 管理操作审计日志。</summary>
[SugarTable("EdgeGatewayOperationLog")]
public class EdgeOperationLogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string OperationType { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? Operator { get; set; }
    [SugarColumn(Length = 128)]
    public string Target { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? DetailJson { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中标准消息到 B 系统请求的映射配置。</summary>
[SugarTable("EdgeGatewayBMappingRule")]
public class EdgeBMappingRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    /// <summary>精确订阅设备映射所属的数据源；为空表示节点或全局默认映射。</summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SourceId { get; set; }
    /// <summary>精确订阅设备映射使用的 deviceCode；为空表示节点或全局默认映射。</summary>
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceDeviceCode { get; set; }
    [SugarColumn(Length = 128)]
    public string MappingName { get; set; } = string.Empty;
    public int? PayloadTemplateId { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? PayloadTemplateName { get; set; }
    [SugarColumn(Length = 16)]
    public string Protocol { get; set; } = "HTTP";
    [SugarColumn(Length = 512)]
    public string TargetUrl { get; set; } = string.Empty;
    [SugarColumn(Length = 16)]
    public string HttpMethod { get; set; } = "POST";
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? MqttTopic { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MqttClientId { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MqttUsername { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? MqttPassword { get; set; }
    public int MqttQosLevel { get; set; } = 1;
    public bool MqttRetain { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string MappingJson { get; set; } = "{}";
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? HeadersJson { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中可复用的 B 系统 JSON payload 模板。</summary>
[SugarTable("EdgeGatewayBPayloadTemplate")]
public class EdgeBPayloadTemplateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 64)]
    public string TemplateCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string TemplateName { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string TemplateJson { get; set; } = "{}";
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中 B 系统自动转发的单例运行设置和最近执行状态。</summary>
[SugarTable("EdgeGatewayBAutoForwardSetting")]
public class EdgeBAutoForwardSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public int ScanIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
    [SugarColumn(IsNullable = true)]
    public DateTime? LastRunTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LastSuccessTime { get; set; }
    [SugarColumn(Length = 500, IsNullable = true)]
    public string? LastError { get; set; }
    public int LastRequestedCount { get; set; }
    public int LastSuccessCount { get; set; }
    public int LastFailedCount { get; set; }
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>中心 SQL Server 中边缘计算中心全局启停单例设置。</summary>
[SugarTable("EdgeGatewayFeatureSetting")]
public sealed class EdgeGatewayFeatureSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdatedBy { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}
