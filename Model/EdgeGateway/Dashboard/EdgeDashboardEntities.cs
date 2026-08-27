using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>一个看板机械手与一个 HTTP 实时数据源之间的绑定。</summary>
[SugarTable("EdgeDashboardRobotBinding")]
public class EdgeDashboardRobotBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string RobotCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RobotName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    public int OnlineTimeoutSeconds { get; set; } = 15;
    public int BusinessDayStartHour { get; set; }
    public int RuleVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>机械手从完整 HTTP 报文中选择的一个展示字段。</summary>
[SugarTable("EdgeDashboardRobotFieldBinding")]
public class EdgeDashboardRobotFieldBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Purpose { get; set; } = "DisplayValue";
    [SugarColumn(Length = 32)]
    public string ValueType { get; set; } = "String";
    [SugarColumn(Length = 32)]
    public string FormatType { get; set; } = "Direct";
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Unit { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? Decimals { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? MappingJson { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>把报文字段和值判断为运行、空闲或故障状态的规则。</summary>
[SugarTable("EdgeDashboardRobotStateRule")]
public class EdgeDashboardRobotStateRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    [SugarColumn(Length = 64)]
    public string RuleCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RuleName { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string TargetState { get; set; } = EdgeDashboardConstants.Unknown;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Operator { get; set; } = "Equals";
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? CompareValue { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? FaultCode { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? FaultReason { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? FaultReasonPath { get; set; }
    [SugarColumn(Length = 24, IsNullable = true)]
    public string? FaultSeverity { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按设备模型配置的报警规则，同一 modelMark 下的设备共用。</summary>
[SugarTable("EdgeDashboardModelAlarmRule")]
public class EdgeDashboardModelAlarmRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string ModelMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string RuleCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RuleName { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Operator { get; set; } = "Equals";
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? CompareValue { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? FaultCode { get; set; }
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? FaultReason { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? FaultReasonPath { get; set; }
    [SugarColumn(Length = 24, IsNullable = true)]
    public string? FaultSeverity { get; set; }
    public int Priority { get; set; } = 100;
    public int RuleVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按设备模型沉淀历史接收字段，供模型报警配置下拉选择，避免只依赖某一条最新数据。</summary>
[SugarTable("EdgeDashboardModelFieldCatalog")]
public class EdgeDashboardModelFieldCatalogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string ModelMark { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string FieldName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string ValueType { get; set; } = "String";
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? LastValueJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SampleTenantMark { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SampleDeviceMark { get; set; }
    public long SeenCount { get; set; } = 1;
    public byte IsDelete { get; set; }
    public DateTime FirstSeenTime { get; set; } = DateTime.Now;
    public DateTime LastSeenTime { get; set; } = DateTime.Now;
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>机械手统计状态机的持久化检查点，服务重启后可继续累计。</summary>
[SugarTable("EdgeDashboardRobotRuntimeState")]
public class EdgeDashboardRobotRuntimeStateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    [SugarColumn(Length = 24)]
    public string CurrentState { get; set; } = EdgeDashboardConstants.Unknown;
    public DateTime StateStartTime { get; set; } = DateTime.Now;
    public DateTime LastSampleTime { get; set; } = DateTime.Now;
    public DateTime LastAccountedTime { get; set; } = DateTime.Now;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? LastMessageId { get; set; }
    [SugarColumn(IsNullable = true)]
    public long? LastSourceTimestamp { get; set; }
    public int RuleVersion { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按业务日保存机械手互斥状态的累计秒数。</summary>
[SugarTable("EdgeDashboardRobotDailyStatistics")]
public class EdgeDashboardRobotDailyStatisticEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    public DateTime StatDate { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string RobotCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RobotName { get; set; } = string.Empty;
    public long RunningSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public long FaultSeconds { get; set; }
    public long StoppedSeconds { get; set; }
    public long OfflineSeconds { get; set; }
    public long UnknownSeconds { get; set; }
    public int FaultCount { get; set; }
    public long SampleCount { get; set; }
    [SugarColumn(Length = 24)]
    public string LastState { get; set; } = EdgeDashboardConstants.Unknown;
    public int RuleVersion { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>故障从首次触发到恢复的完整生命周期记录。</summary>
[SugarTable("EdgeDashboardRobotFaultRecord")]
public class EdgeDashboardRobotFaultRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    public long FaultRuleId { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string RobotCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string RobotName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string FaultCode { get; set; } = string.Empty;
    [SugarColumn(Length = 24, IsNullable = true)]
    public string RuleScope { get; set; } = "Binding";
    [SugarColumn(Length = 256)]
    public string FaultReason { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Severity { get; set; } = "Warning";
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? RawValue { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LastSeenTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
    public long DurationSeconds { get; set; }
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Active";
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? StartMessageId { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? EndMessageId { get; set; }
    public int RuleVersion { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>数据库驱动的表格、饼图或柱状图部件定义。</summary>
[SugarTable("EdgeDashboardWidget")]
public class EdgeDashboardWidgetEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? LineCode { get; set; }
    [SugarColumn(Length = 64)]
    public string WidgetCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string Title { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string WidgetType { get; set; } = "Table";
    [SugarColumn(Length = 32)]
    public string DataSourceType { get; set; } = "DailyStatistics";
    [SugarColumn(Length = 16)]
    public string Period { get; set; } = "Day";
    public int RefreshSeconds { get; set; } = 30;
    public int TopN { get; set; } = 10;
    public bool Stacked { get; set; } = true;
    [SugarColumn(Length = 16)]
    public string Orientation { get; set; } = "Vertical";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ConfigJson { get; set; } = "{}";
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>部件中的表格列、图表分类或数值系列。</summary>
[SugarTable("EdgeDashboardWidgetField")]
public class EdgeDashboardWidgetFieldEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long WidgetId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldKey { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Role { get; set; } = "Column";
    [SugarColumn(Length = 24)]
    public string Aggregate { get; set; } = "None";
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Unit { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? Decimals { get; set; }
    [SugarColumn(Length = 16, IsNullable = true)]
    public string? Color { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? Width { get; set; }
    public int Sort { get; set; }
    public bool Visible { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>看板线体主配置；与 Three.js 场景分开保存，供首屏快速加载线体导航。</summary>
[SugarTable("EdgeDashboardLineConfig")]
public class EdgeDashboardLineConfigEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string LineName { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string Area { get; set; } = string.Empty;
    [SugarColumn(Length = 16)]
    public string Color { get; set; } = "#55d5ff";
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按看板和线体保存一份完整的 Three.js 场景组态。</summary>
[SugarTable("EdgeDashboardSceneLayout")]
public class EdgeDashboardSceneLayoutEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string LineName { get; set; } = string.Empty;
    public int LayoutVersion { get; set; } = 1;
    public int Revision { get; set; } = 1;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string LayoutJson { get; set; } = "{}";
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
