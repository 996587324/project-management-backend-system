using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 数据源协议适配器。MQTT 和 WebSocket 通过同一接口向处理管线提交报文。
/// </summary>
public interface IEdgeSourceAdapter
{
    /// <summary>适配器支持的协议名称，用于运行时匹配数据源配置。</summary>
    string Protocol { get; }

    /// <summary>
    /// 建立数据源连接并持续接收消息，连接生命周期受取消令牌控制。
    /// </summary>
    Task RunAsync(SourceOptions source, Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken);
}

/// <summary>
/// 支持在不重建连接的情况下更新订阅内容的数据源适配器。
/// </summary>
public interface IEdgeSourceSubscriptionUpdater
{
    /// <summary>
    /// 向当前已连接的数据源发送最新订阅；返回 false 表示连接当前不可用，应由管理器重建。
    /// </summary>
    Task<bool> UpdateSubscriptionAsync(
        string sourceId,
        SourceOptions source,
        CancellationToken cancellationToken);
}
