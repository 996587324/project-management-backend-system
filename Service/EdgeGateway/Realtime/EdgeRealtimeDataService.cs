using System.Text;
using AutoMapper;
using Interface;
using Model.EdgeGateway;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>
/// 使用 SQL Server 独立暂存对方 HTTP 上报的设备最新数据，不依赖 WebSocket 采集配置。
/// </summary>
public sealed class EdgeRealtimeDataService : IEdgeRealtimeDataService
{
    private const int MaxDataListCount = 500;
    private const int MaxPayloadBytes = 1024 * 1024;
    private static readonly SemaphoreSlim UpsertGate = new(1, 1);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IEdgeGatewayFeatureService _featureService;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>通过 IOC 注入项目现有 SqlSugarScope、AutoMapper 和全局功能开关。</summary>
    public EdgeRealtimeDataService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IEdgeGatewayFeatureService featureService)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _featureService = featureService;
    }

    /// <summary>
    /// 校验上报信封，并按 tenantMark 和 deviceMark 新增、覆盖或忽略旧快照。
    /// </summary>
    public async Task<EdgeRealtimeReceiveResultDto> ReceiveAsync(
        EdgeRealtimeDataReceiveDto dto,
        CancellationToken cancellationToken)
    {
        await _featureService.EnsureEnabledAsync();
        var normalized = NormalizeAndValidate(dto);

        var receiveTime = DateTime.Now;
        var dataTime = ParseDataTime(normalized.SourceTimestamp, receiveTime);
        var writeModel = new EdgeRealtimeSnapshotWriteModel
        {
            DeviceCode = normalized.DeviceCode,
            ModelMark = normalized.ModelMark,
            DataType = normalized.DataType,
            TenantMark = normalized.TenantMark,
            MessageId = normalized.MessageId,
            SourceTimestamp = normalized.SourceTimestamp,
            DataList = normalized.DataList,
            DataTime = dataTime,
            ReceiveTime = receiveTime,
            LastRequestId = normalized.MessageId,
            UpdateTime = receiveTime
        };
        var snapshot = _mapper.Map<EdgeRealtimeDeviceSnapshotEntity>(writeModel);
        if (Encoding.UTF8.GetByteCount(snapshot.DataListJson) > MaxPayloadBytes)
            throw new InvalidOperationException("data JSON 不能超过 1 MB");

        await UpsertGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync(x =>
                x.TenantMark == normalized.TenantMark &&
                x.DeviceCode == normalized.DeviceCode);
            if (existing != null && dataTime < existing.DataTime)
            {
                return new EdgeRealtimeReceiveResultDto
                {
                    SnapshotId = existing.Id,
                    IgnoredOlderCount = 1,
                    DataTime = existing.DataTime,
                    ReceiveTime = existing.ReceiveTime
                };
            }

            if (existing == null)
            {
                snapshot.CreateTime = receiveTime;
                snapshot.UpdateTime = receiveTime;
                snapshot.Id = await Db.Insertable(snapshot).ExecuteReturnBigIdentityAsync();
                return BuildResult(snapshot, inserted: true);
            }

            snapshot.Id = existing.Id;
            snapshot.CreateTime = existing.CreateTime;
            await Db.Updateable(snapshot).ExecuteCommandAsync();
            return BuildResult(snapshot, inserted: false);
        }
        finally
        {
            UpsertGate.Release();
        }
    }

    /// <summary>分页查询快照，并通过 AutoMapper 还原动态 dataList。</summary>
    public async Task<EdgePagedResultDto<EdgeRealtimeSnapshotOutputDto>> GetPageAsync(
        EdgeRealtimeSnapshotQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        var tenantMark = NormalizeQueryValue(query.TenantMark);
        var modelMark = NormalizeQueryValue(query.ModelMark);
        var deviceCode = NormalizeQueryValue(query.DeviceCode);
        var onlineDeadline = DateTime.Now.Subtract(EdgeRealtimeDataReception.OnlineWindow);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(tenantMark != null, x => x.TenantMark == tenantMark)
            .WhereIF(modelMark != null, x => x.ModelMark == modelMark)
            .WhereIF(deviceCode != null, x => x.DeviceCode == deviceCode)
            .WhereIF(query.DataOnline == true, x => x.ReceiveTime >= onlineDeadline)
            .WhereIF(query.DataOnline == false, x => x.ReceiveTime < onlineDeadline)
            .OrderByDescending(x => x.ReceiveTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeRealtimeSnapshotOutputDto>
        {
            Records = _mapper.Map<List<EdgeRealtimeSnapshotOutputDto>>(records),
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>按租户和设备标识读取唯一的当前快照。</summary>
    public async Task<EdgeRealtimeSnapshotOutputDto?> GetLatestAsync(
        string tenantMark,
        string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(tenantMark) || string.IsNullOrWhiteSpace(deviceCode))
            throw new InvalidOperationException("tenantMark 和 deviceMark 不能为空");

        tenantMark = tenantMark.Trim();
        deviceCode = deviceCode.Trim();
        var record = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == tenantMark && x.DeviceCode == deviceCode);
        return record == null
            ? null
            : _mapper.Map<EdgeRealtimeSnapshotOutputDto>(record);
    }

    /// <summary>校验上报信封，并把新旧两种 data 结构统一成动态数据列表。</summary>
    private static NormalizedRealtimeEnvelope NormalizeAndValidate(EdgeRealtimeDataReceiveDto dto)
    {
        if (dto == null) throw new InvalidOperationException("请求 JSON 不能为空");

        var deviceCode = NormalizeQueryValue(dto.DeviceMark);
        var modelMark = NormalizeQueryValue(dto.ModelMark);
        var tenantMark = NormalizeQueryValue(dto.TenantMark);
        var messageId = NormalizeQueryValue(dto.MessageId);

        if (deviceCode == null) throw new InvalidOperationException("deviceMark 不能为空");
        if (tenantMark == null) throw new InvalidOperationException("tenantMark 不能为空");

        if (deviceCode.Length > 128) throw new InvalidOperationException("deviceMark 不能超过 128 个字符");
        if (modelMark?.Length > 64) throw new InvalidOperationException("modelMark 不能超过 64 个字符");
        if (tenantMark?.Length > 64) throw new InvalidOperationException("tenantMark 不能超过 64 个字符");
        if (messageId?.Length > 128) throw new InvalidOperationException("messageId 不能超过 128 个字符");

        List<EdgeRealtimeDataItemDto> dataList;
        if (dto.Data is { Count: > 0 })
        {
            dataList = [new EdgeRealtimeDataItemDto { Data = dto.Data }];
        }
        else if (dto.DataList is { Count: > 0 })
        {
            dataList = dto.DataList;
        }
        else
        {
            throw new InvalidOperationException("data 必须是非空动态对象");
        }

        if (dataList.Count > MaxDataListCount)
            throw new InvalidOperationException($"dataList 最多包含 {MaxDataListCount} 组 data");
        if (dataList.Any(x => x?.Data == null || x.Data.Count == 0))
            throw new InvalidOperationException("dataList 中每一项都必须包含非空 data 对象");

        return new NormalizedRealtimeEnvelope(
            deviceCode,
            modelMark,
            dto.DataType,
            tenantMark,
            messageId,
            dto.Time,
            dataList);
    }

    /// <summary>解析 dto.time 的 Unix 毫秒时间，避免迟到数据覆盖新快照。</summary>
    private static DateTime ParseDataTime(long? sourceTimestamp, DateTime receiveTime)
    {
        if (sourceTimestamp.HasValue)
        {
            try
            {
                var localTime = DateTimeOffset.FromUnixTimeMilliseconds(sourceTimestamp.Value)
                    .ToLocalTime()
                    .DateTime;
                if (localTime > receiveTime.AddMinutes(5))
                    throw new InvalidOperationException("数据时间不能超过中心时间五分钟");
                return localTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new InvalidOperationException("time 不是有效的 Unix 毫秒时间戳");
            }
        }
        else
        {
            return receiveTime;
        }
    }

    /// <summary>生成新增或覆盖后的统一接收统计。</summary>
    private static EdgeRealtimeReceiveResultDto BuildResult(
        EdgeRealtimeDeviceSnapshotEntity snapshot,
        bool inserted) =>
        new()
        {
            SnapshotId = snapshot.Id,
            InsertedCount = inserted ? 1 : 0,
            UpdatedCount = inserted ? 0 : 1,
            DataTime = snapshot.DataTime,
            ReceiveTime = snapshot.ReceiveTime
        };

    /// <summary>把可选分页条件统一为去除首尾空格后的值。</summary>
    private static string? NormalizeQueryValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record NormalizedRealtimeEnvelope(
        string DeviceCode,
        string? ModelMark,
        int? DataType,
        string? TenantMark,
        string? MessageId,
        long? SourceTimestamp,
        List<EdgeRealtimeDataItemDto> DataList);
}
