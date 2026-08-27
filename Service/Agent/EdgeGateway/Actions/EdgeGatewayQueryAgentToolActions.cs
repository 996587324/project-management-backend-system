using AutoMapper;
using Interface;
using Model.Agent;
using Model.Agent.ToolArguments;
using Model.EdgeGateway;

namespace Service;

/// <summary>查询边缘计算中心的设备、连接、队列和转发概况。</summary>
public sealed class EdgeOverviewAgentToolAction : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeGatewayService _service;
    public EdgeOverviewAgentToolAction(IEdgeGatewayService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.overview.get", "运行概况", "查询连接接收、消息队列、告警和 B 系统转发汇总。",
        "GetOverview", EdgeGatewayAgentToolSeedFactory.EmptySchema, 10, "{}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetOverviewAsync(), "边缘计算中心运行概况查询成功");
}

/// <summary>查询边缘计算中心全局功能开关。</summary>
public sealed class EdgeFeatureSettingAgentToolAction : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeGatewayFeatureService _service;
    public EdgeFeatureSettingAgentToolAction(IEdgeGatewayFeatureService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.feature.get", "功能开关", "查询边缘计算中心当前是否启用以及最近更新时间。",
        "GetFeatureSetting", EdgeGatewayAgentToolSeedFactory.EmptySchema, 20, "{}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(await _service.GetSettingAsync(), "边缘计算中心功能设置查询成功");
}

/// <summary>分页查询边缘设备。</summary>
public sealed class EdgeDeviceListAgentToolAction : AgentToolActionBase<EdgeDeviceListArguments>
{
    private readonly IEdgeGatewayService _service;
    private readonly IMapper _mapper;
    public EdgeDeviceListAgentToolAction(IEdgeGatewayService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.device.list", "查询边缘设备", "按关键字、启停状态或在线状态分页查询边缘设备。",
        "GetDevices", """
        {"type":"object","properties":{"keyword":{"type":"string"},"enabled":{"type":"boolean"},"onlineStatus":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 30, "{\"keyword\":\"W1\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDeviceListArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetDevicePageAsync(_mapper.Map<EdgeDeviceQueryDto>(arguments));
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 个边缘设备");
    }
}

/// <summary>查询指定边缘设备的 WebSocket 连接摘要。</summary>
public sealed class EdgeConnectionListAgentToolAction : AgentToolActionBase<EdgeConnectionListArguments>
{
    private readonly IEdgeGatewayService _gatewayService;
    private readonly IEdgeWebSocketSourceService _sourceService;
    private readonly IMapper _mapper;
    public EdgeConnectionListAgentToolAction(
        IEdgeGatewayService gatewayService,
        IEdgeWebSocketSourceService sourceService,
        IMapper mapper)
    {
        _gatewayService = gatewayService;
        _sourceService = sourceService;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.connection.list", "查询连接配置", "按设备编码、启停状态或最近数据接收状态查询 WebSocket 连接，不返回连接密钥。",
        "GetConnections", """
        {"type":"object","properties":{"deviceId":{"type":"integer","minimum":1},"deviceCode":{"type":"string"},"enabled":{"type":"boolean"},"dataOnline":{"type":"boolean"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 40, "{\"dataOnline\":false,\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeConnectionListArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var deviceIds = new List<int>();
        if (arguments.DeviceId.HasValue)
        {
            deviceIds.Add(arguments.DeviceId.Value);
        }
        else
        {
            var deviceQuery = _mapper.Map<EdgeDeviceQueryDto>(arguments);
            // 一次最多解析 100 个中心设备，连接结果再按照工具自己的 current/size 分页。
            deviceQuery.Current = 1;
            deviceQuery.Size = 100;
            var devices = await _gatewayService.GetDevicePageAsync(deviceQuery);
            deviceIds.AddRange(devices.Records.Select(x => x.Id));
        }

        var sources = new List<EdgeWebSocketSourceOutputDto>();
        foreach (var deviceId in deviceIds.Distinct())
            sources.AddRange(await _sourceService.GetByDeviceAsync(deviceId));

        var filtered = sources.AsEnumerable();
        if (arguments.Enabled.HasValue)
            filtered = filtered.Where(x => x.Enabled == arguments.Enabled.Value);
        if (arguments.DataOnline.HasValue)
            filtered = filtered.Where(x => x.DataOnline == arguments.DataOnline.Value);

        var rows = _mapper.Map<List<EdgeAgentWebSocketSourceSummaryDto>>(filtered.ToList());
        var page = Math.Max(arguments.Current, 1);
        var size = Math.Clamp(arguments.Size, 1, 100);
        var result = new EdgePagedResultDto<EdgeAgentWebSocketSourceSummaryDto>
        {
            Records = rows.Skip((page - 1) * size).Take(size).ToList(),
            Current = page,
            Size = size,
            Total = rows.Count
        };
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 个 WebSocket 连接");
    }
}

/// <summary>分页查询中心消息队列。</summary>
public sealed class EdgeQueueListAgentToolAction : AgentToolActionBase<EdgeQueueListArguments>
{
    private readonly IEdgeGatewayService _service;
    private readonly IMapper _mapper;
    public EdgeQueueListAgentToolAction(IEdgeGatewayService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.queue.list", "查询消息队列", "按设备编码、消息编号或状态分页查询中心消息队列。",
        "GetQueue", """
        {"type":"object","properties":{"deviceCode":{"type":"string"},"status":{"type":"string","enum":["Pending","Failed","Uploaded"]},"messageId":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 50, "{\"status\":\"Failed\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeQueueListArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetQueuePageAsync(_mapper.Map<EdgeQueueQueryDto>(arguments));
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条队列记录");
    }
}

/// <summary>分页查询 B 系统上传结果。</summary>
public sealed class EdgeUploadRecordListAgentToolAction : AgentToolActionBase<EdgeUploadRecordListArguments>
{
    private readonly IEdgeGatewayService _service;
    private readonly IMapper _mapper;
    public EdgeUploadRecordListAgentToolAction(IEdgeGatewayService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.upload.list", "查询上传记录", "按队列、设备、消息编号或成功状态查询 B 系统上传结果和失败原因。",
        "GetUploadRecords", """
        {"type":"object","properties":{"queueId":{"type":"integer","minimum":1},"deviceCode":{"type":"string"},"messageId":{"type":"string"},"success":{"type":"boolean"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 60, "{\"success\":false,\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeUploadRecordListArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetUploadRecordPageAsync(_mapper.Map<EdgeUploadRecordQueryDto>(arguments));
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条上传记录");
    }
}

/// <summary>分页查询边缘运行事件。</summary>
public sealed class EdgeEventListAgentToolAction : AgentToolActionBase<EdgeEventListArguments>
{
    private readonly IEdgeGatewayService _service;
    private readonly IMapper _mapper;
    public EdgeEventListAgentToolAction(IEdgeGatewayService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.event.list", "查询运行事件", "按设备、日志级别或事件类型查询最近七天的边缘运行事件。",
        "GetEvents", """
        {"type":"object","properties":{"deviceCode":{"type":"string"},"level":{"type":"string"},"eventType":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """, 70, "{\"level\":\"Error\",\"current\":1,\"size\":20}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeEventListArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.GetEventPageAsync(_mapper.Map<EdgeEventQueryDto>(arguments));
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 条运行事件");
    }
}

/// <summary>查询独立 HTTP 实时数据的设备最新快照。</summary>
public sealed class EdgeRealtimeLatestAgentToolAction : AgentToolActionBase<EdgeRealtimeLatestArguments>
{
    private readonly IEdgeRealtimeDataService _service;
    public EdgeRealtimeLatestAgentToolAction(IEdgeRealtimeDataService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.realtime.latest", "查询实时快照", "按租户和设备标识查询独立 HTTP 接收链路的最新数据快照。",
        "GetRealtimeLatest", """
        {"type":"object","properties":{"tenantMark":{"type":"string","minLength":1},"deviceCode":{"type":"string","minLength":1}},"required":["tenantMark","deviceCode"],"additionalProperties":false}
        """, 80, "{\"tenantMark\":\"geek\",\"deviceCode\":\"PUNCH_13\"}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeRealtimeLatestArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var snapshot = await _service.GetLatestAsync(arguments.TenantMark.Trim(), arguments.DeviceCode.Trim());
        return snapshot == null
            ? AgentBusinessToolResult.Fail("未找到该租户和设备的实时数据快照")
            : AgentBusinessToolResult.Ok(snapshot, "实时数据快照查询成功");
    }
}

/// <summary>查询不包含密钥的 B 系统映射摘要。</summary>
public sealed class EdgeBMappingListAgentToolAction : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeGatewayService _service;
    private readonly IMapper _mapper;
    public EdgeBMappingListAgentToolAction(IEdgeGatewayService service, IMapper mapper)
    {
        _service = service;
        _mapper = mapper;
    }

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.mapping.list", "查询 B 系统映射", "查询设备、数据源和子设备对应的 B 系统目标摘要，不返回 Token 或密码。",
        "GetBMappings", EdgeGatewayAgentToolSeedFactory.EmptySchema, 90, "{}");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var mappings = await _service.GetBMappingRulesAsync();
        var result = _mapper.Map<List<EdgeAgentBMappingSummaryDto>>(mappings);
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Count} 条 B 系统映射");
    }
}
