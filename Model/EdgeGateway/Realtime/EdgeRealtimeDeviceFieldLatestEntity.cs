using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>设备字段最后已知值，不是历史记录；JSON null 与未上报严格区分。</summary>
[SugarTable("EdgeGatewayRealtimeDeviceFieldLatest")]
[SugarIndex("UX_EdgeRealtimeField_DevicePath", nameof(TenantMark), OrderByType.Asc,
    nameof(DeviceCode), OrderByType.Asc, nameof(SourcePathHash), OrderByType.Asc, true)]
public class EdgeRealtimeDeviceFieldLatestEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ModelMark { get; set; }

    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>保留路径大小写，避免 SQL Server 默认排序规则混淆同名 JSON 字段。</summary>
    [SugarColumn(Length = 64)]
    public string SourcePathHash { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ValueJson { get; set; } = "null";

    [SugarColumn(Length = 32)]
    public string ValueType { get; set; } = "Null";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MessageId { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? SourceTimestamp { get; set; }

    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime DataTime { get; set; }
    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime ReceiveTime { get; set; }
    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime CreateTime { get; set; }
    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime UpdateTime { get; set; }
}
