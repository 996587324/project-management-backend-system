using Interface;
using Model.Agent;
using Model.Agent.ToolArguments;
using Model.EdgeGateway;

namespace Service;

/// <summary>为单个边缘设备下发 WebSocket 重连版本。</summary>
public sealed class EdgeReconnectDeviceAgentToolAction : AgentToolActionBase<EdgeDeviceIdArguments>
{
    private readonly IEdgeWebSocketSourceService _service;
    public EdgeReconnectDeviceAgentToolAction(IEdgeWebSocketSourceService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.connection.reconnect-device", "重新连接单个设备", "让指定边缘设备重新建立全部已启用的 WebSocket 连接，执行前必须确认。",
        "ReconnectDevice", """
        {"type":"object","properties":{"deviceId":{"type":"integer","minimum":1}},"required":["deviceId"],"additionalProperties":false}
        """, 200, "{\"deviceId\":1}", true, true, "Medium");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeDeviceIdArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.ReconnectDeviceAsync(arguments.DeviceId),
            "设备 WebSocket 重新连接指令已下发");
}

/// <summary>为全部边缘设备下发 WebSocket 重连版本。</summary>
public sealed class EdgeReconnectAllAgentToolAction : AgentToolActionBase<EmptyAgentToolArguments>
{
    private readonly IEdgeWebSocketSourceService _service;
    public EdgeReconnectAllAgentToolAction(IEdgeWebSocketSourceService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.connection.reconnect-all", "重新连接全部设备", "让全部设备重新建立已启用的 WebSocket 连接，影响范围较大，执行前必须确认。",
        "ReconnectAll", EdgeGatewayAgentToolSeedFactory.EmptySchema, 210, "{}", true, true, "High");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EmptyAgentToolArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken) =>
        AgentBusinessToolResult.Ok(
            await _service.ReconnectAllAsync(),
            "全部 WebSocket 重新连接指令已下发");
}

/// <summary>把失败队列记录重置为待处理。</summary>
public sealed class EdgeQueueRetryAgentToolAction : AgentToolActionBase<EdgeQueueIdsArguments>
{
    private readonly IEdgeGatewayService _service;
    public EdgeQueueRetryAgentToolAction(IEdgeGatewayService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.queue.retry", "重试失败队列", "把指定失败队列记录重置为待处理，执行前必须确认。",
        "RetryQueue", """
        {"type":"object","properties":{"ids":{"type":"array","minItems":1,"items":{"type":"integer","minimum":1}}},"required":["ids"],"additionalProperties":false}
        """, 220, "{\"ids\":[101,102]}", true, true, "Medium");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeQueueIdsArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var count = await _service.ResetQueueAsync(new EdgeRetryRequestDto { Ids = arguments.Ids });
        return AgentBusinessToolResult.Ok(new { affectedCount = count }, $"已重置 {count} 条队列记录");
    }
}

/// <summary>立即把指定中心队列记录转发到 B 系统。</summary>
public sealed class EdgeQueueForwardAgentToolAction : AgentToolActionBase<EdgeQueueIdsArguments>
{
    private readonly IEdgeGatewayService _service;
    public EdgeQueueForwardAgentToolAction(IEdgeGatewayService service) => _service = service;

    public override AgentToolSeed Definition { get; } = EdgeGatewayAgentToolSeedFactory.Create(
        "edge.queue.forward", "立即转发队列", "立即把指定消息队列记录映射并上传到 B 系统，执行前必须确认。",
        "ForwardQueue", """
        {"type":"object","properties":{"ids":{"type":"array","minItems":1,"items":{"type":"integer","minimum":1}}},"required":["ids"],"additionalProperties":false}
        """, 230, "{\"ids\":[101]}", true, true, "High");

    protected override async Task<AgentBusinessToolResult> ExecuteAsync(
        EdgeQueueIdsArguments arguments, AgentBusinessToolContext context, CancellationToken cancellationToken)
    {
        var result = await _service.ForwardQueueAsync(new EdgeRetryRequestDto { Ids = arguments.Ids });
        return result.FailedCount == 0
            ? AgentBusinessToolResult.Ok(result, $"成功转发 {result.SuccessCount} 条队列记录")
            : new AgentBusinessToolResult
            {
                Success = false,
                PartialSuccess = result.SuccessCount > 0,
                Message = $"转发完成：成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条",
                Data = result,
                ErrorMessage = result.SuccessCount > 0 ? null : "指定队列记录转发失败"
            };
    }
}
