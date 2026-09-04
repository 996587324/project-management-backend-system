using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// WebSocket 数据源协议适配器，负责建连、订阅、按设备拆分推送消息和断线重连。
/// </summary>
public class WebSocketSourceAdapter : IEdgeSourceAdapter, IEdgeSourceSubscriptionUpdater
{
    private static readonly SemaphoreSlim SubscriptionSendPacingGate = new(1, 1);
    private static readonly TimeSpan MinimumSubscriptionSendInterval = TimeSpan.FromSeconds(2);
    private static DateTime _lastSubscriptionSendStartedUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, ActiveWebSocket> _activeConnections =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WebSocketSourceAdapter> _logger;
    private readonly ICenterApiClient _centerApi;

    /// <summary>用于匹配 SourceOptions.Protocol 的协议名称。</summary>
    public string Protocol => "WebSocket";

    public WebSocketSourceAdapter(ILogger<WebSocketSourceAdapter> logger, ICenterApiClient centerApi)
    {
        _logger = logger;
        _centerApi = centerApi;
    }

    /// <summary>
    /// 按数据源配置持续运行 WebSocket。连接成功后立即发送订阅，异常断开后自动重连。
    /// </summary>
    public async Task RunAsync(
        SourceOptions source,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ActiveWebSocket? active = null;
            try
            {
                using var socket = new ClientWebSocket();
                // 配置停用时 SourceManager 会取消令牌；主动 Abort 可立即打断正在等待数据的
                // ReceiveAsync/ConnectAsync，确保物理连接及时释放，而不是继续等待服务端推送。
                using var cancellationRegistration = cancellationToken.Register(
                    static state => ((ClientWebSocket)state!).Abort(),
                    socket);
                ApplyHeaders(socket, source.HeadersJson);

                // WebSocket 的实际建连位置。Endpoint 必须是 ws:// 或 wss:// 完整地址。
                await socket.ConnectAsync(new Uri(source.Endpoint), cancellationToken);
                active = new ActiveWebSocket(socket, source.SubscriptionDevices);
                _activeConnections[source.Id] = active;
                if (consecutiveFailures > 0)
                {
                    _logger.LogInformation(
                        "WebSocket source {SourceId} recovered after {FailureCount} failed attempts",
                        source.Id,
                        consecutiveFailures);
                    consecutiveFailures = 0;
                }
                _logger.LogInformation("WebSocket source {SourceId} connected", source.Id);

                // 目标接口要求连接成功后立即发送订阅，否则不会推送实时数据。
                if (!string.IsNullOrWhiteSpace(source.SubscriptionPayload))
                    await SendAndRecordAsync(source, active, source.SubscriptionPayload,
                        "InitialSubscription", cancellationToken);

                await ReceiveLoopAsync(source, active, onMessage, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Abort 可能以 WebSocketException 结束阻塞操作；主动停用不记录为连接故障。
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                LogConnectionFailure(source.Id, ex, consecutiveFailures);
            }
            finally
            {
                if (active != null &&
                    _activeConnections.TryGetValue(source.Id, out var current) &&
                    ReferenceEquals(active, current))
                {
                    _activeConnections.TryRemove(source.Id, out _);
                    active.Dispose();
                }
            }

            // 非主动停止导致的断线会按连接自己的重连间隔继续尝试。
            await DelayReconnectAsync(source.ReconnectIntervalSeconds, cancellationToken);
        }

        _logger.LogInformation("WebSocket source {SourceId} stopped and connection released", source.Id);
    }

    /// <summary>
    /// 首次连续失败保留完整异常，之后仅定期输出摘要，避免不可达地址持续灌满控制台和 Windows 事件日志。
    /// </summary>
    private void LogConnectionFailure(string sourceId, Exception exception, int consecutiveFailures)
    {
        if (consecutiveFailures == 1)
        {
            _logger.LogWarning(exception, "WebSocket source {SourceId} disconnected", sourceId);
            return;
        }

        if (consecutiveFailures % 10 == 0)
        {
            _logger.LogWarning(
                "WebSocket source {SourceId} has failed {FailureCount} consecutive attempts: {ErrorMessage}",
                sourceId,
                consecutiveFailures,
                exception.Message);
            return;
        }

        _logger.LogDebug(
            "WebSocket source {SourceId} reconnect attempt {FailureCount} failed: {ErrorMessage}",
            sourceId,
            consecutiveFailures,
            exception.Message);
    }

    /// <summary>
    /// 保持现有 WebSocket 连接并重新发送设备、参数订阅；连接不可用时返回 false。
    /// </summary>
    public async Task<bool> UpdateSubscriptionAsync(
        string sourceId,
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        if (!_activeConnections.TryGetValue(sourceId, out var active) ||
            active.SocketState != WebSocketState.Open)
            return false;
        try
        {
            await SendAndRecordAsync(source, active, source.SubscriptionPayload ?? "{\"devices\":[]}",
                "SubscriptionUpdate", cancellationToken);
            active.UpdateSubscriptions(source.SubscriptionDevices);
            _logger.LogInformation(
                "WebSocket source {SourceId} subscription updated to version {SubscriptionVersion}",
                sourceId,
                source.SubscriptionVersion);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "WebSocket source {SourceId} subscription update failed", sourceId);
            return false;
        }
    }

    /// <summary>持续合并 WebSocket 文本分片，并按配置的响应格式分发消息。</summary>
    private async Task ReceiveLoopAsync(
        SourceOptions source,
        ActiveWebSocket active,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (active.SocketState == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await active.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            // 停用与收到完整报文可能同时发生，分发前再次检查可阻止尾包进入处理队列。
            cancellationToken.ThrowIfCancellationRequested();
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            await DispatchPayloadAsync(source, active, payload, onMessage, cancellationToken);
        }
    }

    /// <summary>发送 WebSocket 报文并异步上报成功或失败结果；记录失败不会影响实际采集连接。</summary>
    private async Task SendAndRecordAsync(
        SourceOptions source,
        ActiveWebSocket active,
        string payload,
        string sendType,
        CancellationToken cancellationToken)
    {
        // 全部重连、批量启用等场景可能同时建立大量连接。将真正的发送起点错开，
        // 防止上游 WebSocket 服务在短时间内收到大量订阅报文而无法响应。
        await WaitForSubscriptionSendSlotAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await active.SendAsync(payload, cancellationToken);
            await ReportSendRecordSafelyAsync(source, payload, sendType, true, null,
                (int)stopwatch.ElapsedMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ReportSendRecordSafelyAsync(source, payload, sendType, false, ex.Message,
                (int)stopwatch.ElapsedMilliseconds, CancellationToken.None);
            throw;
        }
    }

    /// <summary>同一边缘节点内任意两条 WebSocket 订阅/更新报文的发送间隔不少于两秒。</summary>
    private static async Task WaitForSubscriptionSendSlotAsync(CancellationToken cancellationToken)
    {
        await SubscriptionSendPacingGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = _lastSubscriptionSendStartedUtc + MinimumSubscriptionSendInterval - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
            _lastSubscriptionSendStartedUtc = DateTime.UtcNow;
        }
        finally
        {
            SubscriptionSendPacingGate.Release();
        }
    }

    /// <summary>中心端不可用时只写诊断日志，不能把记录上报故障误判为 WebSocket 发送失败。</summary>
    private async Task ReportSendRecordSafelyAsync(
        SourceOptions source,
        string payload,
        string sendType,
        bool success,
        string? errorMessage,
        int durationMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var reportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reportCancellation.CancelAfter(TimeSpan.FromSeconds(3));
            await _centerApi.ReportWebSocketSendRecordAsync(new EdgeWebSocketSendRecordReportDto
            {
                SourceId = source.Id,
                SourceName = source.Name,
                Endpoint = source.Endpoint,
                SendType = sendType,
                Payload = payload,
                Success = success,
                ErrorMessage = errorMessage,
                DurationMs = durationMs
            }, reportCancellation.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Failed to report WebSocket send record for source {SourceId}", source.Id);
        }
    }

    /// <summary>
    /// 结构化订阅连接按外层 deviceCode 拆成多条报文；没有结构化订阅时保留原整包行为。
    /// </summary>
    private async Task DispatchPayloadAsync(
        SourceOptions source,
        ActiveWebSocket active,
        string payload,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var subscriptions = active.GetSubscriptions();
        if (subscriptions.Count == 0)
        {
            await onMessage(
                new SourceEnvelope(source.Id, source.DataType, payload, DateTime.UtcNow),
                cancellationToken);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "WebSocket source {SourceId} returned invalid JSON", source.Id);
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("WebSocket source {SourceId} response root is not an object", source.Id);
                return;
            }

            foreach (var deviceProperty in document.RootElement.EnumerateObject())
            {
                if (!subscriptions.TryGetValue(deviceProperty.Name, out var allowedParameters) ||
                    deviceProperty.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var values = JsonNode.Parse(deviceProperty.Value.GetRawText())?.AsObject();
                if (values == null) continue;
                foreach (var key in values.Select(x => x.Key).ToList())
                {
                    if (!allowedParameters.Contains(key)) values.Remove(key);
                }
                if (values.Count == 0) continue;

                await onMessage(
                    new SourceEnvelope(
                        source.Id,
                        source.DataType,
                        values.ToJsonString(),
                        DateTime.UtcNow,
                        deviceProperty.Name),
                    cancellationToken);
            }
        }
    }

    /// <summary>把中心端配置的文本键值对添加到 WebSocket 握手请求头。</summary>
    private static void ApplyHeaders(ClientWebSocket socket, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        using var document = JsonDocument.Parse(headersJson);
        foreach (var header in document.RootElement.EnumerateObject())
            socket.Options.SetRequestHeader(header.Name, header.Value.GetString());
    }

    /// <summary>等待下一次重连，并把间隔限制在 1 到 300 秒。</summary>
    private static async Task DelayReconnectAsync(int reconnectIntervalSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(reconnectIntervalSeconds, 1, 300)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>封装一个活动连接、并发发送锁和当前订阅白名单。</summary>
    private sealed class ActiveWebSocket : IDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private IReadOnlyDictionary<string, HashSet<string>> _subscriptions;

        public ActiveWebSocket(
            ClientWebSocket socket,
            IEnumerable<WebSocketDeviceSubscriptionOptions> subscriptions)
        {
            _socket = socket;
            _subscriptions = BuildSubscriptionMap(subscriptions);
        }

        public WebSocketState SocketState => _socket.State;

        public Task<WebSocketReceiveResult> ReceiveAsync(
            byte[] buffer,
            CancellationToken cancellationToken) =>
            _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        public async Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_socket.State != WebSocketState.Open)
                    throw new WebSocketException("WebSocket 连接当前不可用");
                await _socket.SendAsync(
                    Encoding.UTF8.GetBytes(payload),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public IReadOnlyDictionary<string, HashSet<string>> GetSubscriptions() =>
            Volatile.Read(ref _subscriptions);

        public void UpdateSubscriptions(IEnumerable<WebSocketDeviceSubscriptionOptions> subscriptions) =>
            Volatile.Write(ref _subscriptions, BuildSubscriptionMap(subscriptions));

        public void Dispose() => _sendLock.Dispose();

        private static IReadOnlyDictionary<string, HashSet<string>> BuildSubscriptionMap(
            IEnumerable<WebSocketDeviceSubscriptionOptions> subscriptions) =>
            subscriptions
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceCode))
                .GroupBy(x => x.DeviceCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.SelectMany(y => y.Parameters)
                        .Where(y => !string.IsNullOrWhiteSpace(y))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
    }
}
