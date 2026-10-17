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
    /// <summary>共享模型映射后的展示值；CurrentValue 始终保留原始实际值。</summary>
    public object? DisplayValue { get; set; }
    /// <summary>是否已匹配当前模型的共享展示映射。</summary>
    public bool HasModelDisplayMapping { get; set; }
    public string FormatType { get; set; } = "Direct";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? MappingJson { get; set; }
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

/// <summary>Three.js 设备组件展示字段配置。</summary>
public class EdgeDashboardDeviceFieldSaveDto
{
    public long Id { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "String";
    public string FormatType { get; set; } = "Direct";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? MappingJson { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>完整保存 Three.js 设备组件的数据源与展示字段。</summary>
public class EdgeDashboardDeviceBindingSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string SourceDeviceMark { get; set; } = string.Empty;
    public int OnlineTimeoutSeconds { get; set; } = 15;
    public bool StatisticsEnabled { get; set; }
    public int BusinessDayStartHour { get; set; }
    public string? StatusSourcePath { get; set; }
    public string? StatusMappingJson { get; set; }
    public bool Enabled { get; set; } = true;
    public List<EdgeDashboardDeviceFieldSaveDto> Fields { get; set; } = [];
}

/// <summary>保存线体产量数据源；累计计数器按日计算增量，当前值模式直接展示字段值。</summary>
public class EdgeDashboardLineOutputBindingSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string FieldName { get; set; } = "累计产量";
    public string CounterMode { get; set; } = "Cumulative";
    public string? Unit { get; set; }
    public int Decimals { get; set; }
    public bool Enabled { get; set; } = true;
}

public class EdgeDashboardLineOutputBindingOutputDto : EdgeDashboardLineOutputBindingSaveDto
{
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>看板左侧“当前线体产出”卡片的只读数据。</summary>
public class EdgeDashboardLineOutputDto
{
    public bool Configured { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string FieldName { get; set; } = "当前产出";
    public string? Unit { get; set; }
    public int Decimals { get; set; }
    public decimal TodayQuantity { get; set; }
    public decimal? CurrentValue { get; set; }
    public decimal? LatestIncrease { get; set; }
    public string? DeviceMark { get; set; }
    public DateTime? DataTime { get; set; }
    public bool DataOnline { get; set; }
}

/// <summary>设备绑定的管理端输出。</summary>
public class EdgeDashboardDeviceBindingOutputDto : EdgeDashboardDeviceBindingSaveDto
{
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>设备模型在看板中的业务显示名称和推荐组件类别。</summary>
public class EdgeDashboardModelDisplayMappingSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string ModelMark { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Robot / Device / Machine / Inspection，仅作为组态引导，不改写既有场景组件。</summary>
    public string EquipmentType { get; set; } = "Device";
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
}

public class EdgeDashboardModelDisplayMappingOutputDto : EdgeDashboardModelDisplayMappingSaveDto
{
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

/// <summary>模型字段的统一展示格式和值映射配置。</summary>
public class EdgeDashboardModelFieldDisplayMappingSaveDto
{
    public long Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ValueType { get; set; } = "String";
    public string FormatType { get; set; } = "Direct";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? MappingJson { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>一次完整保存指定模型的字段展示映射。</summary>
public class EdgeDashboardModelFieldDisplayMappingListSaveDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string ModelMark { get; set; } = string.Empty;
    public List<EdgeDashboardModelFieldDisplayMappingSaveDto> Mappings { get; set; } = [];
}

public class EdgeDashboardModelFieldDisplayMappingOutputDto : EdgeDashboardModelFieldDisplayMappingSaveDto
{
    public string DashboardCode { get; set; } = string.Empty;
    public string ModelMark { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>模型状态冲突时的优先级配置，数值越小越优先。</summary>
public class EdgeDashboardModelStatePrioritySaveDto
{
    public string State { get; set; } = EdgeDashboardConstants.Unknown;
    public int Priority { get; set; }
}

/// <summary>一次完整保存指定模型的状态优先级。</summary>
public class EdgeDashboardModelStatePriorityListSaveDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string ModelMark { get; set; } = string.Empty;
    public List<EdgeDashboardModelStatePrioritySaveDto> Priorities { get; set; } = [];
}

public class EdgeDashboardModelStatePriorityOutputDto : EdgeDashboardModelStatePrioritySaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = string.Empty;
    public string ModelMark { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>模型配置工作簿下载内容。</summary>
public class EdgeDashboardModelConfigurationExportDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public byte[] Content { get; set; } = [];
}

/// <summary>模型配置导入中的单行错误，便于在页面中定位并修正文件。</summary>
public class EdgeDashboardModelConfigurationImportFailureDto
{
    public string SheetName { get; set; } = string.Empty;
    public int RowIndex { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class EdgeDashboardModelConfigurationImportResultDto
{
    public int Total { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<EdgeDashboardModelConfigurationImportFailureDto> Failures { get; set; } = [];
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
    /// <summary>本次明确上报的增量字段；Data 为合并状态。目录与变化追溯仅处理实际上报字段。</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object?>? ReportedData { get; set; }
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

/// <summary>Three.js 设备组件当前的在线状态与展示字段。</summary>
public class EdgeDashboardDeviceRuntimeDto
{
    public long BindingId { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string SourceDeviceMark { get; set; } = string.Empty;
    /// <summary>设备组件当前状态：RUNNING、WARNING、ALARM 或 OFFLINE。</summary>
    public string Status { get; set; } = EdgeDashboardConstants.Unknown;
    /// <summary>命中模型报警映射时显示的当前报警说明。</summary>
    public string? CurrentAlarm { get; set; }
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
    public List<EdgeDashboardDeviceRuntimeDto> Devices { get; set; } = [];
}

/// <summary>生产看板顶部滚动展示的 AI 运行建议。</summary>
public class EdgeDashboardAiInsightOutputDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "production-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string Level { get; set; } = "Info";
    public string Content { get; set; } = string.Empty;
    public string? Evidence { get; set; }
    public List<string> AffectedRobots { get; set; } = [];
    public DateTime GeneratedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>每日机械手统计查询条件。</summary>
public class EdgeDashboardDailyStatisticQueryDto : EdgePageQueryDto
{
    public string? LineCode { get; set; }
    public string? RobotCode { get; set; }
    /// <summary>Robot、Device 或 All；省略时返回两类统计。</summary>
    public string? ComponentType { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

/// <summary>每日运行、空闲、故障及系统兜底状态统计。</summary>
public class EdgeDashboardDailyStatisticOutputDto
{
    public long Id { get; set; }
    public long RobotBindingId { get; set; }
    public string ComponentType { get; set; } = "Robot";
    public long ComponentBindingId { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
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

/// <summary>
/// 从既有线体复制组态到一条新线体；仅复制可编辑配置，不复制实时状态和历史业务数据。
/// </summary>
public class EdgeDashboardLineCopyDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string SourceLineCode { get; set; } = string.Empty;
    public string TargetLineCode { get; set; } = string.Empty;
    public string TargetLineName { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Color { get; set; } = "#55d5ff";
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
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

/// <summary>设备卡片中的一个实时展示字段；可选开启字段变更追溯。</summary>
public class EdgeDashboardDeviceCardFieldSaveDto
{
    public EdgeDeviceCardPushConfigDto? Push { get; set; }
    public long Id { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayMode { get; set; } = "Detail";
    public string ValueType { get; set; } = "String";
    public string FormatType { get; set; } = "Direct";
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? MappingJson { get; set; }
    /// <summary>启用时优先应用模型字段展示映射；关闭后使用当前卡片自己的展示配置。</summary>
    public bool UseModelDisplayMapping { get; set; } = true;
    public bool TraceEnabled { get; set; }
    public decimal? TraceThreshold { get; set; }
    public decimal? AlarmLowerLimit { get; set; }
    public decimal? AlarmUpperLimit { get; set; }
    /// <summary>仅勾选的字段会被汇总给 AI；不会发送原始完整报文。</summary>
    public bool AiAnalysisEnabled { get; set; }
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>保存一张设备卡片及其字段绑定。</summary>
public class EdgeDashboardDeviceCardSaveDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "device-card-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public string? StatusSourcePath { get; set; }
    public string? StatusMappingJson { get; set; }
    public int OnlineTimeoutSeconds { get; set; } = 30;
    public int Sort { get; set; }
    public bool Enabled { get; set; } = true;
    public List<EdgeDashboardDeviceCardFieldSaveDto> Fields { get; set; } = [];
}

public class EdgeDashboardDeviceCardOutputDto : EdgeDashboardDeviceCardSaveDto
{
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>线体设备卡片每行显示数量。</summary>
public class EdgeDashboardDeviceCardBoardSettingDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = "device-card-dashboard";
    public string LineCode { get; set; } = string.Empty;
    public int ColumnCount { get; set; } = 4;
}

/// <summary>一张卡片实时解析后的字段值。</summary>
public class EdgeDashboardDeviceCardRuntimeFieldDto
{
    public string FieldCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayMode { get; set; } = "Detail";
    public object? RawValue { get; set; }
    public object? DisplayValue { get; set; }
    public string? Unit { get; set; }
    public int Sort { get; set; }
}

/// <summary>设备卡片看板的一张实时卡。</summary>
public class EdgeDashboardDeviceCardRuntimeDto
{
    public long CardId { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public bool DataOnline { get; set; }
    public string Status { get; set; } = "OFFLINE";
    public DateTime? DataTime { get; set; }
    public DateTime? ReceiveTime { get; set; }
    /// <summary>当前自然日该卡片已记录的字段变更次数，供顶部“今日变更”联动筛选。</summary>
    public int TodayChangeCount { get; set; }
    public List<EdgeDashboardDeviceCardRuntimeFieldDto> Fields { get; set; } = [];
}

/// <summary>设备卡片看板顶部概览统计。</summary>
public class EdgeDashboardDeviceCardOverviewDto
{
    public int TotalCount { get; set; }
    public int OnlineCount { get; set; }
    public int OfflineCount { get; set; }
    public int FaultCount { get; set; }
    public int AlarmCount { get; set; }
    public int TodayChangedDeviceCount { get; set; }
    public int TodayChangeCount { get; set; }
}

/// <summary>设备卡片看板一次刷新数据。</summary>
public class EdgeDashboardDeviceCardBoardRuntimeDto
{
    public string? LineCode { get; set; }
    public int ColumnCount { get; set; } = 4;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public EdgeDashboardDeviceCardOverviewDto Overview { get; set; } = new();
    public List<EdgeDashboardDeviceCardRuntimeDto> Cards { get; set; } = [];
}

/// <summary>设备字段变更记录的筛选条件。</summary>
public class EdgeDashboardDeviceCardChangeQueryDto : EdgePageQueryDto
{
    public long? CardId { get; set; }
    public string? LineCode { get; set; }
    public string? DeviceMark { get; set; }
    public string? FieldCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public class EdgeDashboardDeviceCardChangeRecordOutputDto
{
    public long Id { get; set; }
    public long CardId { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string FieldCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? OldValueJson { get; set; }
    public string NewValueJson { get; set; } = string.Empty;
    public string? MessageId { get; set; }
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
}

/// <summary>设备卡片越限事件的后台查询条件。</summary>
public class EdgeDashboardDeviceCardAlertQueryDto : EdgePageQueryDto
{
    public long? CardId { get; set; }
    public string? LineCode { get; set; }
    public string? DeviceMark { get; set; }
    public string? FieldCode { get; set; }
    public string? AlertType { get; set; }
    public string? PushStatus { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

/// <summary>提供给后台页面和 MCP 推送智能体的越限事件数据。</summary>
public class EdgeDashboardDeviceCardAlertRecordOutputDto
{
    public long Id { get; set; }
    public long CardId { get; set; }
    public string LineCode { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string FieldCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public decimal NumericValue { get; set; }
    public decimal? AlarmLowerLimit { get; set; }
    public decimal? AlarmUpperLimit { get; set; }
    public string PushStatus { get; set; } = string.Empty;
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimExpireTime { get; set; }
    public DateTime? PushedTime { get; set; }
    public string? LastPushError { get; set; }
    public string? MessageId { get; set; }
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
    public DateTime CreateTime { get; set; }
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
