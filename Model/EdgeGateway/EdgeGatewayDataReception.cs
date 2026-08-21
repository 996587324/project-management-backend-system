namespace Model.EdgeGateway;

/// <summary>
/// 连接配置的数据接收状态定义。中心端只把已经通过边缘解析并成功入队的数据视为有效数据。
/// </summary>
public static class EdgeGatewayDataReception
{
    /// <summary>最后一次有效数据到达后，连接仍视为正常接收的时间窗口。</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public const string Receiving = "Receiving";
    public const string Interrupted = "Interrupted";
    public const string NeverReceived = "NeverReceived";
    public const string Disabled = "Disabled";
}
