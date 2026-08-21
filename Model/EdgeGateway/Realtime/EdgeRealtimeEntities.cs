using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>
/// 中心 SQL Server 中每台业务设备唯一的实时数据快照；重复上报只覆盖当前行，不保存历史。
/// </summary>
[SugarTable("EdgeGatewayRealtimeDeviceSnapshot")]
public class EdgeRealtimeDeviceSnapshotEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 128)]
    public string DeviceCode { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ModelMark { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? DataType { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? TenantMark { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MessageId { get; set; }

    /// <summary>对方上报信封中的 Unix 毫秒时间戳。</summary>
    [SugarColumn(IsNullable = true)]
    public long? SourceTimestamp { get; set; }

    /// <summary>动态 data 的兼容列表 JSON，字段名称和数据类型不做固定限制。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DataListJson { get; set; } = "[]";

    /// <summary>对方数据产生时间；dto.time 未传时使用中心接收时间。</summary>
    public DateTime DataTime { get; set; } = DateTime.Now;

    /// <summary>中心端最后一次接受有效新快照的时间。</summary>
    public DateTime ReceiveTime { get; set; } = DateTime.Now;

    /// <summary>兼容字段，值取自对方信封中的 messageId。</summary>
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? LastRequestId { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
