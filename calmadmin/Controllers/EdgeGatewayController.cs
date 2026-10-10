using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;
using Model.User;
using SqlSugar;
using System.Security.Claims;

namespace calmadmin.Controllers;

/// <summary>
/// EdgeGateway 中心端 HTTP 接口，供运行中心前端和边缘 Worker 调用。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EdgeGatewayController : ControllerBase
{
    private readonly IEdgeGatewayService _service;
    private readonly IEdgeWebSocketSourceService _webSocketSourceService;
    private readonly IEdgeBPayloadTemplateService _bPayloadTemplateService;
    private readonly IEdgeBAutoForwardService _bAutoForwardService;
    private readonly IEdgeGatewayFeatureService _featureService;
    private readonly IEdgeDataTraceService _dataTraceService;
    private readonly SqlSugarScope _dbScope;
    private readonly string? _machineApiKey;

    public EdgeGatewayController(
        IEdgeGatewayService service,
        IEdgeWebSocketSourceService webSocketSourceService,
        IEdgeBPayloadTemplateService bPayloadTemplateService,
        IEdgeBAutoForwardService bAutoForwardService,
        IEdgeGatewayFeatureService featureService,
        IEdgeDataTraceService dataTraceService,
        SqlSugarScope dbScope,
        IConfiguration configuration)
    {
        _service = service;
        _webSocketSourceService = webSocketSourceService;
        _bPayloadTemplateService = bPayloadTemplateService;
        _bAutoForwardService = bAutoForwardService;
        _featureService = featureService;
        _dataTraceService = dataTraceService;
        _dbScope = dbScope;
        _machineApiKey = configuration["EdgeGateway:ApiKey"];
    }

    /// <summary>获取边缘网关运行概览。</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview() => Success(await _service.GetOverviewAsync());

    /// <summary>查询数据库中的边缘计算中心全局启停状态。</summary>
    [HttpGet("feature-setting")]
    public async Task<IActionResult> GetFeatureSetting() =>
        Success(await _featureService.GetSettingAsync());

    /// <summary>保存全局启停状态；关闭后 Worker 在配置同步周期内释放采集连接。</summary>
    [HttpPost("feature-setting")]
    public async Task<IActionResult> SaveFeatureSetting(
        [FromBody] EdgeGatewayFeatureSettingSaveDto dto) =>
        await ExecuteAsync(
            () => _featureService.SaveSettingAsync(dto),
            dto.Enabled ? "边缘计算中心已开启" : "边缘计算中心已关闭");

    /// <summary>下载边缘网关统一配置模板，可选择是否携带当前已配置数据。</summary>
    [HttpGet("configuration-template")]
    public async Task<IActionResult> DownloadConfigurationTemplate([FromQuery] bool includeData = false)
    {
        var file = await _service.ExportConfigurationTemplateStreamAsync(
            includeData,
            HttpContext.RequestAborted);
        // 告知 Nginx 不缓存该响应，使生成后的文件可以立即向浏览器持续传输。
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers.CacheControl = "no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>从统一 Excel 工作簿导入连接、采集、规则和 B 系统适配配置。</summary>
    [HttpPost("configuration-import")]
    public async Task<IActionResult> ImportConfiguration(
        [FromForm] IFormFile file,
        [FromForm] string? operatorName)
    {
        if (file == null || file.Length == 0) return Error("请选择需要导入的文件", 400);
        using var stream = file.OpenReadStream();
        return await ExecuteAsync(
            () => _service.ImportConfigurationAsync(file.FileName, stream, operatorName),
            "配置导入完成");
    }

    /// <summary>分页查询边缘设备及最新心跳状态。</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices([FromQuery] EdgeDeviceQueryDto query) =>
        Success(await _service.GetDevicePageAsync(query));

    /// <summary>新增或修改边缘设备。</summary>
    [HttpPost("devices")]
    public async Task<IActionResult> SaveDevice([FromBody] EdgeDeviceSaveDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.SaveDeviceAsync(dto) }, "设备已保存");

    /// <summary>启用或停用边缘设备。</summary>
    [HttpPost("devices/{id:int}/enabled")]
    public async Task<IActionResult> SetDeviceEnabled(int id, [FromBody] EdgeEnabledDto dto) =>
        Success(await _service.SetDeviceEnabledAsync(id, dto.Enabled), "设备状态已更新");

    /// <summary>软删除设备，并阻止已删除编码通过心跳自动恢复。</summary>
    [HttpPost("devices/{id:int}/delete")]
    public async Task<IActionResult> DeleteDevice(int id) =>
        Success(await _service.DeleteDeviceAsync(id), "设备已删除");

    /// <summary>查询指定设备的 WebSocket 连接配置。</summary>
    [HttpGet("devices/{deviceId:int}/websocket-sources")]
    public async Task<IActionResult> GetWebSocketSources(int deviceId) =>
        await ExecuteAsync(() => _webSocketSourceService.GetByDeviceAsync(deviceId));

    /// <summary>新增或修改指定设备的 WebSocket 连接配置。</summary>
    [HttpPost("devices/{deviceId:int}/websocket-sources")]
    public async Task<IActionResult> SaveWebSocketSource(
        int deviceId,
        [FromBody] EdgeWebSocketSourceSaveDto dto) =>
        await ExecuteAsync(
            async () => new { id = await _webSocketSourceService.SaveAsync(deviceId, dto) },
            "WebSocket 连接已保存");

    /// <summary>查询单个 WebSocket 连接订阅的设备和参数。</summary>
    [HttpGet("websocket-sources/{id:int}/subscriptions")]
    public async Task<IActionResult> GetWebSocketSubscriptions(int id) =>
        await ExecuteAsync(() => _webSocketSourceService.GetSubscriptionsAsync(id));

    /// <summary>保存单个 WebSocket 连接的完整设备和参数订阅清单。</summary>
    [HttpPost("websocket-sources/{id:int}/subscriptions")]
    public async Task<IActionResult> SaveWebSocketSubscriptions(
        int id,
        [FromBody] EdgeWebSocketSubscriptionSaveDto dto) =>
        await ExecuteAsync(
            async () => new { version = await _webSocketSourceService.SaveSubscriptionsAsync(id, dto) },
            "WebSocket 订阅已保存并下发");

    /// <summary>启用或停用单个 WebSocket 连接。</summary>
    [HttpPost("websocket-sources/{id:int}/enabled")]
    public async Task<IActionResult> SetWebSocketSourceEnabled(int id, [FromBody] EdgeEnabledDto dto) =>
        Success(await _webSocketSourceService.SetEnabledAsync(id, dto.Enabled), "WebSocket 连接状态已更新");

    /// <summary>软删除单个 WebSocket 连接。</summary>
    [HttpPost("websocket-sources/{id:int}/delete")]
    public async Task<IActionResult> DeleteWebSocketSource(int id) =>
        Success(await _webSocketSourceService.DeleteAsync(id), "WebSocket 连接已删除");

    /// <summary>停用所有设备下当前已启用的 WebSocket 连接，保留配置供后续重新启用。</summary>
    [HttpPost("websocket-sources/disable-all")]
    public async Task<IActionResult> DisableAllWebSocketSources() =>
        await ExecuteAsync(
            () => _webSocketSourceService.DisableAllAsync(),
            "全部 WebSocket 连接已停用并下发");

    /// <summary>通知指定设备重建全部已启用 WebSocket 连接。</summary>
    [HttpPost("devices/{deviceId:int}/websocket-sources/reconnect")]
    public async Task<IActionResult> ReconnectDeviceWebSocketSources(int deviceId) =>
        await ExecuteAsync(
            () => _webSocketSourceService.ReconnectDeviceAsync(deviceId),
            "设备 WebSocket 重新连接指令已下发");

    /// <summary>通知所有设备重建已启用 WebSocket 连接。</summary>
    [HttpPost("websocket-sources/reconnect-all")]
    public async Task<IActionResult> ReconnectAllWebSocketSources() =>
        await ExecuteAsync(
            () => _webSocketSourceService.ReconnectAllAsync(),
            "全部 WebSocket 重新连接指令已下发");

    /// <summary>接收边缘节点心跳并返回中心端当前版本。</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] EdgeHeartbeatDto dto)
    {
        var unauthorized = ValidateMachineKey();
        return unauthorized ?? await ExecuteAsync(() => _service.ReceiveHeartbeatAsync(dto));
    }

    /// <summary>查询指定设备的采集配置历史。</summary>
    [HttpGet("devices/{deviceId:int}/configs")]
    public async Task<IActionResult> GetDeviceConfigs(int deviceId) =>
        Success(await _service.GetDeviceConfigsAsync(deviceId));

    /// <summary>保存指定设备的采集配置草稿。</summary>
    [HttpPost("devices/{deviceId:int}/configs")]
    public async Task<IActionResult> SaveDeviceConfig(int deviceId, [FromBody] EdgeDeviceConfigSaveDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.SaveDeviceConfigAsync(deviceId, dto) }, "配置草稿已保存");

    /// <summary>发布指定采集配置。</summary>
    [HttpPost("configs/{configId:int}/publish")]
    public async Task<IActionResult> PublishDeviceConfig(int configId, [FromQuery] string? publishedBy) =>
        Success(await _service.PublishDeviceConfigAsync(configId, publishedBy), "配置已发布");

    /// <summary>软删除非当前生效的采集配置，历史发布快照继续保留。</summary>
    [HttpPost("configs/{configId:int}/delete")]
    public async Task<IActionResult> DeleteDeviceConfig(int configId) =>
        await ExecuteAsync(() => _service.DeleteDeviceConfigAsync(configId), "配置已删除");

    /// <summary>供边缘 Worker 按设备编码获取完整生效配置。</summary>
    [HttpGet("device-config/{deviceCode}/effective")]
    public async Task<IActionResult> GetEffectiveConfig(string deviceCode)
    {
        var unauthorized = ValidateMachineKey();
        if (unauthorized != null) return unauthorized;
        var config = await _service.GetEffectiveConfigAsync(deviceCode);
        return config == null ? Error("设备不存在", 404) : Success(config);
    }

    /// <summary>分页查询边缘规则。</summary>
    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] EdgeRuleQueryDto query) =>
        Success(await _service.GetRulePageAsync(query));

    /// <summary>新增或修改边缘规则。</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> SaveRule([FromBody] EdgeRuleSaveDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.SaveRuleAsync(dto) }, "规则已保存");

    /// <summary>发布新的边缘规则版本。</summary>
    [HttpPost("rules/publish")]
    public async Task<IActionResult> PublishRules([FromBody] EdgeRulePublishDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.PublishRulesAsync(dto) }, "规则版本已发布");

    /// <summary>根据历史快照生成新的规则回滚版本。</summary>
    [HttpPost("rules/rollback")]
    public async Task<IActionResult> RollbackRules([FromBody] EdgeRuleRollbackDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.RollbackRulesAsync(dto) }, "规则版本已回滚");

    /// <summary>查询设备或全局规则版本历史。</summary>
    [HttpGet("rule-versions")]
    public async Task<IActionResult> GetRuleVersions([FromQuery] int? deviceId) =>
        Success(await _service.GetRuleVersionsAsync(deviceId));

    /// <summary>分页查询中心消息队列。</summary>
    [HttpGet("queue")]
    public async Task<IActionResult> GetQueue([FromQuery] EdgeQueueQueryDto query) =>
        Success(await _service.GetQueuePageAsync(query));

    /// <summary>分页查询中心端到 B 系统的上传结果和返回内容。</summary>
    [HttpGet("upload-records")]
    public async Task<IActionResult> GetUploadRecords([FromQuery] EdgeUploadRecordQueryDto query) =>
        Success(await _service.GetUploadRecordPageAsync(query));

    /// <summary>分页查询边缘节点向上游 WebSocket 发送的报文及结果。</summary>
    [HttpGet("websocket-send-records")]
    public async Task<IActionResult> GetWebSocketSendRecords([FromQuery] EdgeWebSocketSendRecordQueryDto query) =>
        Success(await _service.GetWebSocketSendRecordPageAsync(query));

    /// <summary>分页查询最近七天 WebSocket 接收数据日表。</summary>
    [HttpGet("websocket-receive-records")]
    public async Task<IActionResult> GetWebSocketReceiveRecords([FromQuery] EdgeWebSocketReceiveRecordQueryDto query) =>
        Success(await _service.GetWebSocketReceiveRecordPageAsync(query));

    /// <summary>获取数据追溯全局设置。</summary>
    [HttpGet("data-trace/setting")]
    public async Task<IActionResult> GetDataTraceSetting() => Success(await _dataTraceService.GetSettingAsync());

    /// <summary>保存数据追溯保留时长和启停状态。</summary>
    [HttpPost("data-trace/setting")]
    public async Task<IActionResult> SaveDataTraceSetting([FromBody] EdgeDataTraceSettingSaveDto dto)
    {
        if (!await IsAdministratorAsync()) return Error("仅管理员可以修改数据追溯设置", 403);
        return await ExecuteAsync(() => _dataTraceService.SaveSettingAsync(dto, GetOperatorName()), "数据追溯设置已保存");
    }

    /// <summary>查询数据追溯规则。</summary>
    [HttpGet("data-trace/rules")]
    public async Task<IActionResult> GetDataTraceRules() => Success(await _dataTraceService.GetRulesAsync());

    /// <summary>新增或修改数据追溯规则。</summary>
    [HttpPost("data-trace/rules")]
    public async Task<IActionResult> SaveDataTraceRule([FromBody] EdgeDataTraceRuleSaveDto dto)
    {
        if (!await IsAdministratorAsync()) return Error("仅管理员可以维护数据追溯规则", 403);
        return await ExecuteAsync(() => _dataTraceService.SaveRuleAsync(dto, GetOperatorName()), "数据追溯规则已保存");
    }

    /// <summary>物理删除追溯规则及其当前最新值快照，历史点按保留周期自然清理。</summary>
    [HttpPost("data-trace/rules/{id:int}/delete")]
    public async Task<IActionResult> DeleteDataTraceRule(int id)
    {
        if (!await IsAdministratorAsync()) return Error("仅管理员可以维护数据追溯规则", 403);
        return await ExecuteAsync(() => _dataTraceService.DeleteRuleAsync(id, GetOperatorName()), "数据追溯规则已删除");
    }

    /// <summary>查询可用于曲线选择的设备、数据源、来源设备和参数树。</summary>
    [HttpGet("data-trace/tree")]
    public async Task<IActionResult> GetDataTraceTree() => Success(await _dataTraceService.GetTreeAsync());

    /// <summary>查询曲线趋势数据；服务端自动压缩过密采样点。</summary>
    [HttpPost("data-trace/trend")]
    public async Task<IActionResult> GetDataTraceTrend([FromBody] EdgeDataTraceTrendQueryDto dto) =>
        await ExecuteAsync(() => _dataTraceService.GetTrendAsync(dto));

    /// <summary>按游标分页查询参数原始历史点，避免一次加载大量记录。</summary>
    [HttpPost("data-trace/detail")]
    public async Task<IActionResult> GetDataTraceDetail([FromBody] EdgeDataTraceDetailQueryDto dto) =>
        await ExecuteAsync(() => _dataTraceService.GetDetailAsync(dto));

    /// <summary>查询参数统计摘要。</summary>
    [HttpPost("data-trace/summaries")]
    public async Task<IActionResult> GetDataTraceSummaries([FromBody] EdgeDataTraceDetailQueryDto dto) =>
        await ExecuteAsync(() => _dataTraceService.GetSummariesAsync(dto));

    /// <summary>创建后台数据追溯导出任务。</summary>
    [HttpPost("data-trace/exports")]
    public async Task<IActionResult> CreateDataTraceExport([FromBody] EdgeDataTraceExportRequestDto dto) =>
        await ExecuteAsync(() => _dataTraceService.CreateExportTaskAsync(dto, GetOperatorName()), "导出任务已创建");

    /// <summary>查询导出任务进度。</summary>
    [HttpGet("data-trace/exports/{id:long}")]
    public async Task<IActionResult> GetDataTraceExport(long id)
    {
        var task = await _dataTraceService.GetExportTaskAsync(id);
        return task == null ? Error("导出任务不存在", 404) : Success(task);
    }

    /// <summary>下载已完成且未过期的导出文件。</summary>
    [HttpGet("data-trace/exports/{id:long}/file")]
    public async Task<IActionResult> DownloadDataTraceExport(long id)
    {
        try
        {
            var file = await _dataTraceService.OpenExportFileAsync(
                id,
                GetOperatorName(),
                await IsAdministratorAsync());
            if (file == null) return Error("导出文件未完成、已过期或不存在", 404);
            Response.Headers["X-Accel-Buffering"] = "no";
            var contentType = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "text/csv; charset=utf-8";
            return File(file.Content, contentType, file.FileName);
        }
        catch (InvalidOperationException ex) { return Error(ex.Message, 403); }
    }

    /// <summary>接收边缘节点上报的 WebSocket 报文发送结果。</summary>
    [HttpPost("websocket-send-records")]
    public async Task<IActionResult> ReportWebSocketSendRecord([FromBody] EdgeWebSocketSendRecordReportDto dto)
    {
        var unauthorized = ValidateMachineKey();
        return unauthorized ?? Success(
            new { id = await _service.ReportWebSocketSendRecordAsync(dto) },
            "WebSocket 发送记录已保存");
    }

    /// <summary>接收边缘节点批量上传的标准消息。</summary>
    [HttpPost("messages")]
    public async Task<IActionResult> ReceiveMessages([FromBody] EdgeMessageBatchDto dto)
    {
        var unauthorized = ValidateMachineKey();
        return unauthorized ?? await ExecuteAsync(() => _service.ReceiveMessagesAsync(dto), "数据已接收");
    }

    /// <summary>把失败队列记录重置为待处理。</summary>
    [HttpPost("queue/retry")]
    public async Task<IActionResult> RetryQueue([FromBody] EdgeRetryRequestDto dto) =>
        Success(await _service.ResetQueueAsync(dto), "队列已重置为待处理");

    /// <summary>立即把指定队列消息转发到 B 系统。</summary>
    [HttpPost("queue/forward")]
    public async Task<IActionResult> ForwardQueue([FromBody] EdgeRetryRequestDto dto) =>
        await ExecuteAsync(() => _service.ForwardQueueAsync(dto), "转发任务已完成");

    /// <summary>删除一条或多条中心消息队列数据及其上传记录。</summary>
    [HttpPost("queue/delete")]
    public async Task<IActionResult> DeleteQueue([FromBody] EdgeBatchIdsDto dto) =>
        await ExecuteAsync(() => _service.DeleteQueueAsync(dto), "队列数据已删除");

    /// <summary>分页查询边缘网关运行事件。</summary>
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents([FromQuery] EdgeEventQueryDto query) =>
        Success(await _service.GetEventPageAsync(query));

    /// <summary>接收边缘节点上报的运行事件。</summary>
    [HttpPost("events")]
    public async Task<IActionResult> ReportEvent([FromBody] EdgeEventReportDto dto)
    {
        var unauthorized = ValidateMachineKey();
        return unauthorized ?? Success(new { id = await _service.ReportEventAsync(dto) }, "事件已记录");
    }

    /// <summary>查询 B 系统映射配置。</summary>
    [HttpGet("b-mappings")]
    public async Task<IActionResult> GetBMappings() => Success(await _service.GetBMappingRulesAsync());

    /// <summary>按关键字、设备、协议和启停状态分页查询 B 系统映射。</summary>
    [HttpGet("b-mappings/page")]
    public async Task<IActionResult> GetBMappingPage([FromQuery] EdgeBMappingQueryDto query) =>
        Success(await _service.GetBMappingRulePageAsync(query));

    /// <summary>新增或修改 B 系统映射配置。</summary>
    [HttpPost("b-mappings")]
    public async Task<IActionResult> SaveBMapping([FromBody] EdgeBMappingSaveDto dto) =>
        await ExecuteAsync(async () => new { id = await _service.SaveBMappingRuleAsync(dto) }, "B 系统映射已保存");

    /// <summary>软删除一条或多条 B 系统映射。</summary>
    [HttpPost("b-mappings/delete")]
    public async Task<IActionResult> DeleteBMappings([FromBody] EdgeBatchIdsDto dto) =>
        await ExecuteAsync(() => _service.DeleteBMappingRulesAsync(dto), "B 系统映射已删除");

    /// <summary>查询可复用的 B 系统 payload 模板。</summary>
    [HttpGet("b-payload-templates")]
    public async Task<IActionResult> GetBPayloadTemplates() =>
        Success(await _bPayloadTemplateService.GetListAsync());

    /// <summary>新增或修改 B 系统 payload 模板。</summary>
    [HttpPost("b-payload-templates")]
    public async Task<IActionResult> SaveBPayloadTemplate([FromBody] EdgeBPayloadTemplateSaveDto dto) =>
        await ExecuteAsync(
            async () => new { id = await _bPayloadTemplateService.SaveAsync(dto) },
            "B 系统模板已保存");

    /// <summary>软删除 B 系统 payload 模板，已保存的映射快照不受影响。</summary>
    [HttpPost("b-payload-templates/{id:int}/delete")]
    public async Task<IActionResult> DeleteBPayloadTemplate(int id) =>
        await ExecuteAsync(() => _bPayloadTemplateService.DeleteAsync(id), "B 系统模板已删除");

    /// <summary>查询数据库中的 B 系统自动转发设置和最近执行状态。</summary>
    [HttpGet("b-auto-forward-setting")]
    public async Task<IActionResult> GetBAutoForwardSetting() =>
        Success(await _bAutoForwardService.GetSettingAsync());

    /// <summary>保存 B 系统自动转发启停、扫描间隔和单批数量。</summary>
    [HttpPost("b-auto-forward-setting")]
    public async Task<IActionResult> SaveBAutoForwardSetting(
        [FromBody] EdgeBAutoForwardSettingSaveDto dto) =>
        await ExecuteAsync(async () =>
        {
            await _bAutoForwardService.SaveSettingAsync(dto);
            return await _bAutoForwardService.GetSettingAsync();
        }, "B 系统自动转发设置已保存");

    /// <summary>创建 EdgeGateway 统一成功响应。</summary>
    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    /// <summary>创建 EdgeGateway 统一错误响应。</summary>
    private static IActionResult Error(string msg, int code) =>
        new JsonResult(new { code, msg, data = new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    /// <summary>校验边缘 Worker 请求携带的 X-Edge-Api-Key。</summary>
    private IActionResult? ValidateMachineKey()
    {
        if (string.IsNullOrWhiteSpace(_machineApiKey)) return null;
        return Request.Headers.TryGetValue("X-Edge-Api-Key", out var value) && value == _machineApiKey
            ? null
            : Error("边缘设备认证失败", 401);
    }

    private string? GetOperatorName() => User.Identity?.Name ?? User.FindFirst("UserName")?.Value;

    /// <summary>
    /// 数据追溯属于后台配置，管理员身份以数据库中的当前账号配置为准，
    /// 不依赖登录时签发的 JWT 角色声明，避免角色刚调整后仍被旧 Token 拦截。
    /// </summary>
    private async Task<bool> IsAdministratorAsync()
    {
        var identifiers = new[]
            {
                User.FindFirst("Id")?.Value,
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                User.Identity?.Name,
                User.FindFirst("Name")?.Value,
                User.FindFirst(ClaimTypes.Name)?.Value
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (identifiers.Count == 0) return false;

        var db = _dbScope.GetConnectionScope("calmadmin");
        foreach (var identifier in identifiers)
        {
            var user = await db.Queryable<SysUserInfo>()
                .FirstAsync(x => x.Enabled && (x.UserId == identifier || x.UserName == identifier));
            if (user == null) continue;

            return user.RoleList.Any(role =>
                       role.Trim().Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
                       role.Trim().Equals("R_SUPER", StringComparison.OrdinalIgnoreCase)) ||
                   user.PermissionKeyList.Any(permission => permission.Trim() == "*");
        }

        return false;
    }

    /// <summary>执行中心业务并把可预期的参数错误转换为 400 业务响应。</summary>
    private static async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action, string msg = "success")
    {
        try
        {
            return Success(await action(), msg);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }
}
