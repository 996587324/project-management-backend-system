namespace Model.Agent.ToolArguments;

/// <summary>无入参 Agent 工具使用的空参数模型。</summary>
public sealed class EmptyAgentToolArguments
{
}

/// <summary>边缘设备列表查询参数。</summary>
public sealed class EdgeDeviceListArguments
{
    public string? Keyword { get; set; }
    public bool? Enabled { get; set; }
    public string? OnlineStatus { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>按中心端边缘设备主键执行查询或重连。</summary>
public sealed class EdgeDeviceIdArguments
{
    public int DeviceId { get; set; }
}

/// <summary>WebSocket 连接状态查询参数，可按设备或数据接收状态过滤。</summary>
public sealed class EdgeConnectionListArguments
{
    public int? DeviceId { get; set; }
    public string? DeviceCode { get; set; }
    public bool? Enabled { get; set; }
    public bool? DataOnline { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>中心消息队列查询参数。</summary>
public sealed class EdgeQueueListArguments
{
    public string? DeviceCode { get; set; }
    public string? Status { get; set; }
    public string? MessageId { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>B 系统上传记录查询参数。</summary>
public sealed class EdgeUploadRecordListArguments
{
    public long? QueueId { get; set; }
    public string? DeviceCode { get; set; }
    public string? MessageId { get; set; }
    public bool? Success { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>边缘运行事件查询参数。</summary>
public sealed class EdgeEventListArguments
{
    public string? DeviceCode { get; set; }
    public string? Level { get; set; }
    public string? EventType { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>独立 HTTP 实时数据最新快照定位参数。</summary>
public sealed class EdgeRealtimeLatestArguments
{
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
}

/// <summary>按设备编码查询 Worker 生效配置。</summary>
public sealed class EdgeEffectiveConfigArguments
{
    public string DeviceCode { get; set; } = string.Empty;
}

/// <summary>批量重试或立即转发中心消息队列。</summary>
public sealed class EdgeQueueIdsArguments
{
    public List<long> Ids { get; set; } = new();
}
