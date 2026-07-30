using Model.EdgeGateway;

namespace Interface;

/// <summary>
/// 管理边缘节点上多个数据源连接的启动、停止和动态重建。
/// </summary>
public interface IEdgeSourceManager
{
    /// <summary>
    /// 对比期望配置与正在运行的连接，只停止、启动或重建发生变化的数据源。
    /// </summary>
    Task ApplyAsync(
        IEnumerable<SourceOptions> sources,
        Func<SourceEnvelope, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken);

    /// <summary>停止并等待全部数据源连接退出。</summary>
    Task StopAsync();
}
