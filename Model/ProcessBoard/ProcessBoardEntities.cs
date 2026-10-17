using SqlSugar;

namespace Model.ProcessBoard;

[SugarTable("ProcessBoardConfig")]
[SugarIndex("UX_ProcessBoardConfig_KindCode", nameof(Kind), OrderByType.Asc, nameof(Code), OrderByType.Asc, true)]
[SugarIndex("IX_ProcessBoardConfig_Parent", nameof(ParentId), OrderByType.Asc, false)]
public sealed class ProcessBoardConfigEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 16)]
    public string Kind { get; set; } = "Region";
    public long ParentId { get; set; }
    [SugarColumn(Length = 64)]
    public string Code { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string Name { get; set; } = string.Empty;
    public int Sort { get; set; }
    public bool Enabled { get; set; }
    public int Version { get; set; } = 1;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

[SugarTable("ProcessBoardValue")]
[SugarIndex("UX_ProcessBoardValue_Field", nameof(CardId), OrderByType.Asc, nameof(FieldCode), OrderByType.Asc, true)]
public sealed class ProcessBoardValueEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RuntimeJson { get; set; } = "{}";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string BaselineJson { get; set; } = "null";
    [SugarColumn(Length = 16)]
    public string LimitState { get; set; } = "Unknown";
    public DateTime UpdateTime { get; set; }
}

[SugarTable("ProcessBoardSample")]
[SugarIndex("IX_ProcessBoardSample_FieldTime", nameof(CardId), OrderByType.Asc,
    nameof(FieldCode), OrderByType.Asc, nameof(CreateTime), OrderByType.Asc,
    nameof(ValueNumber), OrderByType.Asc, nameof(RawNumber), OrderByType.Asc, false)]
[SugarIndex("IX_ProcessBoardSample_Cleanup", nameof(CreateTime), OrderByType.Asc, false)]
public sealed class ProcessBoardSampleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "float", IsNullable = true)]
    public double? RawNumber { get; set; }
    [SugarColumn(ColumnDataType = "float", IsNullable = true)]
    public double? ValueNumber { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RuntimeJson { get; set; } = "{}";
    public DateTime CreateTime { get; set; }
}

[SugarTable("ProcessBoardEvent")]
[SugarIndex("IX_ProcessBoardEvent_FieldTime", nameof(CardId), OrderByType.Asc,
    nameof(FieldCode), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
[SugarIndex("IX_ProcessBoardEvent_Cleanup", nameof(CreateTime), OrderByType.Asc, false)]
public sealed class ProcessBoardEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string EventType { get; set; } = string.Empty;
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PreviousJson { get; set; } = "null";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RuntimeJson { get; set; } = "{}";
    public DateTime CreateTime { get; set; }
}

[SugarTable("ProcessBoardConfigAudit")]
public sealed class ProcessBoardConfigAuditEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long ConfigId { get; set; }
    public int Version { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreateTime { get; set; }
}

[SugarTable("ProcessBoardSetting")]
public sealed class ProcessBoardSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(Length = 24, IsNullable = true)]
    public string? FormulaUseType { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? FormulaSignature { get; set; }
    [SugarColumn(DefaultValue = "0")]
    public bool FormulaTermsAccepted { get; set; }
    public int SampleIntervalSeconds { get; set; } = 5;
    public int RetentionHours { get; set; } = 120;
    public DateTime UpdateTime { get; set; }
}

[SugarTable("ProcessBoardExport")]
[SugarIndex("IX_ProcessBoardExport_Queue", nameof(Status), OrderByType.Asc, nameof(CreateTime), OrderByType.Asc, false)]
public sealed class ProcessBoardExportEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 8)]
    public string AppCode { get; set; } = string.Empty;
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool Events { get; set; }
    public long LastRecordId { get; set; }
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    public long RowCount { get; set; }
    [SugarColumn(Length = 64)]
    public string FileKey { get; set; } = string.Empty;
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? Error { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? StartedTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? CompletedTime { get; set; }
    public DateTime CreateTime { get; set; }
}
