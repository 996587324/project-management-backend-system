using SqlSugar;

namespace Model.EdgeGateway;

[SugarTable("EdgeGatewayDataTraceSetting")]
public class EdgeDataTraceSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    public int RetentionHours { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdateBy { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeGatewayDataTraceRule")]
public class EdgeDataTraceRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 128)]
    public string RuleName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string SourceType { get; set; } = "WebSocket";
    public int EdgeDeviceId { get; set; }
    [SugarColumn(Length = 64)]
    public string EdgeDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string SourceDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SourceDeviceName { get; set; }
    public bool Enabled { get; set; } = true;
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Remark { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreateBy { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdateBy { get; set; }
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeGatewayDataTraceField")]
public class EdgeDataTraceFieldEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int TraceRuleId { get; set; }
    [SugarColumn(Length = 128)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string ValueType { get; set; } = "Number";
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Unit { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? StateMappingJson { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
}

/// <summary>独立日志库中的高频参数历史点。历史数据按保留时长直接物理清理。</summary>
[SugarTable("EdgeGatewayDataTracePoint")]
[SugarIndex("IX_EdgeGatewayDataTracePoint_Query", nameof(EdgeDeviceCode), OrderByType.Asc,
    nameof(SourceId), OrderByType.Asc, nameof(SourceDeviceCode), OrderByType.Asc,
    nameof(FieldCode), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
[SugarIndex("IX_EdgeGatewayDataTracePoint_Cleanup", nameof(CreateTime), OrderByType.Asc, false)]
[SugarIndex("IX_EdgeGatewayDataTracePoint_FieldTime", nameof(TraceFieldId), OrderByType.Asc,
    nameof(CreateTime), OrderByType.Desc, nameof(Id), OrderByType.Desc, false)]
[SugarIndex("UX_EdgeGatewayDataTracePoint_MessageField", nameof(MessageId), OrderByType.Asc,
    nameof(TraceFieldId), OrderByType.Asc, true)]
public class EdgeDataTracePointEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public int TraceFieldId { get; set; }
    [SugarColumn(Length = 64)]
    public string EdgeDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string SourceDeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string ValueType { get; set; } = "Number";
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Unit { get; set; }
    [SugarColumn(DecimalDigits = 10, Length = 28, IsNullable = true)]
    public decimal? NumericValue { get; set; }
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? TextValue { get; set; }
    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    public DateTime MessageTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeGatewayDataTraceLatest")]
public class EdgeDataTraceLatestEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int TraceFieldId { get; set; }
    [SugarColumn(DecimalDigits = 10, Length = 28, IsNullable = true)]
    public decimal? NumericValue { get; set; }
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? TextValue { get; set; }
    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    public DateTime MessageTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeGatewayDataTraceExportTask")]
public class EdgeDataTraceExportTaskEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RequestJson { get; set; } = "{}";
    [SugarColumn(Length = 32)]
    public string Status { get; set; } = "Pending";
    public long ProcessedCount { get; set; }
    public long? TotalCount { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? FileName { get; set; }
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? FilePath { get; set; }
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? ErrorMessage { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CreatedBy { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? CompleteTime { get; set; }
    public DateTime? ExpireTime { get; set; }
}
