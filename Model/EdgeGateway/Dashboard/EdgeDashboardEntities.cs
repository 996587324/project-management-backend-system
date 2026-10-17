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

/// <summary>Three.js 设备组件与实时 HTTP 数据源的绑定；可选启用独立的设备日运行统计。</summary>
[SugarTable("EdgeDashboardDeviceBinding")]
public class EdgeDashboardDeviceBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string DeviceCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string SourceDeviceMark { get; set; } = string.Empty;
    public int OnlineTimeoutSeconds { get; set; } = 15;
    /// <summary>启用后，设备的状态时长会写入设备日统计表。</summary>
    public bool StatisticsEnabled { get; set; }
    /// <summary>业务日开始小时，取值 0-23。</summary>
    public int BusinessDayStartHour { get; set; }
    /// <summary>设备状态来源字段；为空时，在线且未命中报警映射按运行中统计。</summary>
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? StatusSourcePath { get; set; }
    /// <summary>状态值映射 JSON，例如 {"1":"RUNNING","0":"IDLE"}。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? StatusMappingJson { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>Three.js 设备组件从实时数据源选择的展示字段。</summary>
[SugarTable("EdgeDashboardDeviceFieldBinding")]
public class EdgeDashboardDeviceFieldBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long DeviceBindingId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
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

/// <summary>将接入侧 modelMark 转为看板使用的业务设备名称，不影响原始报文标识。</summary>
[SugarTable("EdgeDashboardModelDisplayMapping")]
public class EdgeDashboardModelDisplayMappingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string ModelMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string EquipmentType { get; set; } = "Device";
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
    /// <summary>人工移出候选库；接收/补齐更新不得覆盖此状态。</summary>
    [SugarColumn(IsNullable = false, DefaultValue = "0")]
    public bool IsSelectionDisabled { get; set; }
    public byte IsDelete { get; set; }
    public DateTime FirstSeenTime { get; set; } = DateTime.Now;
    public DateTime LastSeenTime { get; set; } = DateTime.Now;
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>模型字段的统一展示规则；仅改变展示值，不改写原始实时快照。</summary>
[SugarTable("EdgeDashboardModelFieldDisplayMapping")]
public class EdgeDashboardModelFieldDisplayMappingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string ModelMark { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
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

/// <summary>模型状态冲突时的裁决顺序；数值越小优先级越高。</summary>
[SugarTable("EdgeDashboardModelStatePriority")]
public class EdgeDashboardModelStatePriorityEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string ModelMark { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string State { get; set; } = EdgeDashboardConstants.Unknown;
    public int Priority { get; set; }
    public byte IsDelete { get; set; }
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

/// <summary>设备组件统计状态机的持久化检查点，服务重启后可继续累计。</summary>
[SugarTable("EdgeDashboardDeviceRuntimeState")]
public class EdgeDashboardDeviceRuntimeStateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long DeviceBindingId { get; set; }
    [SugarColumn(Length = 24)]
    public string CurrentState { get; set; } = EdgeDashboardConstants.Unknown;
    public DateTime StateStartTime { get; set; } = DateTime.Now;
    public DateTime LastSampleTime { get; set; } = DateTime.Now;
    public DateTime LastAccountedTime { get; set; } = DateTime.Now;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? LastMessageId { get; set; }
    [SugarColumn(IsNullable = true)]
    public long? LastSourceTimestamp { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按业务日保存设备组件互斥状态的累计秒数，不与机械手历史表混写。</summary>
[SugarTable("EdgeDashboardEquipmentDailyStatistics")]
public class EdgeDashboardEquipmentDailyStatisticEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 24)]
    public string ComponentType { get; set; } = "Device";
    public long ComponentBindingId { get; set; }
    public DateTime StatDate { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string ComponentCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string ComponentName { get; set; } = string.Empty;
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

/// <summary>后台定时生成的生产看板 AI 运行建议；仅保留当前仍可信的一条建议供看板展示。</summary>
[SugarTable("EdgeDashboardAiInsight")]
public class EdgeDashboardAiInsightEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 16)]
    public string Level { get; set; } = "Info";
    [SugarColumn(Length = 512)]
    public string Content { get; set; } = string.Empty;
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? Evidence { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? AffectedRobotsJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public DateTime ExpiresAt { get; set; } = DateTime.Now.AddMinutes(6);
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? Provider { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Model { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>一条线体选择的产量来源设备与数值字段；与 Three.js 设备组件组态独立。</summary>
[SugarTable("EdgeDashboardLineOutputBinding")]
public class EdgeDashboardLineOutputBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string FieldName { get; set; } = "累计产量";
    [SugarColumn(Length = 16)]
    public string CounterMode { get; set; } = "Cumulative";
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? Unit { get; set; }
    public int Decimals { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>按自然日保存线体产量计数器的检查点和累计产出，保证重启后口径连续。</summary>
[SugarTable("EdgeDashboardLineOutputDailyStatistic")]
public class EdgeDashboardLineOutputDailyStatisticEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long OutputBindingId { get; set; }
    public DateTime StatDate { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "decimal(18,3)")]
    public decimal OutputQuantity { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,3)")]
    public decimal BaselineValue { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,3)")]
    public decimal LastValue { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LastDataTime { get; set; }
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

/// <summary>设备卡片看板中的一张设备卡；数据源直接绑定实时快照，不依赖 Three.js 场景。</summary>
[SugarTable("EdgeDashboardDeviceCard")]
public class EdgeDashboardDeviceCardEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "device-card-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string CardCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string CardName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TenantMark { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ModelMark { get; set; }
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? StatusSourcePath { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? StatusMappingJson { get; set; }
    public int OnlineTimeoutSeconds { get; set; } = 30;
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>设备卡片的实时展示字段；TraceEnabled 为 true 时仅在值变化后写入追溯记录。</summary>
[SugarTable("EdgeDashboardDeviceCardField")]
public class EdgeDashboardDeviceCardFieldEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 16)]
    public string DisplayMode { get; set; } = "Detail";
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
    /// <summary>默认继承模型字段展示映射；保留关闭后的卡片级自定义展示能力。</summary>
    [SugarColumn(IsNullable = false, DefaultValue = "1")]
    public bool UseModelDisplayMapping { get; set; } = true;
    public bool TraceEnabled { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? TraceThreshold { get; set; }
    /// <summary>数值字段的可选下限；低于该值时创建一条越限告警事件。</summary>
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? AlarmLowerLimit { get; set; }
    /// <summary>数值字段的可选上限；高于该值时创建一条越限告警事件。</summary>
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? AlarmUpperLimit { get; set; }
    /// <summary>是否允许将该字段的当前展示值作为设备卡片 AI 建议的受控分析输入。</summary>
    [SugarColumn(IsNullable = false, DefaultValue = "0")]
    public bool AiAnalysisEnabled { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>每条线体的设备卡片网格配置。</summary>
[SugarTable("EdgeDashboardDeviceCardBoardSetting")]
public class EdgeDashboardDeviceCardBoardSettingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "device-card-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    public int ColumnCount { get; set; } = 4;
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>字段追溯检查点，只保留最近一次值，避免每条实时报文都写入历史。</summary>
[SugarTable("EdgeDashboardDeviceCardTraceState")]
public class EdgeDashboardDeviceCardTraceStateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string LastValueJson { get; set; } = "null";
    public DateTime LastDataTime { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 字段越限状态检查点。与普通变化追溯检查点分离，确保变化阈值不会影响越限判定。
/// </summary>
[SugarTable("EdgeDashboardDeviceCardAlertState")]
[SugarIndex("UX_EdgeDashboardDeviceCardAlertState_CardField", nameof(CardId), OrderByType.Asc,
    nameof(FieldCode), OrderByType.Asc, true)]
public class EdgeDashboardDeviceCardAlertStateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 16)]
    public string LastLimitState { get; set; } = "Normal";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string LastValueJson { get; set; } = "null";
    public DateTime LastDataTime { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 设备卡片数值字段发生越限或恢复时生成的事件；推送智能体通过认领和确认状态避免反复通知。
/// </summary>
[SugarTable("EdgeDashboardDeviceCardAlertRecord")]
[SugarIndex("IX_EdgeDashboardDeviceCardAlertRecord_Push", nameof(PushStatus), OrderByType.Asc,
    nameof(ClaimExpireTime), OrderByType.Asc, nameof(DataTime), OrderByType.Asc, false)]
[SugarIndex("IX_EdgeDashboardDeviceCardAlertRecord_Query", nameof(LineCode), OrderByType.Asc,
    nameof(DeviceMark), OrderByType.Asc, nameof(FieldCode), OrderByType.Asc,
    nameof(DataTime), OrderByType.Desc, false)]
[SugarIndex("IX_EdgeDashboardDeviceCardAlertRecord_Cleanup", nameof(CreateTime), OrderByType.Asc, false)]
public class EdgeDashboardDeviceCardAlertRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string AlertType { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "decimal(18,6)")]
    public decimal NumericValue { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? AlarmLowerLimit { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? AlarmUpperLimit { get; set; }
    [SugarColumn(Length = 16)]
    public string PushStatus { get; set; } = "Pending";
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ClaimedBy { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ClaimExpireTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? PushedTime { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? LastPushError { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MessageId { get; set; }
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>设备指定字段发生变化后留下的审计记录，默认由后台保留 180 天。</summary>
[SugarTable("EdgeDashboardDeviceCardChangeRecord")]
public class EdgeDashboardDeviceCardChangeRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 512)]
    public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? OldValueJson { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string NewValueJson { get; set; } = "null";
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MessageId { get; set; }
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
