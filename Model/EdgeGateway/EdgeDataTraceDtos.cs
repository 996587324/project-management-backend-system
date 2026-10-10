namespace Model.EdgeGateway;

public class EdgeDataTraceSettingDto
{
    public int RetentionHours { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public string? UpdateBy { get; set; }
    public DateTime UpdateTime { get; set; }
}

public class EdgeDataTraceSettingSaveDto
{
    public int RetentionHours { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

public class EdgeDataTraceFieldSaveDto
{
    public int Id { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "Number";
    public string? Unit { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public string? StateMappingJson { get; set; }
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
}

public class EdgeDataTraceRuleSaveDto
{
    public int Id { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string SourceType { get; set; } = "WebSocket";
    public int EdgeDeviceId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceDeviceCode { get; set; } = string.Empty;
    public string? SourceDeviceName { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Remark { get; set; }
    public List<EdgeDataTraceFieldSaveDto> Fields { get; set; } = new();
}

public class EdgeDataTraceRuleDto : EdgeDataTraceRuleSaveDto
{
    public string EdgeDeviceCode { get; set; } = string.Empty;
    public string? EdgeDeviceName { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

public class EdgeDataTraceTreeNodeDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public int? TraceFieldId { get; set; }
    public string? EdgeDeviceCode { get; set; }
    public string? SourceId { get; set; }
    public string? SourceDeviceCode { get; set; }
    public string? FieldCode { get; set; }
    public string? Unit { get; set; }
    public string? ValueType { get; set; }
    public string? LatestValue { get; set; }
    public DateTime? LastReceiveTime { get; set; }
    public List<EdgeDataTraceTreeNodeDto> Children { get; set; } = new();
}

public class EdgeDataTraceTrendQueryDto
{
    public List<int> TraceFieldIds { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int MaxPointsPerSeries { get; set; } = 1000;
}

public class EdgeDataTraceTrendSeriesDto
{
    public int TraceFieldId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public string ValueType { get; set; } = "Number";
    public List<EdgeDataTraceTrendPointDto> Points { get; set; } = new();
}

public class EdgeDataTraceTrendPointDto
{
    public DateTime Time { get; set; }
    public decimal? NumericValue { get; set; }
    public string? TextValue { get; set; }
}

public class EdgeDataTraceDetailQueryDto
{
    public List<int> TraceFieldIds { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime? CursorTime { get; set; }
    public long? CursorId { get; set; }
    public int Size { get; set; } = 100;
}

public class EdgeDataTracePointDto
{
    public long Id { get; set; }
    public int TraceFieldId { get; set; }
    public string EdgeDeviceCode { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceDeviceCode { get; set; } = string.Empty;
    public string FieldCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public decimal? NumericValue { get; set; }
    public string? TextValue { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public DateTime MessageTime { get; set; }
    public DateTime CreateTime { get; set; }
}

public class EdgeDataTraceDetailPageDto
{
    public List<EdgeDataTracePointDto> Records { get; set; } = new();
    public bool HasMore { get; set; }
    public DateTime? NextCursorTime { get; set; }
    public long? NextCursorId { get; set; }
}

public class EdgeDataTraceSummaryDto
{
    public int TraceFieldId { get; set; }
    public decimal? LatestValue { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public decimal? AverageValue { get; set; }
    public long PointCount { get; set; }
    public DateTime? LastReceiveTime { get; set; }
}

public class EdgeDataTraceExportRequestDto
{
    public List<int> TraceFieldIds { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

public class EdgeDataTraceExportTaskDto
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public long ProcessedCount { get; set; }
    public long? TotalCount { get; set; }
    public string? FileName { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? CompleteTime { get; set; }
    public DateTime? ExpireTime { get; set; }
}

public class EdgeDataTraceExportFileDto
{
    public string FileName { get; set; } = string.Empty;
    public Stream Content { get; set; } = Stream.Null;
}
