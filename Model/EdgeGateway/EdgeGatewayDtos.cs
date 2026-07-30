namespace Model.EdgeGateway;

/// <summary>EdgeGateway 通用分页查询参数。</summary>
public class EdgePageQueryDto
{
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>EdgeGateway 通用分页返回结构。</summary>
public class EdgePagedResultDto<T>
{
    public List<T> Records { get; set; } = new();
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
}

/// <summary>边缘设备分页筛选条件。</summary>
public class EdgeDeviceQueryDto : EdgePageQueryDto
{
    public string? Keyword { get; set; }
    public bool? Enabled { get; set; }
    public string? OnlineStatus { get; set; }
}

/// <summary>新增或修改边缘设备的请求模型。</summary>
public class EdgeDeviceSaveDto
{
    public int Id { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string? SiteName { get; set; }
    /// <summary>管理端人工配置的设备 IP，不会被心跳上报值覆盖。</summary>
    public string? ConfiguredIpAddress { get; set; }
    public string? DeviceType { get; set; }
    public string? GroupName { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>边缘设备列表输出，包含最新心跳和运行状态。</summary>
public class EdgeDeviceOutputDto : EdgeDeviceSaveDto
{
    public bool Online { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public string? IpAddress { get; set; }
    public string? AppVersion { get; set; }
    public decimal CpuUsage { get; set; }
    public decimal MemoryUsage { get; set; }
    public long QueueDepth { get; set; }
    public long CollectedCount { get; set; }
    public long UploadedCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>运行中心首页的设备、队列、告警和上传统计。</summary>
public class EdgeOverviewDto
{
    public int DeviceCount { get; set; }
    public int OnlineCount { get; set; }
    public int OfflineCount { get; set; }
    public int DisabledCount { get; set; }
    public int QueueDepth { get; set; }
    public int FailedCount { get; set; }
    public int ActiveAlarmCount { get; set; }
    /// <summary>累计成功转发到 B 系统的记录数量。</summary>
    public int SuccessfulForwardCount { get; set; }
    public decimal UploadSuccessRate { get; set; }
    public List<EdgeSystemEventEntity> RecentEvents { get; set; } = new();
}

/// <summary>边缘节点定时上报的心跳内容。</summary>
public class EdgeHeartbeatDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }
    public string? AppVersion { get; set; }
    public decimal CpuUsage { get; set; }
    public decimal MemoryUsage { get; set; }
    public long QueueDepth { get; set; }
    public long CollectedCount { get; set; }
    public long UploadedCount { get; set; }
    public string? LastError { get; set; }
}

/// <summary>中心端心跳响应，包含数据库启停状态、服务器时间和三个配置版本。</summary>
public class EdgeHeartbeatResultDto
{
    public bool Enabled { get; set; }
    public DateTime ServerTime { get; set; } = DateTime.Now;
    public int ConfigVersion { get; set; }
    public int RuleVersion { get; set; }
    public int SourceVersion { get; set; }
}

/// <summary>新增或修改单个 WebSocket 连接的请求模型。</summary>
public class EdgeWebSocketSourceSaveDto
{
    public int Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? SubscriptionPayload { get; set; }
    public string? HeadersJson { get; set; }
    public string DataType { get; set; } = "Telemetry";
    public int ReconnectIntervalSeconds { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

/// <summary>WebSocket 连接配置输出，附带设备、版本和时间信息。</summary>
public class EdgeWebSocketSourceOutputDto : EdgeWebSocketSourceSaveDto
{
    public int DeviceId { get; set; }
    public int Version { get; set; }
    public int SubscriptionVersion { get; set; }
    public List<EdgeWebSocketSubscriptionDeviceOutputDto> SubscriptionDevices { get; set; } = new();
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>WebSocket 订阅参数的新增或修改模型。</summary>
public class EdgeWebSocketSubscriptionParameterSaveDto
{
    public string ParameterName { get; set; } = string.Empty;
    public string? ParameterNameCn { get; set; }
    public string? DataType { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
}

/// <summary>WebSocket 订阅设备的新增或修改模型。</summary>
public class EdgeWebSocketSubscriptionDeviceSaveDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public List<EdgeWebSocketSubscriptionParameterSaveDto> Parameters { get; set; } = new();
}

/// <summary>保存一条 WebSocket 连接的完整设备和参数订阅清单。</summary>
public class EdgeWebSocketSubscriptionSaveDto
{
    public List<EdgeWebSocketSubscriptionDeviceSaveDto> Devices { get; set; } = new();
}

/// <summary>WebSocket 订阅参数的前端输出模型。</summary>
public class EdgeWebSocketSubscriptionParameterOutputDto : EdgeWebSocketSubscriptionParameterSaveDto
{
    public int Id { get; set; }
}

/// <summary>WebSocket 订阅设备及其参数的前端输出模型。</summary>
public class EdgeWebSocketSubscriptionDeviceOutputDto
{
    public int Id { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public List<EdgeWebSocketSubscriptionParameterOutputDto> Parameters { get; set; } = new();
}

/// <summary>批量重新连接指令实际影响的设备和连接数量。</summary>
public class EdgeWebSocketReconnectResultDto
{
    public int DeviceCount { get; set; }
    public int ConnectionCount { get; set; }
}

/// <summary>保存设备采集配置草稿的请求模型。</summary>
public class EdgeDeviceConfigSaveDto
{
    public string ConfigName { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public string? CreatedBy { get; set; }
}

/// <summary>下发给边缘节点的完整生效配置。</summary>
public class EdgeEffectiveConfigDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int SourceVersion { get; set; }
    public List<SourceOptions> Sources { get; set; } = new();
    public int ConfigVersion { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public int RuleVersion { get; set; }
    public string RulesJson { get; set; } = "[]";
}

/// <summary>边缘规则分页筛选条件。</summary>
public class EdgeRuleQueryDto : EdgePageQueryDto
{
    public int? DeviceId { get; set; }
    public string? RuleType { get; set; }
    public string? Keyword { get; set; }
}

/// <summary>新增或修改边缘规则的请求模型。</summary>
public class EdgeRuleSaveDto
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string RuleType { get; set; } = "Filter";
    public string RuleJson { get; set; } = "{}";
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}

/// <summary>发布规则版本的请求模型。</summary>
public class EdgeRulePublishDto
{
    public int? DeviceId { get; set; }
    public string? Remark { get; set; }
    public string? PublishedBy { get; set; }
}

/// <summary>回滚历史规则快照的请求模型。</summary>
public class EdgeRuleRollbackDto
{
    public int VersionId { get; set; }
    public string? Remark { get; set; }
    public string? PublishedBy { get; set; }
}

/// <summary>中心消息队列分页筛选条件。</summary>
public class EdgeQueueQueryDto : EdgePageQueryDto
{
    public string? DeviceCode { get; set; }
    public string? Status { get; set; }
    public string? MessageId { get; set; }
}

/// <summary>B 系统上传记录分页筛选条件。</summary>
public class EdgeUploadRecordQueryDto : EdgePageQueryDto
{
    public long? QueueId { get; set; }
    public string? DeviceCode { get; set; }
    public string? MessageId { get; set; }
    public bool? Success { get; set; }
}

/// <summary>边缘节点上传到中心端的标准消息 DTO。</summary>
public class EdgeStandardMessageDto
{
    public string MessageId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string? SourceDeviceCode { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string DataType { get; set; } = "Telemetry";
    public Dictionary<string, object?> Values { get; set; } = new();
    public string? RawPayload { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
}

/// <summary>边缘节点批量上传标准消息的请求模型。</summary>
public class EdgeMessageBatchDto
{
    public string DeviceId { get; set; } = string.Empty;
    public List<EdgeStandardMessageDto> Messages { get; set; } = new();
}

/// <summary>中心端接收批量消息后的去重统计。</summary>
public class EdgeMessageAcceptResultDto
{
    public int AcceptedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int InvalidCount { get; set; }
}

/// <summary>重试或转发指定中心队列记录的请求模型。</summary>
public class EdgeRetryRequestDto
{
    public List<long> Ids { get; set; } = new();
}

/// <summary>中心队列批量处理结果。</summary>
public class EdgeRetryResultDto
{
    public int RequestedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
}

/// <summary>边缘网关事件分页筛选条件。</summary>
public class EdgeEventQueryDto : EdgePageQueryDto
{
    public string? DeviceCode { get; set; }
    public string? Level { get; set; }
    public string? EventType { get; set; }
}

/// <summary>边缘节点上报运行事件的请求模型。</summary>
public class EdgeEventReportDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string Level { get; set; } = "Info";
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
}

/// <summary>新增或修改 B 系统映射配置的请求模型。</summary>
public class EdgeBMappingSaveDto
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public string MappingName { get; set; } = string.Empty;
    public int? PayloadTemplateId { get; set; }
    public string Protocol { get; set; } = "HTTP";
    public string TargetUrl { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "POST";
    public string? MqttTopic { get; set; }
    public string? MqttClientId { get; set; }
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }
    public int MqttQosLevel { get; set; } = 1;
    public bool MqttRetain { get; set; }
    public string MappingJson { get; set; } = "{}";
    public string? HeadersJson { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>B 系统映射的管理端输出模型，不返回 MQTT 明文密码。</summary>
public class EdgeBMappingOutputDto
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public string MappingName { get; set; } = string.Empty;
    public int? PayloadTemplateId { get; set; }
    public string? PayloadTemplateName { get; set; }
    public string Protocol { get; set; } = "HTTP";
    public string TargetUrl { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "POST";
    public string? MqttTopic { get; set; }
    public string? MqttClientId { get; set; }
    public string? MqttUsername { get; set; }
    public bool HasMqttPassword { get; set; }
    public int MqttQosLevel { get; set; } = 1;
    public bool MqttRetain { get; set; }
    public string MappingJson { get; set; } = "{}";
    public string? HeadersJson { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>新增或修改 B 系统 payload 模板的请求模型。</summary>
public class EdgeBPayloadTemplateSaveDto
{
    public int Id { get; set; }
    public string TemplateCode { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string TemplateJson { get; set; } = "{}";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>B 系统 payload 模板的管理端输出模型。</summary>
public class EdgeBPayloadTemplateOutputDto
{
    public int Id { get; set; }
    public string TemplateCode { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string TemplateJson { get; set; } = "{}";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>保存中心端 B 系统自动转发设置的请求模型。</summary>
public class EdgeBAutoForwardSettingSaveDto
{
    public bool Enabled { get; set; }
    public int ScanIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
}

/// <summary>中心端 B 系统自动转发设置及最近执行结果。</summary>
public class EdgeBAutoForwardSettingOutputDto
{
    public bool Enabled { get; set; }
    public int ScanIntervalSeconds { get; set; }
    public int BatchSize { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public string? LastError { get; set; }
    public int LastRequestedCount { get; set; }
    public int LastSuccessCount { get; set; }
    public int LastFailedCount { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>通用启用状态请求模型。</summary>
public class EdgeEnabledDto
{
    public bool Enabled { get; set; }
}
