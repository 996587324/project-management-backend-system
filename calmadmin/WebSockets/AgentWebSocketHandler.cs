using Interface;
using Model.Agent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace calmadmin.WebSockets;

public sealed class AgentWebSocketHandler
{
    private const int MaxMessageBytes = 256 * 1024;
    private readonly IAgentService _agentService;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AgentWebSocketHandler(IAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var applications = context.RequestServices.GetRequiredService<ISystemApplicationService>();
        try
        {
            await RequireAgentAccessAsync(applications, context.User);
        }
        catch (UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required");
            return;
        }

        var userId = GetCurrentUserId(context.User);
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var payload = await ReceiveTextAsync(socket, context.RequestAborted);
                if (payload == null) break;

                AgentSocketRequest? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<AgentSocketRequest>(payload, JsonOptions);
                }
                catch (JsonException)
                {
                    await SendEventAsync(socket, AgentSocketEvent.Error(null, "消息格式不是有效的 JSON"), context.RequestAborted);
                    continue;
                }

                if (string.Equals(envelope?.Type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendEventAsync(socket, new AgentSocketEvent { Type = "pong" }, context.RequestAborted);
                    continue;
                }

                if (envelope == null ||
                    !string.Equals(envelope.Type, "message", StringComparison.OrdinalIgnoreCase) ||
                    envelope.Data == null)
                {
                    await SendEventAsync(socket, AgentSocketEvent.Error(envelope?.RequestId, "不支持的 WebSocket 消息类型"), context.RequestAborted);
                    continue;
                }

                // 长连接不能永久保留连接建立时的授权；撤权、退出或过期后不得继续调用工具。
                try
                {
                    await RequireAgentAccessAsync(applications, context.User);
                }
                catch (UnauthorizedAccessException exception)
                {
                    await SendEventAsync(socket, AgentSocketEvent.Error(envelope.RequestId, exception.Message), context.RequestAborted);
                    break;
                }
                await ProcessMessageAsync(socket, envelope, userId, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser or reverse proxy closed the connection.
        }
        catch (WebSocketException)
        {
            // Connection state no longer allows a response frame.
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Agent WebSocket closed",
                    CancellationToken.None);
            }
        }
    }

    private static async Task RequireAgentAccessAsync(ISystemApplicationService applications, ClaimsPrincipal principal)
    {
        var access = await applications.GetAccessAsync(principal, true);
        if (access.AppCode is not ("A" or "B") || !access.AllResources ||
            !new[] { "Agent", "AgentAssistant", "AgentExecutor", "EdgeGatewayAgent", "ProductionDashboardAgent", "ProcessBoardAgent" }.Any(access.HasPermission))
            throw new UnauthorizedAccessException("Agent 工作接口尚未适配当前系统或数据范围，请使用已授权 MCP 工具");
    }

    private async Task ProcessMessageAsync(
        WebSocket socket,
        AgentSocketRequest envelope,
        string userId,
        CancellationToken cancellationToken)
    {
        var requestId = string.IsNullOrWhiteSpace(envelope.RequestId)
            ? Guid.NewGuid().ToString("N")
            : envelope.RequestId.Trim();
        var request = envelope.Data!;
        request.UserId = userId;
        request.Mode = string.IsNullOrWhiteSpace(request.Mode) ? "Online" : request.Mode;

        await SendEventAsync(socket, new AgentSocketEvent
        {
            Type = "accepted",
            RequestId = requestId,
            Stage = "accepted",
            Message = "请求已接收，Agent 开始处理"
        }, cancellationToken);

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatAsync(socket, requestId, heartbeatCancellation.Token);

        try
        {
            var response = await _agentService.SendMessageAsync(
                request,
                progress => SendEventAsync(socket, new AgentSocketEvent
                {
                    Type = "progress",
                    RequestId = requestId,
                    Stage = progress.Stage,
                    Message = progress.Message,
                    ConversationId = progress.ConversationId
                }, cancellationToken));

            foreach (var chunk in ChunkText(response.Reply, 160))
            {
                await SendEventAsync(socket, new AgentSocketEvent
                {
                    Type = "chunk",
                    RequestId = requestId,
                    Chunk = chunk,
                    ConversationId = response.ConversationId
                }, cancellationToken);
            }

            await SendEventAsync(socket, new AgentSocketEvent
            {
                Type = "completed",
                RequestId = requestId,
                Stage = "completed",
                Message = "处理完成",
                ConversationId = response.ConversationId,
                Data = response
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await SendEventAsync(
                socket,
                AgentSocketEvent.Error(requestId, ex.Message),
                cancellationToken);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when the current request has completed.
            }
        }
    }

    private async Task SendHeartbeatAsync(
        WebSocket socket,
        string requestId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SendEventAsync(socket, new AgentSocketEvent
            {
                Type = "heartbeat",
                RequestId = requestId,
                Message = "Agent 正在处理"
            }, cancellationToken);
        }
    }

    private async Task SendEventAsync(
        WebSocket socket,
        AgentSocketEvent message,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Agent WebSocket 仅支持文本消息");

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (stream.Length > MaxMessageBytes)
                throw new InvalidOperationException("Agent 消息内容过大");
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IEnumerable<string> ChunkText(string text, int maxTextElements)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var chunk = new StringBuilder();
        var count = 0;
        while (enumerator.MoveNext())
        {
            chunk.Append(enumerator.GetTextElement());
            count++;
            if (count < maxTextElements) continue;

            yield return chunk.ToString();
            chunk.Clear();
            count = 0;
        }

        if (chunk.Length > 0) yield return chunk.ToString();
    }

    private static string? GetCurrentUserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue("Id")
               ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? user.FindFirstValue("Name")
               ?? user.FindFirstValue(ClaimTypes.Name);
    }

    private sealed class AgentSocketRequest
    {
        public string Type { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public AgentSendRequest? Data { get; set; }
    }

    private sealed class AgentSocketEvent
    {
        public string Type { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public string? Stage { get; set; }
        public string? Message { get; set; }
        public string? Chunk { get; set; }
        public int? ConversationId { get; set; }
        public AgentResponseDto? Data { get; set; }

        public static AgentSocketEvent Error(string? requestId, string message) => new()
        {
            Type = "error",
            RequestId = requestId,
            Stage = "error",
            Message = message
        };
    }
}
