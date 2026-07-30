using System.Text.Json;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 边缘节点数据源运行管理器，统一管理 MQTT 和 WebSocket 连接实例。
/// </summary>
public class EdgeSourceManager : IEdgeSourceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IEdgeSourceAdapter> _adapters;
    private readonly ILogger<EdgeSourceManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SourceRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    public EdgeSourceManager(IEnumerable<IEdgeSourceAdapter> adapters, ILogger<EdgeSourceManager> logger)
    {
        _adapters = adapters
            .GroupBy(x => x.Protocol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// 对比期望配置与当前运行状态。连接参数变化时重建，只有订阅变化时原连接重新发送订阅。
    /// RuntimeVersion 参与连接指纹，因此前端“重新连接”仍能强制重建。
    /// </summary>
    public async Task ApplyAsync(
        IEnumerable<SourceOptions> sources,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var desired = sources
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var stopping = new List<SourceRuntime>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 先取消已删除、已停用或连接参数发生变化的运行实例。
            foreach (var current in _runtimes.ToList())
            {
                if (!desired.TryGetValue(current.Key, out var source))
                {
                    current.Value.Cancellation.Cancel();
                    stopping.Add(current.Value);
                    _runtimes.Remove(current.Key);
                    continue;
                }

                if (current.Value.ConnectionFingerprint == GetConnectionFingerprint(source) &&
                    current.Value.SubscriptionFingerprint != GetSubscriptionFingerprint(source) &&
                    current.Value.Adapter is IEdgeSourceSubscriptionUpdater updater)
                {
                    var updated = await updater.UpdateSubscriptionAsync(
                        source.Id,
                        source,
                        cancellationToken);
                    if (updated)
                    {
                        current.Value.UpdateSubscription(source);
                        continue;
                    }
                }

                var changed = current.Value.ConnectionFingerprint != GetConnectionFingerprint(source) ||
                              current.Value.SubscriptionFingerprint != GetSubscriptionFingerprint(source);
                if (!changed) continue;
                current.Value.Cancellation.Cancel();
                stopping.Add(current.Value);
                _runtimes.Remove(current.Key);
            }

            // 再为新增或刚被取消重建的数据源启动对应协议适配器。
            foreach (var source in desired.Values)
            {
                if (_runtimes.ContainsKey(source.Id)) continue;
                if (!_adapters.TryGetValue(source.Protocol, out var adapter))
                {
                    _logger.LogWarning("Source {SourceId} protocol {Protocol} has no adapter", source.Id, source.Protocol);
                    continue;
                }

                var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var task = RunSourceAsync(adapter, source, onMessage, sourceCancellation.Token);
                _runtimes[source.Id] = new SourceRuntime(
                    adapter,
                    GetConnectionFingerprint(source),
                    GetSubscriptionFingerprint(source),
                    sourceCancellation,
                    task);
                _logger.LogInformation("Source {SourceId} started with protocol {Protocol}", source.Id, source.Protocol);
            }
        }
        finally
        {
            _gate.Release();
        }

        await AwaitStoppedAsync(stopping);
    }

    /// <summary>取消并等待当前全部协议适配器退出。</summary>
    public async Task StopAsync()
    {
        List<SourceRuntime> stopping;
        await _gate.WaitAsync();
        try
        {
            stopping = _runtimes.Values.ToList();
            _runtimes.Clear();
            foreach (var runtime in stopping) runtime.Cancellation.Cancel();
        }
        finally
        {
            _gate.Release();
        }

        await AwaitStoppedAsync(stopping);
    }

    /// <summary>包装适配器运行过程，统一处理主动取消和意外退出日志。</summary>
    private async Task RunSourceAsync(
        IEdgeSourceAdapter adapter,
        SourceOptions source,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await adapter.RunAsync(source, onMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source {SourceId} stopped unexpectedly", source.Id);
        }

    }

    /// <summary>生成需要重建物理连接的配置指纹，不包含可在线更新的订阅内容。</summary>
    private static string GetConnectionFingerprint(SourceOptions source) =>
        JsonSerializer.Serialize(new
        {
            source.Id,
            source.Name,
            source.Protocol,
            source.Endpoint,
            source.Topic,
            source.ClientId,
            source.Username,
            source.Password,
            source.HeadersJson,
            source.DataType,
            source.ReconnectIntervalSeconds,
            source.Enabled,
            source.RuntimeVersion
        }, JsonOptions);

    /// <summary>生成订阅设备、参数及订阅版本指纹，用于判断是否需要在线重新订阅。</summary>
    private static string GetSubscriptionFingerprint(SourceOptions source) =>
        JsonSerializer.Serialize(new
        {
            source.SubscriptionPayload,
            source.SubscriptionDevices,
            source.SubscriptionVersion
        }, JsonOptions);

    /// <summary>等待已取消的连接完全退出，并释放它们的取消令牌。</summary>
    private static async Task AwaitStoppedAsync(IEnumerable<SourceRuntime> runtimes)
    {
        foreach (var runtime in runtimes)
        {
            try
            {
                await runtime.Task;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                runtime.Cancellation.Dispose();
            }
        }
    }

    /// <summary>保存单个正在运行的数据源任务、适配器和两类配置指纹。</summary>
    private sealed class SourceRuntime
    {
        public SourceRuntime(
            IEdgeSourceAdapter adapter,
            string connectionFingerprint,
            string subscriptionFingerprint,
            CancellationTokenSource cancellation,
            Task task)
        {
            Adapter = adapter;
            ConnectionFingerprint = connectionFingerprint;
            SubscriptionFingerprint = subscriptionFingerprint;
            Cancellation = cancellation;
            Task = task;
        }

        public IEdgeSourceAdapter Adapter { get; }
        public string ConnectionFingerprint { get; }
        public string SubscriptionFingerprint { get; private set; }
        public CancellationTokenSource Cancellation { get; }
        public Task Task { get; }

        /// <summary>在线重新订阅成功后更新内存指纹，避免下一轮重复发送。</summary>
        public void UpdateSubscription(SourceOptions source) =>
            SubscriptionFingerprint = GetSubscriptionFingerprint(source);
    }
}
