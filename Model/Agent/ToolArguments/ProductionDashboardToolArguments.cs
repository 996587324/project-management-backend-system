namespace Model.Agent.ToolArguments;

/// <summary>生产看板 Agent 固定操作的看板范围；当前仅允许生产看板。</summary>
public sealed class ProductionDashboardScopeArguments
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string? LineCode { get; set; }
}

public sealed class ProductionDashboardRuntimeArguments
{
    public string LineCode { get; set; } = string.Empty;
}

public class ProductionDashboardStatisticArguments
{
    public string? LineCode { get; set; }
    public string? RobotCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

public sealed class ProductionDashboardFaultArguments : ProductionDashboardStatisticArguments
{
    public string? FaultReason { get; set; }
    public string? Status { get; set; }
    public int TopN { get; set; } = 10;
}

/// <summary>新增或修改线体时的受控字段。</summary>
public sealed class ProductionDashboardSaveLineArguments
{
    public string LineCode { get; set; } = string.Empty;
    public string? LineName { get; set; }
    public string? Area { get; set; }
    public string? Color { get; set; }
    public bool? Enabled { get; set; }
    public int? Sort { get; set; }
}

/// <summary>新增或修改图表部件时的字段定义。</summary>
public sealed class ProductionDashboardWidgetFieldArguments
{
    public string FieldKey { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public string? Aggregate { get; set; }
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public string? Color { get; set; }
    public int? Width { get; set; }
    public int? Sort { get; set; }
    public bool? Visible { get; set; }
}

/// <summary>Agent 对看板部件允许读写的结构化字段；不接受任意脚本或样式代码。</summary>
public sealed class ProductionDashboardSaveWidgetArguments
{
    public long? Id { get; set; }
    public string? LineCode { get; set; }
    public string? WidgetCode { get; set; }
    public string? Title { get; set; }
    public string? WidgetType { get; set; }
    public string? DataSourceType { get; set; }
    public string? Period { get; set; }
    public int? RefreshSeconds { get; set; }
    public int? TopN { get; set; }
    public bool? Stacked { get; set; }
    public string? Orientation { get; set; }
    public int? Sort { get; set; }
    public bool? Enabled { get; set; }
    public int? ColumnSpan { get; set; }
    public int? RowSpan { get; set; }
    public int? TableRowHeight { get; set; }
    public bool? AutoScroll { get; set; }
    public int? ScrollIntervalSeconds { get; set; }
    public List<ProductionDashboardWidgetFieldArguments>? Fields { get; set; }
}

/// <summary>新增或修改场景内一个受控组件；位置和尺寸均使用数值字段，禁止下发 Three.js 代码。</summary>
public sealed class ProductionDashboardSaveSceneItemArguments
{
    public string LineCode { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public bool Create { get; set; }
    public string? Name { get; set; }
    public string? Station { get; set; }
    public string? DeviceType { get; set; }
    public string? JunctionKind { get; set; }
    public decimal? PositionX { get; set; }
    public decimal? PositionY { get; set; }
    public decimal? FromX { get; set; }
    public decimal? FromY { get; set; }
    public decimal? ToX { get; set; }
    public decimal? ToY { get; set; }
    public decimal? Rotation { get; set; }
    public decimal? Width { get; set; }
    public decimal? Height { get; set; }
    public decimal? Depth { get; set; }
    public string? Color { get; set; }
}

/// <summary>仅调整已有 Three.js 场景物体的位置或朝向，不允许修改名称、尺寸、颜色及其他业务配置。</summary>
public sealed class ProductionDashboardMoveSceneItemArguments
{
    public string LineCode { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public decimal? PositionX { get; set; }
    public decimal? PositionY { get; set; }
    public decimal? FromX { get; set; }
    public decimal? FromY { get; set; }
    public decimal? ToX { get; set; }
    public decimal? ToY { get; set; }
    public decimal? Rotation { get; set; }
}
