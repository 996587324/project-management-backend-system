using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Model.Agent;
using Model.Agent.ToolArguments;
using Model.EdgeGateway;

namespace Service;

/// <summary>生产看板读取工具集合。</summary>
public sealed class ProductionDashboardOverviewAgentToolAction : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardOverviewAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.overview", "查询看板概况", "查询生产看板的线体列表、启用状态和已保存场景。",
        "GetOverview", ProductionDashboardAgentToolSeedFactory.EmptySchema, 10, "{}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetBootstrapAsync(ProductionDashboardAgentTools.DashboardCode),
            "生产看板概况查询成功");
}

public sealed class ProductionDashboardRuntimeAgentToolAction : AgentToolActionBase<ProductionDashboardRuntimeArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardRuntimeAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.runtime", "查询实时状态", "查询指定线体的机械手当前状态、在线情况和展示字段。",
        "GetRuntime", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1}},"required":["lineCode"],"additionalProperties":false}
        """, 20, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardRuntimeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetLineRuntimeAsync(arguments.LineCode), "线体实时状态查询成功");
}

public sealed class ProductionDashboardStatisticsAgentToolAction : AgentToolActionBase<ProductionDashboardStatisticArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardStatisticsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.statistics", "查询运行统计", "按线体、机械手和日期查询运行、空闲、故障时长及运行率。",
        "GetStatistics", """
        {"type":"object","properties":{"lineCode":{"type":"string"},"robotCode":{"type":"string"},"dateFrom":{"type":"string"},"dateTo":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 30, "{\"lineCode\":\"W1\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardStatisticArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetDailyStatisticsAsync(new EdgeDashboardDailyStatisticQueryDto
        {
            LineCode = arguments.LineCode,
            RobotCode = arguments.RobotCode,
            DateFrom = arguments.DateFrom,
            DateTo = arguments.DateTo,
            Current = Math.Max(arguments.Current, 1),
            Size = Math.Clamp(arguments.Size, 1, 100)
        });
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条运行统计");
    }
}

public sealed class ProductionDashboardFaultsAgentToolAction : AgentToolActionBase<ProductionDashboardFaultArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardFaultsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.faults", "查询故障记录", "按线体、机械手、故障原因和日期查询故障生命周期记录。",
        "GetFaults", """
        {"type":"object","properties":{"lineCode":{"type":"string"},"robotCode":{"type":"string"},"faultReason":{"type":"string"},"status":{"type":"string"},"dateFrom":{"type":"string"},"dateTo":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 40, "{\"lineCode\":\"W1\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardFaultArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetFaultRecordsAsync(new EdgeDashboardFaultQueryDto
        {
            LineCode = arguments.LineCode,
            RobotCode = arguments.RobotCode,
            FaultReason = arguments.FaultReason,
            Status = arguments.Status,
            DateFrom = arguments.DateFrom,
            DateTo = arguments.DateTo,
            Current = Math.Max(arguments.Current, 1),
            Size = Math.Clamp(arguments.Size, 1, 100)
        });
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条故障记录");
    }
}

public sealed class ProductionDashboardFaultSummaryAgentToolAction : AgentToolActionBase<ProductionDashboardFaultArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardFaultSummaryAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.fault-summary", "查询故障汇总", "按时间范围汇总高频故障原因、次数和持续时长。",
        "GetFaultSummary", """
        {"type":"object","properties":{"lineCode":{"type":"string"},"robotCode":{"type":"string"},"faultReason":{"type":"string"},"status":{"type":"string"},"dateFrom":{"type":"string"},"dateTo":{"type":"string"},"topN":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 50, "{\"lineCode\":\"W1\",\"topN\":10}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardFaultArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetFaultSummaryAsync(new EdgeDashboardFaultQueryDto
            {
                LineCode = arguments.LineCode,
                RobotCode = arguments.RobotCode,
                FaultReason = arguments.FaultReason,
                Status = arguments.Status,
                DateFrom = arguments.DateFrom,
                DateTo = arguments.DateTo
            }, Math.Clamp(arguments.TopN, 1, 100)),
            "故障汇总查询成功");
}

public sealed class ProductionDashboardWidgetsAgentToolAction : AgentToolActionBase<ProductionDashboardScopeArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardWidgetsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.widgets", "查询图表配置", "查询指定线体可见的表格、饼图和柱状图配置。",
        "GetWidgets", """
        {"type":"object","properties":{"lineCode":{"type":"string"}},"additionalProperties":false}
        """, 60, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardScopeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetWidgetsAsync(ProductionDashboardAgentTools.DashboardCode, arguments.LineCode),
            "图表配置查询成功");
}

public sealed class ProductionDashboardSceneAgentToolAction : AgentToolActionBase<ProductionDashboardRuntimeArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSceneAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.scene", "查询场景布局", "查询指定线体 Three.js 场景中的机械手、设备、工位和输送线配置。",
        "GetScene", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1}},"required":["lineCode"],"additionalProperties":false}
        """, 70, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardRuntimeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetSceneLayoutAsync(ProductionDashboardAgentTools.DashboardCode, arguments.LineCode);
        return result == null
            ? AgentBusinessToolResult.Fail("该线体尚未保存 Three.js 场景布局")
            : AgentBusinessToolResult.Ok(result, "场景布局查询成功");
    }
}

public sealed class ProductionDashboardBindingsAgentToolAction : AgentToolActionBase<ProductionDashboardRuntimeArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardBindingsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.bindings", "查询机械手绑定", "查询指定线体机械手的数据源、展示字段和运行状态规则。",
        "GetBindings", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1}},"required":["lineCode"],"additionalProperties":false}
        """, 80, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardRuntimeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetBindingsAsync(arguments.LineCode), "机械手绑定查询成功");
}

public sealed class ProductionDashboardDataSourcesAgentToolAction : AgentToolActionBase<ProductionDashboardDataSourceArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardDataSourcesAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.data-sources", "查询可绑定数据源", "按设备标识查询可绑定到机械手的 HTTP 最新数据源。",
        "GetDataSources", """
        {"type":"object","properties":{"keyword":{"type":"string"}},"additionalProperties":false}
        """, 90, "{\"keyword\":\"W1-R-01\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardDataSourceArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetDataSourcesAsync(arguments.Keyword), "可绑定数据源查询成功");
}

public sealed class ProductionDashboardSourceFieldsAgentToolAction : AgentToolActionBase<ProductionDashboardSourceFieldsArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSourceFieldsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.source-fields", "查询数据源字段", "查询指定数据源当前报文中的可绑定叶子字段。",
        "GetSourceFields", """
        {"type":"object","properties":{"tenantMark":{"type":"string","minLength":1},"deviceMark":{"type":"string","minLength":1}},"required":["tenantMark","deviceMark"],"additionalProperties":false}
        """, 100, "{\"tenantMark\":\"default\",\"deviceMark\":\"W1-R-01\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardSourceFieldsArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetSourceFieldsAsync(arguments.TenantMark, arguments.DeviceMark),
            "数据源字段查询成功");
}

public sealed class ProductionDashboardDeviceBindingsAgentToolAction : AgentToolActionBase<ProductionDashboardRuntimeArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardDeviceBindingsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.device-bindings", "查询设备绑定", "查询指定线体 Three.js 设备组件的数据源、在线判定和展示字段。",
        "GetDeviceBindings", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1}},"required":["lineCode"],"additionalProperties":false}
        """, 105, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardRuntimeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetDeviceBindingsAsync(arguments.LineCode), "设备绑定查询成功");
}

/// <summary>生产看板写入工具集合。调度器会先把这些调用变成待确认计划。</summary>
public sealed class ProductionDashboardSaveLineAgentToolAction : AgentToolActionBase<ProductionDashboardSaveLineArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSaveLineAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-line", "新增或修改线体", "新增线体，或修改已有线体的名称、区域、颜色、排序和启用状态；不提供删除。",
        "SaveLine", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1},"lineName":{"type":"string"},"area":{"type":"string"},"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"},"enabled":{"type":"boolean"},"sort":{"type":"integer"}},"required":["lineCode"],"additionalProperties":false}
        """, 200, "{\"lineCode\":\"W2\",\"lineName\":\"W2 自动化线\",\"area\":\"B 区\",\"color\":\"#55d5ff\"}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardSaveLineArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var bootstrap = await _service.GetBootstrapAsync(ProductionDashboardAgentTools.DashboardCode);
        var existing = bootstrap.Lines.FirstOrDefault(x =>
            x.LineCode.Equals(arguments.LineCode, StringComparison.OrdinalIgnoreCase));
        if (existing == null && string.IsNullOrWhiteSpace(arguments.LineName))
            return AgentBusinessToolResult.Fail("新增线体必须提供 lineName");

        var dto = new EdgeDashboardLineConfigSaveDto
        {
            Id = existing?.Id ?? 0,
            DashboardCode = ProductionDashboardAgentTools.DashboardCode,
            LineCode = arguments.LineCode,
            LineName = arguments.LineName?.Trim() ?? existing!.LineName,
            Area = arguments.Area?.Trim() ?? existing?.Area ?? string.Empty,
            Color = arguments.Color?.Trim() ?? existing?.Color ?? "#55d5ff",
            Enabled = arguments.Enabled ?? existing?.Enabled ?? true,
            Sort = arguments.Sort ?? existing?.Sort ?? 0
        };
        var saved = await _service.SaveLineConfigAsync(dto);
        return AgentBusinessToolResult.Ok(saved, existing == null ? "线体已新增" : "线体配置已修改");
    }
}

public sealed class ProductionDashboardSaveWidgetAgentToolAction : AgentToolActionBase<ProductionDashboardSaveWidgetArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSaveWidgetAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-widget", "新增或修改图表部件", "新增图表部件，或修改标题、数据源、字段和受控显示样式；不提供删除。",
        "SaveWidget", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"lineCode":{"type":"string"},"widgetCode":{"type":"string"},"title":{"type":"string"},"widgetType":{"type":"string","enum":["Table","Pie","Bar"]},"dataSourceType":{"type":"string","enum":["RealtimeRobot","DailyStatistics","FaultRecords","FaultSummary"]},"period":{"type":"string","enum":["Day","Week","Month"]},"refreshSeconds":{"type":"integer","minimum":5,"maximum":3600},"topN":{"type":"integer","minimum":1,"maximum":100},"stacked":{"type":"boolean"},"orientation":{"type":"string","enum":["Vertical","Horizontal"]},"sort":{"type":"integer"},"enabled":{"type":"boolean"},"columnSpan":{"type":"integer","enum":[3,4,6,8,9,12]},"rowSpan":{"type":"integer","minimum":1,"maximum":3},"tableRowHeight":{"type":"integer","minimum":32,"maximum":72},"autoScroll":{"type":"boolean"},"scrollIntervalSeconds":{"type":"integer","minimum":1,"maximum":30},"fields":{"type":"array","items":{"type":"object","properties":{"fieldKey":{"type":"string"},"displayName":{"type":"string"},"role":{"type":"string","enum":["Column","Category","Value"]},"aggregate":{"type":"string","enum":["None","Sum","Average","Count","Max","Min"]},"unit":{"type":"string"},"decimals":{"type":"integer","minimum":0,"maximum":8},"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"},"width":{"type":"integer","minimum":1,"maximum":100},"sort":{"type":"integer"},"visible":{"type":"boolean"}},"required":["fieldKey"]}}},"additionalProperties":false}
        """, 210, "{\"lineCode\":\"W1\",\"widgetCode\":\"W1-fault-summary\",\"title\":\"今日故障汇总\",\"widgetType\":\"Bar\",\"dataSourceType\":\"FaultSummary\",\"period\":\"Day\",\"fields\":[{\"fieldKey\":\"faultReason\",\"role\":\"Category\"},{\"fieldKey\":\"faultCount\",\"role\":\"Value\"}]}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardSaveWidgetArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var widgets = await _service.GetWidgetsAsync(ProductionDashboardAgentTools.DashboardCode, arguments.LineCode);
        var existing = arguments.Id.HasValue
            ? widgets.FirstOrDefault(x => x.Id == arguments.Id.Value)
            : !string.IsNullOrWhiteSpace(arguments.WidgetCode)
                ? widgets.FirstOrDefault(x => x.WidgetCode.Equals(arguments.WidgetCode, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(x.LineCode, arguments.LineCode, StringComparison.OrdinalIgnoreCase))
                : null;
        if (arguments.Id.HasValue && existing == null)
            return AgentBusinessToolResult.Fail("指定图表部件不存在");
        if (existing == null && (string.IsNullOrWhiteSpace(arguments.WidgetCode) ||
                                 string.IsNullOrWhiteSpace(arguments.Title) ||
                                 string.IsNullOrWhiteSpace(arguments.WidgetType) ||
                                 string.IsNullOrWhiteSpace(arguments.DataSourceType)))
            return AgentBusinessToolResult.Fail("新增图表部件必须提供 widgetCode、title、widgetType 和 dataSourceType");

        var dto = new EdgeDashboardWidgetSaveDto
        {
            Id = existing?.Id ?? 0,
            DashboardCode = ProductionDashboardAgentTools.DashboardCode,
            LineCode = arguments.LineCode ?? existing?.LineCode,
            WidgetCode = arguments.WidgetCode?.Trim() ?? existing!.WidgetCode,
            Title = arguments.Title?.Trim() ?? existing!.Title,
            WidgetType = arguments.WidgetType ?? existing?.WidgetType ?? "Table",
            DataSourceType = arguments.DataSourceType ?? existing?.DataSourceType ?? "DailyStatistics",
            Period = arguments.Period ?? existing?.Period ?? "Day",
            RefreshSeconds = arguments.RefreshSeconds ?? existing?.RefreshSeconds ?? 30,
            TopN = arguments.TopN ?? existing?.TopN ?? 10,
            Stacked = arguments.Stacked ?? existing?.Stacked ?? true,
            Orientation = arguments.Orientation ?? existing?.Orientation ?? "Vertical",
            Sort = arguments.Sort ?? existing?.Sort ?? 0,
            Enabled = arguments.Enabled ?? existing?.Enabled ?? true,
            ConfigJson = ProductionDashboardAgentTools.MergeWidgetLayout(existing?.ConfigJson, arguments),
            Fields = arguments.Fields == null
                ? existing?.Fields.Select(ProductionDashboardAgentTools.ToWidgetField).ToList() ?? []
                : arguments.Fields.Select(ProductionDashboardAgentTools.ToWidgetField).ToList()
        };
        var saved = await _service.SaveWidgetAsync(dto);
        return AgentBusinessToolResult.Ok(saved, existing == null ? "图表部件已新增" : "图表部件已修改");
    }
}

public sealed class ProductionDashboardSaveSceneItemAgentToolAction : AgentToolActionBase<ProductionDashboardSaveSceneItemArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSaveSceneItemAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-scene-item", "新增或修改场景组件", "在指定线体新增或修改机械手、设备、工位、输送线或合流分流组件的位置、名称、尺寸和颜色；不提供删除。",
        "SaveSceneItem", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1},"itemType":{"type":"string","enum":["robot","device","workCell","conveyor","junction"]},"itemId":{"type":"string","minLength":1},"create":{"type":"boolean"},"name":{"type":"string"},"station":{"type":"string"},"deviceType":{"type":"string","enum":["generic","machine","inspection"]},"junctionKind":{"type":"string","enum":["merge","split"]},"positionX":{"type":"number"},"positionY":{"type":"number"},"fromX":{"type":"number"},"fromY":{"type":"number"},"toX":{"type":"number"},"toY":{"type":"number"},"rotation":{"type":"number"},"width":{"type":"number","exclusiveMinimum":0},"height":{"type":"number","exclusiveMinimum":0},"depth":{"type":"number","exclusiveMinimum":0},"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"}},"required":["lineCode","itemType","itemId"],"additionalProperties":false}
        """, 220, "{\"lineCode\":\"W1\",\"itemType\":\"workCell\",\"itemId\":\"cell-05\",\"create\":true,\"name\":\"装配工位\",\"positionX\":3,\"positionY\":1,\"width\":3,\"depth\":1.5}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardSaveSceneItemArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var bootstrap = await _service.GetBootstrapAsync(ProductionDashboardAgentTools.DashboardCode);
        var line = bootstrap.Lines.FirstOrDefault(x => x.LineCode.Equals(arguments.LineCode, StringComparison.OrdinalIgnoreCase));
        if (line == null)
            return AgentBusinessToolResult.Fail("目标线体不存在，请先新增线体");
        var existing = await _service.GetSceneLayoutAsync(ProductionDashboardAgentTools.DashboardCode, arguments.LineCode);
        var layout = ProductionDashboardAgentTools.LoadOrCreateLayout(existing?.LayoutJson, line.LineCode, line.LineName);
        var changed = ProductionDashboardAgentTools.UpsertSceneItem(layout, arguments);
        if (!changed.Success) return AgentBusinessToolResult.Fail(changed.Message);

        var saved = await _service.SaveSceneLayoutAsync(new EdgeDashboardSceneLayoutSaveDto
        {
            DashboardCode = ProductionDashboardAgentTools.DashboardCode,
            LineCode = line.LineCode,
            LineName = line.LineName,
            LayoutVersion = 1,
            LayoutJson = layout.ToJsonString(ProductionDashboardAgentTools.JsonOptions)
        });
        return AgentBusinessToolResult.Ok(saved, arguments.Create ? "场景组件已新增" : "场景组件已修改");
    }
}

/// <summary>只调整已有 Three.js 场景组件的位置，避免移动操作误修改其他配置。</summary>
public sealed class ProductionDashboardMoveSceneItemAgentToolAction : AgentToolActionBase<ProductionDashboardMoveSceneItemArguments>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardMoveSceneItemAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.move-scene-item", "调整场景物体位置", "仅调整已有 Three.js 机械手、设备、工位、输送线或合流分流组件的位置和朝向；不新增、不删除，也不修改名称、尺寸、颜色或数据绑定。",
        "MoveSceneItem", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1},"itemType":{"type":"string","enum":["robot","device","workCell","conveyor","junction"]},"itemId":{"type":"string","minLength":1},"positionX":{"type":"number"},"positionY":{"type":"number"},"fromX":{"type":"number"},"fromY":{"type":"number"},"toX":{"type":"number"},"toY":{"type":"number"},"rotation":{"type":"number"}},"required":["lineCode","itemType","itemId"],"additionalProperties":false}
        """, 225, "{\"lineCode\":\"W1\",\"itemType\":\"robot\",\"itemId\":\"W1-R-01\",\"positionX\":4.5,\"positionY\":-1,\"rotation\":1.57}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardMoveSceneItemArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var bootstrap = await _service.GetBootstrapAsync(ProductionDashboardAgentTools.DashboardCode);
        var line = bootstrap.Lines.FirstOrDefault(x => x.LineCode.Equals(arguments.LineCode, StringComparison.OrdinalIgnoreCase));
        if (line == null) return AgentBusinessToolResult.Fail("目标线体不存在，请先新增线体");

        var existing = await _service.GetSceneLayoutAsync(ProductionDashboardAgentTools.DashboardCode, line.LineCode);
        if (existing == null) return AgentBusinessToolResult.Fail("该线体尚未保存 Three.js 场景布局");

        var layout = ProductionDashboardAgentTools.LoadOrCreateLayout(existing.LayoutJson, line.LineCode, line.LineName);
        var moved = ProductionDashboardAgentTools.MoveSceneItem(layout, arguments);
        if (!moved.Success) return AgentBusinessToolResult.Fail(moved.Message);

        var saved = await _service.SaveSceneLayoutAsync(new EdgeDashboardSceneLayoutSaveDto
        {
            DashboardCode = ProductionDashboardAgentTools.DashboardCode,
            LineCode = line.LineCode,
            LineName = line.LineName,
            LayoutVersion = 1,
            LayoutJson = layout.ToJsonString(ProductionDashboardAgentTools.JsonOptions)
        });
        return AgentBusinessToolResult.Ok(saved, "场景物体位置已调整");
    }
}

public sealed class ProductionDashboardSaveBindingAgentToolAction : AgentToolActionBase<EdgeDashboardRobotBindingSaveDto>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSaveBindingAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-binding", "新增或修改机械手绑定", "新增或修改机械手的数据源、展示字段和状态规则。修改前应先查询已有绑定和数据源字段；运行、空闲、故障三类启用状态规则均为必填；不提供删除。",
        "SaveBinding", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"lineCode":{"type":"string","minLength":1},"robotCode":{"type":"string","minLength":1},"robotName":{"type":"string","minLength":1},"tenantMark":{"type":"string","minLength":1},"deviceMark":{"type":"string","minLength":1},"onlineTimeoutSeconds":{"type":"integer","minimum":6,"maximum":3600},"businessDayStartHour":{"type":"integer","minimum":0,"maximum":23},"enabled":{"type":"boolean"},"fields":{"type":"array","items":{"type":"object","properties":{"fieldCode":{"type":"string"},"sourcePath":{"type":"string","pattern":"^/"},"displayName":{"type":"string"},"purpose":{"type":"string"},"valueType":{"type":"string"},"formatType":{"type":"string","enum":["Direct","ValueMap","UnixMilliseconds"]},"unit":{"type":"string"},"decimals":{"type":"integer","minimum":0,"maximum":8},"mappingJson":{"type":"string"},"sort":{"type":"integer"},"enabled":{"type":"boolean"}},"required":["fieldCode","sourcePath","displayName"]}},"stateRules":{"type":"array","items":{"type":"object","properties":{"ruleCode":{"type":"string"},"ruleName":{"type":"string"},"targetState":{"type":"string","enum":["RUNNING","IDLE","FAULT","STOPPED","OFFLINE","UNKNOWN","WARNING","ALARM","MAINTENANCE"]},"sourcePath":{"type":"string","pattern":"^/"},"operator":{"type":"string"},"compareValue":{"type":"string"},"faultCode":{"type":"string"},"faultReason":{"type":"string"},"faultReasonPath":{"type":"string","pattern":"^/"},"faultSeverity":{"type":"string"},"priority":{"type":"integer"},"enabled":{"type":"boolean"}},"required":["ruleCode","ruleName","targetState","sourcePath","operator"]}}},"required":["lineCode","robotCode","robotName","tenantMark","deviceMark","stateRules"],"additionalProperties":false}
        """, 230, "{\"lineCode\":\"W1\",\"robotCode\":\"W1-R-05\",\"robotName\":\"机械手 05\",\"tenantMark\":\"default\",\"deviceMark\":\"W1-R-05\",\"fields\":[],\"stateRules\":[]}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardRobotBindingSaveDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = ProductionDashboardAgentTools.DashboardCode;
        var saved = await _service.SaveBindingAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, arguments.Id == 0 ? "机械手绑定已新增" : "机械手绑定已修改");
    }
}

public sealed class ProductionDashboardSaveDeviceBindingAgentToolAction : AgentToolActionBase<EdgeDashboardDeviceBindingSaveDto>
{
    private readonly IEdgeDashboardService _service;
    public ProductionDashboardSaveDeviceBindingAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-device-binding", "新增或修改设备绑定", "把已有 Three.js 设备组件绑定到 HTTP 实时数据源，并配置只读展示字段。修改前必须先查询场景、设备绑定、数据源和字段；不提供删除。",
        "SaveDeviceBinding", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"lineCode":{"type":"string","minLength":1},"deviceCode":{"type":"string","minLength":1},"deviceName":{"type":"string"},"tenantMark":{"type":"string","minLength":1},"sourceDeviceMark":{"type":"string","minLength":1},"onlineTimeoutSeconds":{"type":"integer","minimum":6,"maximum":3600},"enabled":{"type":"boolean"},"fields":{"type":"array","items":{"type":"object","properties":{"fieldCode":{"type":"string"},"sourcePath":{"type":"string","pattern":"^/"},"displayName":{"type":"string"},"valueType":{"type":"string"},"formatType":{"type":"string","enum":["Direct","ValueMap","UnixMilliseconds"]},"unit":{"type":"string"},"decimals":{"type":"integer","minimum":0,"maximum":8},"mappingJson":{"type":"string"},"sort":{"type":"integer"},"enabled":{"type":"boolean"}},"required":["fieldCode","sourcePath","displayName"]}}},"required":["lineCode","deviceCode","tenantMark","sourceDeviceMark"],"additionalProperties":false}
        """, 240, "{\"lineCode\":\"W1\",\"deviceCode\":\"device-01\",\"tenantMark\":\"default\",\"sourceDeviceMark\":\"CNC-01\",\"fields\":[{\"fieldCode\":\"temperature\",\"sourcePath\":\"/data/temperature\",\"displayName\":\"温度\",\"unit\":\"℃\"}]}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardDeviceBindingSaveDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = ProductionDashboardAgentTools.DashboardCode;
        var saved = await _service.SaveDeviceBindingAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, arguments.Id == 0 ? "设备绑定已新增" : "设备绑定已修改");
    }
}

/// <summary>生产看板 Agent 的参数与 JSON 白名单处理。</summary>
internal static class ProductionDashboardAgentTools
{
    internal const string DashboardCode = "production-dashboard";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static EdgeDashboardWidgetFieldSaveDto ToWidgetField(ProductionDashboardWidgetFieldArguments source) => new()
    {
        FieldKey = source.FieldKey,
        DisplayName = source.DisplayName ?? source.FieldKey,
        Role = source.Role ?? "Column",
        Aggregate = source.Aggregate ?? "None",
        Unit = source.Unit,
        Decimals = source.Decimals,
        Color = source.Color,
        Width = source.Width,
        Sort = source.Sort ?? 0,
        Visible = source.Visible ?? true
    };

    internal static EdgeDashboardWidgetFieldSaveDto ToWidgetField(EdgeDashboardWidgetFieldSaveDto source) => new()
    {
        Id = source.Id,
        FieldKey = source.FieldKey,
        DisplayName = source.DisplayName,
        Role = source.Role,
        Aggregate = source.Aggregate,
        Unit = source.Unit,
        Decimals = source.Decimals,
        Color = source.Color,
        Width = source.Width,
        Sort = source.Sort,
        Visible = source.Visible
    };

    internal static string MergeWidgetLayout(string? currentConfig, ProductionDashboardSaveWidgetArguments arguments)
    {
        var root = string.IsNullOrWhiteSpace(currentConfig)
            ? new JsonObject()
            : JsonNode.Parse(currentConfig) as JsonObject ?? new JsonObject();
        var layout = root["layout"] as JsonObject ?? new JsonObject();
        root["layout"] = layout;
        SetIfPresent(layout, "columnSpan", arguments.ColumnSpan);
        SetIfPresent(layout, "rowSpan", arguments.RowSpan);
        SetIfPresent(layout, "tableRowHeight", arguments.TableRowHeight);
        SetIfPresent(layout, "autoScroll", arguments.AutoScroll);
        SetIfPresent(layout, "scrollIntervalSeconds", arguments.ScrollIntervalSeconds);
        return root.ToJsonString(JsonOptions);
    }

    internal static JsonObject LoadOrCreateLayout(string? source, string lineCode, string lineName)
    {
        var root = string.IsNullOrWhiteSpace(source)
            ? new JsonObject()
            : JsonNode.Parse(source) as JsonObject ?? throw new InvalidOperationException("场景布局不是 JSON 对象");
        root["version"] = 1;
        root["lineId"] = lineCode;
        root["lineName"] = lineName;
        root["updatedAt"] = DateTime.UtcNow.ToString("O");
        foreach (var property in new[] { "robots", "devices", "conveyors", "workCells", "junctions" })
            root[property] ??= new JsonArray();
        return root;
    }

    internal static (bool Success, string Message) UpsertSceneItem(
        JsonObject layout,
        ProductionDashboardSaveSceneItemArguments arguments)
    {
        var collectionName = arguments.ItemType switch
        {
            "robot" => "robots",
            "device" => "devices",
            "workCell" => "workCells",
            "conveyor" => "conveyors",
            "junction" => "junctions",
            _ => string.Empty
        };
        if (collectionName.Length == 0) return (false, "不支持的场景组件类型");
        var collection = layout[collectionName] as JsonArray;
        if (collection == null) return (false, "场景组件集合无效");
        var item = collection.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["id"]?.GetValue<string>(), arguments.ItemId, StringComparison.OrdinalIgnoreCase));
        if (arguments.Create)
        {
            if (item != null) return (false, "该组件编码已存在");
            item = CreateSceneItem(arguments);
            collection.Add(item);
        }
        else if (item == null)
        {
            return (false, "未找到需要修改的场景组件");
        }

        ApplySceneItemPatch(item, arguments);
        return (true, "ok");
    }

    internal static (bool Success, string Message) MoveSceneItem(
        JsonObject layout,
        ProductionDashboardMoveSceneItemArguments arguments)
    {
        var collectionName = arguments.ItemType switch
        {
            "robot" => "robots",
            "device" => "devices",
            "workCell" => "workCells",
            "conveyor" => "conveyors",
            "junction" => "junctions",
            _ => string.Empty
        };
        if (collectionName.Length == 0) return (false, "不支持的场景组件类型");
        var collection = layout[collectionName] as JsonArray;
        if (collection == null) return (false, "场景组件集合无效");
        var item = collection.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["id"]?.GetValue<string>(), arguments.ItemId, StringComparison.OrdinalIgnoreCase));
        if (item == null) return (false, "未找到需要调整位置的场景组件");

        if (arguments.ItemType == "conveyor")
        {
            if (!arguments.FromX.HasValue && !arguments.FromY.HasValue && !arguments.ToX.HasValue && !arguments.ToY.HasValue)
                return (false, "调整输送线位置至少需要提供起点或终点坐标");
            SetCoordinateIfPresent(item, "from", arguments.FromX, arguments.FromY);
            SetCoordinateIfPresent(item, "to", arguments.ToX, arguments.ToY);
            return (true, "ok");
        }

        if (!arguments.PositionX.HasValue && !arguments.PositionY.HasValue && !arguments.Rotation.HasValue)
            return (false, "至少需要提供一个位置坐标或旋转角度");
        SetCoordinateIfPresent(item, "position", arguments.PositionX, arguments.PositionY);
        if (arguments.ItemType is "robot" or "device" or "workCell")
            SetIfPresent(item, "rotation", arguments.Rotation);
        else if (arguments.Rotation.HasValue)
            return (false, "合流分流组件不支持旋转角度调整");
        return (true, "ok");
    }

    private static JsonObject CreateSceneItem(ProductionDashboardSaveSceneItemArguments value)
    {
        if (string.IsNullOrWhiteSpace(value.Name))
            throw new InvalidOperationException("新增场景组件必须提供 name");
        var item = new JsonObject { ["id"] = value.ItemId, ["name"] = value.Name.Trim() };
        switch (value.ItemType)
        {
            case "robot":
                item["station"] = value.Station ?? "新建工位";
                item["status"] = "OFFLINE";
                item["position"] = Coordinate(value.PositionX, value.PositionY);
                item["rotation"] = value.Rotation ?? 0;
                item["sourcePoint"] = Coordinate(0, 0);
                item["today"] = new JsonObject { ["runningMinutes"] = 0, ["stoppedMinutes"] = 0, ["alarmMinutes"] = 0 };
                item["bindings"] = new JsonObject { ["deviceId"] = "", ["status"] = "", ["alarmCode"] = "", ["runningMinutes"] = "", ["stoppedMinutes"] = "" };
                break;
            case "device":
                item["deviceType"] = value.DeviceType ?? "generic";
                item["position"] = Coordinate(value.PositionX, value.PositionY);
                item["rotation"] = value.Rotation ?? 0;
                item["width"] = value.Width ?? 1.4m;
                item["height"] = value.Height ?? 1.5m;
                item["depth"] = value.Depth ?? 1.1m;
                item["color"] = value.Color ?? "#4db7d8";
                break;
            case "workCell":
                item["position"] = Coordinate(value.PositionX, value.PositionY);
                item["width"] = value.Width ?? 3m;
                item["depth"] = value.Depth ?? 1.5m;
                item["rotation"] = value.Rotation ?? 0;
                break;
            case "conveyor":
                item["from"] = Coordinate(value.FromX, value.FromY);
                item["to"] = Coordinate(value.ToX, value.ToY);
                item["width"] = value.Width ?? 0.8m;
                break;
            case "junction":
                item["kind"] = value.JunctionKind ?? "split";
                item["position"] = Coordinate(value.PositionX, value.PositionY);
                break;
        }
        return item;
    }

    private static void ApplySceneItemPatch(JsonObject item, ProductionDashboardSaveSceneItemArguments value)
    {
        SetIfPresent(item, "name", value.Name?.Trim());
        switch (value.ItemType)
        {
            case "robot":
                SetIfPresent(item, "station", value.Station?.Trim());
                SetCoordinateIfPresent(item, "position", value.PositionX, value.PositionY);
                SetIfPresent(item, "rotation", value.Rotation);
                break;
            case "device":
                SetIfPresent(item, "deviceType", value.DeviceType);
                SetCoordinateIfPresent(item, "position", value.PositionX, value.PositionY);
                SetIfPresent(item, "rotation", value.Rotation);
                SetIfPresent(item, "width", value.Width);
                SetIfPresent(item, "height", value.Height);
                SetIfPresent(item, "depth", value.Depth);
                SetIfPresent(item, "color", value.Color);
                break;
            case "workCell":
                SetCoordinateIfPresent(item, "position", value.PositionX, value.PositionY);
                SetIfPresent(item, "rotation", value.Rotation);
                SetIfPresent(item, "width", value.Width);
                SetIfPresent(item, "depth", value.Depth);
                break;
            case "conveyor":
                SetCoordinateIfPresent(item, "from", value.FromX, value.FromY);
                SetCoordinateIfPresent(item, "to", value.ToX, value.ToY);
                SetIfPresent(item, "width", value.Width);
                break;
            case "junction":
                SetIfPresent(item, "kind", value.JunctionKind);
                SetCoordinateIfPresent(item, "position", value.PositionX, value.PositionY);
                break;
        }
    }

    private static JsonArray Coordinate(decimal? x, decimal? y) => new(x ?? 0, y ?? 0);

    private static void SetCoordinateIfPresent(JsonObject item, string property, decimal? x, decimal? y)
    {
        if (!x.HasValue && !y.HasValue) return;
        var old = item[property] as JsonArray;
        var oldX = old?.Count > 0 && old[0] != null ? old[0]!.GetValue<decimal>() : 0;
        var oldY = old?.Count > 1 && old[1] != null ? old[1]!.GetValue<decimal>() : 0;
        item[property] = Coordinate(x ?? oldX, y ?? oldY);
    }

    private static void SetIfPresent<T>(JsonObject target, string property, T? value) where T : struct
    {
        if (value.HasValue) target[property] = JsonValue.Create(value.Value);
    }

    private static void SetIfPresent(JsonObject target, string property, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[property] = value;
    }
}

public sealed class ProductionDashboardDataSourceArguments
{
    public string? Keyword { get; set; }
}

public sealed class ProductionDashboardSourceFieldsArguments
{
    public string TenantMark { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
}
