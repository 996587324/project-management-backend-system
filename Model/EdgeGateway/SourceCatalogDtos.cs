namespace Model.EdgeGateway;

/// <summary>仅用于选择的元数据，不携带设备报文、连接地址或认证信息。</summary>
public sealed class SourceCatalogQuery
{
    public string Kind { get; set; } = "Realtime";
    public string Purpose { get; set; } = "dashboard";
    public string? ModelMark { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 50;
    public int? GatewayId { get; set; }
    public string? ConnectionKey { get; set; }
    public string? SourceKey { get; set; }
    public string? DeviceKey { get; set; }
    public string? SourceDeviceCode { get; set; }
    public long? ProcessLineId { get; set; }
}

public sealed class SourceCatalogDevice
{
    public string Kind { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = string.Empty;
    public string SourceDeviceCode { get; set; } = string.Empty;
    public string ModelMark { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int GatewayId { get; set; }
    public int ConnectionId { get; set; }
    public string GatewayName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public DateTime? ReceiveTime { get; set; }
}

public sealed record SourceCatalogModel(string ModelMark, string ModelName, int DeviceCount, DateTime? LatestReceiveTime = null);
public sealed record SourceCatalogPage<T>(List<T> Records, int Total, int Page, int Size);
