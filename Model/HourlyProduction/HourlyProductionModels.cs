using SqlSugar;

namespace Model.HourlyProduction;

/// <summary>配置整体以带版本的文档保存；事件、小时结果、报表分别持久化，不从快照倒推产量。</summary>
[SugarTable("HourlyProductionBoard")]
public sealed class HourlyProductionBoardEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Code { get; set; } = "";
    [SugarColumn(Length = 8)]
    public string AppCode { get; set; } = "A";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DefinitionJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public bool Published { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(64)")]
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdateTime { get; set; }
}

[SugarTable("HourlyProductionInbox")]
[SugarIndex("UX_HourlyInbox_Key", nameof(EventKey), OrderByType.Asc, true)]
[SugarIndex("IX_HourlyInbox_Pending", nameof(ProcessedAt), OrderByType.Asc, nameof(Id), OrderByType.Asc)]
[SugarIndex("IX_HourlyInbox_Retention", nameof(ReceivedAt), OrderByType.Asc)]
public sealed class HourlyProductionInboxEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string EventKey { get; set; } = "";
    [SugarColumn(Length = 16)]
    public string Kind { get; set; } = "Realtime";
    [SugarColumn(ColumnDataType = "nvarchar(128)")]
    public string SourceKey { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(128)")]
    public string DeviceKey { get; set; } = "";
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(128)")]
    public string? SourceDeviceCode { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ValuesJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ProcessedAt { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(1000)")]
    public string? Error { get; set; }
}

[SugarTable("HourlyProductionPass")]
[SugarIndex("UX_HourlyPass_Key", nameof(EventKey), OrderByType.Asc, true)]
[SugarIndex("IX_HourlyPass_Sn", nameof(BoardCode), OrderByType.Asc, nameof(ProcessKey), OrderByType.Asc,
    nameof(ProductionDate), OrderByType.Asc, nameof(Sn), OrderByType.Asc)]
public sealed class HourlyProductionPassEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string EventKey { get; set; } = "";
    [SugarColumn(Length = 64)]
    public string BoardCode { get; set; } = "";
    [SugarColumn(Length = 64)]
    public string ProcessKey { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(256)")]
    public string Sn { get; set; } = "";
    public DateTime ProductionDate { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime SlotStart { get; set; }
    [SugarColumn(ColumnDataType = "decimal(28,6)")]
    public decimal Quantity { get; set; }
    public bool ExplicitRework { get; set; }
    [SugarColumn(ColumnDataType = "decimal(28,6)")]
    public decimal Output { get; set; }
    [SugarColumn(ColumnDataType = "decimal(28,6)")]
    public decimal Rework { get; set; }
    public int RuleVersion { get; set; }
}

[SugarTable("HourlyProductionBucket")]
[SugarIndex("UX_HourlyBucket_Key", nameof(BoardCode), OrderByType.Asc, nameof(ProcessKey), OrderByType.Asc,
    nameof(SlotStart), OrderByType.Asc, true)]
public sealed class HourlyProductionBucketEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string BoardCode { get; set; } = "";
    [SugarColumn(Length = 64)]
    public string ProcessKey { get; set; } = "";
    public DateTime SlotStart { get; set; }
    [SugarColumn(ColumnDataType = "decimal(28,6)")]
    public decimal Output { get; set; }
    [SugarColumn(ColumnDataType = "decimal(28,6)")]
    public decimal Rework { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[SugarTable("HourlyProductionReport")]
[SugarIndex("UX_HourlyReport_Key", nameof(ReportKey), OrderByType.Asc, true)]
[SugarIndex("IX_HourlyReport_Board", nameof(BoardCode), OrderByType.Asc, nameof(Id), OrderByType.Desc)]
public sealed class HourlyProductionReportEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string ReportKey { get; set; } = "";
    [SugarColumn(Length = 64)]
    public string BoardCode { get; set; } = "";
    public DateTime SlotStart { get; set; }
    public int ConfigVersion { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string SnapshotJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

[SugarTable("HourlyProductionDispatch")]
[SugarIndex("UX_HourlyDispatch_Key", nameof(JobKey), OrderByType.Asc, true)]
[SugarIndex("IX_HourlyDispatch_Pending", nameof(Status), OrderByType.Asc, nameof(NextAttemptAt), OrderByType.Asc)]
public sealed class HourlyProductionDispatchEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string JobKey { get; set; } = "";
    public long ReportId { get; set; }
    [SugarColumn(Length = 64)]
    public string BoardCode { get; set; } = "";
    [SugarColumn(Length = 16)]
    public string Channel { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(64)")]
    public string RecipientId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(64)")]
    public string OwnerId { get; set; } = "";
    [SugarColumn(Length = 8)]
    public string AppCode { get; set; } = "A";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string Content { get; set; } = "";
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(1000)")]
    public string? Error { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(max)")]
    public string? QueueResultJson { get; set; }
}

[SugarTable("HourlyProductionWorkerState")]
public sealed class HourlyProductionWorkerStateEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    public long EdgeCursor { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? NextCleanupAt { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseUntil { get; set; }
    [SugarColumn(IsNullable = true, Length = 64)]
    public string? LeaseOwner { get; set; }
}

[SugarTable("HourlyProductionAdvice")]
public sealed class HourlyProductionAdviceEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string BoardCode { get; set; } = "";
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Content { get; set; }
    public DateTime AttemptedAt { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAt { get; set; }
}

public sealed class HourlyShiftPlan
{
    public DateTime EffectiveAt { get; set; }
    public string DayStart { get; set; } = "08:00";
    public string NightStart { get; set; } = "20:00";
    public Dictionary<string,List<decimal?>> DayTargets { get; set; } = new();
    public Dictionary<string,List<decimal?>> NightTargets { get; set; } = new();
}

public sealed class HourlyBoardDefinition
{
    public List<HourlyRemovedLine> RemovedLines { get; set; } = [];
    public HourlyBoardLayout? Layout { get; set; }
    public DateTime? CancelShiftEffectiveAt { get; set; }
    public List<HourlyShiftCancellation> CancelledShifts { get; set; } = [];
    public string Code { get; set; } = "";
    public string Name { get; set; } = "小时产量与目标看板";
    public int Version { get; set; }
    public bool Published { get; set; }
    public Dictionary<string,List<decimal?>>? LegacyTargets { get; set; }
    public List<HourlyShiftPlan> ShiftHistory { get; set; } = [];
    public HourlyShiftPlan? ShiftChange { get; set; }
    public string StartTime { get; set; } = "08:00";
    public string EndTime { get; set; } = "20:00";
    public int RefreshSeconds { get; set; } = 10;
    public bool AiEnabled { get; set; }
    public int AiIntervalMinutes { get; set; } = 5;
    public string AiAgentCode { get; set; } = "production-dashboard";
    public List<HourlyLineDefinition> Lines { get; set; } = [];
    public HourlyPushSetting Push { get; set; } = new();
}

public sealed class HourlyShiftCancellation
{
    public HourlyShiftPlan Plan { get; set; } = new();
    public DateTime CancelledAt { get; set; }
    public string CancelledBy { get; set; } = "";
}

public sealed class HourlyRemovedLine
{
    public HourlyLineDefinition Definition { get; set; } = new();
    public DateTime RemovedAt { get; set; }
    public string RemovedBy { get; set; } = "";
}

public sealed class HourlyBoardLayout
{
    public string Mode { get; set; } = "Compare";
    public int RotationSeconds { get; set; } = 15;
    public List<List<string>> Groups { get; set; } = [];
}

public sealed class HourlyLineDefinition
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string SummaryProcessCode { get; set; } = "";
    public List<decimal?> Targets { get; set; } = [];
    public List<HourlyProcessDefinition> Processes { get; set; } = [];
}

public sealed class HourlyProcessDefinition
{
    // 统计身份由服务端分配；旧工序沿用 line/code，新实例使用不重复的内部键。
    public string InstanceId { get; set; } = "";
    public string CreationMode { get; set; } = "";
    public DateTime? ActiveSince { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string RowCode { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "DistinctSn";
    public int ReworkMinutes { get; set; } = 30;
    public bool ReworkRequiresDifferentHour { get; set; } = true;
    public bool IncludeReworkInOutput { get; set; }
    public List<HourlyBinding> Bindings { get; set; } = [];
    public List<decimal?> Targets { get; set; } = [];
}

[SugarTable("HourlyProductionProcess")]
[SugarIndex("UX_HourlyProcess_Key", nameof(BoardCode), OrderByType.Asc, nameof(ProcessKey), OrderByType.Asc, true)]
public sealed class HourlyProductionProcessEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string Id { get; set; } = "";
    [SugarColumn(Length = 64)] public string BoardCode { get; set; } = "";
    [SugarColumn(Length = 64)] public string ProcessKey { get; set; } = "";
    [SugarColumn(Length = 30)] public string LineCode { get; set; } = "";
    [SugarColumn(Length = 30)] public string Code { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(100)")] public string Name { get; set; } = "";
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(max)")] public string? DefinitionJson { get; set; }
    public bool Active { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ActivatedAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? RemovedAt { get; set; }
}

public sealed class HourlyRemovedProcess
{
    public string InstanceId { get; set; } = "";
    public string LineCode { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? RemovedAt { get; set; }
    public bool CanRestore { get; set; }
    public string Reason { get; set; } = "";
    public HourlyProcessDefinition? Definition { get; set; }
}

public sealed class HourlyBinding
{
    public string Code { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Realtime";
    public string SourceKey { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public string SourceDeviceCode { get; set; } = "";
    public string SnPath { get; set; } = "SN";
    public string TimePath { get; set; } = "";
    public string QuantityPath { get; set; } = "";
    public string FilterPath { get; set; } = "";
    public string FilterValue { get; set; } = "";
    public string ReworkPath { get; set; } = "";
    public string ReworkValue { get; set; } = "1";
}

public sealed class HourlyPushSetting
{
    public bool Enabled { get; set; }
    public int DelayMinutes { get; set; } = 2;
    public int CatchUpHours { get; set; } = 2;
    public List<string> LineCodes { get; set; } = [];
    public List<string> WeChatUserIds { get; set; } = [];
    public List<string> EmailUserIds { get; set; } = [];
}

public sealed class HourlyBoardRuntime
{
    public HourlyBoardLayout? Layout { get; set; }
    public string ShiftName { get; set; } = "原班次";
    public DateTime ShiftStart { get; set; }
    public DateTime ShiftEnd { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public int RefreshSeconds { get; set; }
    public DateTime ProductionDate { get; set; }
    public DateTime ServerTime { get; set; }
    public DateTime? DataUpdatedAt { get; set; }
    public string? Advice { get; set; }
    public List<HourlyLineRuntime> Lines { get; set; } = [];
}

public sealed class HourlyLineRuntime
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal TotalOutput { get; set; }
    public string? SummaryProcessName { get; set; }
    public int AchievedProcesses { get; set; }
    public int TargetedProcesses { get; set; }
    public HourlyPoint? LatestCompleted { get; set; }
    public List<HourlyPoint> SummaryPoints { get; set; } = [];
    public List<HourlyProcessRuntime> Processes { get; set; } = [];
}

public sealed class HourlyProcessRuntime
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string RowCode { get; set; } = "";
    public bool Configured { get; set; }
    public List<HourlyPoint> Points { get; set; } = [];
}

public sealed class HourlyPoint
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public decimal? Output { get; set; }
    public decimal? Rework { get; set; }
    public decimal? Target { get; set; }
    public decimal? Achievement { get; set; }
    public string Status { get; set; } = "Pending";
}
