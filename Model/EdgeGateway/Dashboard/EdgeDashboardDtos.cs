namespace Model.EdgeGateway;

/// <summary>HTTP 最新快照按 modelMark 汇总后的设备模型选项。</summary>
public class EdgeDashboardDataSourceModelOptionDto
{
    public string ModelMark { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int DeviceCount { get; set; }
    public DateTime LatestReceiveTime { get; set; }
}

/// <summary>HTTP 最新快照可作为机械手数据源的选项。</summary>
public class EdgeDashboardDataSourceOptionDto
{
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public bool DataOnline { get; set; }
    public DateTime ReceiveTime { get; set; }
}

/// <summary>从完整 HTTP 报文中发现的可选叶子字段。</summary>
public class EdgeDashboardSourceFieldOptionDto
{
    public string SourcePath { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "String";
    public object? CurrentValue { get; set; }
}

/// <summary>机械手选择的一个展示字段配置。</summary>
public class EdgeDashboardRobotFieldSaveDto
{
    public long Id { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Purpose { get; set; } = "DisplayValue";
    public string ValueType { get; set; } = "String";
    public string FormatType { get; set; } = "Direct";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? MappingJson { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>机械手运行、空闲或故障的字段判断规则。</summary>
public class EdgeDashboardRobotStateRuleSaveDto
{
    public long Id { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string TargetState { get; set; } = EdgeDashboardConstants.Unknown;
    public string SourcePath { get; set; } = string.Empty;
    public string Operator { get; set; } = "Equals";
    public string? CompareValue { get; set; }
    public string? FaultCode { get; set; }
    public string? FaultReason { get; set; }
    public string? FaultReasonPath { get; set; }
    public string? FaultSeverity { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}

/// <summary>完整保存一个机械手的数据源、字段和状态统计规则。</summary>
public class EdgeDashboardRobotBindingSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string RobotCode { get; set; } = string.Empty;
    public string RobotName { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public int OnlineTimeoutSeconds { get; set; } = 15;
    public int BusinessDayStartHour { get; set; }
    public bool Enabled { get; set; } = true;
    public List<EdgeDashboardRobotFieldSaveDto> Fields { get; set; } = [];
    public List<EdgeDashboardRobotStateRuleSaveDto> StateRules { get; set; } = [];
}

/// <summary>机械手绑定及其字段和规则的管理端输出。</summary>
public class EdgeDashboardRobotBindingOutputDto : EdgeDashboardRobotBindingSaveDto
{
    public int RuleVersion { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>按设备模型保存的一条报警映射规则。</summary>
public class EdgeDashboardModelAlarmRuleSaveDto
{
    public long Id { get; set; }
    public string RuleCode { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Operator { get; set; } = "Equals";
    public string? CompareValue { get; set; }
    public string? FaultCode { get; set; }
    public string? FaultReason { get; set; }
    public string? FaultReasonPath { get; set; }
    public string? FaultSeverity { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}

/// <summary>一个模型下完整的报警映射规则保存请求。</summary>
public class EdgeDashboardModelAlarmRuleListSaveDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string ModelMark { get; set; } = string.Empty;
    public List<EdgeDashboardModelAlarmRuleSaveDto> Rules { get; set; } = [];
}

/// <summary>模型报警映射规则的管理端输出。</summary>
public class EdgeDashboardModelAlarmRuleOutputDto : EdgeDashboardModelAlarmRuleSaveDto
{
    public string DashboardCode { get; set; } = string.Empty;
    public string ModelMark { get; set; } = string.Empty;
    public int RuleVersion { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>成功接收后交给统计状态机的标准样本。</summary>
public class EdgeDashboardTelemetrySampleDto
{
    public string DeviceMark { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public int? DataType { get; set; }
    public string TenantMark { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public long? SourceTimestamp { get; set; }
    public Dictionary<string, object?> Data { get; set; } = [];
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
}

/// <summary>机械手当前值中一个已经转换好的展示字段。</summary>
public class EdgeDashboardRuntimeFieldDto
{
    public string FieldCode { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public object? RawValue { get; set; }
    public object? DisplayValue { get; set; }
    public string? Unit { get; set; }
    public int Sort { get; set; }
}

/// <summary>看板一台机械手的当前解析结果。</summary>
public class EdgeDashboardRobotRuntimeDto
{
    public long BindingId { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string RobotCode { get; set; } = string.Empty;
    public string RobotName { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string Status { get; set; } = EdgeDashboardConstants.Unknown;
    public bool DataOnline { get; set; }
    public DateTime? DataTime { get; set; }
    public DateTime? ReceiveTime { get; set; }
    public List<EdgeDashboardRuntimeFieldDto> Fields { get; set; } = [];
}

/// <summary>线体看板一次批量刷新返回的全部机械手。</summary>
public class EdgeDashboardLineRuntimeDto
{
    public string LineCode { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public List<EdgeDashboardRobotRuntimeDto> Robots { get; set; } = [];
}

/// <summary>每日机械手统计查询条件。</summary>
public class EdgeDashboardDailyStatisticQueryDto : EdgePageQueryDto
{
    public string? LineCode { get; set; }
    public string? RobotCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

/// <summary>每日运行、空闲、故障及系统兜底状态统计。</summary>
public class EdgeDashboardDailyStatisticOutputDto
{
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    public DateTime StatDate { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string RobotCode { get; set; } = string.Empty;
    public string RobotName { get; set; } = string.Empty;
    public long RunningSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public long FaultSeconds { get; set; }
    public long StoppedSeconds { get; set; }
    public long OfflineSeconds { get; set; }
    public long UnknownSeconds { get; set; }
    public int FaultCount { get; set; }
    public long SampleCount { get; set; }
    public string LastState { get; set; } = EdgeDashboardConstants.Unknown;
    public decimal AvailabilityRate { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>故障记录分页筛选条件。</summary>
public class EdgeDashboardFaultQueryDto : EdgePageQueryDto
{
    public string? LineCode { get; set; }
    public string? RobotCode { get; set; }
    public string? FaultReason { get; set; }
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

/// <summary>单次故障生命周期输出。</summary>
public class EdgeDashboardFaultRecordOutputDto
{
    public long Id { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string RobotCode { get; set; } = string.Empty;
    public string RobotName { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string FaultCode { get; set; } = string.Empty;
    public string RuleScope { get; set; } = string.Empty;
    public string FaultReason { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string? RawValue { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime LastSeenTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long DurationSeconds { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>周月高频故障原因聚合结果。</summary>
public class EdgeDashboardFaultSummaryDto
{
    public string FaultCode { get; set; } = string.Empty;
    public string FaultReason { get; set; } = string.Empty;
    public int FaultCount { get; set; }
    public long DurationSeconds { get; set; }
}

/// <summary>表格或图表中的一列、分类维度或数值系列。</summary>
public class EdgeDashboardWidgetFieldSaveDto
{
    public long Id { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "Column";
    public string Aggregate { get; set; } = "None";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? Color { get; set; }
    public int? Width { get; set; }
    public int Sort { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>数据库驱动的通用看板部件保存模型。</summary>
public class EdgeDashboardWidgetSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string? LineCode { get; set; }
    public string WidgetCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string WidgetType { get; set; } = "Table";
    public string DataSourceType { get; set; } = "DailyStatistics";
    public string Period { get; set; } = "Day";
    public int RefreshSeconds { get; set; } = 30;
    public int TopN { get; set; } = 10;
    public bool Stacked { get; set; } = true;
    public string Orientation { get; set; } = "Vertical";
    public string ConfigJson { get; set; } = "{}";
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public List<EdgeDashboardWidgetFieldSaveDto> Fields { get; set; } = [];
}

/// <summary>通用看板部件管理端输出。</summary>
public class EdgeDashboardWidgetOutputDto : EdgeDashboardWidgetSaveDto
{
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>供配置界面下拉选择的受控字段。</summary>
public class EdgeDashboardFieldCatalogDto
{
    public string FieldKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "String";
    public string[] DataSourceTypes { get; set; } = [];
}

/// <summary>新增或修改看板线体主配置的请求。</summary>
public class EdgeDashboardLineConfigSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Color { get; set; } = "#55d5ff";
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
}

/// <summary>数据库中的线体主配置及更新时间。</summary>
public class EdgeDashboardLineConfigOutputDto : EdgeDashboardLineConfigSaveDto
{
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>保存一条线体完整 Three.js 场景组态的请求。</summary>
public class EdgeDashboardSceneLayoutSaveDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;
    public int LayoutVersion { get; set; } = 1;
    public string LayoutJson { get; set; } = "{}";
}

/// <summary>修改线体编码时需要同步迁移的场景和关联数据。</summary>
public class EdgeDashboardLineCodeChangeDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string OldLineCode { get; set; } = string.Empty;
    public string NewLineCode { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;
    public int LayoutVersion { get; set; } = 1;
    public string LayoutJson { get; set; } = "{}";
}

/// <summary>数据库中一条 Three.js 场景组态及其修订信息。</summary>
public class EdgeDashboardSceneLayoutOutputDto : EdgeDashboardSceneLayoutSaveDto
{
    public long Id { get; set; }
    public int Revision { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>看板首屏初始化所需的线体主配置和全部 Three.js 场景。</summary>
public class EdgeDashboardBootstrapOutputDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public List<EdgeDashboardLineConfigOutputDto> Lines { get; set; } = [];
    public List<EdgeDashboardSceneLayoutOutputDto> SceneLayouts { get; set; } = [];
}
