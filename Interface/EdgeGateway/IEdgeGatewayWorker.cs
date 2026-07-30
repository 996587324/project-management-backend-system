namespace Interface;

/// <summary>
/// 边缘网关后台 Worker 入口。
/// </summary>
public interface IEdgeGatewayWorker
{
    /// <summary>
    /// 启动数据源采集、可靠上传和心跳配置同步循环，直到收到停止信号。
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);
}
