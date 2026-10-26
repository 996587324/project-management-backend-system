using System.Text;
using AutoMapper;
using Interface;
using Microsoft.Extensions.Logging;
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
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly IEdgeDashboardTelemetryProcessor _telemetryProcessor;
    private readonly IEdgeDashboardService _dashboardService;
    private readonly ILogger<EdgeRealtimeDataService> _logger;
    private readonly IHourlyProductionIngress? _hourlyIngress;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    /// <summary>通过 IOC 注入项目现有 SqlSugarScope、AutoMapper 和全局功能开关。</summary>
    public EdgeRealtimeDataService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IEdgeGatewayFeatureService featureService,
        IEdgeDashboardTelemetryProcessor telemetryProcessor,
        IEdgeDashboardService dashboardService,
        ILogger<EdgeRealtimeDataService> logger,
        IHourlyProductionIngress? hourlyIngress = null)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _featureService = featureService;
        _telemetryProcessor = telemetryProcessor;
        _dashboardService = dashboardService;
        _logger = logger;
        _hourlyIngress = hourlyIngress;
    }

    /// <summary>
    /// 校验上报信封，并按 tenantMark 和 deviceMark 新增、覆盖或忽略旧快照。
    /// </summary>
    public async Task<EdgeRealtimeReceiveResultDto> ReceiveAsync(
        EdgeRealtimeDataReceiveDto dto,
        CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
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
        // 生产过站与最新快照是两种语义；先完成报文校验，再可靠保存旧时间补传。
        if (_hourlyIngress != null)
        {
            var productionTime = normalized.SourceTimestamp.HasValue
                ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.FromUnixTimeMilliseconds(normalized.SourceTimestamp.Value).UtcDateTime, "China Standard Time")
                : HourlyProductionRules.Now;
            await _hourlyIngress.CaptureRealtimeAsync(normalized.TenantMark ?? "", normalized.DeviceCode,
                normalized.MessageId, productionTime, normalized.DataList.Select(x => JsonConvert.SerializeObject(x.Data)).ToArray(), cancellationToken);
        }

        EdgeRealtimeReceiveResultDto result;
        EdgeDashboardTelemetrySampleDto telemetrySample;
        Dictionary<string, object?> currentData;
        await EdgeRealtimeFieldLatestStore.WriteGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync(x =>
                x.TenantMark == normalized.TenantMark &&
                x.DeviceCode == normalized.DeviceCode);
            var older = existing != null && (snapshot.SourceTimestamp.HasValue && existing.SourceTimestamp.HasValue
                ? snapshot.SourceTimestamp.Value < existing.SourceTimestamp.Value
                : dataTime < existing.DataTime);
            if (older)
            {
                return new EdgeRealtimeReceiveResultDto
                {
                    SnapshotId = existing!.Id,
                    IgnoredOlderCount = 1,
                    DataTime = existing.DataTime,
                    ReceiveTime = existing.ReceiveTime
                };
            }

            // 增量报文可以省略模型元信息，不能因此清掉已确定的设备模型。
            snapshot.ModelMark ??= existing?.ModelMark;
            snapshot.DataType ??= existing?.DataType;
            await Db.Ado.BeginTranAsync();
            try
            {
                currentData = await EdgeRealtimeFieldLatestStore.ApplyAsync(Db, snapshot, existing);
                telemetrySample = new EdgeDashboardTelemetrySampleDto
                {
                    DeviceMark = normalized.DeviceCode,
                    ModelMark = snapshot.ModelMark,
                    DataType = snapshot.DataType,
                    TenantMark = normalized.TenantMark ?? string.Empty,
                    MessageId = normalized.MessageId,
                    SourceTimestamp = normalized.SourceTimestamp,
                    Data = currentData,
                    ReportedData = normalized.DataList.First().Data,
                    DataTime = dataTime,
                    ReceiveTime = receiveTime
                };
                // 只在接收事务内生成任务；微信网络请求由后台独立发送。
                await EdgeDeviceCardPushService.CaptureAsync(Db, telemetrySample, cancellationToken);
                if (existing == null)
                {
                    snapshot.CreateTime = receiveTime;
                    snapshot.UpdateTime = receiveTime;
                    snapshot.Id = await Db.Insertable(snapshot).ExecuteReturnBigIdentityAsync();
                    result = BuildResult(snapshot, inserted: true);
                }
                else
                {
                    snapshot.Id = existing.Id;
                    snapshot.CreateTime = existing.CreateTime;
                    await Db.Updateable(snapshot).ExecuteCommandAsync();
                    result = BuildResult(snapshot, inserted: false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await Db.Ado.CommitTranAsync();
            }
            catch
            {
                await Db.Ado.RollbackTranAsync();
                throw;
            }
        }
        finally
        {
            EdgeRealtimeFieldLatestStore.WriteGate.Release();
        }

        // 快照保存成功后再推进看板统计；失败会返回给上报方重试，原快照仍保持幂等覆盖。
        await _telemetryProcessor.ProcessAsync(telemetrySample, cancellationToken);

        // 追溯配置错误不能阻断上游数据接收；快照已持久化，下一条数据仍可继续生成追溯记录。
        try
        {
            await _dashboardService.TrackDeviceCardChangesAsync(telemetrySample, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "记录设备卡片字段变更失败。Tenant: {TenantMark}, Device: {DeviceMark}",
                telemetrySample.TenantMark, telemetrySample.DeviceMark);
        }
        return result;
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
