using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Model.EdgeGateway;
using Model.ExternalTools;
using Model.Other;
using Model.ProjectManage;
using Model.User;
using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

/// <summary>
/// 外部工具的唯一业务适配层。
/// 不调用 AgentDispatcher，也不公开原始报文或删除动作。
/// 按调用者工具范围和业务资源授权执行；写操作必须确认并先记录审计。
/// </summary>
public sealed class ExternalToolService : IExternalToolService
{
    private const int AccessTokenLifetimeMinutes = 30;
    private readonly SqlSugarScope _dbScope;
    private readonly IEdgeDashboardService _dashboardService;
    private readonly IEdgeGatewayService _edgeGatewayService;
    private readonly IProjectManageService _projectManageService;
    private readonly IUserActivityService _userActivityService;
    private readonly IWeChatClawBotService _weChatClawBotService;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    private static readonly IReadOnlyDictionary<string, ExternalToolDefinition> Definitions =
        new Dictionary<string, ExternalToolDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["production.line-runtime"] = Definition(
                "production.line-runtime", "生产线实时状态", "读取指定线体的机械手、设备绑定展示字段和实时运行状态。",
                "dashboard.read", LineSchema),
            ["production.line-output"] = Definition(
                "production.line-output", "生产线当日产量", "读取指定线体依据已配置产量字段计算出的当日产出。",
                "dashboard.read", LineSchema),
            ["production.fault-summary"] = Definition(
                "production.fault-summary", "生产故障汇总", "按线体和时间范围统计故障次数、持续时间及高频故障原因。",
                "dashboard.read", FaultSummarySchema),
            ["device-card.runtime"] = Definition(
                "device-card.runtime", "设备卡片实时状态", "读取指定线体已配置设备卡片的当前状态及展示字段，不返回完整原始报文。",
                "device-card.read", LineSchema),
            ["device-card.history"] = Definition(
                "device-card.history", "设备字段变更追溯", "查询指定线体设备卡片已启用追溯字段的变更记录。",
                "device-history.read", DeviceHistorySchema),
            ["device-card.alerts"] = Definition(
                "device-card.alerts", "设备字段越限告警记录", "查询设备卡片数值字段超出配置上下限后生成的告警及恢复记录。",
                "device-alert.read", DeviceAlertSchema),
            ["device-card.alerts.claim"] = Definition(
                "device-card.alerts.claim", "认领待推送设备越限告警", "原子认领待推送越限事件，返回消息推送所需数据；认领后须确认结果。",
                "device-alert.write", DeviceAlertClaimSchema, true),
            ["device-card.alerts.ack"] = Definition(
                "device-card.alerts.ack", "确认设备越限告警推送结果", "推送成功后确认已发送，失败时退回待推送队列。",
                "device-alert.write", DeviceAlertAckSchema, true),
            ["project.get"] = Definition(
                "project.get", "项目详情", "按已授权项目编号读取项目基本信息、进度和任务数量。",
                "project.read", ProjectSchema),
            ["project.tasks"] = Definition(
                "project.tasks", "项目任务", "按已授权项目编号读取任务进度、负责人和状态。",
                "project.read", ProjectSchema),
            ["edge.overview"] = Definition(
                "edge.overview", "边缘计算运行概览", "读取边缘设备在线、连接收数、积压、转发和告警汇总。",
                "edge.overview.read", EmptySchema),
            ["edge.device-status"] = Definition(
                "edge.device-status", "边缘设备状态", "按已授权设备编码读取心跳、在线、队列深度和资源使用情况。",
                "edge.device.read", EdgeDeviceSchema),
            ["edge.queue-status"] = Definition(
                "edge.queue-status", "边缘消息队列状态", "按已授权设备编码读取队列处理状态，不返回报文内容。",
                "edge.queue.read", EdgeDeviceSchema),
            ["edge.events"] = Definition(
                "edge.events", "边缘运行事件", "按已授权设备编码读取运行事件摘要，不返回事件详细报文。",
                "edge.event.read", EdgeEventSchema),
            ["user.online-overview"] = Definition(
                "user.online-overview", "后台用户在线概览", "读取当前在线用户、会话数量、当日登录与写操作数量。",
                "user.audit.read", EmptySchema),
            ["user.login-records"] = Definition(
                "user.login-records", "后台用户登录记录", "分页查询后台用户成功或失败的登录记录及在线时长，不返回令牌或浏览器完整信息。",
                "user.audit.read", UserLoginLogSchema),
            ["user.operation-records"] = Definition(
                "user.operation-records", "后台用户操作记录", "分页查询后台用户的新增、修改、删除、导入、导出和配置保存审计记录。",
                "user.audit.read", UserOperationLogSchema),
            ["clawbot.recipients.list"] = Definition(
                "clawbot.recipients.list", "可推送 ClawBot 用户", "查询已绑定、已启用且同意接收外部 MCP 推送的后台用户。",
                "clawbot.read", EmptySchema),
            ["clawbot.channel-status"] = Definition(
                "clawbot.channel-status", "ClawBot 通道状态", "查询指定接收用户或全部已绑定用户的推送通道可用状态。",
                "clawbot.read", ClawBotStatusSchema),
            ["clawbot.delivery-status"] = Definition(
                "clawbot.delivery-status", "ClawBot 投递记录", "查询微信 ClawBot 的消息投递结果、失败原因和重试关联。",
                "clawbot.read", ClawBotDeliverySchema),
            ["clawbot.send"] = Definition(
                "clawbot.send", "发送 ClawBot 消息", "向一个或多个已授权、已绑定的后台用户发送文本消息；必须使用幂等键避免重复发送。",
                "clawbot.write", ClawBotSendSchema, true),
            ["clawbot.retry"] = Definition(
                "clawbot.retry", "重试失败 ClawBot 消息", "仅重试单条失败投递；会产生新的投递记录并关联原记录。",
                "clawbot.write", ClawBotRetrySchema, true)
        };

    public ExternalToolService(
        SqlSugarScope dbScope,
        IEdgeDashboardService dashboardService,
        IEdgeGatewayService edgeGatewayService,
        IProjectManageService projectManageService,
        IUserActivityService userActivityService,
        IWeChatClawBotService weChatClawBotService,
        IConfiguration configuration)
    {
        _dbScope = dbScope;
        _dashboardService = dashboardService;
        _edgeGatewayService = edgeGatewayService;
        _projectManageService = projectManageService;
        _userActivityService = userActivityService;
        _weChatClawBotService = weChatClawBotService;
        _configuration = configuration;
    }

    public async Task<ExternalToolClientSecretResultDto> SaveClientAsync(ExternalToolClientSaveDto dto)
    {
        await EnsureInitializedAsync();
        ValidateClientInput(dto);

        ExternalToolClientEntity client;
        string? plainSecret = null;

        if (dto.Id is > 0)
        {
            client = await Db.Queryable<ExternalToolClientEntity>().InSingleAsync(dto.Id.Value)
                     ?? throw new InvalidOperationException("外部工具客户端不存在");
            ApplyClientInput(client, dto);
            if (dto.RotateSecret)
            {
                plainSecret = GenerateClientSecret();
                client.SecretHash = Hash(plainSecret);
            }

            await Db.Updateable(client).ExecuteCommandAsync();
        }
        else
        {
            plainSecret = GenerateClientSecret();
            client = new ExternalToolClientEntity
            {
                ClientId = $"ext_{Guid.NewGuid():N}",
                SecretHash = Hash(plainSecret),
                CreateTime = DateTime.Now
            };
            ApplyClientInput(client, dto);
            await Db.Insertable(client).ExecuteCommandAsync();
        }

        return new ExternalToolClientSecretResultDto
        {
            Client = ToOutput(client),
            ClientSecret = plainSecret
        };
    }

    public async Task<List<ExternalToolClientOutputDto>> GetClientsAsync()
    {
        await EnsureInitializedAsync();
        var clients = await Db.Queryable<ExternalToolClientEntity>()
            .OrderByDescending(x => x.UpdateTime)
            .ToListAsync();
        return clients.Select(ToOutput).ToList();
    }

    public async Task<ExternalToolServerSettingDto> GetServerSettingAsync()
    {
        await EnsureInitializedAsync();
        return ToServerSettingDto(await GetOrCreateServerSettingAsync());
    }

    public async Task<ExternalToolServerSettingDto> SaveServerSettingAsync(
        ExternalToolServerSettingSaveDto dto,
        string updatedBy)
    {
        await EnsureInitializedAsync();
        if (string.IsNullOrWhiteSpace(dto.ServerName)) throw new InvalidOperationException("MCP 服务名称不能为空");
        if (dto.ServerName.Trim().Length > 128) throw new InvalidOperationException("MCP 服务名称不能超过 128 个字符");
        if (string.IsNullOrWhiteSpace(dto.Version)) throw new InvalidOperationException("MCP 服务版本不能为空");
        if (dto.Version.Trim().Length > 32) throw new InvalidOperationException("MCP 服务版本不能超过 32 个字符");

        var setting = await GetOrCreateServerSettingAsync();
        setting.ServerName = dto.ServerName.Trim();
        setting.Version = dto.Version.Trim();
        setting.UpdatedBy = updatedBy;
        setting.UpdateTime = DateTime.Now;
        await Db.Updateable(setting).ExecuteCommandAsync();
        return ToServerSettingDto(setting);
    }

    public async Task<ExternalToolTokenResponseDto?> IssueClientTokenAsync(ExternalToolTokenRequestDto dto)
    {
        await EnsureInitializedAsync();
        if (!string.Equals(dto.GrantType, "client_credentials", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(dto.ClientId) || string.IsNullOrWhiteSpace(dto.ClientSecret)) return null;

        var client = await Db.Queryable<ExternalToolClientEntity>()
            .SingleAsync(x => x.ClientId == dto.ClientId.Trim());
        if (client == null || !client.Enabled || client.ExpireTime <= DateTime.Now) return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(client.SecretHash),
                Encoding.UTF8.GetBytes(Hash(dto.ClientSecret)))) return null;

        var expires = DateTime.UtcNow.AddMinutes(AccessTokenLifetimeMinutes);
        var tokenOptions = _configuration.GetSection("JWTTokenOptions").Get<JWTTokenOptions>()
                           ?? throw new InvalidOperationException("JWTTokenOptions 未配置");
        var claims = new List<Claim>
        {
            new("client_id", client.ClientId),
            new("scope", NormalizeScopes(client.Scopes)),
            new("external_lines", client.AllowedLineCodes),
            new("external_projects", client.AllowedProjectCodes),
            new("external_devices", client.AllowedDeviceCodes)
        };
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SecurityKey));
        var token = new JwtSecurityToken(
            issuer: tokenOptions.Issuer,
            audience: ExternalToolAuthenticationDefaults.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new ExternalToolTokenResponseDto
        {
            AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresIn = (int)TimeSpan.FromMinutes(AccessTokenLifetimeMinutes).TotalSeconds,
            Scope = NormalizeScopes(client.Scopes)
        };
    }

    public IReadOnlyList<ExternalToolDefinitionDto> GetDefinitions(ExternalToolCallerContext caller) =>
        Definitions.Values
            .Where(definition => caller.IsDirectAccess || caller.Scopes.Contains(definition.RequiredScope))
            .Where(definition => caller.IsDirectAccess || caller.AllResources ||
                !definition.Name.StartsWith("user.") && !definition.Name.StartsWith("clawbot."))
            .Select(definition => ToAuthorizedDto(definition, caller))
            .ToList();

    private static ExternalToolDefinitionDto ToAuthorizedDto(ExternalToolDefinition definition, ExternalToolCallerContext caller)
    {
        var dto = definition.ToDto();
        if (caller.IsDirectAccess || caller.AllResources || definition.IsWrite) return dto;
        var schema = System.Text.Json.Nodes.JsonNode.Parse(dto.InputSchema.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        properties["scopeCurrent"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "授权资源分页页码，默认 1" };
        properties["scopeSize"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["description"] = "每次聚合的授权资源数量，默认 20" };
        dto.InputSchema = JsonSerializer.SerializeToElement(schema);
        dto.Description += " 不传资源编码时按授权子集分页查询，绝不查询未授权资源。";
        return dto;
    }

    public async Task<ExternalToolExecutionResultDto> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        ExternalToolCallerContext caller,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await EnsureInitializedAsync();
        var stopwatch = Stopwatch.StartNew();
        ExternalToolExecutionResultDto result;

        try
        {
            if (!Definitions.TryGetValue(toolName ?? string.Empty, out var definition))
                throw new InvalidOperationException("未找到允许公开的工具");
            if (!caller.IsDirectAccess && !caller.Scopes.Contains(definition.RequiredScope))
                throw new UnauthorizedAccessException("客户端未获此工具调用权限");
            if (definition.IsWrite)
                EnsureWriteConfirmed(arguments);

            if (!caller.IsDirectAccess && !caller.AllResources &&
                (definition.Name.StartsWith("user.") || definition.Name.StartsWith("clawbot.")))
                throw new UnauthorizedAccessException("该工具需要独立的用户或推送对象授权，不能沿用线体/项目资源权限");
            if (definition.IsWrite)
                await WriteAuditAsync(caller, definition.Name, requestId, arguments, "Attempted", null, 0,
                    stopwatch.ElapsedMilliseconds, required: true);
            var data = await ExecuteScopedAsync(definition.Name, arguments, caller, cancellationToken);
            result = new ExternalToolExecutionResultDto { Success = true, Message = "success", Data = data };
            await WriteAuditAsync(caller, definition.Name, requestId, arguments, "Success", null, CountResult(data), stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            result = new ExternalToolExecutionResultDto { Success = false, Message = ex.Message };
            await WriteAuditAsync(caller, toolName, requestId, arguments, "Rejected", ex.Message, 0, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            result = new ExternalToolExecutionResultDto { Success = false, Message = "外部工具执行失败，请联系平台管理员" };
            await WriteAuditAsync(caller, toolName, requestId, arguments, "Failed", "UnhandledError", 0, stopwatch.ElapsedMilliseconds);
        }

        return result;
    }

    private async Task<object?> ExecuteCoreAsync(
        string toolName,
        JsonElement arguments,
        ExternalToolCallerContext caller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (toolName)
        {
            case "production.line-runtime":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode))
                {
                    EnsureLineAllowed(caller, lineCode);
                    return await _dashboardService.GetLineRuntimeAsync(lineCode);
                }

                var bootstrap = await _dashboardService.GetBootstrapAsync("production-dashboard", false);
                var runtimes = new List<EdgeDashboardLineRuntimeDto>();
                foreach (var line in bootstrap.Lines)
                {
                    if (!caller.IsDirectAccess && !caller.AllResources && !caller.AllowedLineCodes.Contains(line.LineCode)) continue;
                    runtimes.Add(await _dashboardService.GetLineRuntimeAsync(line.LineCode));
                }
                return new { scope = "all", totalLines = runtimes.Count, lines = runtimes };
            }
            case "production.line-output":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode))
                {
                    EnsureLineAllowed(caller, lineCode);
                    return await _dashboardService.GetLineOutputAsync(lineCode);
                }

                var bootstrap = await _dashboardService.GetBootstrapAsync("production-dashboard", false);
                var outputs = new List<EdgeDashboardLineOutputDto>();
                foreach (var line in bootstrap.Lines)
                {
                    if (!caller.IsDirectAccess && !caller.AllResources && !caller.AllowedLineCodes.Contains(line.LineCode)) continue;
                    outputs.Add(await _dashboardService.GetLineOutputAsync(line.LineCode));
                }
                return new
                {
                    scope = "all",
                    totalLines = outputs.Count,
                    totalTodayQuantity = outputs.Sum(x => x.TodayQuantity),
                    lines = outputs
                };
            }
            case "production.fault-summary":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode)) EnsureLineAllowed(caller, lineCode);
                var query = new EdgeDashboardFaultQueryDto
                {
                    LineCode = lineCode,
                    DateFrom = GetOptionalDateTime(arguments, "dateFrom"),
                    DateTo = GetOptionalDateTime(arguments, "dateTo"),
                    RobotCode = GetOptionalString(arguments, "robotCode")
                };
                return await _dashboardService.GetFaultSummaryAsync(query, GetBoundedInt(arguments, "topN", 10, 1, 30));
            }
            case "device-card.runtime":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode)) EnsureLineAllowed(caller, lineCode);
                return await _dashboardService.GetDeviceCardBoardRuntimeAsync(lineCode);
            }
            case "device-card.history":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode)) EnsureLineAllowed(caller, lineCode);
                return await _dashboardService.GetDeviceCardChangeRecordsAsync(new EdgeDashboardDeviceCardChangeQueryDto
                {
                    LineCode = lineCode,
                    DeviceMark = GetOptionalString(arguments, "deviceMark"),
                    FieldCode = GetOptionalString(arguments, "fieldCode"),
                    DateFrom = GetOptionalDateTime(arguments, "dateFrom"),
                    DateTo = GetOptionalDateTime(arguments, "dateTo"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 100)
                });
            }
            case "device-card.alerts":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode)) EnsureLineAllowed(caller, lineCode);
                return await _dashboardService.GetDeviceCardAlertRecordsAsync(new EdgeDashboardDeviceCardAlertQueryDto
                {
                    LineCode = lineCode,
                    DeviceMark = GetOptionalString(arguments, "deviceMark"),
                    FieldCode = GetOptionalString(arguments, "fieldCode"),
                    AlertType = GetOptionalString(arguments, "alertType"),
                    PushStatus = GetOptionalString(arguments, "pushStatus"),
                    DateFrom = GetOptionalDateTime(arguments, "dateFrom"),
                    DateTo = GetOptionalDateTime(arguments, "dateTo"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 100)
                });
            }
            case "device-card.alerts.claim":
            {
                var lineCode = GetOptionalString(arguments, "lineCode");
                if (!string.IsNullOrWhiteSpace(lineCode)) EnsureLineAllowed(caller, lineCode);
                return await _dashboardService.ClaimDeviceCardAlertsAsync(
                    GetRequiredString(arguments, "consumerCode"),
                    GetBoundedInt(arguments, "maxCount", 20, 1, 100),
                    lineCode,
                    GetOptionalString(arguments, "deviceMark"),
                    GetOptionalString(arguments, "fieldCode"),
                    cancellationToken);
            }
            case "device-card.alerts.ack":
                return new
                {
                    affectedCount = await _dashboardService.AcknowledgeDeviceCardAlertsAsync(
                        GetRequiredString(arguments, "consumerCode"),
                        GetRequiredLongList(arguments, "alertIds"),
                        GetOptionalBoolean(arguments, "sent", true),
                        GetOptionalString(arguments, "message"),
                        cancellationToken)
                };
            case "project.get":
            {
                var projectCode = GetOptionalString(arguments, "projectCode");
                if (!string.IsNullOrWhiteSpace(projectCode))
                {
                    EnsureProjectAllowed(caller, projectCode);
                    var result = await _projectManageService.GetProjectPageAsync(new ProjectQueryDto { ProjectCode = projectCode, Current = 1, Size = 10 });
                    return result.Records.Where(x => string.Equals(x.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                var projects = await _projectManageService.GetProjectListAsync();
                if (!caller.IsDirectAccess && !caller.AllResources)
                    projects = projects.Where(x => caller.AllowedProjectCodes.Contains(x.ProjectCode)).ToList();
                return new
                {
                    scope = "all",
                    totalProjects = projects.Count,
                    averageProgress = projects.Count == 0 ? 0 : Math.Round(projects.Average(x => x.Progress), 2),
                    statusDistribution = projects
                        .GroupBy(x => x.ProjectStatus)
                        .OrderBy(x => x.Key)
                        .Select(x => new { status = x.Key, count = x.Count() }),
                    records = projects.Take(100)
                };
            }
            case "project.tasks":
            {
                var projectCode = GetOptionalString(arguments, "projectCode");
                string? projectId = null;
                if (!string.IsNullOrWhiteSpace(projectCode))
                {
                    EnsureProjectAllowed(caller, projectCode);
                    var projects = await _projectManageService.GetProjectPageAsync(new ProjectQueryDto { ProjectCode = projectCode, Current = 1, Size = 10 });
                    var project = projects.Records.FirstOrDefault(x => string.Equals(x.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase));
                    if (project == null) throw new InvalidOperationException("未找到项目");
                    projectId = project.Id.ToString();
                }
                return await _projectManageService.GetTaskPageAsync(new TaskQueryDto
                {
                    ProjectId = projectId,
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 100),
                    TaskStatus = GetOptionalByte(arguments, "taskStatus")
                });
            }
            case "edge.overview":
                return await _edgeGatewayService.GetOverviewAsync();
            case "edge.device-status":
            {
                var deviceCode = GetOptionalString(arguments, "deviceCode");
                if (!string.IsNullOrWhiteSpace(deviceCode)) EnsureDeviceAllowed(caller, deviceCode);
                var page = await _edgeGatewayService.GetDevicePageAsync(new EdgeDeviceQueryDto
                {
                    DeviceCode = deviceCode,
                    OnlineStatus = GetOptionalString(arguments, "status"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 100, 1, 100)
                });
                return page.Records
                    .Where(x => string.IsNullOrWhiteSpace(deviceCode) || string.Equals(x.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new { x.DeviceCode, x.DeviceName, x.SiteName, x.DeviceType, x.GroupName, x.Enabled, x.Online, x.LastHeartbeat, x.CpuUsage, x.MemoryUsage, x.QueueDepth, x.CollectedCount, x.UploadedCount, x.LastError })
                    .ToList();
            }
            case "edge.queue-status":
            {
                var deviceCode = GetOptionalString(arguments, "deviceCode");
                if (!string.IsNullOrWhiteSpace(deviceCode)) EnsureDeviceAllowed(caller, deviceCode);
                var page = await _edgeGatewayService.GetQueuePageAsync(new EdgeQueueQueryDto
                {
                    DeviceCode = deviceCode,
                    Status = GetOptionalString(arguments, "status"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 100)
                });
                return new
                {
                    page.Current,
                    page.Size,
                    page.Total,
                    records = page.Records.Select(x => new { x.Id, x.MessageId, x.DeviceCode, x.SourceId, x.SourceDeviceCode, x.DataType, x.Status, x.RetryCount, x.NextRetryTime, x.LastError, x.MessageTime, x.CreateTime, x.UploadTime })
                };
            }
            case "edge.events":
            {
                var deviceCode = GetOptionalString(arguments, "deviceCode");
                if (!string.IsNullOrWhiteSpace(deviceCode)) EnsureDeviceAllowed(caller, deviceCode);
                var page = await _edgeGatewayService.GetEventPageAsync(new EdgeEventQueryDto
                {
                    DeviceCode = deviceCode,
                    Level = GetOptionalString(arguments, "level"),
                    EventType = GetOptionalString(arguments, "eventType"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 100)
                });
                return new
                {
                    page.Current,
                    page.Size,
                    page.Total,
                    records = page.Records.Select(x => new { x.Id, x.DeviceCode, x.Level, x.EventType, x.Message, x.CreateTime })
                };
            }
            case "user.online-overview":
                return await _userActivityService.GetOverviewAsync(cancellationToken);
            case "user.login-records":
                return await _userActivityService.GetLoginPageAsync(new UserLoginLogQueryDto
                {
                    UserName = GetOptionalString(arguments, "userName"),
                    Status = GetOptionalString(arguments, "status"),
                    Success = GetOptionalNullableBoolean(arguments, "success"),
                    StartTime = GetOptionalDateTime(arguments, "startTime"),
                    EndTime = GetOptionalDateTime(arguments, "endTime"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 200)
                }, cancellationToken);
            case "user.operation-records":
                return await _userActivityService.GetOperationPageAsync(new UserOperationLogQueryDto
                {
                    UserName = GetOptionalString(arguments, "userName"),
                    Module = GetOptionalString(arguments, "module"),
                    Action = GetOptionalString(arguments, "action"),
                    Success = GetOptionalNullableBoolean(arguments, "success"),
                    StartTime = GetOptionalDateTime(arguments, "startTime"),
                    EndTime = GetOptionalDateTime(arguments, "endTime"),
                    Current = GetBoundedInt(arguments, "current", 1, 1, 1000),
                    Size = GetBoundedInt(arguments, "size", 20, 1, 200)
                }, cancellationToken);
            case "clawbot.recipients.list":
                return await _weChatClawBotService.GetMcpRecipientsAsync(cancellationToken);
            case "clawbot.channel-status":
                return await _weChatClawBotService.GetMcpChannelStatusAsync(GetOptionalString(arguments, "recipientCode"), cancellationToken);
            case "clawbot.delivery-status":
                return await _weChatClawBotService.GetMcpDeliveryStatusAsync(
                    GetOptionalString(arguments, "recipientCode"),
                    GetOptionalString(arguments, "idempotencyKey"),
                    GetOptionalString(arguments, "status"),
                    GetBoundedInt(arguments, "current", 1, 1, 1000),
                    GetBoundedInt(arguments, "size", 20, 1, 100),
                    cancellationToken);
            case "clawbot.send":
                return await _weChatClawBotService.SendByMcpAsync(new WeChatClawBotMcpSendDto
                {
                    RecipientCodes = GetRequiredStringList(arguments, "recipientCodes"),
                    Content = GetRequiredString(arguments, "content"),
                    Source = GetOptionalString(arguments, "source") ?? $"MCP:{caller.ClientId}",
                    IdempotencyKey = GetRequiredString(arguments, "idempotencyKey"),
                    ExternalTaskId = GetOptionalString(arguments, "externalTaskId")
                }, cancellationToken);
            case "clawbot.retry":
                return await _weChatClawBotService.RetryDeliveryAsync(GetRequiredLong(arguments, "deliveryId"), true, cancellationToken);
            default:
                throw new InvalidOperationException("未找到允许公开的工具");
        }
    }

    /// <summary>未提供编码时遍历授权子集；不把空参数解释为读取整个数据库。</summary>
    private async Task<object?> ExecuteScopedAsync(string toolName, JsonElement arguments,
        ExternalToolCallerContext caller, CancellationToken cancellationToken)
    {
        if (toolName == "device-card.alerts.ack" && !caller.IsDirectAccess && !caller.AllResources)
        {
            var ids = GetRequiredLongList(arguments, "alertIds");
            var alertRecords = await Db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>()
                .Where(x => ids.Contains(x.Id)).ToListAsync();
            if (alertRecords.Count != ids.Distinct().Count() || alertRecords.Any(x => !caller.AllowedLineCodes.Contains(x.LineCode)))
                throw new UnauthorizedAccessException("确认列表包含不存在或未授权的告警");
        }
        if (caller.IsDirectAccess || caller.AllResources)
            return await ExecuteCoreAsync(toolName, arguments, caller, cancellationToken);
        var parameter = toolName switch
        {
            "production.line-runtime" or "production.line-output" or "production.fault-summary" or
                "device-card.runtime" or "device-card.history" or "device-card.alerts" or "device-card.alerts.claim" => "lineCode",
            "project.get" or "project.tasks" => "projectCode",
            "edge.device-status" or "edge.queue-status" or "edge.events" or "edge.overview" => "deviceCode",
            _ => null
        };
        if (parameter == null || !string.IsNullOrWhiteSpace(GetOptionalString(arguments, parameter)))
            return await ExecuteCoreAsync(toolName, arguments, caller, cancellationToken);
        if (toolName == "device-card.alerts.claim")
            throw new ArgumentException("认领操作必须明确指定已授权的 lineCode");
        var permitted = (parameter switch
        {
            "lineCode" => caller.AllowedLineCodes,
            "projectCode" => caller.AllowedProjectCodes,
            _ => caller.AllowedDeviceCodes
        }).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var current = GetBoundedInt(arguments, "scopeCurrent", 1, 1, 10000);
        var size = GetBoundedInt(arguments, "scopeSize", 20, 1, 50);
        var records = new List<object>();
        foreach (var code in permitted.Skip((current - 1) * size).Take(size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopedArguments = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
            scopedArguments[parameter] = code;
            var result = await ExecuteCoreAsync(toolName == "edge.overview" ? "edge.device-status" : toolName,
                JsonSerializer.SerializeToElement(scopedArguments), caller, cancellationToken);
            records.Add(new { resourceCode = code, data = result });
        }
        return new { scope = "authorized", totalResources = permitted.Count, scopeCurrent = current, scopeSize = size, records };
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await EnsureClientScopeColumnLengthsAsync();
            Db.CodeFirst.InitTables<ExternalToolClientEntity, ExternalToolAccessLogEntity, ExternalToolServerSettingEntity>();
            await Db.Ado.ExecuteCommandAsync(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ExternalToolClient_ClientId' AND object_id = OBJECT_ID(N'ExternalToolClient')) CREATE UNIQUE INDEX UX_ExternalToolClient_ClientId ON ExternalToolClient(ClientId); IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExternalToolAccessLog_ClientTime' AND object_id = OBJECT_ID(N'ExternalToolAccessLog')) CREATE INDEX IX_ExternalToolAccessLog_ClientTime ON ExternalToolAccessLog(ClientId, CreateTime DESC);");
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task<ExternalToolServerSettingEntity> GetOrCreateServerSettingAsync()
    {
        var setting = await Db.Queryable<ExternalToolServerSettingEntity>().InSingleAsync(1);
        if (setting != null) return setting;

        setting = new ExternalToolServerSettingEntity
        {
            Id = 1,
            ServerName = "calmadmin-external-tools",
            Version = "1.0.0",
            UpdateTime = DateTime.Now
        };
        try
        {
            await Db.Insertable(setting).ExecuteCommandAsync();
            return setting;
        }
        catch
        {
            var existing = await Db.Queryable<ExternalToolServerSettingEntity>().InSingleAsync(1);
            if (existing != null) return existing;
            throw;
        }
    }

    /// <summary>
    /// 外部客户端可被授权全部项目、线体或设备，编码清单不能使用固定长度。
    /// 兼容已部署的初版表；只扩大列长度，不删除或转换任何既有数据。
    /// </summary>
    private async Task EnsureClientScopeColumnLengthsAsync()
    {
        await Db.Ado.ExecuteCommandAsync(
            """
            IF OBJECT_ID(N'ExternalToolClient', N'U') IS NOT NULL
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'ExternalToolClient') AND name = N'Scopes' AND max_length <> -1)
                    ALTER TABLE ExternalToolClient ALTER COLUMN Scopes nvarchar(max) NOT NULL;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'ExternalToolClient') AND name = N'AllowedLineCodes' AND max_length <> -1)
                    ALTER TABLE ExternalToolClient ALTER COLUMN AllowedLineCodes nvarchar(max) NOT NULL;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'ExternalToolClient') AND name = N'AllowedProjectCodes' AND max_length <> -1)
                    ALTER TABLE ExternalToolClient ALTER COLUMN AllowedProjectCodes nvarchar(max) NOT NULL;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'ExternalToolClient') AND name = N'AllowedDeviceCodes' AND max_length <> -1)
                    ALTER TABLE ExternalToolClient ALTER COLUMN AllowedDeviceCodes nvarchar(max) NOT NULL;
            END
            """);
    }

    private async Task WriteAuditAsync(ExternalToolCallerContext caller, string toolName, string requestId, JsonElement arguments, string status, string? message, int resultCount, long durationMs, bool required = false)
    {
        try
        {
            await Db.Insertable(new ExternalToolAccessLogEntity
            {
                ClientId = caller.ClientId,
                UserId = caller.UserId,
                AppCode = caller.AppCode,
                ToolName = toolName ?? string.Empty,
                RequestId = requestId,
                ArgumentHash = Hash(arguments.GetRawText()),
                Status = status,
                Message = message?.Length > 1000 ? message[..1000] : message,
                ResultCount = resultCount,
                DurationMs = durationMs,
                RemoteIp = caller.RemoteIp,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
        catch
        {
            if (required) throw new InvalidOperationException("无法记录写操作审计，本次写入未执行");
            // 只读查询和完成记录允许审计故障降级；写入开始前必须存在 Attempted 记录。
        }
    }

    private static void ApplyClientInput(ExternalToolClientEntity client, ExternalToolClientSaveDto dto)
    {
        client.ClientName = dto.ClientName.Trim();
        client.Scopes = NormalizeScopes(dto.Scopes);
        client.AllowedLineCodes = NormalizeCodes(dto.AllowedLineCodes);
        client.AllowedProjectCodes = NormalizeCodes(dto.AllowedProjectCodes);
        client.AllowedDeviceCodes = NormalizeCodes(dto.AllowedDeviceCodes);
        client.Enabled = dto.Enabled;
        client.ExpireTime = dto.ExpireTime;
        client.UpdateTime = DateTime.Now;
    }

    private static void ValidateClientInput(ExternalToolClientSaveDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.ClientName)) throw new InvalidOperationException("客户端名称不能为空");
        var invalidScopes = SplitCodes(dto.Scopes).Where(x => !Definitions.Values.Select(y => y.RequiredScope).Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        if (invalidScopes.Count > 0) throw new InvalidOperationException($"存在不支持的权限范围：{string.Join("、", invalidScopes)}");
    }

    private static ExternalToolClientOutputDto ToOutput(ExternalToolClientEntity client) => new()
    {
        Id = client.Id,
        ClientId = client.ClientId,
        ClientName = client.ClientName,
        Scopes = client.Scopes,
        AllowedLineCodes = client.AllowedLineCodes,
        AllowedProjectCodes = client.AllowedProjectCodes,
        AllowedDeviceCodes = client.AllowedDeviceCodes,
        Enabled = client.Enabled,
        ExpireTime = client.ExpireTime,
        CreateTime = client.CreateTime,
        UpdateTime = client.UpdateTime
    };

    private static ExternalToolServerSettingDto ToServerSettingDto(ExternalToolServerSettingEntity setting) => new()
    {
        ServerName = setting.ServerName,
        Version = setting.Version,
        UpdatedBy = setting.UpdatedBy,
        UpdateTime = setting.UpdateTime
    };

    private static void EnsureLineAllowed(ExternalToolCallerContext caller, string lineCode)
    {
        if (caller.IsDirectAccess || caller.AllResources) return;
        if (!caller.AllowedLineCodes.Contains(lineCode)) throw new UnauthorizedAccessException("客户端未获该线体数据授权");
    }

    private static void EnsureProjectAllowed(ExternalToolCallerContext caller, string projectCode)
    {
        if (caller.IsDirectAccess || caller.AllResources) return;
        if (!caller.AllowedProjectCodes.Contains(projectCode)) throw new UnauthorizedAccessException("客户端未获该项目数据授权");
    }

    private static void EnsureDeviceAllowed(ExternalToolCallerContext caller, string deviceCode)
    {
        if (caller.IsDirectAccess || caller.AllResources) return;
        if (!caller.AllowedDeviceCodes.Contains(deviceCode)) throw new UnauthorizedAccessException("客户端未获该边缘设备数据授权");
    }

    private static int CountResult(object? value) => value switch
    {
        null => 0,
        System.Collections.ICollection collection => collection.Count,
        _ => 1
    };

    private static ExternalToolDefinition Definition(
        string name,
        string title,
        string description,
        string scope,
        string schema,
        bool isWrite = false) =>
        new(name, title, description, scope, JsonDocument.Parse(schema).RootElement.Clone(), isWrite);

    private static string GetRequiredString(JsonElement arguments, string name) =>
        GetOptionalString(arguments, name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} 不能为空");

    private static string? GetOptionalString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    /// <summary>
    /// 写工具必须同时有明确业务入参与 confirm=true；当前公开目录仅包含读取工具，
    /// 此处作为后续新增写工具时的统一防误操作门禁。
    /// </summary>
    private static void EnsureWriteConfirmed(JsonElement arguments)
    {
        var confirmed = arguments.ValueKind == JsonValueKind.Object &&
                        arguments.TryGetProperty("confirm", out var value) &&
                        value.ValueKind is JsonValueKind.True;
        if (!confirmed) throw new InvalidOperationException("写操作必须明确传入 confirm: true");
    }

    private static DateTime? GetOptionalDateTime(JsonElement arguments, string name) =>
        DateTime.TryParse(GetOptionalString(arguments, name), out var value) ? value : null;

    private static byte? GetOptionalByte(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetByte(out var number) ? number : null;

    private static bool GetOptionalBoolean(JsonElement arguments, string name, bool defaultValue) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static bool? GetOptionalNullableBoolean(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static List<long> GetRequiredLongList(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} 必须是非空数字数组");
        var result = value.EnumerateArray()
            .Where(x => x.TryGetInt64(out var number) && number > 0)
            .Select(x => x.GetInt64())
            .Distinct()
            .Take(100)
            .ToList();
        if (result.Count == 0) throw new ArgumentException($"{name} 必须至少包含一个有效告警编号");
        return result;
    }

    private static long GetRequiredLong(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number > 0
            ? number
            : throw new ArgumentException($"{name} 必须是大于 0 的数字");

    private static List<string> GetRequiredStringList(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} 必须是非空字符串数组");
        var result = value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        if (result.Count == 0) throw new ArgumentException($"{name} 必须至少包含一个接收用户编码");
        return result;
    }

    private static int GetBoundedInt(JsonElement arguments, string name, int defaultValue, int min, int max) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max)
            : defaultValue;

    private static string GenerateClientSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string NormalizeScopes(string value) => string.Join(' ', SplitCodes(value));
    private static string NormalizeCodes(string value) => string.Join(',', SplitCodes(value));
    private static HashSet<string> SplitCodes(string? value) => (value ?? string.Empty)
        .Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record ExternalToolDefinition(
        string Name,
        string Title,
        string Description,
        string RequiredScope,
        JsonElement InputSchema,
        bool IsWrite = false)
    {
        public ExternalToolDefinitionDto ToDto() => new()
        {
            Name = Name,
            Title = Title,
            Description = Description,
            RequiredScope = RequiredScope,
            InputSchema = InputSchema
        };
    }

    private const string EmptySchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
    private const string LineSchema = "{\"type\":\"object\",\"properties\":{\"lineCode\":{\"type\":\"string\",\"description\":\"可选；不传时汇总全部线体\"}},\"additionalProperties\":false}";
    private const string FaultSummarySchema = "{\"type\":\"object\",\"properties\":{\"lineCode\":{\"type\":\"string\",\"description\":\"可选；不传时汇总全部线体\"},\"robotCode\":{\"type\":\"string\"},\"dateFrom\":{\"type\":\"string\",\"format\":\"date-time\"},\"dateTo\":{\"type\":\"string\",\"format\":\"date-time\"},\"topN\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":30}},\"additionalProperties\":false}";
    private const string DeviceHistorySchema = "{\"type\":\"object\",\"properties\":{\"lineCode\":{\"type\":\"string\",\"description\":\"可选；不传时查询全部线体\"},\"deviceMark\":{\"type\":\"string\"},\"fieldCode\":{\"type\":\"string\"},\"dateFrom\":{\"type\":\"string\",\"format\":\"date-time\"},\"dateTo\":{\"type\":\"string\",\"format\":\"date-time\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100}},\"additionalProperties\":false}";
    private const string DeviceAlertSchema = "{\"type\":\"object\",\"properties\":{\"lineCode\":{\"type\":\"string\",\"description\":\"可选；不传时查询全部线体\"},\"deviceMark\":{\"type\":\"string\"},\"fieldCode\":{\"type\":\"string\"},\"alertType\":{\"type\":\"string\",\"enum\":[\"LowerExceeded\",\"UpperExceeded\",\"Recovered\"]},\"pushStatus\":{\"type\":\"string\",\"enum\":[\"Pending\",\"Processing\",\"Sent\",\"NotRequired\"]},\"dateFrom\":{\"type\":\"string\",\"format\":\"date-time\"},\"dateTo\":{\"type\":\"string\",\"format\":\"date-time\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100}},\"additionalProperties\":false}";
    private const string DeviceAlertClaimSchema = "{\"type\":\"object\",\"required\":[\"consumerCode\",\"confirm\"],\"properties\":{\"consumerCode\":{\"type\":\"string\",\"description\":\"执行消息推送的智能体或系统标识\"},\"maxCount\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"lineCode\":{\"type\":\"string\"},\"deviceMark\":{\"type\":\"string\"},\"fieldCode\":{\"type\":\"string\"},\"confirm\":{\"type\":\"boolean\",\"const\":true}},\"additionalProperties\":false}";
    private const string DeviceAlertAckSchema = "{\"type\":\"object\",\"required\":[\"consumerCode\",\"alertIds\",\"sent\",\"confirm\"],\"properties\":{\"consumerCode\":{\"type\":\"string\"},\"alertIds\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"integer\"}},\"sent\":{\"type\":\"boolean\"},\"message\":{\"type\":\"string\",\"maxLength\":1000},\"confirm\":{\"type\":\"boolean\",\"const\":true}},\"additionalProperties\":false}";
    private const string ProjectSchema = "{\"type\":\"object\",\"properties\":{\"projectCode\":{\"type\":\"string\",\"description\":\"可选；不传时返回全部项目进度汇总\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100},\"taskStatus\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":4}},\"additionalProperties\":false}";
    private const string EdgeDeviceSchema = "{\"type\":\"object\",\"properties\":{\"deviceCode\":{\"type\":\"string\",\"description\":\"可选；不传时返回全部设备\"},\"status\":{\"type\":\"string\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100}},\"additionalProperties\":false}";
    private const string EdgeEventSchema = "{\"type\":\"object\",\"properties\":{\"deviceCode\":{\"type\":\"string\",\"description\":\"可选；不传时查询全部设备事件\"},\"level\":{\"type\":\"string\"},\"eventType\":{\"type\":\"string\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100}},\"additionalProperties\":false}";
    private const string UserLoginLogSchema = "{\"type\":\"object\",\"properties\":{\"userName\":{\"type\":\"string\"},\"status\":{\"type\":\"string\",\"enum\":[\"Online\",\"Offline\",\"Timeout\",\"Failed\"]},\"success\":{\"type\":\"boolean\"},\"startTime\":{\"type\":\"string\",\"format\":\"date-time\"},\"endTime\":{\"type\":\"string\",\"format\":\"date-time\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":200}},\"additionalProperties\":false}";
    private const string UserOperationLogSchema = "{\"type\":\"object\",\"properties\":{\"userName\":{\"type\":\"string\"},\"module\":{\"type\":\"string\"},\"action\":{\"type\":\"string\"},\"success\":{\"type\":\"boolean\"},\"startTime\":{\"type\":\"string\",\"format\":\"date-time\"},\"endTime\":{\"type\":\"string\",\"format\":\"date-time\"},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":200}},\"additionalProperties\":false}";
    private const string ClawBotStatusSchema = "{\"type\":\"object\",\"properties\":{\"recipientCode\":{\"type\":\"string\",\"description\":\"可选；不传时返回全部绑定通道摘要\"}},\"additionalProperties\":false}";
    private const string ClawBotDeliverySchema = "{\"type\":\"object\",\"properties\":{\"recipientCode\":{\"type\":\"string\"},\"idempotencyKey\":{\"type\":\"string\"},\"status\":{\"type\":\"string\",\"enum\":[\"Pending\",\"Sending\",\"Sent\",\"Failed\"]},\"current\":{\"type\":\"integer\"},\"size\":{\"type\":\"integer\",\"maximum\":100}},\"additionalProperties\":false}";
    private const string ClawBotSendSchema = "{\"type\":\"object\",\"required\":[\"recipientCodes\",\"content\",\"idempotencyKey\",\"confirm\"],\"properties\":{\"recipientCodes\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":100,\"items\":{\"type\":\"string\"}},\"content\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":10000},\"source\":{\"type\":\"string\",\"maxLength\":32},\"externalTaskId\":{\"type\":\"string\",\"maxLength\":128},\"idempotencyKey\":{\"type\":\"string\",\"maxLength\":128},\"confirm\":{\"type\":\"boolean\",\"const\":true}},\"additionalProperties\":false}";
    private const string ClawBotRetrySchema = "{\"type\":\"object\",\"required\":[\"deliveryId\",\"confirm\"],\"properties\":{\"deliveryId\":{\"type\":\"integer\",\"minimum\":1},\"confirm\":{\"type\":\"boolean\",\"const\":true}},\"additionalProperties\":false}";
}

/// <summary>外部 MCP 与 OpenAPI 令牌使用独立 audience，避免接受后台登录 JWT。</summary>
public static class ExternalToolAuthenticationDefaults
{
    public const string Scheme = "ExternalToolBearer";
    public const string Audience = "calmadmin-external-tools";
}
