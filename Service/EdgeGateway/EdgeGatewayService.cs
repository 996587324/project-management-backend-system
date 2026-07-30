using AutoMapper;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>
/// EdgeGateway 中心端综合业务服务，使用 SqlSugar 管理设备、版本、规则、队列、事件和 B 系统转发。
/// </summary>
public class EdgeGatewayService : IEdgeGatewayService
{
    private const int MaxStoredResponseContentLength = 32000;
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);
    private static readonly HttpClient BSystemClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim BForwardSemaphore = new(1, 1);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IEdgeWebSocketSourceService _webSocketSourceService;
    private readonly IReadOnlyDictionary<string, IBSystemTransportSender> _bSystemTransportSenders;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    /// <summary>注入 SqlSugar、AutoMapper、数据源服务和 B 系统协议发送器。</summary>
    public EdgeGatewayService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IEdgeWebSocketSourceService webSocketSourceService,
        IEnumerable<IBSystemTransportSender> bSystemTransportSenders)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _webSocketSourceService = webSocketSourceService;
        _bSystemTransportSenders = bSystemTransportSenders.ToDictionary(
            x => x.Protocol,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>汇总设备在线状态、中心队列、未处理告警和最近上传成功率。</summary>
    public async Task<EdgeOverviewDto> GetOverviewAsync()
    {
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var statuses = await Db.Queryable<EdgeDeviceStatusEntity>().ToListAsync();
        var onlineDeadline = DateTime.Now.Subtract(OnlineWindow);
        var onlineIds = statuses.Where(x => x.LastHeartbeat >= onlineDeadline).Select(x => x.DeviceId).ToHashSet();
        var totalUploads = await Db.Queryable<EdgeUploadRecordEntity>().CountAsync();
        var successfulUploads = await Db.Queryable<EdgeUploadRecordEntity>().Where(x => x.Success).CountAsync();

        return new EdgeOverviewDto
        {
            DeviceCount = devices.Count,
            OnlineCount = devices.Count(x => x.Enabled && onlineIds.Contains(x.Id)),
            OfflineCount = devices.Count(x => x.Enabled && !onlineIds.Contains(x.Id)),
            DisabledCount = devices.Count(x => !x.Enabled),
            QueueDepth = await Db.Queryable<EdgeMessageQueueEntity>().Where(x => x.Status == "Pending" || x.Status == "Failed").CountAsync(),
            FailedCount = await Db.Queryable<EdgeMessageQueueEntity>().Where(x => x.Status == "Failed").CountAsync(),
            ActiveAlarmCount = await Db.Queryable<EdgeAlarmRecordEntity>().Where(x => !x.Handled).CountAsync(),
            SuccessfulForwardCount = successfulUploads,
            UploadSuccessRate = totalUploads == 0 ? 0 : Math.Round(successfulUploads * 100m / totalUploads, 2),
            RecentEvents = await Db.Queryable<EdgeSystemEventEntity>().OrderByDescending(x => x.CreateTime).Take(8).ToListAsync()
        };
    }

    /// <summary>分页查询设备，并合并每台设备的最新心跳状态。</summary>
    public async Task<EdgePagedResultDto<EdgeDeviceOutputDto>> GetDevicePageAsync(EdgeDeviceQueryDto query)
    {
        var devices = await Db.Queryable<EdgeDeviceEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Keyword), x =>
                x.DeviceCode.Contains(query.Keyword!) || x.DeviceName.Contains(query.Keyword!) ||
                (x.SiteName != null && x.SiteName.Contains(query.Keyword!)))
            .WhereIF(query.Enabled.HasValue, x => x.Enabled == query.Enabled!.Value)
            .OrderByDescending(x => x.CreateTime)
            .ToListAsync();

        var deviceIds = devices.Select(x => x.Id).ToList();
        var statusMap = deviceIds.Count == 0
            ? new Dictionary<int, EdgeDeviceStatusEntity>()
            : (await Db.Queryable<EdgeDeviceStatusEntity>().Where(x => deviceIds.Contains(x.DeviceId)).ToListAsync())
                .GroupBy(x => x.DeviceId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.LastHeartbeat).First());

        var outputs = devices.Select(device =>
        {
            var output = _mapper.Map<EdgeDeviceOutputDto>(device);
            var status = statusMap.GetValueOrDefault(device.Id);
            if (status != null) _mapper.Map(status, output);
            output.Online = device.Enabled && status?.LastHeartbeat >= DateTime.Now.Subtract(OnlineWindow);
            return output;
        }).ToList();
        if (string.Equals(query.OnlineStatus, "Online", StringComparison.OrdinalIgnoreCase))
            outputs = outputs.Where(x => x.Online).ToList();
        if (string.Equals(query.OnlineStatus, "Offline", StringComparison.OrdinalIgnoreCase))
            outputs = outputs.Where(x => !x.Online).ToList();

        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        return new EdgePagedResultDto<EdgeDeviceOutputDto>
        {
            Records = outputs.Skip((page - 1) * size).Take(size).ToList(),
            Current = page,
            Size = size,
            Total = outputs.Count
        };
    }

    /// <summary>新增或修改设备基础信息，设备编码在未删除记录中必须唯一。</summary>
    public async Task<int> SaveDeviceAsync(EdgeDeviceSaveDto dto)
    {
        dto.DeviceCode = dto.DeviceCode.Trim();
        dto.DeviceName = dto.DeviceName.Trim();
        dto.ConfiguredIpAddress = string.IsNullOrWhiteSpace(dto.ConfiguredIpAddress)
            ? null
            : dto.ConfiguredIpAddress.Trim();
        if (string.IsNullOrWhiteSpace(dto.DeviceCode) || string.IsNullOrWhiteSpace(dto.DeviceName))
            throw new InvalidOperationException("设备编码和设备名称不能为空");
        if (dto.ConfiguredIpAddress != null && !IPAddress.TryParse(dto.ConfiguredIpAddress, out _))
            throw new InvalidOperationException("配置 IP 不是有效的 IPv4 或 IPv6 地址");

        var duplicate = await Db.Queryable<EdgeDeviceEntity>()
            .AnyAsync(x => x.IsDelete == 0 && x.DeviceCode == dto.DeviceCode && x.Id != dto.Id);
        if (duplicate) throw new InvalidOperationException("设备编码已存在");

        if (dto.Id > 0)
        {
            var entity = await Db.Queryable<EdgeDeviceEntity>().FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
            if (entity == null) throw new InvalidOperationException("设备不存在");
            _mapper.Map(dto, entity);
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
            await AddOperationAsync("Device.Update", dto.DeviceCode, dto);
            return entity.Id;
        }

        var created = _mapper.Map<EdgeDeviceEntity>(dto);
        created.CreateTime = DateTime.Now;
        var id = await Db.Insertable(created).ExecuteReturnIdentityAsync();
        await AddOperationAsync("Device.Create", dto.DeviceCode, dto);
        return id;
    }

    /// <summary>启用或停用指定设备。</summary>
    public async Task<bool> SetDeviceEnabledAsync(int id, bool enabled)
    {
        var changed = await Db.Updateable<EdgeDeviceEntity>()
            .SetColumns(x => new EdgeDeviceEntity { Enabled = enabled, UpdateTime = DateTime.Now })
            .Where(x => x.Id == id && x.IsDelete == 0)
            .ExecuteCommandHasChangeAsync();
        if (changed) await AddOperationAsync("Device.Enabled", id.ToString(), new { enabled });
        return changed;
    }

    /// <summary>
    /// 软删除设备主数据。删除记录作为设备编码墓碑保留，避免在线 Worker 的后续心跳自动注册回来。
    /// </summary>
    public async Task<bool> DeleteDeviceAsync(int id)
    {
        var device = await Db.Queryable<EdgeDeviceEntity>()
            .FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (device == null) return false;

        device.Enabled = false;
        device.IsDelete = 1;
        device.UpdateTime = DateTime.Now;
        await Db.Updateable(device).ExecuteCommandAsync();
        await AddOperationAsync(
            "Device.Delete",
            device.DeviceCode,
            new { device.Id, device.DeviceCode });
        return true;
    }

    /// <summary>
    /// 接收边缘节点心跳，更新设备状态和告警，并返回中心端三个最高版本号。
    /// </summary>
    public async Task<EdgeHeartbeatResultDto> ReceiveHeartbeatAsync(EdgeHeartbeatDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode)) throw new InvalidOperationException("设备编码不能为空");
        var device = await Db.Queryable<EdgeDeviceEntity>().FirstAsync(x => x.DeviceCode == dto.DeviceCode && x.IsDelete == 0);
        if (device == null)
        {
            // 已删除记录作为墓碑存在时只返回停用状态，防止在线设备被心跳立即重新创建。
            var deletedDeviceExists = await Db.Queryable<EdgeDeviceEntity>()
                .AnyAsync(x => x.DeviceCode == dto.DeviceCode && x.IsDelete == 1);
            if (deletedDeviceExists)
            {
                return new EdgeHeartbeatResultDto
                {
                    Enabled = false,
                    ServerTime = DateTime.Now
                };
            }

            device = _mapper.Map<EdgeDeviceEntity>(dto);
            device.CreateTime = DateTime.Now;
            device.Id = await Db.Insertable(device).ExecuteReturnIdentityAsync();
            await AddEventAsync(dto.DeviceCode, "Info", "Device.Registered", "边缘设备已自动注册", null);
        }

        var status = await Db.Queryable<EdgeDeviceStatusEntity>().FirstAsync(x => x.DeviceId == device.Id);
        if (status == null)
        {
            status = _mapper.Map<EdgeDeviceStatusEntity>(dto);
            status.DeviceId = device.Id;
            await Db.Insertable(status).ExecuteCommandAsync();
        }
        else
        {
            _mapper.Map(dto, status);
            await Db.Updateable(status).ExecuteCommandAsync();
        }

        if (!string.IsNullOrWhiteSpace(dto.LastError))
        {
            await AddEventAsync(dto.DeviceCode, "Error", "Gateway.Error", dto.LastError, null);
            var hasRecentAlarm = await Db.Queryable<EdgeAlarmRecordEntity>().AnyAsync(x =>
                x.DeviceCode == dto.DeviceCode && !x.Handled && x.CreateTime >= DateTime.Now.AddMinutes(-10));
            if (!hasRecentAlarm)
            {
                await Db.Insertable(new EdgeAlarmRecordEntity
                {
                    DeviceCode = dto.DeviceCode,
                    AlarmType = "GatewayError",
                    Level = "Error",
                    Message = dto.LastError,
                    CreateTime = DateTime.Now
                }).ExecuteCommandAsync();
            }
        }

        var configVersion = await Db.Queryable<EdgeDeviceConfigEntity>()
            .Where(x => x.DeviceId == device.Id && x.IsDelete == 0 && x.IsPublished)
            .MaxAsync(x => x.Version);
        var ruleVersion = await Db.Queryable<EdgeRuleVersionEntity>()
            .Where(x => x.DeviceId == device.Id || x.DeviceId == null).MaxAsync(x => x.Version);
        var sourceVersion = await _webSocketSourceService.GetVersionAsync(device.Id);
        return new EdgeHeartbeatResultDto
        {
            Enabled = device.Enabled,
            ServerTime = DateTime.Now,
            ConfigVersion = configVersion,
            RuleVersion = ruleVersion,
            SourceVersion = sourceVersion
        };
    }

    /// <summary>按版本倒序查询指定设备的采集配置历史。</summary>
    public Task<List<EdgeDeviceConfigEntity>> GetDeviceConfigsAsync(int deviceId) =>
        Db.Queryable<EdgeDeviceConfigEntity>().Where(x => x.DeviceId == deviceId && x.IsDelete == 0)
            .OrderByDescending(x => x.Version).ToListAsync();

    /// <summary>校验并保存新的采集配置草稿，版本号按设备递增。</summary>
    public async Task<int> SaveDeviceConfigAsync(int deviceId, EdgeDeviceConfigSaveDto dto)
    {
        await EnsureDeviceAsync(deviceId);
        ValidateJson(dto.ConfigJson, "配置 JSON");
        var maxVersion = await Db.Queryable<EdgeDeviceConfigEntity>()
            .Where(x => x.DeviceId == deviceId).MaxAsync(x => x.Version);
        var entity = _mapper.Map<EdgeDeviceConfigEntity>(dto);
        entity.DeviceId = deviceId;
        entity.Version = maxVersion + 1;
        entity.ConfigName = string.IsNullOrWhiteSpace(dto.ConfigName) ? $"配置 v{maxVersion + 1}" : entity.ConfigName;
        entity.CreateTime = DateTime.Now;
        var id = await Db.Insertable(entity).ExecuteReturnIdentityAsync();
        await AddOperationAsync("Config.Create", id.ToString(), entity);
        return id;
    }

    /// <summary>发布指定采集配置，并取消同设备之前的已发布状态。</summary>
    public async Task<bool> PublishDeviceConfigAsync(int configId, string? publishedBy)
    {
        var config = await Db.Queryable<EdgeDeviceConfigEntity>()
            .FirstAsync(x => x.Id == configId && x.IsDelete == 0);
        if (config == null) return false;
        await Db.Updateable<EdgeDeviceConfigEntity>()
            .SetColumns(x => new EdgeDeviceConfigEntity { IsPublished = false })
            .Where(x => x.DeviceId == config.DeviceId && x.IsDelete == 0 && x.IsPublished)
            .ExecuteCommandAsync();
        config.IsPublished = true;
        config.PublishTime = DateTime.Now;
        await Db.Updateable(config).ExecuteCommandAsync();
        var version = _mapper.Map<EdgeConfigVersionEntity>(config);
        version.PublishedBy = publishedBy;
        version.PublishTime = DateTime.Now;
        await Db.Insertable(version).ExecuteCommandAsync();
        await AddOperationAsync("Config.Publish", configId.ToString(), new { config.Version, publishedBy });
        return true;
    }

    /// <summary>
    /// 软删除采集配置。当前生效版本必须先由其他版本替换，避免在线 Worker 突然失去映射配置。
    /// </summary>
    public async Task<bool> DeleteDeviceConfigAsync(int configId)
    {
        var config = await Db.Queryable<EdgeDeviceConfigEntity>()
            .FirstAsync(x => x.Id == configId && x.IsDelete == 0);
        if (config == null) return false;
        if (config.IsPublished)
            throw new InvalidOperationException("当前生效的采集配置不能删除，请先新建并发布更高版本");

        var changed = await Db.Updateable<EdgeDeviceConfigEntity>()
            .SetColumns(x => new EdgeDeviceConfigEntity { IsDelete = 1 })
            .Where(x => x.Id == configId && x.IsDelete == 0)
            .ExecuteCommandAsync() > 0;
        if (changed)
            await AddOperationAsync(
                "Config.Delete",
                configId.ToString(),
                new { config.DeviceId, config.Version, config.ConfigName });
        return changed;
    }

    /// <summary>
    /// 按设备编码组装 Worker 生效配置，包括 WebSocket、字段映射和规则快照。
    /// </summary>
    public async Task<EdgeEffectiveConfigDto?> GetEffectiveConfigAsync(string deviceCode)
    {
        var device = await Db.Queryable<EdgeDeviceEntity>().FirstAsync(x => x.DeviceCode == deviceCode && x.IsDelete == 0);
        if (device == null) return null;
        var config = await Db.Queryable<EdgeDeviceConfigEntity>()
            .Where(x => x.DeviceId == device.Id && x.IsDelete == 0 && x.IsPublished)
            .OrderByDescending(x => x.Version).FirstAsync();
        var ruleVersion = await Db.Queryable<EdgeRuleVersionEntity>()
            .Where(x => x.DeviceId == device.Id || x.DeviceId == null)
            .OrderByDescending(x => x.PublishTime).FirstAsync();
        var activeRules = await Db.Queryable<EdgeRuleEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled && (x.DeviceId == device.Id || x.DeviceId == null))
            .OrderBy(x => x.Priority).ToListAsync();
        var sourceVersion = await _webSocketSourceService.GetVersionAsync(device.Id);
        var sources = sourceVersion > 0
            ? await _webSocketSourceService.GetEffectiveSourcesAsync(device.Id)
            : new List<SourceOptions>();
        return new EdgeEffectiveConfigDto
        {
            DeviceCode = deviceCode,
            Enabled = device.Enabled,
            SourceVersion = sourceVersion,
            Sources = sources,
            ConfigVersion = config?.Version ?? 0,
            ConfigJson = config?.ConfigJson ?? "{}",
            RuleVersion = ruleVersion?.Version ?? 0,
            RulesJson = ruleVersion?.SnapshotJson ?? JsonSerializer.Serialize(activeRules)
        };
    }

    /// <summary>分页查询设备专属规则和全局规则。</summary>
    public async Task<EdgePagedResultDto<EdgeRuleEntity>> GetRulePageAsync(EdgeRuleQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeRuleEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(query.DeviceId.HasValue, x => x.DeviceId == query.DeviceId || x.DeviceId == null)
            .WhereIF(!string.IsNullOrWhiteSpace(query.RuleType), x => x.RuleType == query.RuleType)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Keyword), x =>
                x.RuleCode.Contains(query.Keyword!) || x.RuleName.Contains(query.Keyword!))
            .OrderBy(x => x.Priority).OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeRuleEntity> { Records = records, Current = page, Size = size, Total = total };
    }

    /// <summary>新增或修改规则定义，并校验规则编码和规则 JSON。</summary>
    public async Task<int> SaveRuleAsync(EdgeRuleSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.RuleCode) || string.IsNullOrWhiteSpace(dto.RuleName))
            throw new InvalidOperationException("规则编码和规则名称不能为空");
        ValidateJson(dto.RuleJson, "规则 JSON");
        var duplicate = await Db.Queryable<EdgeRuleEntity>().AnyAsync(x =>
            x.IsDelete == 0 && x.RuleCode == dto.RuleCode && x.DeviceId == dto.DeviceId && x.Id != dto.Id);
        if (duplicate) throw new InvalidOperationException("同一设备下规则编码已存在");

        if (dto.Id > 0)
        {
            var entity = await Db.Queryable<EdgeRuleEntity>().FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
            if (entity == null) throw new InvalidOperationException("规则不存在");
            _mapper.Map(dto, entity);
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
            await AddOperationAsync("Rule.Update", dto.RuleCode, dto);
            return entity.Id;
        }

        var created = _mapper.Map<EdgeRuleEntity>(dto);
        created.CreateTime = DateTime.Now;
        var id = await Db.Insertable(created).ExecuteReturnIdentityAsync();
        await AddOperationAsync("Rule.Create", dto.RuleCode, dto);
        return id;
    }

    /// <summary>把当前启用规则序列化为新的不可变发布快照。</summary>
    public async Task<int> PublishRulesAsync(EdgeRulePublishDto dto)
    {
        var rules = await Db.Queryable<EdgeRuleEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled && (x.DeviceId == dto.DeviceId || x.DeviceId == null))
            .OrderBy(x => x.Priority).ToListAsync();
        var maxVersion = await Db.Queryable<EdgeRuleVersionEntity>()
            .Where(x => x.DeviceId == dto.DeviceId).MaxAsync(x => x.Version);
        var version = _mapper.Map<EdgeRuleVersionEntity>(dto);
        version.Version = maxVersion + 1;
        version.SnapshotJson = JsonSerializer.Serialize(rules);
        version.PublishTime = DateTime.Now;
        var id = await Db.Insertable(version).ExecuteReturnIdentityAsync();
        await AddOperationAsync("Rule.Publish", dto.DeviceId?.ToString() ?? "Global", new { version.Version, dto.Remark });
        return id;
    }

    /// <summary>复制历史规则快照生成新版本，保留完整回滚审计链。</summary>
    public async Task<int> RollbackRulesAsync(EdgeRuleRollbackDto dto)
    {
        var source = await Db.Queryable<EdgeRuleVersionEntity>().FirstAsync(x => x.Id == dto.VersionId);
        if (source == null) throw new InvalidOperationException("规则版本不存在");
        var maxVersion = await Db.Queryable<EdgeRuleVersionEntity>()
            .Where(x => x.DeviceId == source.DeviceId).MaxAsync(x => x.Version);
        var version = _mapper.Map<EdgeRuleVersionEntity>(dto);
        version.DeviceId = source.DeviceId;
        version.Version = maxVersion + 1;
        version.SnapshotJson = source.SnapshotJson;
        version.Remark = string.IsNullOrWhiteSpace(dto.Remark) ? $"回滚至 v{source.Version}" : dto.Remark;
        version.PublishTime = DateTime.Now;
        var id = await Db.Insertable(version).ExecuteReturnIdentityAsync();
        await AddOperationAsync("Rule.Rollback", source.DeviceId?.ToString() ?? "Global",
            new { SourceVersionId = source.Id, SourceVersion = source.Version, NewVersion = version.Version });
        return id;
    }

    /// <summary>查询最近 50 个设备或全局规则版本。</summary>
    public Task<List<EdgeRuleVersionEntity>> GetRuleVersionsAsync(int? deviceId) =>
        Db.Queryable<EdgeRuleVersionEntity>().WhereIF(deviceId.HasValue, x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.PublishTime).Take(50).ToListAsync();

    /// <summary>分页查询中心端等待转发或已经处理的消息队列。</summary>
    public async Task<EdgePagedResultDto<EdgeMessageQueueEntity>> GetQueuePageAsync(EdgeQueueQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeMessageQueueEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.DeviceCode), x => x.DeviceCode == query.DeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status)
            .WhereIF(!string.IsNullOrWhiteSpace(query.MessageId), x => x.MessageId.Contains(query.MessageId!))
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeMessageQueueEntity> { Records = records, Current = page, Size = size, Total = total };
    }

    /// <summary>分页查询每次中心端到 B 系统的上传结果，包括响应码、响应内容、耗时和错误原因。</summary>
    public async Task<EdgePagedResultDto<EdgeUploadRecordEntity>> GetUploadRecordPageAsync(
        EdgeUploadRecordQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeUploadRecordEntity>()
            .WhereIF(query.QueueId.HasValue, x => x.QueueId == query.QueueId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.DeviceCode), x => x.DeviceCode == query.DeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.MessageId), x => x.MessageId == query.MessageId)
            .WhereIF(query.Success.HasValue, x => x.Success == query.Success!.Value)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeUploadRecordEntity>
        {
            Records = records,
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>接收边缘批量消息，以 MessageId 去重后写入中心 Pending 队列。</summary>
    public async Task<EdgeMessageAcceptResultDto> ReceiveMessagesAsync(EdgeMessageBatchDto dto)
    {
        var result = new EdgeMessageAcceptResultDto();
        var candidates = dto.Messages.Where(x => !string.IsNullOrWhiteSpace(x.MessageId)).ToList();
        result.InvalidCount = dto.Messages.Count - candidates.Count;
        var ids = candidates.Select(x => x.MessageId).Distinct().ToList();
        var existingIds = ids.Count == 0
            ? new HashSet<string>()
            : (await Db.Queryable<EdgeMessageQueueEntity>().Where(x => ids.Contains(x.MessageId)).Select(x => x.MessageId).ToListAsync()).ToHashSet();

        foreach (var message in candidates)
        {
            if (!existingIds.Add(message.MessageId))
            {
                result.DuplicateCount++;
                continue;
            }
            var deviceCode = string.IsNullOrWhiteSpace(message.DeviceId) ? dto.DeviceId : message.DeviceId;
            if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(message.SourceId))
            {
                result.InvalidCount++;
                continue;
            }
            var queueEntity = _mapper.Map<EdgeMessageQueueEntity>(message);
            queueEntity.DeviceCode = deviceCode;
            await Db.Insertable(queueEntity).ExecuteCommandAsync();
            result.AcceptedCount++;
        }
        return result;
    }

    /// <summary>把指定失败队列记录重置为 Pending，允许再次转发。</summary>
    public async Task<int> ResetQueueAsync(EdgeRetryRequestDto dto)
    {
        var query = Db.Updateable<EdgeMessageQueueEntity>()
            .SetColumns(x => new EdgeMessageQueueEntity
            {
                Status = "Pending",
                NextRetryTime = DateTime.Now,
                LastError = null
            });
        query = dto.Ids.Count > 0
            ? query.Where(x => dto.Ids.Contains(x.Id) && x.Status != "Uploaded")
            : query.Where(x => x.Status == "Failed");
        var count = await query.ExecuteCommandAsync();
        if (count > 0) await AddOperationAsync("Queue.Reset", "MessageQueue", new { dto.Ids, count });
        return count;
    }

    /// <summary>
    /// 按设备或全局 B 映射转换消息，再通过配置的 HTTP 或扩展协议逐条发送并记录结果。
    /// </summary>
    public async Task<EdgeRetryResultDto> ForwardQueueAsync(EdgeRetryRequestDto dto)
    {
        await BForwardSemaphore.WaitAsync();
        try
        {
            var records = await Db.Queryable<EdgeMessageQueueEntity>()
                .WhereIF(dto.Ids.Count > 0, x => dto.Ids.Contains(x.Id))
                .Where(x => x.Status == "Pending" || x.Status == "Failed")
                .OrderBy(x => x.CreateTime).Take(100).ToListAsync();
            return await ForwardRecordsAsync(records, CancellationToken.None);
        }
        finally
        {
            BForwardSemaphore.Release();
        }
    }

    /// <summary>串行读取重试时间已经到期的消息，供中心后台自动转发使用。</summary>
    public async Task<EdgeRetryResultDto> ForwardDueQueueAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await BForwardSemaphore.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.Now;
            var records = await Db.Queryable<EdgeMessageQueueEntity>()
                .Where(x => (x.Status == "Pending" || x.Status == "Failed") &&
                            (x.NextRetryTime == null || x.NextRetryTime <= now))
                .OrderBy(x => x.CreateTime)
                .Take(Math.Clamp(limit, 1, 500))
                .ToListAsync();
            return await ForwardRecordsAsync(records, cancellationToken);
        }
        finally
        {
            BForwardSemaphore.Release();
        }
    }

    /// <summary>使用当前有效映射逐条转发已经选出的中心队列记录。</summary>
    private async Task<EdgeRetryResultDto> ForwardRecordsAsync(
        IReadOnlyCollection<EdgeMessageQueueEntity> records,
        CancellationToken cancellationToken)
    {
        var result = new EdgeRetryResultDto { RequestedCount = records.Count };
        if (records.Count == 0) return result;

        var mappings = await Db.Queryable<EdgeBMappingRuleEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled).OrderByDescending(x => x.DeviceId).ToListAsync();
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceIdMap = devices.ToDictionary(x => x.DeviceCode, x => x.Id);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deviceId = deviceIdMap.GetValueOrDefault(record.DeviceCode);
            var mapping = mappings.FirstOrDefault(x => x.DeviceId == deviceId) ?? mappings.FirstOrDefault(x => x.DeviceId == null);
            if (mapping == null)
            {
                await MarkForwardFailedAsync(record, null, "未配置可用的 B 系统映射");
                result.FailedCount++;
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var payload = BuildBPayload(record.StandardPayloadJson, mapping.MappingJson);
                var transportResult = await SendBPayloadAsync(mapping, payload, cancellationToken);
                stopwatch.Stop();
                if (!transportResult.Success)
                {
                    await MarkForwardFailedAsync(
                        record,
                        transportResult.Target,
                        transportResult.ErrorMessage ?? "B 系统转发失败",
                        transportResult.StatusCode,
                        (int)stopwatch.ElapsedMilliseconds,
                        transportResult.ResponseContent);
                    result.FailedCount++;
                    continue;
                }

                record.Status = "Uploaded";
                record.UploadTime = DateTime.Now;
                record.LastError = null;
                await Db.Updateable(record).ExecuteCommandAsync();
                await AddUploadRecordAsync(
                    record,
                    transportResult.Target,
                    true,
                    transportResult.StatusCode,
                    null,
                    (int)stopwatch.ElapsedMilliseconds,
                    transportResult.ResponseContent);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await MarkForwardFailedAsync(record, mapping.TargetUrl, ex.Message, null, (int)stopwatch.ElapsedMilliseconds);
                result.FailedCount++;
            }
        }
        return result;
    }

    /// <summary>分页查询中心端保存的边缘网关运行事件。</summary>
    public async Task<EdgePagedResultDto<EdgeSystemEventEntity>> GetEventPageAsync(EdgeEventQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeSystemEventEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.DeviceCode), x => x.DeviceCode == query.DeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Level), x => x.Level == query.Level)
            .WhereIF(!string.IsNullOrWhiteSpace(query.EventType), x => x.EventType.Contains(query.EventType!))
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeSystemEventEntity> { Records = records, Current = page, Size = size, Total = total };
    }

    /// <summary>保存边缘节点上报的运行事件。</summary>
    public Task<long> ReportEventAsync(EdgeEventReportDto dto)
    {
        var entity = _mapper.Map<EdgeSystemEventEntity>(dto);
        return Db.Insertable(entity).ExecuteReturnBigIdentityAsync();
    }

    /// <summary>查询全部未删除的 B 系统映射配置。</summary>
    public async Task<List<EdgeBMappingOutputDto>> GetBMappingRulesAsync()
    {
        var entities = await Db.Queryable<EdgeBMappingRuleEntity>().Where(x => x.IsDelete == 0)
            .OrderByDescending(x => x.CreateTime).ToListAsync();
        return _mapper.Map<List<EdgeBMappingOutputDto>>(entities);
    }

    /// <summary>新增或修改 B 系统协议、目标地址、字段映射和协议参数。</summary>
    public async Task<int> SaveBMappingRuleAsync(EdgeBMappingSaveDto dto)
    {
        NormalizeAndValidateBMapping(dto);
        var templateName = await GetPayloadTemplateNameAsync(dto.PayloadTemplateId);

        if (dto.Id > 0)
        {
            var entity = await Db.Queryable<EdgeBMappingRuleEntity>().FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
            if (entity == null) throw new InvalidOperationException("B 系统映射不存在");
            _mapper.Map(dto, entity);
            entity.PayloadTemplateName = templateName;
            entity.UpdateTime = DateTime.Now;
            await Db.Updateable(entity).ExecuteCommandAsync();
            await AddOperationAsync("BMapping.Update", dto.Id.ToString(), BuildBMappingAuditDetail(dto));
            return entity.Id;
        }

        var created = _mapper.Map<EdgeBMappingRuleEntity>(dto);
        created.PayloadTemplateName = templateName;
        created.CreateTime = DateTime.Now;
        var id = await Db.Insertable(created).ExecuteReturnIdentityAsync();
        await AddOperationAsync("BMapping.Create", id.ToString(), BuildBMappingAuditDetail(dto));
        return id;
    }

    /// <summary>确认模板仍存在，并返回写入映射快照的模板名称。</summary>
    private async Task<string?> GetPayloadTemplateNameAsync(int? templateId)
    {
        if (!templateId.HasValue) return null;
        var template = await Db.Queryable<EdgeBPayloadTemplateEntity>()
            .FirstAsync(x => x.Id == templateId.Value && x.IsDelete == 0);
        if (template == null) throw new InvalidOperationException("选择的 B 系统模板不存在");
        return template.TemplateName;
    }

    /// <summary>确认中心端设备存在且未被软删除。</summary>
    private async Task EnsureDeviceAsync(int deviceId)
    {
        if (!await Db.Queryable<EdgeDeviceEntity>().AnyAsync(x => x.Id == deviceId && x.IsDelete == 0))
            throw new InvalidOperationException("设备不存在");
    }

    /// <summary>写入一条中心端系统事件并返回主键。</summary>
    private async Task<long> AddEventAsync(string deviceCode, string level, string eventType, string message, string? detailJson)
    {
        var id = await Db.Insertable(new EdgeSystemEventEntity
        {
            DeviceCode = deviceCode,
            Level = level,
            EventType = eventType,
            Message = message,
            DetailJson = detailJson,
            CreateTime = DateTime.Now
        }).ExecuteReturnIdentityAsync();
        return id;
    }

    /// <summary>写入一条 EdgeGateway 管理操作审计日志。</summary>
    private Task AddOperationAsync(string operationType, string target, object detail) =>
        Db.Insertable(new EdgeOperationLogEntity
        {
            OperationType = operationType,
            Target = target,
            DetailJson = JsonSerializer.Serialize(detail),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();

    /// <summary>把中心队列记录标记为转发失败并保存失败原因。</summary>
    private async Task MarkForwardFailedAsync(
        EdgeMessageQueueEntity record,
        string? targetUrl,
        string error,
        int? statusCode = null,
        int durationMs = 0,
        string? responseContent = null)
    {
        record.Status = "Failed";
        record.RetryCount++;
        record.LastError = error.Length > 500 ? error[..500] : error;
        record.NextRetryTime = DateTime.Now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(record.RetryCount, 6))));
        await Db.Updateable(record).ExecuteCommandAsync();
        await AddUploadRecordAsync(
            record,
            targetUrl,
            false,
            statusCode,
            error,
            durationMs,
            responseContent);
    }

    /// <summary>记录一次中心端到 B 系统的协议转发尝试，并限制响应内容长度以控制日志表体积。</summary>
    private Task AddUploadRecordAsync(
        EdgeMessageQueueEntity record,
        string? targetUrl,
        bool success,
        int? statusCode,
        string? error,
        int durationMs,
        string? responseContent)
    {
        var uploadRecord = _mapper.Map<EdgeUploadRecordEntity>(record);
        uploadRecord.TargetUrl = targetUrl;
        uploadRecord.Success = success;
        uploadRecord.StatusCode = statusCode;
        uploadRecord.ErrorMessage = error != null && error.Length > 500 ? error[..500] : error;
        uploadRecord.ResponseContentTruncated =
            responseContent?.Length > MaxStoredResponseContentLength;
        uploadRecord.ResponseContent = responseContent?.Length > MaxStoredResponseContentLength
            ? responseContent[..MaxStoredResponseContentLength]
            : responseContent;
        uploadRecord.DurationMs = durationMs;
        uploadRecord.CreateTime = DateTime.Now;
        return Db.Insertable(uploadRecord).ExecuteCommandAsync();
    }

    /// <summary>按协议选择内置 HTTP 发送或 IOC 注入的扩展协议发送器。</summary>
    private async Task<EdgeBTransportResult> SendBPayloadAsync(
        EdgeBMappingRuleEntity mapping,
        string payload,
        CancellationToken cancellationToken)
    {
        var protocol = string.IsNullOrWhiteSpace(mapping.Protocol)
            ? "HTTP"
            : mapping.Protocol.Trim().ToUpperInvariant();
        if (protocol == "HTTP")
            return await SendHttpBPayloadAsync(mapping, payload, cancellationToken);
        if (!_bSystemTransportSenders.TryGetValue(protocol, out var sender))
            return new EdgeBTransportResult(false, mapping.TargetUrl, null, $"不支持的 B 系统协议：{protocol}");
        return await sender.SendAsync(mapping, payload, cancellationToken);
    }

    /// <summary>发送 HTTP JSON，并把响应码、响应正文或通信异常转换成统一协议结果。</summary>
    private static async Task<EdgeBTransportResult> SendHttpBPayloadAsync(
        EdgeBMappingRuleEntity mapping,
        string payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(mapping.HttpMethod), mapping.TargetUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            ApplyHeaders(request, mapping.HeadersJson);
            using var response = await BSystemClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new EdgeBTransportResult(
                    false,
                    mapping.TargetUrl,
                    statusCode,
                    $"B 系统返回 HTTP {statusCode}",
                    responseContent);
            }

            // 部分 B 系统始终返回 HTTP 200，需要继续校验响应 JSON 中的业务 code。
            if (TryGetBSystemBusinessFailure(responseContent, out var businessError))
            {
                return new EdgeBTransportResult(
                    false,
                    mapping.TargetUrl,
                    statusCode,
                    businessError,
                    responseContent);
            }

            return new EdgeBTransportResult(
                true,
                mapping.TargetUrl,
                statusCode,
                ResponseContent: responseContent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            return new EdgeBTransportResult(
                false,
                mapping.TargetUrl,
                null,
                $"请求 B 系统超时：{ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            int? statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null;
            return new EdgeBTransportResult(
                false,
                mapping.TargetUrl,
                statusCode,
                $"请求 B 系统失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new EdgeBTransportResult(
                false,
                mapping.TargetUrl,
                null,
                $"B 系统 HTTP 转发异常：{ex.Message}");
        }

    }

    /// <summary>
    /// 解析 B 系统响应中的业务 code；存在 code 时仅 200 表示成功，同时提取 msg 或 message 作为失败原因。
    /// </summary>
    private static bool TryGetBSystemBusinessFailure(string responseContent, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(responseContent)) return false;

        try
        {
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetJsonPropertyIgnoreCase(root, "code", out var codeElement))
                return false;

            var codeText = codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()?.Trim()
                : codeElement.GetRawText();
            if (int.TryParse(codeText, out var businessCode) && businessCode == 200)
                return false;

            string? message = null;
            if (TryGetJsonPropertyIgnoreCase(root, "msg", out var messageElement) ||
                TryGetJsonPropertyIgnoreCase(root, "message", out messageElement))
            {
                message = messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : messageElement.GetRawText();
            }

            error = string.IsNullOrWhiteSpace(message)
                ? $"B 系统业务返回失败：code={codeText ?? "null"}"
                : $"B 系统业务返回失败：code={codeText ?? "null"}，{message}";
            return true;
        }
        catch (JsonException)
        {
            // 无法解析或不包含业务 code 的旧接口继续沿用 HTTP 状态判断。
            return false;
        }
    }

    /// <summary>不区分大小写读取 JSON 对象属性，兼容 code、Code 等返回格式。</summary>
    private static bool TryGetJsonPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 按 B 系统 MappingJson 递归处理对象和数组；旧的根节点平面映射保持原有行为。
    /// </summary>
    private static string BuildBPayload(string standardPayloadJson, string mappingJson)
    {
        using var payloadDocument = JsonDocument.Parse(standardPayloadJson);
        using var mappingDocument = JsonDocument.Parse(mappingJson);
        var mappingRoot = mappingDocument.RootElement;
        if (mappingRoot.ValueKind == JsonValueKind.Object &&
            !mappingRoot.EnumerateObject().Any()) return standardPayloadJson;

        if (IsLegacyFlatMapping(mappingRoot))
        {
            var legacyResult = new Dictionary<string, object?>();
            foreach (var mapping in mappingRoot.EnumerateObject())
            {
                if (mapping.Value.ValueKind != JsonValueKind.String) continue;
                legacyResult[mapping.Name] = ResolveJsonPath(
                    payloadDocument.RootElement,
                    mapping.Value.GetString() ?? string.Empty);
            }
            return JsonSerializer.Serialize(legacyResult);
        }

        var result = BuildBPayloadNode(mappingRoot, payloadDocument.RootElement);
        return result?.ToJsonString() ?? "null";
    }

    /// <summary>判断是否为原有的根节点键值映射，确保其非字符串字段仍按旧逻辑忽略。</summary>
    private static bool IsLegacyFlatMapping(JsonElement mappingRoot) =>
        mappingRoot.ValueKind == JsonValueKind.Object &&
        mappingRoot.EnumerateObject().All(x =>
            x.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array));

    /// <summary>递归构造 B 系统 payload，字符串叶子作为标准消息字段路径解析。</summary>
    private static JsonNode? BuildBPayloadNode(JsonElement mappingNode, JsonElement payloadRoot)
    {
        if (mappingNode.ValueKind == JsonValueKind.Object)
        {
            var properties = mappingNode.EnumerateObject().ToList();
            if (properties.Count == 1 &&
                properties[0].Name.Equals("$literal", StringComparison.OrdinalIgnoreCase))
                return JsonNode.Parse(properties[0].Value.GetRawText());

            var result = new JsonObject();
            foreach (var property in properties)
                result[property.Name] = BuildBPayloadNode(property.Value, payloadRoot);
            return result;
        }

        if (mappingNode.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in mappingNode.EnumerateArray())
                result.Add(BuildBPayloadNode(item, payloadRoot));
            return result;
        }

        if (mappingNode.ValueKind == JsonValueKind.String)
        {
            var value = ResolveJsonPath(payloadRoot, mappingNode.GetString() ?? string.Empty);
            return JsonSerializer.SerializeToNode(value);
        }

        return JsonNode.Parse(mappingNode.GetRawText());
    }

    /// <summary>按点分隔路径从标准消息 JSON 中解析字段值。</summary>
    private static object? ResolveJsonPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            var property = current.EnumerateObject().FirstOrDefault(x => x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.Undefined) return null;
            current = property.Value;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number when current.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => current.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object>(current.GetRawText())
        };
    }

    /// <summary>把 B 系统配置的文本请求头附加到 HTTP 请求。</summary>
    private static void ApplyHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        using var document = JsonDocument.Parse(headersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var header in document.RootElement.EnumerateObject())
        {
            var value = header.Value.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Name, value))
                request.Content?.Headers.TryAddWithoutValidation(header.Name, value);
        }
    }

    /// <summary>规范化并校验 B 系统 HTTP 或 MQTT 配置。</summary>
    private static void NormalizeAndValidateBMapping(EdgeBMappingSaveDto dto)
    {
        dto.MappingName = dto.MappingName.Trim();
        dto.Protocol = string.IsNullOrWhiteSpace(dto.Protocol)
            ? "HTTP"
            : dto.Protocol.Trim().ToUpperInvariant();
        dto.TargetUrl = dto.TargetUrl.Trim();
        dto.HttpMethod = string.IsNullOrWhiteSpace(dto.HttpMethod)
            ? "POST"
            : dto.HttpMethod.Trim().ToUpperInvariant();
        dto.MqttTopic = string.IsNullOrWhiteSpace(dto.MqttTopic) ? null : dto.MqttTopic.Trim();
        dto.MqttClientId = string.IsNullOrWhiteSpace(dto.MqttClientId) ? null : dto.MqttClientId.Trim();
        dto.MqttUsername = string.IsNullOrWhiteSpace(dto.MqttUsername) ? null : dto.MqttUsername.Trim();
        if (string.IsNullOrEmpty(dto.MqttPassword)) dto.MqttPassword = null;

        if (string.IsNullOrWhiteSpace(dto.MappingName))
            throw new InvalidOperationException("映射名称不能为空");
        ValidateJson(dto.MappingJson, "映射 JSON");

        if (dto.Protocol == "HTTP")
        {
            if (!Uri.TryCreate(dto.TargetUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("HTTP 目标地址必须是有效的 http:// 或 https:// 地址");
            if (dto.HttpMethod is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                throw new InvalidOperationException("HTTP 方法仅支持 GET、POST、PUT、PATCH 或 DELETE");
            if (!string.IsNullOrWhiteSpace(dto.HeadersJson))
                ValidateJson(dto.HeadersJson, "请求头 JSON");
            return;
        }

        if (dto.Protocol == "MQTT")
        {
            if (!Uri.TryCreate(dto.TargetUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "mqtt" && uri.Scheme != "mqtts") ||
                string.IsNullOrWhiteSpace(uri.Host))
                throw new InvalidOperationException("MQTT Broker 地址必须是有效的 mqtt:// 或 mqtts:// 地址");
            if (string.IsNullOrWhiteSpace(dto.MqttTopic))
                throw new InvalidOperationException("MQTT 发布主题不能为空");
            if (dto.MqttTopic.Contains('#') || dto.MqttTopic.Contains('+'))
                throw new InvalidOperationException("MQTT 发布主题不能包含 # 或 + 通配符");
            if (dto.MqttQosLevel is < 0 or > 2)
                throw new InvalidOperationException("MQTT QoS 只能是 0、1 或 2");
            return;
        }

        throw new InvalidOperationException($"不支持的 B 系统协议：{dto.Protocol}");
    }

    /// <summary>生成不包含 MQTT 明文密码的 B 系统映射审计内容。</summary>
    private static object BuildBMappingAuditDetail(EdgeBMappingSaveDto dto) => new
    {
        dto.Id,
        dto.DeviceId,
        dto.MappingName,
        dto.PayloadTemplateId,
        dto.Protocol,
        dto.TargetUrl,
        dto.HttpMethod,
        dto.MqttTopic,
        dto.MqttClientId,
        dto.MqttUsername,
        HasMqttPassword = dto.MqttPassword != null,
        dto.MqttQosLevel,
        dto.MqttRetain,
        dto.MappingJson,
        dto.HeadersJson,
        dto.Enabled
    };

    /// <summary>校验管理端提交的 JSON 字段并返回中文错误。</summary>
    private static void ValidateJson(string value, string fieldName)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{fieldName} 格式错误: {ex.Message}");
        }
    }
}
