using System.Collections.Concurrent;
using System.Text;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Service;

/// <summary>
/// B 系统 MQTT 出站发送器，在一次队列转发请求内复用 Broker 连接并按 QoS 发布 JSON。
/// </summary>
public sealed class MqttBSystemTransportSender : IBSystemTransportSender, IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, MqttConnectionState> _connections = new();
    private readonly ILogger<MqttBSystemTransportSender> _logger;

    /// <summary>用于匹配 B 系统映射中的协议编码。</summary>
    public string Protocol => "MQTT";

    /// <summary>创建 B 系统 MQTT 发送器并注入日志组件。</summary>
    public MqttBSystemTransportSender(ILogger<MqttBSystemTransportSender> logger)
    {
        _logger = logger;
    }

    /// <summary>连接 MQTT Broker 并把映射后的 JSON 发布到配置主题。</summary>
    public async Task<EdgeBTransportResult> SendAsync(
        EdgeBMappingRuleEntity mapping,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var target = $"{mapping.TargetUrl.TrimEnd('/')}/{mapping.MqttTopic}";
        var state = _connections.GetOrAdd(mapping.Id, _ => CreateConnection(mapping));
        await state.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (!state.Client.IsConnected)
                await state.Client.ConnectAsync(state.Options, cancellationToken);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(mapping.MqttTopic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)mapping.MqttQosLevel)
                .WithRetainFlag(mapping.MqttRetain)
                .Build();
            var result = await state.Client.PublishAsync(message, cancellationToken);
            var responseContent = string.IsNullOrWhiteSpace(result.ReasonString)
                ? $"ReasonCode={result.ReasonCode}"
                : $"ReasonCode={result.ReasonCode}; ReasonString={result.ReasonString}";
            return result.IsSuccess
                ? new EdgeBTransportResult(
                    true,
                    target,
                    ResponseContent: responseContent)
                : new EdgeBTransportResult(
                    false,
                    target,
                    null,
                    $"MQTT 发布失败：{result.ReasonCode} {result.ReasonString}".Trim(),
                    responseContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "B system MQTT publish failed for mapping {MappingId} and topic {Topic}",
                mapping.Id,
                mapping.MqttTopic);
            return new EdgeBTransportResult(false, target, null, ex.Message);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    /// <summary>释放本次请求中建立的 MQTT 连接。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var state in _connections.Values)
        {
            try
            {
                if (state.Client.IsConnected) await state.Client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to disconnect B system MQTT client");
            }
            state.Client.Dispose();
            state.SendLock.Dispose();
        }
        _connections.Clear();
    }

    /// <summary>根据 mqtt:// 或 mqtts:// 地址创建 MQTT 客户端和连接参数。</summary>
    private static MqttConnectionState CreateConnection(EdgeBMappingRuleEntity mapping)
    {
        var uri = new Uri(mapping.TargetUrl);
        var factory = new MqttFactory();
        var client = factory.CreateMqttClient();
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(uri.Host, GetPort(uri))
            .WithClientId(string.IsNullOrWhiteSpace(mapping.MqttClientId)
                ? $"calmadmin-b-{mapping.Id}-{Guid.NewGuid():N}"
                : mapping.MqttClientId)
            .WithCleanSession();

        if (!string.IsNullOrWhiteSpace(mapping.MqttUsername))
            optionsBuilder.WithCredentials(mapping.MqttUsername, mapping.MqttPassword);
        if (uri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase))
            optionsBuilder.WithTlsOptions(options => options.UseTls());

        return new MqttConnectionState(client, optionsBuilder.Build());
    }

    /// <summary>未显式填写端口时按协议选择 MQTT 默认端口。</summary>
    private static int GetPort(Uri uri) =>
        uri.IsDefaultPort
            ? uri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase) ? 8883 : 1883
            : uri.Port;

    /// <summary>封装单个映射在当前请求内复用的客户端、连接参数和发送锁。</summary>
    private sealed record MqttConnectionState(
        IMqttClient Client,
        MqttClientOptions Options)
    {
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
