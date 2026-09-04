using AutoMapper;
using System.Text.Json;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// 中心端 WebSocket 连接配置业务实现，使用 SqlSugar 持久化并维护设备连接版本。
/// </summary>
public class EdgeWebSocketSourceService : IEdgeWebSocketSourceService
{
    private static readonly JsonSerializerOptions SubscriptionJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>注入项目 SqlSugar 连接和 AutoMapper，表结构由启动初始化服务统一维护。</summary>
    public EdgeWebSocketSourceService(SqlSugarScope dbScope, IMapper mapper)
    {
        _dbScope = dbScope;
        _mapper = mapper;
    }

    /// <summary>
    /// 查询指定设备全部未删除连接，并根据中心队列最后一次有效数据时间计算连接接收状态。
    /// </summary>
    public async Task<List<EdgeWebSocketSourceOutputDto>> GetByDeviceAsync(int deviceId)
    {
        var device = await Db.Queryable<EdgeDeviceEntity>()
            .FirstAsync(x => x.Id == deviceId && x.IsDelete == 0);
        if (device == null) throw new InvalidOperationException("设备不存在");

        var entities = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.DeviceId == deviceId && x.IsDelete == 0)
            .OrderByDescending(x => x.Enabled)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        var result = _mapper.Map<List<EdgeWebSocketSourceOutputDto>>(entities);
        await AttachDataReceptionStatusAsync(device, result);
        await AttachSubscriptionsAsync(result);
        return result;
    }

    /// <summary>
    /// 新增或修改连接。每次保存都会生成更高版本，使 Worker 在配置同步时应用变化。
    /// </summary>
    public async Task<int> SaveAsync(int deviceId, EdgeWebSocketSourceSaveDto dto)
    {
        await EnsureDeviceAsync(deviceId);
        Validate(dto);

        var duplicate = await Db.Queryable<EdgeWebSocketSourceEntity>().AnyAsync(x =>
            x.DeviceId == deviceId && x.IsDelete == 0 && x.SourceId == dto.SourceId.Trim() && x.Id != dto.Id);
        if (duplicate) throw new InvalidOperationException("同一设备下数据源标识不能重复");

        var nextVersion = await GetNextVersionAsync(deviceId);
        if (dto.Id > 0)
        {
            var entity = await Db.Queryable<EdgeWebSocketSourceEntity>()
                .FirstAsync(x => x.Id == dto.Id && x.DeviceId == deviceId && x.IsDelete == 0);
            if (entity == null) throw new InvalidOperationException("WebSocket 连接配置不存在");

            _mapper.Map(dto, entity);
            entity.Version = nextVersion;
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
            await AddOperationAsync("WebSocketSource.Update", entity.SourceId, entity);
            return entity.Id;
        }

        var created = _mapper.Map<EdgeWebSocketSourceEntity>(dto);
        created.DeviceId = deviceId;
        created.Version = nextVersion;
        created.CreateTime = DateTime.Now;
        var id = await Db.Insertable(created).ExecuteReturnIdentityAsync();
        await AddOperationAsync("WebSocketSource.Create", created.SourceId, created);
        return id;
    }

    /// <summary>查询指定连接全部未删除订阅设备及其参数。</summary>
    public async Task<List<EdgeWebSocketSubscriptionDeviceOutputDto>> GetSubscriptionsAsync(int sourceId)
    {
        var sourceExists = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .AnyAsync(x => x.Id == sourceId && x.IsDelete == 0);
        if (!sourceExists) throw new InvalidOperationException("WebSocket 连接配置不存在");
        return await BuildSubscriptionOutputsAsync(sourceId);
    }

    /// <summary>
    /// 在事务中整体替换订阅设备和参数，并单独提升订阅版本以触发在线重新订阅。
    /// </summary>
    public async Task<int> SaveSubscriptionsAsync(int sourceId, EdgeWebSocketSubscriptionSaveDto dto)
    {
        var source = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .FirstAsync(x => x.Id == sourceId && x.IsDelete == 0);
        if (source == null) throw new InvalidOperationException("WebSocket 连接配置不存在");

        ValidateSubscriptions(dto);
        var nextVersion = await GetNextVersionAsync(source.DeviceId);
        var payload = BuildSubscriptionPayload(dto.Devices);
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            var oldDeviceIds = await Db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
                .Where(x => x.WebSocketSourceId == sourceId)
                .Select(x => x.Id)
                .ToListAsync();
            if (oldDeviceIds.Count > 0)
            {
                await Db.Deleteable<EdgeWebSocketSubscriptionParameterEntity>()
                    .Where(x => oldDeviceIds.Contains(x.SubscriptionDeviceId))
                    .ExecuteCommandAsync();
            }
            await Db.Deleteable<EdgeWebSocketSubscriptionDeviceEntity>()
                .Where(x => x.WebSocketSourceId == sourceId)
                .ExecuteCommandAsync();

            foreach (var (deviceDto, deviceIndex) in dto.Devices.Select((item, index) => (item, index)))
            {
                var device = _mapper.Map<EdgeWebSocketSubscriptionDeviceEntity>(deviceDto);
                device.WebSocketSourceId = sourceId;
                device.Sort = deviceDto.Sort == 0 ? deviceIndex : deviceDto.Sort;
                device.CreateTime = DateTime.Now;
                device.UpdateTime = DateTime.Now;
                device.Id = await Db.Insertable(device).ExecuteReturnIdentityAsync();

                var parameters = deviceDto.Parameters
                    .Select((parameterDto, parameterIndex) =>
                    {
                        var parameter = _mapper.Map<EdgeWebSocketSubscriptionParameterEntity>(parameterDto);
                        parameter.SubscriptionDeviceId = device.Id;
                        parameter.Sort = parameterDto.Sort == 0 ? parameterIndex : parameterDto.Sort;
                        parameter.CreateTime = DateTime.Now;
                        parameter.UpdateTime = DateTime.Now;
                        return parameter;
                    })
                    .ToList();
                if (parameters.Count > 0) await Db.Insertable(parameters).ExecuteCommandAsync();
            }

            await Db.Updateable<EdgeWebSocketSourceEntity>()
                .SetColumns(x => new EdgeWebSocketSourceEntity
                {
                    SubscriptionPayload = payload,
                    SubscriptionVersion = nextVersion,
                    UpdateTime = DateTime.Now
                })
                .Where(x => x.Id == sourceId)
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存 WebSocket 订阅事务失败");

        await AddOperationAsync("WebSocketSource.Subscription.Update", source.SourceId,
            new { Version = nextVersion, DeviceCount = dto.Devices.Count, Payload = payload });
        return nextVersion;
    }

    /// <summary>启用或停用单个连接，并提升设备连接版本。</summary>
    public async Task<bool> SetEnabledAsync(int id, bool enabled)
    {
        var entity = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return false;

        entity.Enabled = enabled;
        entity.Version = await GetNextVersionAsync(entity.DeviceId);
        entity.UpdateTime = DateTime.Now;
        await Db.Updateable(entity).ExecuteCommandAsync();
        await AddOperationAsync("WebSocketSource.Enabled", entity.SourceId, new { enabled, entity.Version });
        return true;
    }

    /// <summary>软删除单个连接，使边缘 Worker 在下次配置同步时停止它。</summary>
    public async Task<bool> DeleteAsync(int id)
    {
        var entity = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return false;

        entity.IsDelete = 1;
        entity.Enabled = false;
        entity.Version = await GetNextVersionAsync(entity.DeviceId);
        entity.UpdateTime = DateTime.Now;
        await Db.Updateable(entity).ExecuteCommandAsync();
        await AddOperationAsync("WebSocketSource.Delete", entity.SourceId, new { entity.Version });
        return true;
    }

    /// <summary>
    /// 停用所有设备下已启用连接。按设备提升一次版本，以便在线 Worker 在下一次同步时
    /// 收到空的有效连接清单并立即关闭运行中的连接；配置本身保留，后续仍可单独启用。
    /// </summary>
    public async Task<EdgeWebSocketReconnectResultDto> DisableAllAsync()
    {
        var deviceIds = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled)
            .Select(x => x.DeviceId)
            .Distinct()
            .ToListAsync();

        var connectionCount = 0;
        foreach (var deviceId in deviceIds)
        {
            var nextVersion = await GetNextVersionAsync(deviceId);
            connectionCount += await Db.Updateable<EdgeWebSocketSourceEntity>()
                .SetColumns(x => new EdgeWebSocketSourceEntity
                {
                    Enabled = false,
                    Version = nextVersion,
                    UpdateTime = DateTime.Now
                })
                .Where(x => x.DeviceId == deviceId && x.IsDelete == 0 && x.Enabled)
                .ExecuteCommandAsync();
        }

        var result = new EdgeWebSocketReconnectResultDto
        {
            DeviceCount = deviceIds.Count,
            ConnectionCount = connectionCount
        };
        await AddOperationAsync("WebSocketSource.DisableAll", "All", result);
        return result;
    }

    /// <summary>
    /// 提升指定设备所有已启用连接的运行版本，用原配置触发一次主动重连。
    /// </summary>
    public async Task<EdgeWebSocketReconnectResultDto> ReconnectDeviceAsync(int deviceId)
    {
        await EnsureDeviceAsync(deviceId);
        var connectionCount = await BumpEnabledSourceVersionsAsync(deviceId);
        if (connectionCount == 0)
            throw new InvalidOperationException("该设备没有已启用的 WebSocket 连接");

        var result = new EdgeWebSocketReconnectResultDto
        {
            DeviceCount = 1,
            ConnectionCount = connectionCount
        };
        await AddOperationAsync("WebSocketSource.ReconnectDevice", deviceId.ToString(), result);
        return result;
    }

    /// <summary>按设备分别提升全部已启用连接的运行版本，通知所有在线 Worker 重连。</summary>
    public async Task<EdgeWebSocketReconnectResultDto> ReconnectAllAsync()
    {
        var deviceIds = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled)
            .Select(x => x.DeviceId)
            .Distinct()
            .ToListAsync();
        var connectionCount = 0;
        foreach (var deviceId in deviceIds)
            connectionCount += await BumpEnabledSourceVersionsAsync(deviceId);

        var result = new EdgeWebSocketReconnectResultDto
        {
            DeviceCount = deviceIds.Count,
            ConnectionCount = connectionCount
        };
        await AddOperationAsync("WebSocketSource.ReconnectAll", "All", result);
        return result;
    }

    /// <summary>
    /// 查询设备历史最高连接版本。软删除记录也参与计算，确保删除最后一个连接仍能下发空列表。
    /// </summary>
    public async Task<int> GetVersionAsync(int deviceId)
    {
        var versions = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.DeviceId == deviceId)
            .Select(x => new { x.Version, x.SubscriptionVersion })
            .ToListAsync();
        return versions.Count == 0
            ? 0
            : versions.Max(x => Math.Max(x.Version, x.SubscriptionVersion));
    }

    /// <summary>查询未删除连接并通过 AutoMapper 转换为 Worker 的 SourceOptions。</summary>
    public async Task<List<SourceOptions>> GetEffectiveSourcesAsync(int deviceId)
    {
        var entities = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.DeviceId == deviceId && x.IsDelete == 0)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        var result = _mapper.Map<List<SourceOptions>>(entities);
        if (entities.Count == 0) return result;

        var sourceIds = entities.Select(x => x.Id).ToList();
        var devices = await Db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
            .Where(x => sourceIds.Contains(x.WebSocketSourceId) && x.IsDelete == 0 && x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var deviceIds = devices.Select(x => x.Id).ToList();
        var parameters = deviceIds.Count == 0
            ? new List<EdgeWebSocketSubscriptionParameterEntity>()
            : await Db.Queryable<EdgeWebSocketSubscriptionParameterEntity>()
                .Where(x => deviceIds.Contains(x.SubscriptionDeviceId) && x.IsDelete == 0 && x.Enabled)
                .OrderBy(x => x.Sort)
                .ToListAsync();
        var parametersByDevice = parameters
            .GroupBy(x => x.SubscriptionDeviceId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ParameterName).ToList());
        var devicesBySource = devices
            .Where(x => parametersByDevice.ContainsKey(x.Id))
            .GroupBy(x => x.WebSocketSourceId)
            .ToDictionary(
                x => x.Key,
                x => x.Select(device => new WebSocketDeviceSubscriptionOptions
                {
                    DeviceCode = device.DeviceCode,
                    Parameters = parametersByDevice[device.Id]
                }).ToList());

        for (var index = 0; index < entities.Count; index++)
        {
            var subscriptions = devicesBySource.GetValueOrDefault(entities[index].Id) ?? new();
            result[index].SubscriptionDevices = subscriptions;
            if (subscriptions.Count > 0 || string.IsNullOrWhiteSpace(result[index].SubscriptionPayload))
                result[index].SubscriptionPayload = BuildSubscriptionPayload(subscriptions);
        }
        return result;
    }

    /// <summary>计算指定设备的下一个单调递增连接版本。</summary>
    private async Task<int> GetNextVersionAsync(int deviceId) =>
        await GetVersionAsync(deviceId) + 1;

    /// <summary>批量提升设备下已启用连接的运行版本，返回实际更新数量。</summary>
    private async Task<int> BumpEnabledSourceVersionsAsync(int deviceId)
    {
        var nextVersion = await GetNextVersionAsync(deviceId);
        return await Db.Updateable<EdgeWebSocketSourceEntity>()
            .SetColumns(x => new EdgeWebSocketSourceEntity
            {
                Version = nextVersion,
                UpdateTime = DateTime.Now
            })
            .Where(x => x.DeviceId == deviceId && x.IsDelete == 0 && x.Enabled)
            .ExecuteCommandAsync();
    }

    /// <summary>为连接列表附加结构化订阅设备和参数，供前端编辑。</summary>
    private async Task AttachSubscriptionsAsync(List<EdgeWebSocketSourceOutputDto> sources)
    {
        foreach (var source in sources)
            source.SubscriptionDevices = await BuildSubscriptionOutputsAsync(source.Id);
    }

    /// <summary>
    /// 按边缘设备编码和数据源标识查询最后一次中心入队时间，并转换成连接配置的数据接收状态。
    /// </summary>
    private async Task AttachDataReceptionStatusAsync(
        EdgeDeviceEntity device,
        List<EdgeWebSocketSourceOutputDto> sources)
    {
        var sourceIds = sources.Select(x => x.SourceId).Distinct().ToList();
        var lastDataTimeMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (sourceIds.Count > 0)
        {
            var latestRows = await Db.Queryable<EdgeMessageQueueEntity>()
                .Where(x => x.DeviceCode == device.DeviceCode && sourceIds.Contains(x.SourceId))
                .GroupBy(x => x.SourceId)
                .Select(x => new
                {
                    x.SourceId,
                    LastDataTime = SqlFunc.AggregateMax(x.CreateTime)
                })
                .ToListAsync();
            lastDataTimeMap = latestRows.ToDictionary(
                x => x.SourceId,
                x => x.LastDataTime,
                StringComparer.OrdinalIgnoreCase);
        }

        var onlineDeadline = DateTime.Now.Subtract(EdgeGatewayDataReception.OnlineWindow);
        foreach (var source in sources)
        {
            source.LastDataTime = lastDataTimeMap.TryGetValue(source.SourceId, out var lastDataTime)
                ? lastDataTime
                : null;
            var effectiveEnabled = device.Enabled && source.Enabled;
            source.DataOnline = effectiveEnabled && source.LastDataTime >= onlineDeadline;
            source.DataStatus = !effectiveEnabled
                ? EdgeGatewayDataReception.Disabled
                : source.DataOnline
                    ? EdgeGatewayDataReception.Receiving
                    : source.LastDataTime.HasValue
                        ? EdgeGatewayDataReception.Interrupted
                        : EdgeGatewayDataReception.NeverReceived;
        }
    }

    /// <summary>从两张订阅子表组装单个连接的嵌套输出 DTO。</summary>
    private async Task<List<EdgeWebSocketSubscriptionDeviceOutputDto>> BuildSubscriptionOutputsAsync(int sourceId)
    {
        var devices = await Db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
            .Where(x => x.WebSocketSourceId == sourceId && x.IsDelete == 0)
            .OrderByDescending(x => x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var result = _mapper.Map<List<EdgeWebSocketSubscriptionDeviceOutputDto>>(devices);
        if (devices.Count == 0) return result;

        var deviceIds = devices.Select(x => x.Id).ToList();
        var parameters = await Db.Queryable<EdgeWebSocketSubscriptionParameterEntity>()
            .Where(x => deviceIds.Contains(x.SubscriptionDeviceId) && x.IsDelete == 0)
            .OrderByDescending(x => x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync();
        var grouped = parameters
            .GroupBy(x => x.SubscriptionDeviceId)
            .ToDictionary(x => x.Key, x => _mapper.Map<List<EdgeWebSocketSubscriptionParameterOutputDto>>(x.ToList()));
        for (var index = 0; index < devices.Count; index++)
            result[index].Parameters = grouped.GetValueOrDefault(devices[index].Id) ?? new();
        return result;
    }

    /// <summary>确认目标设备存在且未被软删除。</summary>
    private async Task EnsureDeviceAsync(int deviceId)
    {
        var exists = await Db.Queryable<EdgeDeviceEntity>()
            .AnyAsync(x => x.Id == deviceId && x.IsDelete == 0);
        if (!exists) throw new InvalidOperationException("设备不存在");
    }

    /// <summary>规范化并校验连接标识、地址、JSON、请求头和重连间隔。</summary>
    private static void Validate(EdgeWebSocketSourceSaveDto dto)
    {
        dto.SourceId = dto.SourceId.Trim();
        dto.SourceName = dto.SourceName.Trim();
        dto.Endpoint = dto.Endpoint.Trim();
        dto.DataType = string.IsNullOrWhiteSpace(dto.DataType) ? "Telemetry" : dto.DataType.Trim();

        if (string.IsNullOrWhiteSpace(dto.SourceId)) throw new InvalidOperationException("数据源标识不能为空");
        if (string.IsNullOrWhiteSpace(dto.SourceName)) throw new InvalidOperationException("连接名称不能为空");
        if (dto.SourceId.Length > 64) throw new InvalidOperationException("数据源标识不能超过 64 个字符");
        if (dto.SourceName.Length > 128) throw new InvalidOperationException("连接名称不能超过 128 个字符");
        if (dto.DataType.Length > 64) throw new InvalidOperationException("数据类型不能超过 64 个字符");
        if (dto.ReconnectIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("重连间隔必须在 1 到 300 秒之间");

        if (!Uri.TryCreate(dto.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
            throw new InvalidOperationException("连接地址必须是有效的 ws:// 或 wss:// 地址");

        if (!string.IsNullOrWhiteSpace(dto.SubscriptionPayload))
            ValidateJson(dto.SubscriptionPayload, "订阅消息");
        if (!string.IsNullOrWhiteSpace(dto.HeadersJson))
            ValidateHeaders(dto.HeadersJson);
    }

    /// <summary>校验订阅设备数量、单设备参数数量、字段长度和同级唯一性。</summary>
    private static void ValidateSubscriptions(EdgeWebSocketSubscriptionSaveDto dto)
    {
        if (dto.Devices.Count == 0) throw new InvalidOperationException("至少需要配置一个订阅设备");
        if (dto.Devices.Count > 500) throw new InvalidOperationException("单个连接最多配置 500 个订阅设备");

        var deviceCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledDeviceCount = 0;
        foreach (var device in dto.Devices)
        {
            device.DeviceCode = device.DeviceCode.Trim();
            device.DeviceName = string.IsNullOrWhiteSpace(device.DeviceName) ? null : device.DeviceName.Trim();
            if (string.IsNullOrWhiteSpace(device.DeviceCode))
                throw new InvalidOperationException("订阅设备代码不能为空");
            if (device.DeviceCode.Length > 128)
                throw new InvalidOperationException($"设备代码 {device.DeviceCode} 不能超过 128 个字符");
            if (device.DeviceName?.Length > 128)
                throw new InvalidOperationException($"设备 {device.DeviceCode} 的名称不能超过 128 个字符");
            if (!deviceCodes.Add(device.DeviceCode))
                throw new InvalidOperationException($"订阅设备代码重复：{device.DeviceCode}");
            if (device.Parameters.Count == 0)
                throw new InvalidOperationException($"设备 {device.DeviceCode} 至少需要配置一个参数");
            if (device.Parameters.Count > 200)
                throw new InvalidOperationException($"设备 {device.DeviceCode} 最多配置 200 个参数");

            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enabledParameterCount = 0;
            foreach (var parameter in device.Parameters)
            {
                parameter.ParameterName = parameter.ParameterName.Trim();
                parameter.ParameterNameCn = string.IsNullOrWhiteSpace(parameter.ParameterNameCn)
                    ? null
                    : parameter.ParameterNameCn.Trim();
                parameter.DataType = string.IsNullOrWhiteSpace(parameter.DataType)
                    ? null
                    : parameter.DataType.Trim();
                if (string.IsNullOrWhiteSpace(parameter.ParameterName))
                    throw new InvalidOperationException($"设备 {device.DeviceCode} 存在空参数名称");
                if (parameter.ParameterName.Length > 128)
                    throw new InvalidOperationException($"参数 {parameter.ParameterName} 不能超过 128 个字符");
                if (parameter.ParameterNameCn?.Length > 128)
                    throw new InvalidOperationException($"参数 {parameter.ParameterName} 的中文名称不能超过 128 个字符");
                if (parameter.DataType?.Length > 32)
                    throw new InvalidOperationException($"参数 {parameter.ParameterName} 的数据类型不能超过 32 个字符");
                if (!parameterNames.Add(parameter.ParameterName))
                    throw new InvalidOperationException($"设备 {device.DeviceCode} 的参数重复：{parameter.ParameterName}");
                if (parameter.Enabled) enabledParameterCount++;
            }
            if (device.Enabled)
            {
                if (enabledParameterCount == 0)
                    throw new InvalidOperationException($"启用设备 {device.DeviceCode} 至少需要启用一个参数");
                enabledDeviceCount++;
            }
        }
        if (enabledDeviceCount == 0)
            throw new InvalidOperationException("至少需要启用一个订阅设备");
    }

    /// <summary>根据结构化订阅 DTO 生成上游接口要求的 devices JSON。</summary>
    private static string BuildSubscriptionPayload(IEnumerable<EdgeWebSocketSubscriptionDeviceSaveDto> devices) =>
        JsonSerializer.Serialize(new
        {
            devices = devices
                .Where(x => x.Enabled)
                .Select(x => new
                {
                    deviceCode = x.DeviceCode,
                    parameters = x.Parameters.Where(y => y.Enabled).Select(y => y.ParameterName).ToList()
                })
                .Where(x => x.parameters.Count > 0)
                .ToList()
        }, SubscriptionJsonOptions);

    /// <summary>根据 Worker 运行模型生成上游接口要求的 devices JSON。</summary>
    private static string BuildSubscriptionPayload(IEnumerable<WebSocketDeviceSubscriptionOptions> devices) =>
        JsonSerializer.Serialize(new
        {
            devices = devices.Select(x => new { deviceCode = x.DeviceCode, parameters = x.Parameters }).ToList()
        }, SubscriptionJsonOptions);

    /// <summary>校验可选 JSON 文本是否能被正常解析。</summary>
    private static void ValidateJson(string json, string name)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{name} JSON 格式不正确");
        }
    }

    /// <summary>校验握手请求头必须是字符串键值对象。</summary>
    private static void ValidateHeaders(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any(x => x.Value.ValueKind != JsonValueKind.String))
                throw new InvalidOperationException("请求头必须是文本键值对象");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("请求头 JSON 格式不正确");
        }
    }

    /// <summary>记录连接配置管理操作，便于审计新增、修改、启停、删除和重连。</summary>
    private Task AddOperationAsync(string operationType, string target, object detail) =>
        Db.Insertable(new EdgeOperationLogEntity
        {
            OperationType = operationType,
            Target = target,
            DetailJson = JsonSerializer.Serialize(detail),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
}
