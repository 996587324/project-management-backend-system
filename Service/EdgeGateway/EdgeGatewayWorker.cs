using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 边缘网关后台主循环，协调数据源采集、本地队列上传、心跳和配置热更新。
/// </summary>
public class EdgeGatewayWorker : IEdgeGatewayWorker
{
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly IEdgeQueueStore _store;
    private readonly IEdgeMessageProcessor _processor;
    private readonly ICenterApiClient _centerApi;
    private readonly IEdgeSourceManager _sourceManager;
    private readonly ILogger<EdgeGatewayWorker> _logger;
    private long _collectedCount;
    private long _uploadedCount;
    private string? _lastError;
    private int _configVersion;
    private int _ruleVersion;
    private int _sourceVersion;
    private bool _gatewayEnabled;

    public EdgeGatewayWorker(
        EdgeGatewayBootstrapOptions bootstrapOptions,
        IEdgeQueueStore store,
        IEdgeMessageProcessor processor,
        ICenterApiClient centerApi,
        IEdgeSourceManager sourceManager,
        ILogger<EdgeGatewayWorker> logger)
    {
        _bootstrapOptions = bootstrapOptions;
        _store = store;
        _processor = processor;
        _centerApi = centerApi;
        _sourceManager = sourceManager;
        _logger = logger;
    }

    /// <summary>
    /// 初始化边缘存储和数据源，然后并行运行上传及心跳循环。
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        ValidateBootstrapOptions();
        await _store.InitializeAsync(stoppingToken);
        await _store.RecordEventAsync("Info", "Gateway.Started", "边缘网关服务已启动",
            new { _bootstrapOptions.DeviceId, Version = GetVersion() }, stoppingToken);

        // HostedService 始终运行，首次启动直接读取数据库启停状态和业务配置。
        await ApplyInitialDatabaseConfigurationAsync(stoppingToken);

        var tasks = new[]
        {
            RunUploadLoopAsync(stoppingToken),
            RunHeartbeatLoopAsync(stoppingToken)
        };
        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _sourceManager.StopAsync();
        }
    }

    /// <summary>
    /// 启动时通过中心服务读取数据库中的连接、映射和规则，使数据表成为唯一业务配置来源。
    /// </summary>
    private async Task ApplyInitialDatabaseConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await _centerApi.GetEffectiveConfigurationAsync(cancellationToken);
            await ApplyEffectiveConfigurationAsync(configuration, true, cancellationToken);
            if (!configuration.Enabled)
                _logger.LogInformation(
                    "Edge gateway device {DeviceId} is disabled by database configuration; heartbeat remains active.",
                    _bootstrapOptions.DeviceId);
            else if (!configuration.Sources.Any(x => x.Enabled))
                _logger.LogWarning("No database WebSocket source is enabled. Upload and heartbeat loops are still running.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Unable to load initial EdgeGateway configuration from database");
        }
    }

    /// <summary>处理适配器收到的报文，并把通过规则的标准消息写入本地可靠队列。</summary>
    private async Task ProcessEnvelopeAsync(SourceEnvelope envelope, CancellationToken cancellationToken)
    {
        // 数据库停用可能与正在接收的报文并发发生，入队前再次拦截可避免停用后的尾包进入队列。
        if (!Volatile.Read(ref _gatewayEnabled)) return;
        var result = _processor.Process(envelope);
        if (result.Message == null)
        {
            await _store.RecordEventAsync("Debug", "Message.Dropped", result.DropReason ?? "Unknown",
                new { envelope.SourceId }, cancellationToken);
            return;
        }
        if (await _store.EnqueueAsync(result.Message, cancellationToken)) Interlocked.Increment(ref _collectedCount);
    }

    /// <summary>循环读取到期队列并上传中心端，失败时由本地存储计算退避时间。</summary>
    private async Task RunUploadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Volatile.Read(ref _gatewayEnabled))
            {
                await DelayAsync(_bootstrapOptions.UploadIntervalSeconds, cancellationToken);
                continue;
            }
            try
            {
                var messages = await _store.GetDueMessagesAsync(
                    _bootstrapOptions.UploadBatchSize,
                    cancellationToken);
                if (messages.Count > 0)
                {
                    var upload = await _centerApi.UploadAsync(messages, cancellationToken);
                    foreach (var message in messages)
                    {
                        if (upload.Success)
                        {
                            await _store.MarkUploadedAsync(message, upload.StatusCode, cancellationToken);
                            Interlocked.Increment(ref _uploadedCount);
                        }
                        else
                        {
                            await _store.MarkFailedAsync(message, upload.Error ?? "中心端上传失败",
                                upload.StatusCode == 0 ? null : upload.StatusCode, cancellationToken);
                        }
                    }
                    _lastError = upload.Success ? null : upload.Error;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                await RecordErrorAsync("Upload.Error", ex.Message, null, cancellationToken);
            }
            await DelayAsync(_bootstrapOptions.UploadIntervalSeconds, cancellationToken);
        }
    }

    /// <summary>
    /// 上报设备状态，比较中心版本，并热更新连接、字段映射和边缘规则。
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var stats = await _store.GetStatisticsAsync(cancellationToken);
                var process = Process.GetCurrentProcess();
                var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var memoryUsage = availableMemory <= 0 ? 0 : Math.Round(process.WorkingSet64 * 100m / availableMemory, 2);
                var heardata = new
                {
                    DeviceCode = _bootstrapOptions.DeviceId,
                    DeviceName = _bootstrapOptions.DeviceName,
                    IpAddress = GetLocalIpAddress(),
                    AppVersion = GetVersion(),
                    CpuUsage = 0,
                    MemoryUsage = memoryUsage,
                    QueueDepth = stats.Pending + stats.Failed,
                    CollectedCount = Interlocked.Read(ref _collectedCount),
                    UploadedCount = Interlocked.Read(ref _uploadedCount),
                    LastError = _lastError
                };
                var versions = await _centerApi.SendHeartbeatAsync(heardata, cancellationToken);
                var enabledChanged = versions.Enabled != Volatile.Read(ref _gatewayEnabled);
                var versionChanged = versions.ConfigVersion > _configVersion ||
                                     versions.RuleVersion > _ruleVersion ||
                                     versions.SourceVersion > _sourceVersion;
                if (!versions.Enabled && (enabledChanged || versionChanged))
                {
                    // 停用时清空期望数据源以停止全部连接，但继续心跳以等待数据库重新启用。
                    Volatile.Write(ref _gatewayEnabled, false);
                    await _sourceManager.ApplyAsync(
                        Array.Empty<SourceOptions>(),
                        ProcessEnvelopeAsync,
                        cancellationToken);
                    _configVersion = versions.ConfigVersion;
                    _ruleVersion = versions.RuleVersion;
                    _sourceVersion = versions.SourceVersion;
                    if (enabledChanged)
                        await _store.RecordEventAsync(
                            "Info",
                            "Gateway.Disabled",
                            "数据库已停用边缘网关，采集和上传已暂停",
                            new { _bootstrapOptions.DeviceId },
                            cancellationToken);
                }
                else if (versions.Enabled && (enabledChanged || versionChanged))
                {
                    var configuration = await _centerApi.GetEffectiveConfigurationAsync(cancellationToken);
                    await ApplyEffectiveConfigurationAsync(configuration, enabledChanged, cancellationToken);
                    await _store.RecordEventAsync(
                        "Info",
                        enabledChanged ? "Gateway.Enabled" : "Config.Applied",
                        enabledChanged ? "数据库已启用边缘网关，采集和上传已恢复" : "中心配置、规则与连接已热更新",
                        new { _configVersion, _ruleVersion, _sourceVersion },
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Heartbeat upload failed");
            }
            await DelayAsync(_bootstrapOptions.HeartbeatIntervalSeconds, cancellationToken);
        }
    }

    /// <summary>
    /// 应用中心数据库返回的完整配置；数据库停用时传入空数据源以关闭现有连接。
    /// </summary>
    private async Task ApplyEffectiveConfigurationAsync(
        EffectiveEdgeConfiguration configuration,
        bool force,
        CancellationToken cancellationToken)
    {
        if (force ||
            configuration.ConfigVersion > _configVersion ||
            configuration.RuleVersion > _ruleVersion)
            _processor.ApplyConfiguration(configuration.ConfigJson, configuration.RulesJson);

        Volatile.Write(ref _gatewayEnabled, configuration.Enabled);
        if (force || configuration.SourceVersion > _sourceVersion)
        {
            await _sourceManager.ApplyAsync(
                configuration.Enabled ? configuration.Sources : Array.Empty<SourceOptions>(),
                ProcessEnvelopeAsync,
                cancellationToken);
        }

        await _store.SaveEffectiveConfigurationAsync(configuration, cancellationToken);
        _configVersion = configuration.ConfigVersion;
        _ruleVersion = configuration.RuleVersion;
        _sourceVersion = configuration.SourceVersion;
    }

    /// <summary>记录 Worker 错误日志，并尽力写入边缘 SQLite 事件表。</summary>
    private async Task RecordErrorAsync(string eventType, string message, object? detail,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("{EventType}: {Message}", eventType, message);
        try { await _store.RecordEventAsync("Error", eventType, message, detail, cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "Unable to persist edge event"); }
    }

    /// <summary>校验连接中心所需的引导参数，并为非法循环参数恢复安全默认值。</summary>
    private void ValidateBootstrapOptions()
    {
        if (string.IsNullOrWhiteSpace(_bootstrapOptions.DeviceId))
            throw new InvalidOperationException("EdgeGateway:DeviceId 未配置");
        if (_bootstrapOptions.UploadBatchSize <= 0) _bootstrapOptions.UploadBatchSize = 50;
        if (_bootstrapOptions.UploadIntervalSeconds <= 0) _bootstrapOptions.UploadIntervalSeconds = 3;
        if (_bootstrapOptions.HeartbeatIntervalSeconds <= 0) _bootstrapOptions.HeartbeatIntervalSeconds = 30;
    }

    /// <summary>取得当前 Service 程序集版本，随心跳上报中心端。</summary>
    private static string GetVersion() =>
        typeof(EdgeGatewayWorker).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>取得首个可用的非回环 IPv4 地址。</summary>
    private static string? GetLocalIpAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString();
    }

    /// <summary>支持取消令牌的循环延时。</summary>
    private static async Task DelayAsync(int seconds, CancellationToken cancellationToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 1)), cancellationToken); }
        catch (OperationCanceledException) { }
    }
}
