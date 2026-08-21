using Model.EdgeGateway;

namespace Interface;

/// <summary>中心端独立 HTTP 实时数据暂存服务。</summary>
public interface IEdgeRealtimeDataService
{
    /// <summary>
    /// 根据请求体 tenantMark 和 deviceMark 新增或覆盖设备最新快照。
    /// </summary>
    Task<EdgeRealtimeReceiveResultDto> ReceiveAsync(
        EdgeRealtimeDataReceiveDto dto,
        CancellationToken cancellationToken);

    /// <summary>分页查询当前设备快照，不返回历史数据。</summary>
    Task<EdgePagedResultDto<EdgeRealtimeSnapshotOutputDto>> GetPageAsync(
        EdgeRealtimeSnapshotQueryDto query);

    /// <summary>按租户和设备标识查询唯一的当前快照。</summary>
    Task<EdgeRealtimeSnapshotOutputDto?> GetLatestAsync(
        string tenantMark,
        string deviceCode);
}
