using System.Text;
using Interface;
using Microsoft.Extensions.Logging;
using Model.EdgeGateway;
using MQTTnet;
using MQTTnet.Client;

namespace Service;

/// <summary>
/// MQTT 数据源协议适配器，负责连接 Broker、订阅主题、接收消息和断线重连。
/// </summary>
public class MqttSourceAdapter : IEdgeSourceAdapter
{
    private readonly ILogger<MqttSourceAdapter> _logger;
    /// <summary>用于匹配 SourceOptions.Protocol 的协议名称。</summary>
    public string Protocol => "MQTT";

    public MqttSourceAdapter(ILogger<MqttSourceAdapter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 按数据源配置持续运行 MQTT 客户端，并把收到的消息交给统一处理管线。
    /// </summary>
    public async Task RunAsync(SourceOptions source, Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var (host, port) = ParseEndpoint(source.Endpoint);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var factory = new MqttFactory();
                using var client = factory.CreateMqttClient();
                client.ApplicationMessageReceivedAsync += async args =>
                {
                    var segment = args.ApplicationMessage.PayloadSegment;
                    var payload = segment.Array == null
                        ? string.Empty
                        : Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count);
                    await onMessage(new SourceEnvelope(source.Id, source.DataType, payload, DateTime.UtcNow), cancellationToken);
                };

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(host, port)
                    .WithClientId(string.IsNullOrWhiteSpace(source.ClientId) ? $"edge-{Guid.NewGuid():N}" : source.ClientId)
                    .WithCleanSession();
                if (!string.IsNullOrWhiteSpace(source.Username))
                    optionsBuilder.WithCredentials(source.Username, source.Password);

                await client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
                var subscribeOptions = factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(filter => filter.WithTopic(source.Topic ?? "#"))
                    .Build();
                await client.SubscribeAsync(subscribeOptions, cancellationToken);
                _logger.LogInformation("MQTT source {SourceId} subscribed to {Topic}", source.Id, source.Topic);

                while (client.IsConnected && !cancellationToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT source {SourceId} disconnected", source.Id);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>把 mqtt:// 地址解析为主机和端口，未填写协议时自动补全。</summary>
    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var normalized = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : $"mqtt://{endpoint}";
        var uri = new Uri(normalized);
        return (uri.Host, uri.IsDefaultPort ? 1883 : uri.Port);
    }
}
