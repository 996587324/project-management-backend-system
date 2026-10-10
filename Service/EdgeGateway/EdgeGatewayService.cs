using AutoMapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Model.EdgeGateway;
using Newtonsoft.Json.Linq;
using NPOI.SS.UserModel;
using NPOI.XSSF.Streaming;
using NPOI.XSSF.UserModel;
using SqlSugar;

namespace Service;

/// <summary>
/// EdgeGateway 中心端综合业务服务，使用 SqlSugar 管理设备、版本、规则、队列、事件和 B 系统转发。
/// </summary>
public class EdgeGatewayService : IEdgeGatewayService
{
    private const int MaxStoredResponseContentLength = 32000;
    private const int ExportRowAccessWindowSize = 100;
    private const int ExportStreamBufferSize = 64 * 1024;
    private const string SpreadsheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int HeaderRowIndex = 2;
    private const int DataStartRowIndex = 3;
    private const int WebSocketReceiveRecordRetentionDays = 7;
    private const string InstructionSheet = "使用说明";
    private const string ConnectionSheet = "连接配置";
    private const string CollectionSheet = "采集配置";
    private const string RuleSheet = "边缘规则";
    private const string BMappingSheet = "B系统适配";
    private static readonly string[] ConnectionFields =
    [
        "deviceId",
        "deviceCode",
        "deviceName",
        "siteName",
        "configuredIpAddress",
        "deviceType",
        "groupName",
        "deviceEnabled",
        "sourceDbId",
        "sourceId",
        "sourceName",
        "endpoint",
        "dataType",
        "reconnectIntervalSeconds",
        "sourceEnabled",
        "headersJson",
        "subscriptionPayload",
        "subscriptionDeviceCode",
        "subscriptionDeviceName",
        "subscriptionEnabled",
        "subscriptionSort",
        "parameterName",
        "parameterNameCn",
        "parameterDataType",
        "parameterEnabled",
        "parameterSort"
    ];
    private static readonly string[] ConnectionHeaders =
    [
        "边缘节点ID",
        "边缘节点编码",
        "边缘节点名称",
        "站点名称",
        "配置IP地址",
        "设备类型",
        "设备分组",
        "节点是否启用",
        "连接记录ID",
        "数据源标识",
        "连接名称",
        "WebSocket地址",
        "数据类型",
        "重连间隔(秒)",
        "连接是否启用",
        "请求头JSON",
        "订阅报文JSON",
        "订阅设备编码",
        "订阅设备名称",
        "订阅设备是否启用",
        "订阅设备排序",
        "参数名称",
        "参数中文名称",
        "参数数据类型",
        "参数是否启用",
        "参数排序"
    ];
    private static readonly string[] CollectionFields =
    [
        "configId",
        "deviceCode",
        "configName",
        "publishAfterImport",
        "sourceId",
        "sourceField",
        "targetField",
        "transformType",
        "factor",
        "offset",
        "enumMapJson"
    ];
    private static readonly string[] CollectionHeaders =
    [
        "配置记录ID",
        "边缘节点编码",
        "配置名称",
        "导入后发布",
        "数据源标识",
        "来源字段",
        "目标字段",
        "转换类型",
        "倍率",
        "偏移量",
        "枚举映射JSON"
    ];
    private static readonly string[] RuleFields =
    [
        "ruleId",
        "deviceCode",
        "ruleCode",
        "ruleName",
        "ruleType",
        "field",
        "operator",
        "compareValue",
        "compareText",
        "min",
        "max",
        "threshold",
        "windowSeconds",
        "priority",
        "action",
        "enabled",
        "publishAfterImport"
    ];
    private static readonly string[] RuleHeaders =
    [
        "规则记录ID",
        "边缘节点编码",
        "规则编码",
        "规则名称",
        "规则类型",
        "目标字段",
        "操作符",
        "数值比较值",
        "文本比较值",
        "最小值",
        "最大值",
        "变化阈值",
        "窗口秒数",
        "优先级",
        "动作",
        "是否启用",
        "导入后发布"
    ];
    private static readonly string[] BMappingFields =
    [
        "templateId",
        "templateCode",
        "templateName",
        "templateJson",
        "templateDescription",
        "templateEnabled",
        "mappingId",
        "mappingName",
        "deviceCode",
        "sourceId",
        "sourceDeviceCode",
        "protocol",
        "targetUrl",
        "httpMethod",
        "mqttTopic",
        "mqttClientId",
        "mqttUsername",
        "mqttPassword",
        "mqttQosLevel",
        "mqttRetain",
        "mappingJson",
        "headersJson",
        "enabled"
    ];
    private static readonly string[] BMappingHeaders =
    [
        "模板记录ID",
        "模板编码",
        "模板名称",
        "模板JSON",
        "模板说明",
        "模板是否启用",
        "映射记录ID",
        "映射名称",
        "边缘节点编码",
        "数据源标识",
        "来源设备编码",
        "协议",
        "目标地址",
        "HTTP方法",
        "MQTT主题",
        "MQTT客户端ID",
        "MQTT用户名",
        "MQTT密码",
        "MQTT服务质量",
        "MQTT保留消息",
        "映射JSON",
        "请求头JSON",
        "映射是否启用"
    ];
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> ImportHeaderAliases =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [ConnectionSheet] = BuildHeaderAliases(ConnectionFields, ConnectionHeaders),
            [CollectionSheet] = BuildHeaderAliases(CollectionFields, CollectionHeaders),
            [RuleSheet] = BuildHeaderAliases(RuleFields, RuleHeaders),
            [BMappingSheet] = BuildHeaderAliases(BMappingFields, BMappingHeaders)
        };
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);
    private static readonly HttpClient BSystemClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim BForwardSemaphore = new(1, 1);
    private static readonly SemaphoreSlim WebSocketReceiveTableGate = new(1, 1);
    private static readonly HashSet<string> EnsuredWebSocketReceiveTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IEdgeWebSocketSourceService _webSocketSourceService;
    private readonly IEdgeBMappingResolver _bMappingResolver;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly IEdgeDataTraceService _dataTraceService;
    private readonly ILogger<EdgeGatewayService> _logger;
    private readonly IReadOnlyDictionary<string, IBSystemTransportSender> _bSystemTransportSenders;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");
    private ISqlSugarClient WebSocketLogDb => DatabaseOperationScope.GetConnection(_dbScope, "edge-gateway-logs");

    private sealed class EdgeImportRow
    {
        public string SheetName { get; init; } = string.Empty;
        public int RowIndex { get; init; }
        public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string Get(string key) => Values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    /// <summary>注入 SqlSugar、AutoMapper、数据源服务、映射解析器和 B 系统协议发送器。</summary>
    public EdgeGatewayService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IEdgeWebSocketSourceService webSocketSourceService,
        IEdgeBMappingResolver bMappingResolver,
        IEdgeGatewayFeatureService featureService,
        IEdgeDataTraceService dataTraceService,
        ILogger<EdgeGatewayService> logger,
        IEnumerable<IBSystemTransportSender> bSystemTransportSenders)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _webSocketSourceService = webSocketSourceService;
        _bMappingResolver = bMappingResolver;
        _featureService = featureService;
        _dataTraceService = dataTraceService;
        _logger = logger;
        _bSystemTransportSenders = bSystemTransportSenders.ToDictionary(
            x => x.Protocol,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>汇总连接数据接收状态、兼容设备状态、中心队列、告警和上传成功率。</summary>
    public async Task<EdgeOverviewDto> GetOverviewAsync()
    {
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var statuses = await Db.Queryable<EdgeDeviceStatusEntity>().ToListAsync();
        var onlineDeadline = DateTime.Now.Subtract(OnlineWindow);
        var onlineIds = statuses.Where(x => x.LastHeartbeat >= onlineDeadline).Select(x => x.DeviceId).ToHashSet();
        var deviceIdSet = devices.Select(x => x.Id).ToHashSet();
        var sources = (await Db.Queryable<EdgeWebSocketSourceEntity>()
            .Where(x => x.IsDelete == 0)
            .ToListAsync())
            .Where(x => deviceIdSet.Contains(x.DeviceId))
            .ToList();
        var enabledDevices = devices.Where(x => x.Enabled).ToDictionary(x => x.Id, x => x.DeviceCode);
        var activeSources = sources
            .Where(x => x.Enabled && enabledDevices.ContainsKey(x.DeviceId))
            .ToList();
        var dataDeadline = DateTime.Now.Subtract(EdgeGatewayDataReception.OnlineWindow);
        var recentDataSources = await Db.Queryable<EdgeMessageQueueEntity>()
            .Where(x => x.CreateTime >= dataDeadline)
            .Select(x => new { x.DeviceCode, x.SourceId })
            .Distinct()
            .ToListAsync();
        var recentDataKeys = recentDataSources
            .Select(x => BuildSourceDataKey(x.DeviceCode, x.SourceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var receivingConnectionCount = activeSources.Count(source =>
            recentDataKeys.Contains(BuildSourceDataKey(enabledDevices[source.DeviceId], source.SourceId)));
        var totalUploads = await Db.Queryable<EdgeUploadRecordEntity>().CountAsync();
        var successfulUploads = await Db.Queryable<EdgeUploadRecordEntity>().Where(x => x.Success).CountAsync();

        return new EdgeOverviewDto
        {
            DeviceCount = devices.Count,
            OnlineCount = devices.Count(x => x.Enabled && onlineIds.Contains(x.Id)),
            OfflineCount = devices.Count(x => x.Enabled && !onlineIds.Contains(x.Id)),
            DisabledCount = devices.Count(x => !x.Enabled),
            ConnectionCount = sources.Count,
            ReceivingConnectionCount = receivingConnectionCount,
            NoDataConnectionCount = activeSources.Count - receivingConnectionCount,
            DisabledConnectionCount = sources.Count - activeSources.Count,
            QueueDepth = await Db.Queryable<EdgeMessageQueueEntity>().Where(x => x.Status == "Pending" || x.Status == "Failed").CountAsync(),
            FailedCount = await Db.Queryable<EdgeMessageQueueEntity>().Where(x => x.Status == "Failed").CountAsync(),
            ActiveAlarmCount = await Db.Queryable<EdgeAlarmRecordEntity>().Where(x => !x.Handled).CountAsync(),
            SuccessfulForwardCount = successfulUploads,
            UploadSuccessRate = totalUploads == 0 ? 0 : Math.Round(successfulUploads * 100m / totalUploads, 2),
            RecentEvents = await Db.Queryable<EdgeSystemEventEntity>().OrderByDescending(x => x.CreateTime).Take(8).ToListAsync()
        };
    }

    /// <summary>生成不受大小写影响的边缘设备和数据源组合键。</summary>
    private static string BuildSourceDataKey(string deviceCode, string sourceId) =>
        $"{deviceCode}\u001f{sourceId}";

    /// <summary>
    /// 使用 SXSSFWorkbook 将大配置分批写入服务器临时文件，并返回关闭后自动删除的读取流。
    /// 这样 HTTP 下载过程中不会在托管内存中同时保留工作簿、MemoryStream 和 byte[] 三份数据。
    /// </summary>
    public async Task<EdgeGatewayTemplateStreamDto> ExportConfigurationTemplateStreamAsync(
        bool includeData,
        CancellationToken cancellationToken = default)
    {
        var fileName = GetConfigurationExportFileName(includeData);
        var tempFilePath = Path.Combine(
            Path.GetTempPath(),
            $"edge-gateway-export-{Guid.NewGuid():N}.xlsx");

        try
        {
            await using (var output = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             ExportStreamBufferSize,
                             FileOptions.SequentialScan))
            {
                using var workbook = new SXSSFWorkbook(ExportRowAccessWindowSize)
                {
                    CompressTempFiles = true
                };
                await PopulateConfigurationWorkbookAsync(workbook, includeData, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                workbook.Write(output, true);
                await output.FlushAsync(cancellationToken);
            }

            var content = new FileStream(
                tempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                ExportStreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return new EdgeGatewayTemplateStreamDto
            {
                FileName = fileName,
                ContentType = SpreadsheetContentType,
                Content = content
            };
        }
        catch
        {
            if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            throw;
        }
    }

    /// <summary>保留原有内存文件返回方式，兼容已有的服务层调用。</summary>
    public async Task<EdgeGatewayTemplateFileDto> ExportConfigurationTemplateAsync(bool includeData)
    {
        using var workbook = new XSSFWorkbook();
        await PopulateConfigurationWorkbookAsync(workbook, includeData, CancellationToken.None);

        using var stream = new MemoryStream();
        workbook.Write(stream, true);
        return new EdgeGatewayTemplateFileDto
        {
            FileName = GetConfigurationExportFileName(includeData),
            ContentType = SpreadsheetContentType,
            Content = stream.ToArray()
        };
    }

    /// <summary>按固定顺序写入配置说明及四类配置工作表。</summary>
    private async Task PopulateConfigurationWorkbookAsync(
        IWorkbook workbook,
        bool includeData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteInstructionSheet(workbook);
        WriteSheet(
            workbook,
            ConnectionSheet,
            "操作说明：本页用于导入边缘节点、WebSocket 连接、订阅设备和订阅参数；边缘节点编码 + 数据源标识作为连接唯一标识。",
            "填写说明：是否类字段填是/否；请求头JSON、订阅报文JSON必须是合法 JSON；存在订阅明细时订阅报文会由设备和参数自动重建。",
            ConnectionHeaders,
            includeData ? await BuildConnectionExportRowsAsync() : BuildConnectionExampleRows());
        cancellationToken.ThrowIfCancellationRequested();
        WriteSheet(
            workbook,
            CollectionSheet,
            "操作说明：本页用于导入采集字段映射；边缘节点编码 + 配置名称下的多行会合并为一个采集配置版本。",
            "填写说明：转换类型可填直接映射、数值转换、枚举转换、枚举后数值转换；枚举映射JSON需为 JSON 对象；导入后发布填是时自动发布。",
            CollectionHeaders,
            includeData ? await BuildCollectionExportRowsAsync() : BuildCollectionExampleRows());
        cancellationToken.ThrowIfCancellationRequested();
        WriteSheet(
            workbook,
            RuleSheet,
            "操作说明：本页用于导入边缘规则；边缘节点编码为空表示全局规则，规则编码在同一设备范围内唯一。",
            "填写说明：规则类型支持条件过滤、范围过滤、变化阈值、重复消息过滤、时间窗口聚合；导入后发布填是时自动发布规则版本。",
            RuleHeaders,
            includeData ? await BuildRuleExportRowsAsync() : BuildRuleExampleRows());
        cancellationToken.ThrowIfCancellationRequested();
        WriteSheet(
            workbook,
            BMappingSheet,
            "操作说明：本页用于导入 B 系统报文模板和 B 系统映射；同一适用范围只保留一条启用映射。",
            "填写说明：协议支持 HTTP/MQTT；MQTT密码导出时不返回明文，导入为空则保留原密码；JSON 字段必须合法。",
            BMappingHeaders,
            includeData ? await BuildBMappingExportRowsAsync() : BuildBMappingExampleRows());
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>生成下载文件名，模板名称保持固定，当前配置名称带生成时间。</summary>
    private static string GetConfigurationExportFileName(bool includeData) => includeData
        ? $"edge-gateway-config-{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        : "edge-gateway-import-template.xlsx";

    public async Task<EdgeGatewayImportResultDto> ImportConfigurationAsync(
        string fileName,
        Stream stream,
        string? operatorName)
    {
        if (!Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(fileName).Equals(".xls", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仅支持 XLS 或 XLSX 文件");

        var workbookRows = ReadWorkbookRows(stream);
        var result = new EdgeGatewayImportResultDto();
        await ImportConnectionRowsAsync(workbookRows.GetValueOrDefault(ConnectionSheet) ?? [], result);
        await ImportCollectionRowsAsync(workbookRows.GetValueOrDefault(CollectionSheet) ?? [], result, operatorName);
        await ImportRuleRowsAsync(workbookRows.GetValueOrDefault(RuleSheet) ?? [], result, operatorName);
        await ImportBMappingRowsAsync(workbookRows.GetValueOrDefault(BMappingSheet) ?? [], result);
        result.Message = $"导入完成：共识别 {result.Total} 条，成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条";
        return result;
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
        var featureEnabled = await _featureService.IsEnabledAsync();
        return new EdgeHeartbeatResultDto
        {
            Enabled = featureEnabled && device.Enabled,
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
        var featureEnabled = await _featureService.IsEnabledAsync();
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
            Enabled = featureEnabled && device.Enabled,
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

    /// <summary>分页查询边缘节点向上游 WebSocket 发送订阅或请求报文的执行结果。</summary>
    public async Task<EdgePagedResultDto<EdgeWebSocketSendRecordEntity>> GetWebSocketSendRecordPageAsync(
        EdgeWebSocketSendRecordQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<EdgeWebSocketSendRecordEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.DeviceCode), x => x.DeviceCode == query.DeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.SourceId), x => x.SourceId == query.SourceId)
            .WhereIF(query.Success.HasValue, x => x.Success == query.Success!.Value)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeWebSocketSendRecordEntity>
        {
            Records = records,
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>保存边缘节点上报的 WebSocket 发送记录，限制常规字段长度以保护中心库。</summary>
    public async Task<long> ReportWebSocketSendRecordAsync(EdgeWebSocketSendRecordReportDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DeviceCode))
            throw new InvalidOperationException("设备编码不能为空");
        if (string.IsNullOrWhiteSpace(dto.SourceId))
            throw new InvalidOperationException("数据源标识不能为空");
        if (string.IsNullOrWhiteSpace(dto.Payload))
            throw new InvalidOperationException("发送报文不能为空");

        var entity = new EdgeWebSocketSendRecordEntity
        {
            DeviceCode = dto.DeviceCode.Trim(),
            SourceId = dto.SourceId.Trim(),
            SourceName = TrimToNull(dto.SourceName, 128),
            Endpoint = TrimToNull(dto.Endpoint, 1024),
            SendType = string.IsNullOrWhiteSpace(dto.SendType) ? "InitialSubscription" : dto.SendType.Trim()[..Math.Min(dto.SendType.Trim().Length, 32)],
            Payload = dto.Payload,
            Success = dto.Success,
            ErrorMessage = TrimToNull(dto.ErrorMessage, 512),
            DurationMs = Math.Max(dto.DurationMs, 0),
            CreateTime = DateTime.Now
        };
        return await Db.Insertable(entity).ExecuteReturnIdentityAsync();
    }

    /// <summary>
    /// 查询最近七个自然日的 WebSocket 接收记录。记录物理上按日表保存，按日期倒序逐表分页，
    /// 避免将多天高频报文合并到一张大表再扫描。
    /// </summary>
    public async Task<EdgePagedResultDto<EdgeWebSocketReceiveRecordEntity>> GetWebSocketReceiveRecordPageAsync(
        EdgeWebSocketReceiveRecordQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 200);
        var (startDate, endDate) = GetWebSocketReceiveRecordDateRange(query);
        if (startDate > endDate)
            return new EdgePagedResultDto<EdgeWebSocketReceiveRecordEntity>
            {
                Records = new(),
                Current = page,
                Size = size,
                Total = 0
            };

        var recordQuery = WebSocketLogDb.Queryable<EdgeWebSocketReceiveRecordEntity>()
            .SplitTable(startDate, endDate)
            .WhereIF(!string.IsNullOrWhiteSpace(query.DeviceCode), x => x.DeviceCode == query.DeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.SourceId), x => x.SourceId == query.SourceId)
            .WhereIF(!string.IsNullOrWhiteSpace(query.SourceDeviceCode), x => x.SourceDeviceCode == query.SourceDeviceCode)
            .WhereIF(!string.IsNullOrWhiteSpace(query.DataType), x => x.DataType == query.DataType);
        var total = await recordQuery.CountAsync();
        var records = await recordQuery
            .OrderByDescending(x => x.CreateTime)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return new EdgePagedResultDto<EdgeWebSocketReceiveRecordEntity>
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
        await _featureService.EnsureEnabledAsync();
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
            // 主消息已可靠进入中心队列；日志日表和追溯库是可重试投影，不能因短暂故障
            // 让边缘节点反复上传并阻塞后续实时数据。
            await ProjectMessageAsync(queueEntity, message, CancellationToken.None);
            result.AcceptedCount++;
        }
        return result;
    }

    /// <summary>
    /// 补偿近期未完成的接收日表和数据追溯投影。主消息在中心队列中保留，
    /// 因此日志库重连、日表切换或短暂锁表后均可恢复展示数据。
    /// </summary>
    public async Task<int> RecoverMessageProjectionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        var maximum = Math.Clamp(limit, 1, 500);
        var retention = await _dataTraceService.GetSettingAsync();
        var cutoff = DateTime.Now.AddHours(-retention.RetentionHours);
        var pending = await Db.Queryable<EdgeMessageQueueEntity>()
            .Where(x => x.CreateTime >= cutoff &&
                (x.ReceiveRecordProjectedTime == null || x.DataTraceProjectedTime == null))
            .OrderBy(x => x.CreateTime)
            .Take(maximum)
            .ToListAsync();
        var recovered = 0;
        foreach (var queueEntity in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EdgeStandardMessageDto? message;
            try
            {
                message = JsonSerializer.Deserialize<EdgeStandardMessageDto>(queueEntity.StandardPayloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Cannot recover EdgeGateway projections for malformed queue message {MessageId}.",
                    queueEntity.MessageId);
                continue;
            }
            if (message == null || string.IsNullOrWhiteSpace(message.MessageId))
            {
                _logger.LogWarning(
                    "Cannot recover EdgeGateway projections because queue message {MessageId} has no payload identifier.",
                    queueEntity.MessageId);
                continue;
            }
            var before = queueEntity.ReceiveRecordProjectedTime == null || queueEntity.DataTraceProjectedTime == null;
            await ProjectMessageAsync(queueEntity, message, cancellationToken);
            if (before && queueEntity.ReceiveRecordProjectedTime != null && queueEntity.DataTraceProjectedTime != null)
                recovered++;
        }
        return recovered;
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

    public async Task<int> DeleteQueueAsync(EdgeBatchIdsDto dto)
    {
        var ids = dto.Ids.Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("请选择需要删除的消息队列数据");

        var deletedCount = 0;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Deleteable<EdgeUploadRecordEntity>()
                .Where(x => ids.Contains(x.QueueId))
                .ExecuteCommandAsync();
            deletedCount = await Db.Deleteable<EdgeMessageQueueEntity>()
                .Where(x => ids.Contains(x.Id))
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("删除消息队列数据失败");
        if (deletedCount > 0) await AddOperationAsync("Queue.Delete", "MessageQueue", new { ids, deletedCount });
        return deletedCount;
    }

    /// <summary>
    /// 按设备或全局 B 映射转换消息，再通过配置的 HTTP 或扩展协议逐条发送并记录结果。
    /// </summary>
    public async Task<EdgeRetryResultDto> ForwardQueueAsync(EdgeRetryRequestDto dto)
    {
        await _featureService.EnsureEnabledAsync();
        await BForwardSemaphore.WaitAsync();
        try
        {
            var db = Db;
            var records = await db.Queryable<EdgeMessageQueueEntity>()
                .WhereIF(dto.Ids.Count > 0, x => dto.Ids.Contains(x.Id))
                .Where(x => x.Status == "Pending" || x.Status == "Failed")
                .OrderBy(x => x.CreateTime).Take(100).ToListAsync();
            return await ForwardRecordsAsync(db, records, CancellationToken.None);
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
        await _featureService.EnsureEnabledAsync();
        await BForwardSemaphore.WaitAsync(cancellationToken);
        try
        {
            using var db = _dbScope.GetConnection("calmadmin").CopyNew();
            var now = DateTime.Now;
            var records = await db.Queryable<EdgeMessageQueueEntity>()
                .Where(x => (x.Status == "Pending" || x.Status == "Failed") &&
                            (x.NextRetryTime == null || x.NextRetryTime <= now))
                .OrderBy(x => x.CreateTime)
                .Take(Math.Clamp(limit, 1, 500))
                .ToListAsync();
            return await ForwardRecordsAsync(db, records, cancellationToken);
        }
        finally
        {
            BForwardSemaphore.Release();
        }
    }

    /// <summary>使用当前有效映射逐条转发已经选出的中心队列记录。</summary>
    private async Task<EdgeRetryResultDto> ForwardRecordsAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<EdgeMessageQueueEntity> records,
        CancellationToken cancellationToken)
    {
        var result = new EdgeRetryResultDto { RequestedCount = records.Count };
        if (records.Count == 0) return result;

        var mappings = await db.Queryable<EdgeBMappingRuleEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled)
            .ToListAsync();
        var devices = await db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceIdMap = devices.ToDictionary(x => x.DeviceCode, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var relevantDeviceIds = records
            .Select(x => deviceIdMap.TryGetValue(x.DeviceCode, out var id) ? (int?)id : null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        var sources = relevantDeviceIds.Count == 0
            ? []
            : await db.Queryable<EdgeWebSocketSourceEntity>()
                .Where(x => x.IsDelete == 0 && relevantDeviceIds.Contains(x.DeviceId))
                .ToListAsync();
        var sourcePrimaryKeys = sources.Select(x => x.Id).ToList();
        var subscriptionDevices = sourcePrimaryKeys.Count == 0
            ? []
            : await db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
                .Where(x => x.IsDelete == 0 && sourcePrimaryKeys.Contains(x.WebSocketSourceId))
                .ToListAsync();

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? deviceId = deviceIdMap.TryGetValue(record.DeviceCode, out var resolvedDeviceId)
                ? resolvedDeviceId
                : null;
            var mapping = _bMappingResolver.Resolve(
                mappings,
                sources,
                subscriptionDevices,
                deviceId,
                record.SourceId,
                record.SourceDeviceCode);
            if (mapping == null)
            {
                var route = string.IsNullOrWhiteSpace(record.SourceDeviceCode)
                    ? record.SourceId
                    : $"{record.SourceId}/{record.SourceDeviceCode}";
                await MarkForwardFailedAsync(
                    db,
                    record,
                    null,
                    null,
                    $"未配置可用的 B 系统映射：{record.DeviceCode}/{route}");
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
                        db,
                        record,
                        mapping,
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
                await db.Updateable(record).ExecuteCommandAsync();
                await AddUploadRecordAsync(
                    db,
                    record,
                    mapping,
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
                await MarkForwardFailedAsync(
                    db,
                    record,
                    mapping,
                    mapping.TargetUrl,
                    ex.Message,
                    null,
                    (int)stopwatch.ElapsedMilliseconds);
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

    /// <summary>分页查询 B 系统映射，并支持常用路由及目标信息检索。</summary>
    public async Task<EdgePagedResultDto<EdgeBMappingOutputDto>> GetBMappingRulePageAsync(
        EdgeBMappingQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Clamp(query.Size, 1, 100);
        var keyword = string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim();
        var protocol = string.IsNullOrWhiteSpace(query.Protocol)
            ? null
            : query.Protocol.Trim().ToUpperInvariant();
        RefAsync<int> total = 0;
        var entities = await Db.Queryable<EdgeBMappingRuleEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(query.DeviceId.HasValue, x => x.DeviceId == query.DeviceId)
            .WhereIF(protocol != null, x => x.Protocol == protocol)
            .WhereIF(query.Enabled.HasValue, x => x.Enabled == query.Enabled)
            .WhereIF(keyword != null, x =>
                x.MappingName.Contains(keyword!) ||
                (x.SourceId != null && x.SourceId.Contains(keyword!)) ||
                (x.SourceDeviceCode != null && x.SourceDeviceCode.Contains(keyword!)) ||
                x.TargetUrl.Contains(keyword!) ||
                (x.MqttTopic != null && x.MqttTopic.Contains(keyword!)) ||
                (x.MqttClientId != null && x.MqttClientId.Contains(keyword!)))
            .OrderByDescending(x => x.UpdateTime)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new EdgePagedResultDto<EdgeBMappingOutputDto>
        {
            Records = _mapper.Map<List<EdgeBMappingOutputDto>>(entities),
            Current = page,
            Size = size,
            Total = total
        };
    }

    /// <summary>新增或修改 B 系统协议、目标地址、字段映射和协议参数。</summary>
    public async Task<int> SaveBMappingRuleAsync(EdgeBMappingSaveDto dto)
    {
        NormalizeAndValidateBMapping(dto);
        await ValidateBMappingScopeAsync(dto);
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

    public async Task<int> DeleteBMappingRulesAsync(EdgeBatchIdsDto dto)
    {
        var ids = dto.Ids.Where(x => x > 0 && x <= int.MaxValue).Select(x => (int)x).Distinct().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("请选择需要删除的 B 系统映射");

        var count = await Db.Updateable<EdgeBMappingRuleEntity>()
            .SetColumns(x => new EdgeBMappingRuleEntity
            {
                IsDelete = 1,
                Enabled = false,
                UpdateTime = DateTime.Now
            })
            .Where(x => ids.Contains(x.Id) && x.IsDelete == 0)
            .ExecuteCommandAsync();
        if (count > 0) await AddOperationAsync("BMapping.Delete", "BMapping", new { ids, count });
        return count;
    }

    private static void WriteInstructionSheet(IWorkbook workbook)
    {
        var sheet = workbook.CreateSheet(InstructionSheet);
        var rows = new[]
        {
            "边缘网关配置导入说明",
            "1. 本模板包含连接配置、采集配置、边缘规则、B系统适配四类配置。",
            "2. 每个业务工作表的第 1-2 行是操作说明，第 3 行是列名，第 4 行开始填写数据。",
            "3. 记录ID类字段为空时按业务唯一键新增或更新；导入不支持通过删除表格行来删除系统配置。",
            "4. JSON 字段必须填写合法 JSON；是否类字段建议填写是/否，也兼容 true/false、启用/停用。",
            "5. 导出当前配置不会导出 MQTT 明文密码；再次导入时“MQTT密码”留空会保留原密码。"
        };
        for (var i = 0; i < rows.Length; i++)
        {
            var row = sheet.CreateRow(i);
            row.CreateCell(0).SetCellValue(rows[i]);
        }
        sheet.SetColumnWidth(0, 11000);
    }

    private static void WriteSheet(
        IWorkbook workbook,
        string sheetName,
        string operationText,
        string instructionText,
        IReadOnlyList<string> headers,
        IEnumerable<string[]> dataRows)
    {
        var sheet = workbook.CreateSheet(sheetName);
        sheet.CreateRow(0).CreateCell(0).SetCellValue(operationText);
        sheet.CreateRow(1).CreateCell(0).SetCellValue(instructionText);
        var headerRow = sheet.CreateRow(HeaderRowIndex);
        for (var i = 0; i < headers.Count; i++)
            headerRow.CreateCell(i).SetCellValue(headers[i]);

        var rowIndex = DataStartRowIndex;
        foreach (var dataRow in dataRows)
        {
            var row = sheet.CreateRow(rowIndex++);
            for (var i = 0; i < headers.Count; i++)
            {
                var value = i < dataRow.Length ? dataRow[i] : string.Empty;
                if (!string.IsNullOrEmpty(value)) row.CreateCell(i).SetCellValue(value);
            }
        }

        sheet.CreateFreezePane(0, DataStartRowIndex);
        for (var i = 0; i < headers.Count; i++)
            sheet.SetColumnWidth(i, Math.Min(Math.Max(headers[i].Length + 8, 14), 36) * 256);
    }

    private static List<string[]> BuildConnectionExampleRows() =>
    [
        [
            "", "EDGE-001", "一号边缘网关", "一号车间", "192.168.1.10", "EdgeGateway", "A线", "是",
            "", "ws-source-01", "产线 WebSocket", "ws://127.0.0.1:9001/ws", "Telemetry", "5", "是",
            "{\"Authorization\":\"Bearer token\"}", "{\"type\":\"subscribe\"}",
            "MACHINE-001", "一号设备", "是", "1", "temperature", "温度", "number", "是", "1"
        ]
    ];

    private static List<string[]> BuildCollectionExampleRows() =>
    [
        ["", "EDGE-001", "默认采集配置", "是", "ws-source-01", "temperature", "temperature", "数值转换", "1", "0", "{}"],
        ["", "EDGE-001", "默认采集配置", "是", "ws-source-01", "status", "runStatus", "枚举转换", "1", "0", "{\"RUN\":\"1\",\"STOP\":\"0\"}"]
    ];

    private static List<string[]> BuildRuleExampleRows() =>
    [
        ["", "EDGE-001", "TEMP_HIGH", "温度过高过滤", "条件过滤", "temperature", "大于", "85", "", "", "", "", "", "100", "丢弃", "是", "是"],
        ["", "", "DEDUP_10S", "十秒重复过滤", "重复消息过滤", "", "", "", "", "", "", "", "10", "90", "丢弃", "是", "是"]
    ];

    private static List<string[]> BuildBMappingExampleRows() =>
    [
        [
            "", "B_HTTP_PAYLOAD", "B系统 HTTP 模板", "{\"device\":\"deviceId\",\"data\":\"values\"}", "B系统标准报文", "是",
            "", "默认 HTTP 映射", "EDGE-001", "", "", "HTTP", "http://127.0.0.1:8080/api/data", "POST",
            "", "", "", "", "1", "否", "{\"device\":\"deviceId\",\"data\":\"values\"}", "{\"Content-Type\":\"application/json\"}", "是"
        ]
    ];

    private async Task<List<string[]>> BuildConnectionExportRowsAsync()
    {
        var result = new List<string[]>();
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceMap = devices.ToDictionary(x => x.Id);
        var sources = await Db.Queryable<EdgeWebSocketSourceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var sourceIds = sources.Select(x => x.Id).ToList();
        var subscriptionDevices = sourceIds.Count == 0
            ? []
            : await Db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
                .Where(x => x.IsDelete == 0 && sourceIds.Contains(x.WebSocketSourceId))
                .OrderBy(x => x.Sort)
                .ToListAsync();
        var subscriptionIds = subscriptionDevices.Select(x => x.Id).ToList();
        var parameters = subscriptionIds.Count == 0
            ? []
            : await Db.Queryable<EdgeWebSocketSubscriptionParameterEntity>()
                .Where(x => x.IsDelete == 0 && subscriptionIds.Contains(x.SubscriptionDeviceId))
                .OrderBy(x => x.Sort)
                .ToListAsync();
        var subscriptionsBySource = subscriptionDevices
            .GroupBy(x => x.WebSocketSourceId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var parametersByDevice = parameters
            .GroupBy(x => x.SubscriptionDeviceId)
            .ToDictionary(x => x.Key, x => x.ToList());

        foreach (var source in sources.OrderBy(x => x.DeviceId).ThenBy(x => x.SourceId))
        {
            if (!deviceMap.TryGetValue(source.DeviceId, out var device)) continue;
            if (!subscriptionsBySource.TryGetValue(source.Id, out var subscriptions) || subscriptions.Count == 0)
            {
                result.Add(BuildConnectionRow(device, source, null, null, true));
                continue;
            }

            foreach (var subscription in subscriptions)
            {
                if (!parametersByDevice.TryGetValue(subscription.Id, out var sourceParameters) ||
                    sourceParameters.Count == 0)
                {
                    result.Add(BuildConnectionRow(device, source, subscription, null, false));
                    continue;
                }

                foreach (var parameter in sourceParameters)
                    result.Add(BuildConnectionRow(device, source, subscription, parameter, false));
            }
        }

        return result;
    }

    private static string[] BuildConnectionRow(
        EdgeDeviceEntity device,
        EdgeWebSocketSourceEntity source,
        EdgeWebSocketSubscriptionDeviceEntity? subscription,
        EdgeWebSocketSubscriptionParameterEntity? parameter,
        bool includeSubscriptionPayload) =>
    [
        device.Id.ToString(CultureInfo.InvariantCulture),
        device.DeviceCode,
        device.DeviceName,
        device.SiteName ?? "",
        device.ConfiguredIpAddress ?? "",
        device.DeviceType ?? "",
        device.GroupName ?? "",
        ToChineseBool(device.Enabled),
        source.Id.ToString(CultureInfo.InvariantCulture),
        source.SourceId,
        source.SourceName,
        source.Endpoint,
        source.DataType,
        source.ReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        ToChineseBool(source.Enabled),
        source.HeadersJson ?? "",
        // 有订阅明细时导入服务会按设备和参数重建报文，避免把大型 JSON 重复写入每个参数行。
        includeSubscriptionPayload ? source.SubscriptionPayload ?? "" : "",
        subscription?.DeviceCode ?? "",
        subscription?.DeviceName ?? "",
        subscription == null ? "" : ToChineseBool(subscription.Enabled),
        subscription?.Sort.ToString(CultureInfo.InvariantCulture) ?? "",
        parameter?.ParameterName ?? "",
        parameter?.ParameterNameCn ?? "",
        parameter?.DataType ?? "",
        parameter == null ? "" : ToChineseBool(parameter.Enabled),
        parameter?.Sort.ToString(CultureInfo.InvariantCulture) ?? ""
    ];

    private async Task<List<string[]>> BuildCollectionExportRowsAsync()
    {
        var result = new List<string[]>();
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceMap = devices.ToDictionary(x => x.Id, x => x.DeviceCode);
        var configs = await Db.Queryable<EdgeDeviceConfigEntity>()
            .Where(x => x.IsDelete == 0)
            .OrderBy(x => x.DeviceId)
            .OrderByDescending(x => x.Version)
            .ToListAsync();
        foreach (var config in configs)
        {
            if (!deviceMap.TryGetValue(config.DeviceId, out var deviceCode)) continue;
            var mappings = ExtractCollectionMappings(config.ConfigJson);
            if (mappings.Count == 0)
            {
                result.Add([
                    config.Id.ToString(CultureInfo.InvariantCulture), deviceCode, config.ConfigName,
                    ToChineseBool(config.IsPublished), "", "", "", "", "", "", ""
                ]);
                continue;
            }

            foreach (var mapping in mappings)
            {
                result.Add([
                    config.Id.ToString(CultureInfo.InvariantCulture),
                    deviceCode,
                    config.ConfigName,
                    ToChineseBool(config.IsPublished),
                    mapping.GetValueOrDefault("sourceId", ""),
                    mapping.GetValueOrDefault("sourceField", ""),
                    mapping.GetValueOrDefault("targetField", ""),
                    ToChineseTransformType(mapping.GetValueOrDefault("transformType", "direct")),
                    mapping.GetValueOrDefault("factor", "1"),
                    mapping.GetValueOrDefault("offset", "0"),
                    mapping.GetValueOrDefault("enumMapJson", "{}")
                ]);
            }
        }
        return result;
    }

    private async Task<List<string[]>> BuildRuleExportRowsAsync()
    {
        var result = new List<string[]>();
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceMap = devices.ToDictionary(x => x.Id, x => x.DeviceCode);
        var rules = await Db.Queryable<EdgeRuleEntity>()
            .Where(x => x.IsDelete == 0)
            .OrderBy(x => x.DeviceId)
            .OrderBy(x => x.Priority)
            .ToListAsync();
        foreach (var rule in rules)
        {
            var body = ParseJsonObject(rule.RuleJson);
            result.Add([
                rule.Id.ToString(CultureInfo.InvariantCulture),
                rule.DeviceId.HasValue && deviceMap.TryGetValue(rule.DeviceId.Value, out var deviceCode) ? deviceCode : "",
                rule.RuleCode,
                rule.RuleName,
                ToChineseRuleType(rule.RuleType),
                body.GetValueOrDefault("field", ""),
                ToChineseRuleOperator(body.GetValueOrDefault("operator", "")),
                body.GetValueOrDefault("compareValue", ""),
                body.GetValueOrDefault("compareText", ""),
                body.GetValueOrDefault("min", ""),
                body.GetValueOrDefault("max", ""),
                body.GetValueOrDefault("threshold", ""),
                body.GetValueOrDefault("windowSeconds", ""),
                rule.Priority.ToString(CultureInfo.InvariantCulture),
                ToChineseRuleAction(body.GetValueOrDefault("action", "Drop")),
                ToChineseBool(rule.Enabled),
                "否"
            ]);
        }
        return result;
    }

    private async Task<List<string[]>> BuildBMappingExportRowsAsync()
    {
        var result = new List<string[]>();
        var devices = await Db.Queryable<EdgeDeviceEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var deviceMap = devices.ToDictionary(x => x.Id, x => x.DeviceCode);
        var templates = await Db.Queryable<EdgeBPayloadTemplateEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var templateMap = templates.ToDictionary(x => x.Id);
        var usedTemplateIds = new HashSet<int>();
        var mappings = await Db.Queryable<EdgeBMappingRuleEntity>()
            .Where(x => x.IsDelete == 0)
            .OrderByDescending(x => x.CreateTime)
            .ToListAsync();

        foreach (var mapping in mappings)
        {
            EdgeBPayloadTemplateEntity? template = null;
            if (mapping.PayloadTemplateId.HasValue &&
                templateMap.TryGetValue(mapping.PayloadTemplateId.Value, out template))
            {
                usedTemplateIds.Add(template.Id);
            }

            result.Add(BuildBMappingRow(mapping, template, deviceMap));
        }

        foreach (var template in templates.Where(x => !usedTemplateIds.Contains(x.Id)).OrderBy(x => x.TemplateCode))
            result.Add(BuildBTemplateOnlyRow(template));
        return result;
    }

    private static string[] BuildBMappingRow(
        EdgeBMappingRuleEntity mapping,
        EdgeBPayloadTemplateEntity? template,
        IReadOnlyDictionary<int, string> deviceMap) =>
    [
        template?.Id.ToString(CultureInfo.InvariantCulture) ?? "",
        template?.TemplateCode ?? "",
        template?.TemplateName ?? "",
        template?.TemplateJson ?? "",
        template?.Description ?? "",
        template == null ? "" : ToChineseBool(template.Enabled),
        mapping.Id.ToString(CultureInfo.InvariantCulture),
        mapping.MappingName,
        mapping.DeviceId.HasValue && deviceMap.TryGetValue(mapping.DeviceId.Value, out var deviceCode) ? deviceCode : "",
        mapping.SourceId ?? "",
        mapping.SourceDeviceCode ?? "",
        mapping.Protocol,
        mapping.TargetUrl,
        mapping.HttpMethod,
        mapping.MqttTopic ?? "",
        mapping.MqttClientId ?? "",
        mapping.MqttUsername ?? "",
        "",
        mapping.MqttQosLevel.ToString(CultureInfo.InvariantCulture),
        ToChineseBool(mapping.MqttRetain),
        mapping.MappingJson,
        mapping.HeadersJson ?? "",
        ToChineseBool(mapping.Enabled)
    ];

    private static string[] BuildBTemplateOnlyRow(EdgeBPayloadTemplateEntity template) =>
    [
        template.Id.ToString(CultureInfo.InvariantCulture),
        template.TemplateCode,
        template.TemplateName,
        template.TemplateJson,
        template.Description ?? "",
        ToChineseBool(template.Enabled),
        "", "", "", "", "", "", "", "", "", "", "", "", "1", "否", "", "", ""
    ];

    private async Task ImportConnectionRowsAsync(List<EdgeImportRow> rows, EdgeGatewayImportResultDto result)
    {
        var groups = rows
            .Where(x => !IsBlankRow(x))
            .GroupBy(x => $"{x.Get("deviceCode")}\u001f{x.Get("sourceId")}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupRows = group.ToList();
            result.Total += groupRows.Count;
            var first = groupRows[0];
            try
            {
                var deviceId = await SaveDeviceFromImportAsync(first);
                var sourceId = await SaveWebSocketSourceFromImportAsync(deviceId, groupRows);
                var subscriptions = BuildSubscriptionsFromImportRows(groupRows);
                await _webSocketSourceService.SaveSubscriptionsAsync(
                    sourceId,
                    new EdgeWebSocketSubscriptionSaveDto { Devices = subscriptions });
                result.SuccessCount += groupRows.Count;
            }
            catch (Exception ex)
            {
                AddFailures(result, groupRows, $"{first.Get("deviceCode")}/{first.Get("sourceId")}", ex.Message);
            }
        }
    }

    private async Task ImportCollectionRowsAsync(
        List<EdgeImportRow> rows,
        EdgeGatewayImportResultDto result,
        string? operatorName)
    {
        var groups = rows
            .Where(x => !IsBlankRow(x))
            .GroupBy(x => $"{x.Get("deviceCode")}\u001f{x.Get("configName")}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var groupRows = group.ToList();
            result.Total += groupRows.Count;
            var first = groupRows[0];
            try
            {
                var device = await GetDeviceByCodeAsync(first.Get("deviceCode"));
                var mappings = new List<Dictionary<string, object?>>();
                foreach (var row in groupRows)
                {
                    if (string.IsNullOrWhiteSpace(row.Get("sourceField")) &&
                        string.IsNullOrWhiteSpace(row.Get("targetField")))
                        continue;

                    if (string.IsNullOrWhiteSpace(row.Get("sourceId")) ||
                        string.IsNullOrWhiteSpace(row.Get("sourceField")) ||
                        string.IsNullOrWhiteSpace(row.Get("targetField")))
                        throw new InvalidOperationException("数据源标识、来源字段、目标字段不能为空");

                    var enumMapJson = string.IsNullOrWhiteSpace(row.Get("enumMapJson"))
                        ? "{}"
                        : row.Get("enumMapJson");
                    ValidateJson(enumMapJson, "enumMapJson");
                    var transformType = NormalizeTransformType(row.Get("transformType"));
                    var usesNumeric = transformType is "numeric" or "enumNumeric";
                    var usesEnum = transformType is "enum" or "enumNumeric";
                    mappings.Add(new Dictionary<string, object?>
                    {
                        ["sourceId"] = row.Get("sourceId"),
                        ["sourceField"] = row.Get("sourceField"),
                        ["targetField"] = row.Get("targetField"),
                        ["factor"] = usesNumeric ? ToDecimal(row.Get("factor"), 1) : 1,
                        ["offset"] = usesNumeric ? ToDecimal(row.Get("offset"), 0) : 0,
                        ["enumMap"] = usesEnum
                            ? JsonSerializer.Deserialize<Dictionary<string, string>>(enumMapJson) ?? new()
                            : new Dictionary<string, string>()
                    });
                }

                if (mappings.Count == 0) throw new InvalidOperationException("采集字段映射不能为空");
                var configJson = JsonSerializer.Serialize(new
                {
                    mappings,
                    rules = Array.Empty<object>()
                });
                var configId = await SaveDeviceConfigAsync(
                    device.Id,
                    new EdgeDeviceConfigSaveDto
                    {
                        ConfigName = string.IsNullOrWhiteSpace(first.Get("configName"))
                            ? "导入采集配置"
                            : first.Get("configName"),
                        ConfigJson = configJson,
                        CreatedBy = operatorName
                    });
                if (ToBool(first.Get("publishAfterImport"), false))
                    await PublishDeviceConfigAsync(configId, operatorName);
                result.SuccessCount += groupRows.Count;
            }
            catch (Exception ex)
            {
                AddFailures(result, groupRows, $"{first.Get("deviceCode")}/{first.Get("configName")}", ex.Message);
            }
        }
    }

    private async Task ImportRuleRowsAsync(
        List<EdgeImportRow> rows,
        EdgeGatewayImportResultDto result,
        string? operatorName)
    {
        foreach (var row in rows.Where(x => !IsBlankRow(x)))
        {
            result.Total++;
            var key = string.IsNullOrWhiteSpace(row.Get("deviceCode"))
                ? $"Global/{row.Get("ruleCode")}"
                : $"{row.Get("deviceCode")}/{row.Get("ruleCode")}";
            try
            {
                var deviceId = string.IsNullOrWhiteSpace(row.Get("deviceCode"))
                    ? (int?)null
                    : (await GetDeviceByCodeAsync(row.Get("deviceCode"))).Id;
                var id = await ResolveRuleIdAsync(row, deviceId);
                var ruleType = NormalizeRuleType(row.Get("ruleType"));
                var dto = new EdgeRuleSaveDto
                {
                    Id = id,
                    DeviceId = deviceId,
                    RuleCode = row.Get("ruleCode"),
                    RuleName = row.Get("ruleName"),
                    RuleType = ruleType,
                    Priority = ToInt(row.Get("priority"), 100),
                    Enabled = ToBool(row.Get("enabled"), true),
                    RuleJson = BuildRuleJson(row)
                };
                await SaveRuleAsync(dto);
                if (ToBool(row.Get("publishAfterImport"), false))
                {
                    await PublishRulesAsync(new EdgeRulePublishDto
                    {
                        DeviceId = deviceId,
                        Remark = "Excel 导入后发布",
                        PublishedBy = operatorName
                    });
                }
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                AddFailure(result, row, key, ex.Message);
            }
        }
    }

    private async Task ImportBMappingRowsAsync(List<EdgeImportRow> rows, EdgeGatewayImportResultDto result)
    {
        foreach (var row in rows.Where(x => !IsBlankRow(x)))
        {
            result.Total++;
            var key = string.IsNullOrWhiteSpace(row.Get("mappingName"))
                ? row.Get("templateCode")
                : row.Get("mappingName");
            try
            {
                var templateId = await SaveBPayloadTemplateFromImportAsync(row);
                if (string.IsNullOrWhiteSpace(row.Get("mappingName")))
                {
                    result.SuccessCount++;
                    continue;
                }

                var deviceId = string.IsNullOrWhiteSpace(row.Get("deviceCode"))
                    ? (int?)null
                    : (await GetDeviceByCodeAsync(row.Get("deviceCode"))).Id;
                var mappingId = await ResolveBMappingIdAsync(row, deviceId);
                var dto = new EdgeBMappingSaveDto
                {
                    Id = mappingId,
                    DeviceId = deviceId,
                    SourceId = row.Get("sourceId"),
                    SourceDeviceCode = row.Get("sourceDeviceCode"),
                    MappingName = row.Get("mappingName"),
                    PayloadTemplateId = templateId,
                    Protocol = string.IsNullOrWhiteSpace(row.Get("protocol")) ? "HTTP" : row.Get("protocol"),
                    TargetUrl = row.Get("targetUrl"),
                    HttpMethod = string.IsNullOrWhiteSpace(row.Get("httpMethod")) ? "POST" : row.Get("httpMethod"),
                    MqttTopic = row.Get("mqttTopic"),
                    MqttClientId = row.Get("mqttClientId"),
                    MqttUsername = row.Get("mqttUsername"),
                    MqttPassword = string.IsNullOrWhiteSpace(row.Get("mqttPassword")) ? null : row.Get("mqttPassword"),
                    MqttQosLevel = ToInt(row.Get("mqttQosLevel"), 1),
                    MqttRetain = ToBool(row.Get("mqttRetain"), false),
                    MappingJson = string.IsNullOrWhiteSpace(row.Get("mappingJson")) ? "{}" : row.Get("mappingJson"),
                    HeadersJson = string.IsNullOrWhiteSpace(row.Get("headersJson")) ? null : row.Get("headersJson"),
                    Enabled = ToBool(row.Get("enabled"), true)
                };
                await SaveBMappingRuleAsync(dto);
                result.SuccessCount++;
            }
            catch (Exception ex)
            {
                AddFailure(result, row, key, ex.Message);
            }
        }
    }

    private async Task<int> SaveDeviceFromImportAsync(EdgeImportRow row)
    {
        var deviceCode = row.Get("deviceCode");
        if (string.IsNullOrWhiteSpace(deviceCode)) throw new InvalidOperationException("边缘节点编码不能为空");
        var existing = await FindDeviceAsync(ToInt(row.Get("deviceId"), 0), deviceCode);
        var dto = new EdgeDeviceSaveDto
        {
            Id = existing?.Id ?? 0,
            DeviceCode = deviceCode,
            DeviceName = string.IsNullOrWhiteSpace(row.Get("deviceName"))
                ? existing?.DeviceName ?? deviceCode
                : row.Get("deviceName"),
            SiteName = row.Get("siteName"),
            ConfiguredIpAddress = row.Get("configuredIpAddress"),
            DeviceType = row.Get("deviceType"),
            GroupName = row.Get("groupName"),
            Enabled = ToBool(row.Get("deviceEnabled"), existing?.Enabled ?? true)
        };
        return await SaveDeviceAsync(dto);
    }

    private async Task<int> SaveWebSocketSourceFromImportAsync(int deviceId, List<EdgeImportRow> rows)
    {
        var row = rows[0];
        if (string.IsNullOrWhiteSpace(row.Get("sourceId"))) throw new InvalidOperationException("数据源标识不能为空");
        var existingId = await ResolveWebSocketSourceIdAsync(deviceId, row);
        var dto = new EdgeWebSocketSourceSaveDto
        {
            Id = existingId,
            SourceId = row.Get("sourceId"),
            SourceName = string.IsNullOrWhiteSpace(row.Get("sourceName")) ? row.Get("sourceId") : row.Get("sourceName"),
            Endpoint = row.Get("endpoint"),
            SubscriptionPayload = string.IsNullOrWhiteSpace(row.Get("subscriptionPayload")) ? null : row.Get("subscriptionPayload"),
            HeadersJson = string.IsNullOrWhiteSpace(row.Get("headersJson")) ? null : row.Get("headersJson"),
            DataType = string.IsNullOrWhiteSpace(row.Get("dataType")) ? "Telemetry" : row.Get("dataType"),
            ReconnectIntervalSeconds = ToInt(row.Get("reconnectIntervalSeconds"), 5),
            Enabled = ToBool(row.Get("sourceEnabled"), true)
        };
        return await _webSocketSourceService.SaveAsync(deviceId, dto);
    }

    private static List<EdgeWebSocketSubscriptionDeviceSaveDto> BuildSubscriptionsFromImportRows(
        List<EdgeImportRow> rows)
    {
        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Get("subscriptionDeviceCode")))
            .GroupBy(x => x.Get("subscriptionDeviceCode"), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new EdgeWebSocketSubscriptionDeviceSaveDto
                {
                    DeviceCode = first.Get("subscriptionDeviceCode"),
                    DeviceName = first.Get("subscriptionDeviceName"),
                    Enabled = ToBool(first.Get("subscriptionEnabled"), true),
                    Sort = ToInt(first.Get("subscriptionSort"), 0),
                    Parameters = group
                        .Where(x => !string.IsNullOrWhiteSpace(x.Get("parameterName")))
                        .Select(parameter => new EdgeWebSocketSubscriptionParameterSaveDto
                        {
                            ParameterName = parameter.Get("parameterName"),
                            ParameterNameCn = parameter.Get("parameterNameCn"),
                            DataType = parameter.Get("parameterDataType"),
                            Enabled = ToBool(parameter.Get("parameterEnabled"), true),
                            Sort = ToInt(parameter.Get("parameterSort"), 0)
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    private async Task<EdgeDeviceEntity> GetDeviceByCodeAsync(string deviceCode)
    {
        if (string.IsNullOrWhiteSpace(deviceCode)) throw new InvalidOperationException("边缘节点编码不能为空");
        var device = await Db.Queryable<EdgeDeviceEntity>()
            .FirstAsync(x => x.IsDelete == 0 && x.DeviceCode == deviceCode.Trim());
        return device ?? throw new InvalidOperationException($"设备不存在：{deviceCode}");
    }

    private async Task<EdgeDeviceEntity?> FindDeviceAsync(int id, string deviceCode)
    {
        if (id > 0)
        {
            var byId = await Db.Queryable<EdgeDeviceEntity>()
                .FirstAsync(x => x.Id == id && x.IsDelete == 0);
            if (byId != null) return byId;
        }
        if (string.IsNullOrWhiteSpace(deviceCode)) return null;
        return await Db.Queryable<EdgeDeviceEntity>()
            .FirstAsync(x => x.DeviceCode == deviceCode.Trim() && x.IsDelete == 0);
    }

    private async Task<int> ResolveWebSocketSourceIdAsync(int deviceId, EdgeImportRow row)
    {
        var sourceDbId = ToInt(row.Get("sourceDbId"), 0);
        if (sourceDbId > 0)
        {
            var exists = await Db.Queryable<EdgeWebSocketSourceEntity>()
                .AnyAsync(x => x.Id == sourceDbId && x.DeviceId == deviceId && x.IsDelete == 0);
            if (exists) return sourceDbId;
        }

        var source = await Db.Queryable<EdgeWebSocketSourceEntity>()
            .FirstAsync(x => x.DeviceId == deviceId && x.SourceId == row.Get("sourceId") && x.IsDelete == 0);
        return source?.Id ?? 0;
    }

    private async Task<int> ResolveRuleIdAsync(EdgeImportRow row, int? deviceId)
    {
        var id = ToInt(row.Get("ruleId"), 0);
        if (id > 0)
        {
            var exists = await Db.Queryable<EdgeRuleEntity>().AnyAsync(x => x.Id == id && x.IsDelete == 0);
            if (exists) return id;
        }
        var ruleCode = row.Get("ruleCode");
        var existing = await Db.Queryable<EdgeRuleEntity>()
            .FirstAsync(x => x.IsDelete == 0 && x.DeviceId == deviceId && x.RuleCode == ruleCode);
        return existing?.Id ?? 0;
    }

    private async Task<int?> SaveBPayloadTemplateFromImportAsync(EdgeImportRow row)
    {
        var templateId = ToInt(row.Get("templateId"), 0);
        var templateCode = row.Get("templateCode");
        var templateName = row.Get("templateName");
        var templateJson = row.Get("templateJson");
        var hasTemplatePayload = templateId > 0 ||
                                 !string.IsNullOrWhiteSpace(templateCode) ||
                                 !string.IsNullOrWhiteSpace(templateName) ||
                                 !string.IsNullOrWhiteSpace(templateJson);
        if (!hasTemplatePayload) return null;

        var existing = templateId > 0
            ? await Db.Queryable<EdgeBPayloadTemplateEntity>().FirstAsync(x => x.Id == templateId && x.IsDelete == 0)
            : null;
        if (existing == null && !string.IsNullOrWhiteSpace(templateCode))
            existing = await Db.Queryable<EdgeBPayloadTemplateEntity>()
                .FirstAsync(x => x.TemplateCode == templateCode && x.IsDelete == 0);
        if (existing != null &&
            string.IsNullOrWhiteSpace(templateCode) &&
            string.IsNullOrWhiteSpace(templateName) &&
            string.IsNullOrWhiteSpace(templateJson))
            return existing.Id;

        templateCode = string.IsNullOrWhiteSpace(templateCode) ? existing?.TemplateCode ?? "" : templateCode;
        templateName = string.IsNullOrWhiteSpace(templateName) ? existing?.TemplateName ?? "" : templateName;
        templateJson = string.IsNullOrWhiteSpace(templateJson) ? existing?.TemplateJson ?? "{}" : templateJson;
        if (string.IsNullOrWhiteSpace(templateCode) || string.IsNullOrWhiteSpace(templateName))
            throw new InvalidOperationException("模板编码和模板名称不能为空");
        ValidateJson(templateJson, "templateJson");

        if (existing == null)
        {
            var created = new EdgeBPayloadTemplateEntity
            {
                TemplateCode = templateCode.Trim(),
                TemplateName = templateName.Trim(),
                TemplateJson = templateJson,
                Description = string.IsNullOrWhiteSpace(row.Get("templateDescription")) ? null : row.Get("templateDescription"),
                Enabled = ToBool(row.Get("templateEnabled"), true),
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
            return await Db.Insertable(created).ExecuteReturnIdentityAsync();
        }

        existing.TemplateCode = templateCode.Trim();
        existing.TemplateName = templateName.Trim();
        existing.TemplateJson = templateJson;
        existing.Description = string.IsNullOrWhiteSpace(row.Get("templateDescription")) ? null : row.Get("templateDescription");
        existing.Enabled = ToBool(row.Get("templateEnabled"), existing.Enabled);
        existing.UpdateTime = DateTime.Now;
        await Db.Updateable(existing).ExecuteCommandAsync();
        return existing.Id;
    }

    private async Task<int> ResolveBMappingIdAsync(EdgeImportRow row, int? deviceId)
    {
        var id = ToInt(row.Get("mappingId"), 0);
        if (id > 0)
        {
            var exists = await Db.Queryable<EdgeBMappingRuleEntity>().AnyAsync(x => x.Id == id && x.IsDelete == 0);
            if (exists) return id;
        }

        var sourceId = string.IsNullOrWhiteSpace(row.Get("sourceId")) ? null : row.Get("sourceId");
        var sourceDeviceCode = string.IsNullOrWhiteSpace(row.Get("sourceDeviceCode")) ? null : row.Get("sourceDeviceCode");
        var query = Db.Queryable<EdgeBMappingRuleEntity>().Where(x => x.IsDelete == 0);
        query = deviceId.HasValue ? query.Where(x => x.DeviceId == deviceId) : query.Where(x => x.DeviceId == null);
        query = sourceId == null ? query.Where(x => x.SourceId == null) : query.Where(x => x.SourceId == sourceId);
        query = sourceDeviceCode == null
            ? query.Where(x => x.SourceDeviceCode == null)
            : query.Where(x => x.SourceDeviceCode == sourceDeviceCode);
        var existing = await query.OrderByDescending(x => x.Enabled).FirstAsync();
        return existing?.Id ?? 0;
    }

    private static Dictionary<string, List<EdgeImportRow>> ReadWorkbookRows(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var workbook = WorkbookFactory.Create(stream);
        var result = new Dictionary<string, List<EdgeImportRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetName in new[] { ConnectionSheet, CollectionSheet, RuleSheet, BMappingSheet })
            result[sheetName] = ReadSheetRows(workbook.GetSheet(sheetName), sheetName);
        return result;
    }

    private static List<EdgeImportRow> ReadSheetRows(ISheet? sheet, string sheetName)
    {
        var result = new List<EdgeImportRow>();
        if (sheet == null || sheet.LastRowNum < HeaderRowIndex) return result;

        var headerRow = sheet.GetRow(HeaderRowIndex);
        if (headerRow == null) return result;
        var headers = new List<(int Index, string Name)>();
        for (var cellIndex = 0; cellIndex < headerRow.LastCellNum; cellIndex++)
        {
            var header = GetCellText(headerRow.GetCell(cellIndex)).Trim();
            if (!string.IsNullOrWhiteSpace(header))
                headers.Add((cellIndex, TranslateImportHeader(sheetName, header)));
        }

        for (var rowIndex = DataStartRowIndex; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var sheetRow = sheet.GetRow(rowIndex);
            if (sheetRow == null) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
                values[header.Name] = GetCellText(sheetRow.GetCell(header.Index)).Trim();
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            result.Add(new EdgeImportRow
            {
                SheetName = sheetName,
                RowIndex = rowIndex + 1,
                Values = values
            });
        }
        return result;
    }

    private static Dictionary<string, string> BuildHeaderAliases(
        IReadOnlyList<string> fields,
        IReadOnlyList<string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Count; index++)
        {
            result[fields[index]] = fields[index];
            if (index < headers.Count) result[headers[index]] = fields[index];
        }
        return result;
    }

    private static string TranslateImportHeader(string sheetName, string header)
    {
        if (ImportHeaderAliases.TryGetValue(sheetName, out var aliases) &&
            aliases.TryGetValue(header, out var field))
            return field;
        return header;
    }

    private static string GetCellText(ICell? cell)
    {
        if (cell == null) return string.Empty;
        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue ?? string.Empty,
            CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty
                : FormatNumber(cell.NumericCellValue),
            CellType.Formula => GetFormulaCellText(cell),
            CellType.Error => FormulaError.ForInt(cell.ErrorCellValue).String,
            _ => string.Empty
        };
    }

    private static string GetFormulaCellText(ICell cell)
    {
        return cell.CachedFormulaResultType switch
        {
            CellType.String => cell.StringCellValue ?? string.Empty,
            CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty
                : FormatNumber(cell.NumericCellValue),
            CellType.Error => FormulaError.ForInt(cell.ErrorCellValue).String,
            _ => string.Empty
        };
    }

    private static string FormatNumber(double value) =>
        Math.Abs(value % 1) < 0.0000001
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static bool IsBlankRow(EdgeImportRow row) =>
        row.Values.Values.All(string.IsNullOrWhiteSpace);

    private static void AddFailures(
        EdgeGatewayImportResultDto result,
        IEnumerable<EdgeImportRow> rows,
        string key,
        string reason)
    {
        foreach (var row in rows) AddFailure(result, row, key, reason);
    }

    private static void AddFailure(
        EdgeGatewayImportResultDto result,
        EdgeImportRow row,
        string key,
        string reason)
    {
        result.FailedCount++;
        result.Failures.Add(new EdgeGatewayImportFailureDto
        {
            SheetName = row.SheetName,
            RowIndex = row.RowIndex,
            Key = key,
            Reason = string.IsNullOrWhiteSpace(reason) ? "导入失败" : reason
        });
    }

    private static bool ToBool(string value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" or "是" or "启用" or "有效" => true,
            "false" or "0" or "no" or "n" or "否" or "停用" or "禁用" or "无效" => false,
            _ => fallback
        };
    }

    private static int ToInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static decimal ToDecimal(string value, decimal fallback) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static string ToChineseBool(bool value) => value ? "是" : "否";

    private static string NormalizeTransformType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "direct";
        return value.Trim().ToLowerInvariant() switch
        {
            "direct" or "直接映射" => "direct",
            "numeric" or "数值转换" => "numeric",
            "enum" or "枚举转换" => "enum",
            "enumnumeric" or "枚举后数值转换" => "enumNumeric",
            _ => throw new InvalidOperationException(
                "转换类型仅支持直接映射、数值转换、枚举转换、枚举后数值转换")
        };
    }

    private static string ToChineseTransformType(string value) =>
        NormalizeTransformType(value) switch
        {
            "numeric" => "数值转换",
            "enum" => "枚举转换",
            "enumNumeric" => "枚举后数值转换",
            _ => "直接映射"
        };

    private static string NormalizeRuleType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Filter";
        return value.Trim().ToLowerInvariant() switch
        {
            "filter" or "条件过滤" => "Filter",
            "range" or "范围过滤" => "Range",
            "threshold" or "变化阈值" => "Threshold",
            "deduplicate" or "重复消息过滤" => "Deduplicate",
            "aggregate" or "时间窗口聚合" => "Aggregate",
            _ => value.Trim()
        };
    }

    private static string ToChineseRuleType(string value)
    {
        var normalized = NormalizeRuleType(value);
        return normalized switch
        {
            "Filter" => "条件过滤",
            "Range" => "范围过滤",
            "Threshold" => "变化阈值",
            "Deduplicate" => "重复消息过滤",
            "Aggregate" => "时间窗口聚合",
            _ => value
        };
    }

    private static string NormalizeRuleOperator(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Trim().ToLowerInvariant() switch
        {
            "greaterthan" or "大于" => "GreaterThan",
            "greaterthanorequal" or "大于等于" => "GreaterThanOrEqual",
            "lessthan" or "小于" => "LessThan",
            "lessthanorequal" or "小于等于" => "LessThanOrEqual",
            "equal" or "等于" => "Equal",
            "notequal" or "不等于" => "NotEqual",
            "contains" or "包含" => "Contains",
            "avg" or "平均值" => "Avg",
            "sum" or "求和" => "Sum",
            "min" or "最小值" => "Min",
            "max" or "最大值" => "Max",
            "count" or "计数" => "Count",
            _ => value.Trim()
        };
    }

    private static string ToChineseRuleOperator(string value) =>
        NormalizeRuleOperator(value) switch
        {
            "GreaterThan" => "大于",
            "GreaterThanOrEqual" => "大于等于",
            "LessThan" => "小于",
            "LessThanOrEqual" => "小于等于",
            "Equal" => "等于",
            "NotEqual" => "不等于",
            "Contains" => "包含",
            "Avg" => "平均值",
            "Sum" => "求和",
            "Min" => "最小值",
            "Max" => "最大值",
            "Count" => "计数",
            _ => value
        };

    private static string NormalizeRuleAction(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Drop";
        return value.Trim().ToLowerInvariant() switch
        {
            "drop" or "丢弃" => "Drop",
            _ => value.Trim()
        };
    }

    private static string ToChineseRuleAction(string value) =>
        NormalizeRuleAction(value) == "Drop" ? "丢弃" : value;

    private static Dictionary<string, string> ParseJsonObject(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => property.Value.GetRawText()
                };
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    private static List<Dictionary<string, string>> ExtractCollectionMappings(string configJson)
    {
        var result = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(configJson)) return result;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(document.RootElement, "mappings", out var mappings) ||
                mappings.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var mapping in mappings.EnumerateArray())
            {
                if (mapping.ValueKind != JsonValueKind.Object) continue;
                var factor = ReadJsonText(mapping, "factor", "1");
                var offset = ReadJsonText(mapping, "offset", "0");
                var enumMapJson = ReadJsonText(mapping, "enumMap", "{}");
                var hasEnum = enumMapJson != "{}" && enumMapJson != "null";
                var hasNumeric = ToDecimal(factor, 1) != 1 || ToDecimal(offset, 0) != 0;
                result.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sourceId"] = ReadJsonText(mapping, "sourceId"),
                    ["sourceField"] = ReadJsonText(mapping, "sourceField"),
                    ["targetField"] = ReadJsonText(mapping, "targetField"),
                    ["transformType"] = hasEnum && hasNumeric
                        ? "enumNumeric"
                        : hasEnum ? "enum" : hasNumeric ? "numeric" : "direct",
                    ["factor"] = factor,
                    ["offset"] = offset,
                    ["enumMapJson"] = enumMapJson
                });
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    private static string BuildRuleJson(EdgeImportRow row)
    {
        var ruleType = NormalizeRuleType(row.Get("ruleType"));
        var supportedTypes = new[] { "Filter", "Range", "Threshold", "Deduplicate", "Aggregate" };
        var canonicalType = supportedTypes.FirstOrDefault(x => x.Equals(ruleType, StringComparison.OrdinalIgnoreCase));
        if (canonicalType == null)
            throw new InvalidOperationException("规则类型不受支持");

        var action = NormalizeRuleAction(row.Get("action"));
        if (!action.Equals("Drop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("动作当前仅支持丢弃");

        var body = new Dictionary<string, object?> { ["action"] = "Drop" };
        if (!canonicalType.Equals("Deduplicate", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(row.Get("field")))
                throw new InvalidOperationException("目标字段不能为空");
            body["field"] = row.Get("field");
        }

        if (canonicalType.Equals("Filter", StringComparison.OrdinalIgnoreCase))
        {
            var operatorName = string.IsNullOrWhiteSpace(row.Get("operator"))
                ? "Equal"
                : NormalizeRuleOperator(row.Get("operator"));
            body["operator"] = operatorName;
            if (!string.IsNullOrWhiteSpace(row.Get("compareValue")))
                body["compareValue"] = ParseRequiredDecimal(row.Get("compareValue"), "数值比较值");
            else if (!string.IsNullOrWhiteSpace(row.Get("compareText")))
                body["compareText"] = row.Get("compareText");
            else
                throw new InvalidOperationException("条件过滤规则必须填写数值比较值或文本比较值");
        }
        else if (canonicalType.Equals("Range", StringComparison.OrdinalIgnoreCase))
        {
            var hasMin = !string.IsNullOrWhiteSpace(row.Get("min"));
            var hasMax = !string.IsNullOrWhiteSpace(row.Get("max"));
            if (!hasMin && !hasMax) throw new InvalidOperationException("范围过滤规则至少填写最小值或最大值");
            if (hasMin) body["min"] = ParseRequiredDecimal(row.Get("min"), "最小值");
            if (hasMax) body["max"] = ParseRequiredDecimal(row.Get("max"), "最大值");
            if (hasMin && hasMax && (decimal)body["min"]! > (decimal)body["max"]!)
                throw new InvalidOperationException("最小值不能大于最大值");
        }
        else if (canonicalType.Equals("Threshold", StringComparison.OrdinalIgnoreCase))
        {
            body["threshold"] = ParseRequiredDecimal(row.Get("threshold"), "变化阈值");
        }
        else if (canonicalType.Equals("Deduplicate", StringComparison.OrdinalIgnoreCase))
        {
            body["windowSeconds"] = ParsePositiveInt(row.Get("windowSeconds"), "窗口秒数");
        }
        else
        {
            var operatorName = string.IsNullOrWhiteSpace(row.Get("operator"))
                ? "Avg"
                : NormalizeRuleOperator(row.Get("operator"));
            var supportedOperators = new[] { "Avg", "Sum", "Min", "Max", "Count" };
            var canonicalOperator = supportedOperators.FirstOrDefault(x =>
                x.Equals(operatorName, StringComparison.OrdinalIgnoreCase));
            if (canonicalOperator == null)
                throw new InvalidOperationException("时间窗口聚合操作符仅支持平均值、求和、最小值、最大值、计数");
            body["operator"] = canonicalOperator;
            body["windowSeconds"] = ParsePositiveInt(row.Get("windowSeconds"), "窗口秒数");
        }

        return JsonSerializer.Serialize(body);
    }

    private static decimal ParseRequiredDecimal(string value, string fieldName)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"{fieldName} 必须是数字");
        return parsed;
    }

    private static int ParsePositiveInt(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            throw new InvalidOperationException($"{fieldName} 必须是大于 0 的整数");
        return parsed;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string ReadJsonText(JsonElement element, string name, string fallback = "")
    {
        if (!TryGetPropertyIgnoreCase(element, name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => value.GetRawText()
        };
    }

    /// <summary>
    /// 校验映射作用范围真实存在，并保证同一精确路由最多只有一条启用映射。
    /// </summary>
    private async Task ValidateBMappingScopeAsync(EdgeBMappingSaveDto dto)
    {
        if (dto.DeviceId.HasValue) await EnsureDeviceAsync(dto.DeviceId.Value);

        if (!string.IsNullOrWhiteSpace(dto.SourceId))
        {
            var source = await Db.Queryable<EdgeWebSocketSourceEntity>().FirstAsync(x =>
                x.DeviceId == dto.DeviceId!.Value &&
                x.SourceId == dto.SourceId &&
                x.IsDelete == 0);
            if (source == null)
                throw new InvalidOperationException("选择的 WebSocket 数据源不存在或不属于当前边缘节点");

            var subscriptionExists = await Db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>().AnyAsync(x =>
                x.WebSocketSourceId == source.Id &&
                x.DeviceCode == dto.SourceDeviceCode &&
                x.IsDelete == 0);
            if (!subscriptionExists)
                throw new InvalidOperationException("选择的 WebSocket 订阅设备不存在或不属于当前数据源");
        }

        if (!dto.Enabled) return;

        var duplicateQuery = Db.Queryable<EdgeBMappingRuleEntity>()
            .Where(x => x.IsDelete == 0 && x.Enabled && x.Id != dto.Id);
        duplicateQuery = dto.DeviceId.HasValue
            ? duplicateQuery.Where(x => x.DeviceId == dto.DeviceId.Value)
            : duplicateQuery.Where(x => x.DeviceId == null);
        duplicateQuery = string.IsNullOrWhiteSpace(dto.SourceId)
            ? duplicateQuery.Where(x => x.SourceId == null)
            : duplicateQuery.Where(x => x.SourceId == dto.SourceId);
        duplicateQuery = string.IsNullOrWhiteSpace(dto.SourceDeviceCode)
            ? duplicateQuery.Where(x => x.SourceDeviceCode == null)
            : duplicateQuery.Where(x => x.SourceDeviceCode == dto.SourceDeviceCode);

        if (await duplicateQuery.AnyAsync())
            throw new InvalidOperationException($"{GetBMappingScopeDescription(dto)}已经存在启用的 B 系统映射");
    }

    /// <summary>生成便于前端理解的 B 映射作用范围名称。</summary>
    private static string GetBMappingScopeDescription(EdgeBMappingSaveDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.SourceDeviceCode))
            return $"订阅设备 {dto.SourceDeviceCode}";
        return dto.DeviceId.HasValue ? "当前边缘节点默认范围" : "全局默认范围";
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

    /// <summary>去除首尾空白、保留空值，并限制可索引文本字段的长度。</summary>
    private static string? TrimToNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>
    /// 写入接收日表和追溯历史点，并分别持久化完成标记。一项失败不会影响另一项，
    /// 未完成标记会由后台补偿任务继续处理。
    /// </summary>
    private async Task ProjectMessageAsync(
        EdgeMessageQueueEntity queueEntity,
        EdgeStandardMessageDto message,
        CancellationToken cancellationToken)
    {
        if (queueEntity.ReceiveRecordProjectedTime == null)
        {
            try
            {
                await AddWebSocketReceiveRecordAsync(message, queueEntity.DeviceCode, queueEntity.CreateTime);
                queueEntity.ReceiveRecordProjectedTime = DateTime.Now;
                await Db.Updateable<EdgeMessageQueueEntity>()
                    .SetColumns(x => new EdgeMessageQueueEntity
                    {
                        ReceiveRecordProjectedTime = queueEntity.ReceiveRecordProjectedTime
                    })
                    .Where(x => x.Id == queueEntity.Id)
                    .ExecuteCommandAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "WebSocket receive record projection failed for queue message {MessageId}; it will be retried.",
                    queueEntity.MessageId);
            }
        }

        if (queueEntity.DataTraceProjectedTime == null)
        {
            try
            {
                await _dataTraceService.CaptureAsync(message, queueEntity.DeviceCode, cancellationToken);
                queueEntity.DataTraceProjectedTime = DateTime.Now;
                await Db.Updateable<EdgeMessageQueueEntity>()
                    .SetColumns(x => new EdgeMessageQueueEntity
                    {
                        DataTraceProjectedTime = queueEntity.DataTraceProjectedTime
                    })
                    .Where(x => x.Id == queueEntity.Id)
                    .ExecuteCommandAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Data trace projection failed for queue message {MessageId}; it will be retried.",
                    queueEntity.MessageId);
            }
        }
    }

    /// <summary>把一条已进入中心端队列的 WebSocket 原始报文写入所属日期的物理日表。</summary>
    private async Task AddWebSocketReceiveRecordAsync(
        EdgeStandardMessageDto message,
        string deviceCode,
        DateTime receivedTime)
    {
        var now = receivedTime == default ? DateTime.Now : receivedTime;
        await EnsureWebSocketReceiveRecordTableAsync(now, cancellationToken: CancellationToken.None);
        var exists = await WebSocketLogDb.Queryable<EdgeWebSocketReceiveRecordEntity>()
            .SplitTable(now.Date, now.Date)
            .AnyAsync(x => x.MessageId == message.MessageId);
        if (exists) return;
        var record = new EdgeWebSocketReceiveRecordEntity
        {
            MessageId = message.MessageId.Trim(),
            DeviceCode = deviceCode.Trim(),
            SourceId = message.SourceId.Trim(),
            SourceDeviceCode = TrimToNull(message.SourceDeviceCode, 128),
            DataType = string.IsNullOrWhiteSpace(message.DataType) ? "Telemetry" : message.DataType.Trim()[..Math.Min(message.DataType.Trim().Length, 64)],
            RawPayload = string.IsNullOrWhiteSpace(message.RawPayload) ? "{}" : message.RawPayload,
            MessageTime = message.Timestamp,
            CreateTime = now
        };
        record.Id = await WebSocketLogDb.Insertable(record)
            .SplitTable()
            .ExecuteReturnSnowflakeIdAsync();
    }

    /// <summary>
    /// 服务跨天运行时，不能依赖启动阶段创建的旧日日表；首次写入新日期前按实体模型创建表结构。
    /// </summary>
    private async Task EnsureWebSocketReceiveRecordTableAsync(DateTime day, CancellationToken cancellationToken)
    {
        var tableName = $"EdgeGatewayWebSocketReceiveRecord_{day:yyyyMMdd}";
        lock (EnsuredWebSocketReceiveTables)
        {
            if (EnsuredWebSocketReceiveTables.Contains(tableName)) return;
        }

        await WebSocketReceiveTableGate.WaitAsync(cancellationToken);
        try
        {
            lock (EnsuredWebSocketReceiveTables)
            {
                if (EnsuredWebSocketReceiveTables.Contains(tableName)) return;
            }

            if (!WebSocketLogDb.DbMaintenance.IsAnyTable(tableName, false))
                WebSocketLogDb.CodeFirst.As<EdgeWebSocketReceiveRecordEntity>(tableName).InitTables();
            lock (EnsuredWebSocketReceiveTables)
            {
                EnsuredWebSocketReceiveTables.Add(tableName);
            }
        }
        finally
        {
            WebSocketReceiveTableGate.Release();
        }
    }

    /// <summary>日表查询日期最多覆盖今天及之前六日，严格限制在最近七个自然日。</summary>
    private static (DateTime StartDate, DateTime EndDate) GetWebSocketReceiveRecordDateRange(
        EdgeWebSocketReceiveRecordQueryDto query)
    {
        var today = DateTime.Today;
        var oldestRetainedDate = today.AddDays(-(WebSocketReceiveRecordRetentionDays - 1));
        var startDate = query.StartDate?.Date ?? oldestRetainedDate;
        var endDate = query.EndDate?.Date ?? today;
        if (startDate < oldestRetainedDate) startDate = oldestRetainedDate;
        if (endDate > today) endDate = today;
        return (startDate, endDate);
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
        ISqlSugarClient db,
        EdgeMessageQueueEntity record,
        EdgeBMappingRuleEntity? mapping,
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
        await db.Updateable(record).ExecuteCommandAsync();
        await AddUploadRecordAsync(
            db,
            record,
            mapping,
            targetUrl,
            false,
            statusCode,
            error,
            durationMs,
            responseContent);
    }

    /// <summary>记录一次中心端到 B 系统的协议转发尝试，并限制响应内容长度以控制日志表体积。</summary>
    private Task AddUploadRecordAsync(
        ISqlSugarClient db,
        EdgeMessageQueueEntity record,
        EdgeBMappingRuleEntity? mapping,
        string? targetUrl,
        bool success,
        int? statusCode,
        string? error,
        int durationMs,
        string? responseContent)
    {
        var uploadRecord = _mapper.Map<EdgeUploadRecordEntity>(record);
        uploadRecord.MappingRuleId = mapping?.Id;
        uploadRecord.MappingName = mapping?.MappingName;
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
        return db.Insertable(uploadRecord).ExecuteCommandAsync();
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
            var root = JObject.Parse(responseContent);
            var codeToken = root.GetValue("code", StringComparison.OrdinalIgnoreCase);
            if (codeToken == null) return false;

            var codeText = codeToken.Type == JTokenType.String
                ? codeToken.Value<string>()?.Trim()
                : codeToken.ToString(Newtonsoft.Json.Formatting.None);
            if (int.TryParse(codeText, out var businessCode) && (businessCode == 0|| businessCode == 200))
                return false;

            var messageToken = root.GetValue("msg", StringComparison.OrdinalIgnoreCase) ??
                               root.GetValue("message", StringComparison.OrdinalIgnoreCase);
            var message = messageToken?.Type == JTokenType.String
                ? messageToken.Value<string>()
                : messageToken?.ToString(Newtonsoft.Json.Formatting.None);

            error = string.IsNullOrWhiteSpace(message)
                ? $"B 系统业务返回失败：code={codeText ?? "null"}"
                : $"B 系统业务返回失败：code={codeText ?? "null"}，{message}";
            return true;
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            // 无法解析或不包含业务 code 的旧接口继续沿用 HTTP 状态判断。
            return false;
        }
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
        dto.SourceId = string.IsNullOrWhiteSpace(dto.SourceId) ? null : dto.SourceId.Trim();
        dto.SourceDeviceCode = string.IsNullOrWhiteSpace(dto.SourceDeviceCode)
            ? null
            : dto.SourceDeviceCode.Trim();
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
        if ((dto.SourceId == null) != (dto.SourceDeviceCode == null))
            throw new InvalidOperationException("数据源标识和订阅设备代码必须同时选择");
        if (dto.SourceId != null && !dto.DeviceId.HasValue)
            throw new InvalidOperationException("订阅设备映射必须先选择所属边缘节点");
        if (dto.SourceId?.Length > 64)
            throw new InvalidOperationException("数据源标识不能超过 64 个字符");
        if (dto.SourceDeviceCode?.Length > 128)
            throw new InvalidOperationException("订阅设备代码不能超过 128 个字符");
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
        dto.SourceId,
        dto.SourceDeviceCode,
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
