using Interface;
using Model.Agent;
using Model.Agent.ToolArguments;
using Model.EdgeGateway;

namespace Service;

/// <summary>设备卡片看板的查询工具。复用生产看板 Agent，避免为同一业务域创建第二个 Agent。</summary>
public sealed class ProductionDashboardDeviceCardRuntimeAgentToolAction
    : AgentToolActionBase<ProductionDashboardScopeArguments>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardDeviceCardRuntimeAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.device-card-runtime", "查询设备卡片实时状态",
        "查询设备卡片看板的设备总览、在线/故障/告警状态、最后接收时间和展示字段。",
        "GetDeviceCardRuntime", """
        {"type":"object","properties":{"lineCode":{"type":"string"}},"additionalProperties":false}
        """, 110, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardScopeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetDeviceCardBoardRuntimeAsync(arguments.LineCode),
            "设备卡片实时状态查询成功");
}

public sealed class ProductionDashboardDeviceCardsAgentToolAction
    : AgentToolActionBase<ProductionDashboardScopeArguments>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardDeviceCardsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.device-cards", "查询设备卡片配置",
        "查询设备卡片、字段绑定、状态映射、字段追溯和 AI 分析开关。",
        "GetDeviceCards", """
        {"type":"object","properties":{"lineCode":{"type":"string"}},"additionalProperties":false}
        """, 115, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardScopeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetDeviceCardsAsync(arguments.LineCode), "设备卡片配置查询成功");
}

public sealed class ProductionDashboardDeviceCardHistoryAgentToolAction
    : AgentToolActionBase<EdgeDashboardDeviceCardChangeQueryDto>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardDeviceCardHistoryAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.device-card-history", "查询设备字段变更追溯",
        "按设备卡片、线体、设备标识、字段和时间范围查询已启用追溯的字段变更记录。",
        "GetDeviceCardHistory", """
        {"type":"object","properties":{"cardId":{"type":"integer","minimum":1},"lineCode":{"type":"string"},"deviceMark":{"type":"string"},"fieldCode":{"type":"string"},"dateFrom":{"type":"string"},"dateTo":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 120, "{\"lineCode\":\"W1\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardDeviceCardChangeQueryDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.Current = Math.Max(arguments.Current, 1);
        arguments.Size = Math.Clamp(arguments.Size, 1, 100);
        var result = await _service.GetDeviceCardChangeRecordsAsync(arguments);
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条设备字段变更记录");
    }
}

/// <summary>将卡片配置与实时返回结果比对，优先输出可排查的配置或数据时效问题。</summary>
public sealed class ProductionDashboardDeviceCardHealthAgentToolAction
    : AgentToolActionBase<ProductionDashboardScopeArguments>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardDeviceCardHealthAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.device-card-health", "检查设备卡片数据健康度",
        "比对设备卡片配置与实时快照，定位未启用、无展示字段、实时数据超时或未返回的卡片。",
        "CheckDeviceCardHealth", """
        {"type":"object","properties":{"lineCode":{"type":"string"}},"additionalProperties":false}
        """, 125, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardScopeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var cards = await _service.GetDeviceCardsAsync(arguments.LineCode);
        var runtime = await _service.GetDeviceCardBoardRuntimeAsync(arguments.LineCode);
        var runtimeById = runtime.Cards.ToDictionary(item => item.CardId);
        var items = cards.Select(card =>
        {
            var problems = new List<string>();
            runtimeById.TryGetValue(card.Id, out var current);
            if (!card.Enabled) problems.Add("卡片已停用");
            if (card.Fields.Count == 0) problems.Add("未配置展示字段");
            if (card.Enabled && current == null) problems.Add("未返回实时卡片，请核对线体和数据源绑定");
            if (current != null && !current.DataOnline) problems.Add("实时数据已超时或暂无快照");
            return new
            {
                card.Id,
                card.LineCode,
                card.CardName,
                card.DeviceMark,
                healthy = problems.Count == 0,
                status = current?.Status,
                receiveTime = current?.ReceiveTime,
                problems
            };
        }).ToList();
        return AgentBusinessToolResult.Ok(new { total = items.Count, unhealthy = items.Count(x => !x.healthy), items },
            "设备卡片数据健康检查完成");
    }
}

/// <summary>设备卡片的受控写入工具；保存前由 Agent 调度器等待用户文字确认。</summary>
public sealed class ProductionDashboardSaveDeviceCardAgentToolAction
    : AgentToolActionBase<EdgeDashboardDeviceCardSaveDto>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardSaveDeviceCardAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-device-card", "新增或修改设备卡片",
        "新增或修改设备卡片、数据源、状态映射、展示字段、追溯开关和 AI 分析字段；不提供删除。",
        "SaveDeviceCard", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"lineCode":{"type":"string","minLength":1},"cardCode":{"type":"string","minLength":1},"cardName":{"type":"string","minLength":1},"tenantMark":{"type":"string","minLength":1},"deviceMark":{"type":"string","minLength":1},"modelMark":{"type":"string"},"statusSourcePath":{"type":"string"},"statusMappingJson":{"type":"string"},"onlineTimeoutSeconds":{"type":"integer","minimum":5,"maximum":3600},"sort":{"type":"integer"},"enabled":{"type":"boolean"},"fields":{"type":"array","items":{"type":"object","properties":{"fieldCode":{"type":"string","minLength":1},"sourcePath":{"type":"string","pattern":"^/"},"displayName":{"type":"string","minLength":1},"displayMode":{"type":"string","enum":["Primary","Tag","Detail"]},"valueType":{"type":"string"},"formatType":{"type":"string","enum":["Direct","ValueMap","UnixMilliseconds"]},"unit":{"type":"string"},"decimals":{"type":"integer","minimum":0,"maximum":8},"mappingJson":{"type":"string"},"traceEnabled":{"type":"boolean"},"traceThreshold":{"type":"number","minimum":0},"aiAnalysisEnabled":{"type":"boolean"},"sort":{"type":"integer"},"enabled":{"type":"boolean"}},"required":["fieldCode","sourcePath","displayName"]}}},"required":["lineCode","cardCode","cardName","tenantMark","deviceMark","fields"],"additionalProperties":false}
        """, 245, "{\"lineCode\":\"W1\",\"cardCode\":\"CNC-01\",\"cardName\":\"加工中心 01\",\"tenantMark\":\"default\",\"deviceMark\":\"CNC-01\",\"fields\":[{\"fieldCode\":\"status\",\"sourcePath\":\"/data/status\",\"displayName\":\"运行状态\",\"displayMode\":\"Primary\",\"aiAnalysisEnabled\":true}]}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardDeviceCardSaveDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = "device-card-dashboard";
        var saved = await _service.SaveDeviceCardAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, arguments.Id == 0 ? "设备卡片已新增" : "设备卡片已修改");
    }
}

public sealed class ProductionDashboardSaveDeviceCardBoardSettingAgentToolAction
    : AgentToolActionBase<EdgeDashboardDeviceCardBoardSettingDto>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardSaveDeviceCardBoardSettingAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-device-card-board-setting", "调整设备卡片布局",
        "调整指定线体设备卡片看板每行的卡片数量，仅支持 1 到 6 列。",
        "SaveDeviceCardBoardSetting", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1},"columnCount":{"type":"integer","minimum":1,"maximum":6}},"required":["lineCode","columnCount"],"additionalProperties":false}
        """, 250, "{\"lineCode\":\"W1\",\"columnCount\":4}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardDeviceCardBoardSettingDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = "device-card-dashboard";
        var saved = await _service.SaveDeviceCardBoardSettingAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, "设备卡片布局已保存");
    }
}

/// <summary>补齐生产监控 Agent 的产量和型号映射配置能力。</summary>
public sealed class ProductionDashboardLineOutputAgentToolAction
    : AgentToolActionBase<ProductionDashboardRuntimeArguments>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardLineOutputAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.line-output", "查询线体产量", "查询指定线体的产量绑定、当前值和当日累计产出。",
        "GetLineOutput", """
        {"type":"object","properties":{"lineCode":{"type":"string","minLength":1}},"required":["lineCode"],"additionalProperties":false}
        """, 55, "{\"lineCode\":\"W1\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        ProductionDashboardRuntimeArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetLineOutputAsync(arguments.LineCode), "线体产量查询成功");
}

public sealed class ProductionDashboardSaveLineOutputAgentToolAction
    : AgentToolActionBase<EdgeDashboardLineOutputBindingSaveDto>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardSaveLineOutputAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-line-output", "配置线体产量来源",
        "配置线体产量来源设备、字段、计数方式、单位和小数位；不删除历史产量记录。",
        "SaveLineOutput", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"lineCode":{"type":"string","minLength":1},"tenantMark":{"type":"string","minLength":1},"deviceMark":{"type":"string","minLength":1},"sourcePath":{"type":"string","pattern":"^/"},"fieldName":{"type":"string"},"counterMode":{"type":"string","enum":["Cumulative","CurrentValue"]},"unit":{"type":"string"},"decimals":{"type":"integer","minimum":0,"maximum":8},"enabled":{"type":"boolean"}},"required":["lineCode","tenantMark","deviceMark","sourcePath"],"additionalProperties":false}
        """, 235, "{\"lineCode\":\"W1\",\"tenantMark\":\"default\",\"deviceMark\":\"CNC-01\",\"sourcePath\":\"/data/quantity\",\"fieldName\":\"累计产量\",\"counterMode\":\"Cumulative\"}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardLineOutputBindingSaveDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = ProductionDashboardAgentTools.DashboardCode;
        var saved = await _service.SaveLineOutputBindingAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, "线体产量来源已保存");
    }
}

public sealed class ProductionDashboardSaveModelDisplayMappingAgentToolAction
    : AgentToolActionBase<EdgeDashboardModelDisplayMappingSaveDto>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardSaveModelDisplayMappingAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.save-model-display-mapping", "新增或修改设备型号名称映射",
        "把接入侧模型标识映射为生产看板中的业务设备名称和类别；不提供删除。",
        "SaveModelDisplayMapping", """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"modelMark":{"type":"string","minLength":1},"displayName":{"type":"string","minLength":1},"equipmentType":{"type":"string","enum":["Robot","Device","Machine","Inspection"]},"sort":{"type":"integer"},"enabled":{"type":"boolean"}},"required":["modelMark","displayName"],"additionalProperties":false}
        """, 230, "{\"modelMark\":\"CHUANPXX_JQR\",\"displayName\":\"穿片下线机器人\",\"equipmentType\":\"Robot\"}", true);

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDashboardModelDisplayMappingSaveDto arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        arguments.DashboardCode = ProductionDashboardAgentTools.DashboardCode;
        var saved = await _service.SaveModelDisplayMappingAsync(arguments);
        return AgentBusinessToolResult.Ok(saved, arguments.Id == 0 ? "设备型号名称映射已新增" : "设备型号名称映射已修改");
    }
}

public sealed class ProductionDashboardModelDisplayMappingsAgentToolAction
    : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeDashboardService _service;

    public ProductionDashboardModelDisplayMappingsAgentToolAction(IEdgeDashboardService service) => _service = service;

    public override AgentToolSeed Definition { get; } = ProductionDashboardAgentToolSeedFactory.Create(
        "dashboard.model-display-mappings", "查询设备型号名称映射",
        "查询接入侧模型标识到业务设备名称和推荐类别的映射。",
        "GetModelDisplayMappings", ProductionDashboardAgentToolSeedFactory.EmptySchema, 57, "{}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.GetModelDisplayMappingsAsync(ProductionDashboardAgentTools.DashboardCode),
            "设备型号名称映射查询成功");
}
