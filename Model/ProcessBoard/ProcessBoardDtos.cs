namespace Model.ProcessBoard;

public sealed class ProcessBoardDefinition
{
    public long Id { get; set; }
    public string Kind { get; set; } = "Region";
    public long ParentId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }
    public string Layout { get; set; } = "grid";
    public int Columns { get; set; } = 3;
    // 卡片内部每行的参数组数；Columns 控制外层卡片/线体数量。
    public int FieldColumns { get; set; } = 1;
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 240;
    public string PanelLayout { get; set; } = "tabs";
    public int FlowHeight { get; set; } = 270;
    public int TrendHeight { get; set; } = 400;
    public int FlowWidth { get; set; }
    public int TrendWidth { get; set; }
    public List<ProcessSourceBinding> Devices { get; set; } = [];
    public List<ProcessFieldDefinition> Fields { get; set; } = [];
    public List<ProcessFlowNode> Flow { get; set; } = [];
    public List<ProcessFlowEdge> Edges { get; set; } = [];
    public List<ProcessBlockLayout> Blocks { get; set; } = [];
}

public sealed class ProcessSourceBinding
{
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public double? FixedValue { get; set; }
}

public sealed class ProcessFieldDefinition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public ProcessSourceBinding Source { get; set; } = new();
    public string? FixedText { get; set; }
    public string Formula { get; set; } = string.Empty;
    public bool RecalculateEverySample { get; set; }
    public Dictionary<string, ProcessSourceBinding> Variables { get; set; } = [];
    public List<string> Functions { get; set; } = [];
    public Dictionary<string, double> Constants { get; set; } = [];
    public int Precision { get; set; } = 2;
    public int ComparisonPrecision { get; set; } = 6;
    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public bool TraceEnabled { get; set; }
    public bool TrendEnabled { get; set; }
    public bool TraceRawValue { get; set; }
    public double ChangeThreshold { get; set; }
    public bool PushEnabled { get; set; }
    public bool PushOnChange { get; set; }
    public bool PushOnLimit { get; set; } = true;
    public bool PushOnRecovery { get; set; }
    public List<string> RecipientUserIds { get; set; } = [];
}

public sealed class ProcessFlowNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public long? CardId { get; set; }
}

public sealed class ProcessFlowEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class ProcessBlockLayout
{
    public long CardId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 240;
}

public sealed class ProcessFormulaRequest
{
    public string Formula { get; set; } = string.Empty;
    public Dictionary<string, double> Variables { get; set; } = [];
    public Dictionary<string, double> Constants { get; set; } = [];
    public List<string> Functions { get; set; } = [];
}

public sealed record ProcessFormulaResult(bool Success, double? Value, string? Error);

public sealed class ProcessFormulaOptions
{
    // Personal 只适用于真实的个人非商业用途，不等于免费企业生产授权。
    public string UseType { get; set; } = "Personal";
    public string Signature { get; set; } = string.Empty;
    public bool TermsAccepted { get; set; }
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class ProcessFormulaWorkerRequest
{
    public ProcessFormulaOptions License { get; set; } = new();
    public ProcessFormulaRequest Expression { get; set; } = new();
}

public sealed class ProcessFieldRuntime
{
    public string InputIdentity { get; set; } = string.Empty;
    public string FieldIdentity { get; set; } = string.Empty;
    public double? RawNumber { get; set; }
    public double? ValueNumber { get; set; }
    public string Code { get; set; } = string.Empty;
    public string RawJson { get; set; } = "null";
    public string ValueJson { get; set; } = "null";
    public string State { get; set; } = "Unknown";
    public string? Error { get; set; }
    public DateTime? DataTime { get; set; }
    public DateTime CalculatedAt { get; set; }
    public string SourceTimesJson { get; set; } = "{}";
}

public sealed class ProcessBoardSetting
{
    public string FormulaUseType { get; set; } = "Personal";
    public string FormulaSignature { get; set; } = string.Empty;
    public bool FormulaTermsAccepted { get; set; }
    public int SampleIntervalSeconds { get; set; } = 5;
    public int RetentionHours { get; set; } = 120;
    public int PushIntervalSeconds { get; set; } = 5;
}

public sealed class ProcessCurveBucket
{
    public long Bucket { get; set; }
    public double? Average { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public int Count { get; set; }
}

public sealed class ProcessExportRequest
{
    public long CardId { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool Events { get; set; }
}
