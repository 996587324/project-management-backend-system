using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;

namespace calmadmin.Controllers;

/// <summary>机械手数据绑定、状态统计、故障记录和通用看板部件配置接口。</summary>
[Authorize]
[ApiController]
[Route("api/EdgeGateway/dashboard")]
public sealed class EdgeDashboardController : ControllerBase
{
    private readonly IEdgeDashboardService _service;
    private readonly IEdgeDashboardAiInsightService _insightService;
    private readonly IEdgeDashboardModelConfigurationTransferService _modelConfigurationTransferService;

    /// <summary>通过 IOC 注入机械手看板业务服务。</summary>
    public EdgeDashboardController(
        IEdgeDashboardService service,
        IEdgeDashboardAiInsightService insightService,
        IEdgeDashboardModelConfigurationTransferService modelConfigurationTransferService)
    {
        _service = service;
        _insightService = insightService;
        _modelConfigurationTransferService = modelConfigurationTransferService;
    }

    /// <summary>查询可绑定 HTTP 数据源的模型大类及其设备数量。</summary>
    [HttpGet("data-source-models")]
    public async Task<IActionResult> GetDataSourceModels() =>
        Success(await _service.GetDataSourceModelsAsync());

    /// <summary>按模型大类查询设备，并支持使用设备标识进行远程检索。</summary>
    [HttpGet("data-sources/by-model")]
    public async Task<IActionResult> GetDataSourcesByModel(
        [FromQuery] string modelMark,
        [FromQuery] string? keyword) =>
        await ExecuteAsync(() => _service.GetDataSourcesByModelAsync(modelMark, keyword));

    /// <summary>查询可绑定的 HTTP 最新数据源。</summary>
    [HttpGet("data-sources")]
    public async Task<IActionResult> GetDataSources([FromQuery] string? keyword) =>
        Success(await _service.GetDataSourcesAsync(keyword));

    /// <summary>查询一台 HTTP 数据源完整报文内的所有可选叶子字段。</summary>
    [HttpGet("source-fields")]
    public async Task<IActionResult> GetSourceFields(
        [FromQuery] string tenantMark,
        [FromQuery] string deviceMark) =>
        await ExecuteAsync(() => _service.GetSourceFieldsAsync(tenantMark, deviceMark));

    /// <summary>查询一个模型最近样本内的所有可选叶子字段。</summary>
    [HttpGet("model-source-fields")]
    public async Task<IActionResult> GetModelSourceFields([FromQuery] string modelMark) =>
        await ExecuteAsync(() => _service.GetModelSourceFieldsAsync(modelMark));

    /// <summary>查询按设备模型共享的报警映射规则。</summary>
    [HttpGet("model-alarm-rules")]
    public async Task<IActionResult> GetModelAlarmRules(
        [FromQuery] string dashboardCode,
        [FromQuery] string modelMark) =>
        await ExecuteAsync(() => _service.GetModelAlarmRulesAsync(dashboardCode, modelMark));

    /// <summary>整体保存按设备模型共享的报警映射规则。</summary>
    [HttpPost("model-alarm-rules")]
    public async Task<IActionResult> SaveModelAlarmRules(
        [FromBody] EdgeDashboardModelAlarmRuleListSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveModelAlarmRulesAsync(dto), "模型报警映射已保存");

    /// <summary>查询按模型统一维护的字段展示映射。</summary>
    [HttpGet("model-field-display-mappings")]
    public async Task<IActionResult> GetModelFieldDisplayMappings(
        [FromQuery] string dashboardCode,
        [FromQuery] string modelMark) =>
        await ExecuteAsync(() => _service.GetModelFieldDisplayMappingsAsync(dashboardCode, modelMark));

    /// <summary>整体保存按模型共享的字段展示映射。</summary>
    [HttpPost("model-field-display-mappings")]
    public async Task<IActionResult> SaveModelFieldDisplayMappings(
        [FromBody] EdgeDashboardModelFieldDisplayMappingListSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveModelFieldDisplayMappingsAsync(dto), "模型字段展示映射已保存");

    /// <summary>查询按模型共享的状态冲突优先级。</summary>
    [HttpGet("model-state-priorities")]
    public async Task<IActionResult> GetModelStatePriorities(
        [FromQuery] string dashboardCode,
        [FromQuery] string modelMark) =>
        await ExecuteAsync(() => _service.GetModelStatePrioritiesAsync(dashboardCode, modelMark));

    /// <summary>整体保存按模型共享的状态冲突优先级。</summary>
    [HttpPost("model-state-priorities")]
    public async Task<IActionResult> SaveModelStatePriorities(
        [FromBody] EdgeDashboardModelStatePriorityListSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveModelStatePrioritiesAsync(dto), "模型状态优先级已保存");

    /// <summary>下载模型映射、字段展示映射和状态优先级的 Excel 模板或当前配置。</summary>
    [HttpGet("model-configuration-template")]
    public async Task<IActionResult> DownloadModelConfigurationTemplate(
        [FromQuery] string dashboardCode = "production-dashboard",
        [FromQuery] bool includeData = false)
    {
        var file = await _modelConfigurationTransferService.ExportAsync(
            dashboardCode,
            includeData,
            HttpContext.RequestAborted);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>导入设备模型映射、模型字段展示映射或模型状态优先级。Excel 支持三个工作表，CSV 需指定类别。</summary>
    [HttpPost("model-configuration-import")]
    public async Task<IActionResult> ImportModelConfiguration(
        [FromForm] IFormFile file,
        [FromForm] string dashboardCode = "production-dashboard",
        [FromForm] string? csvConfigurationType = null)
    {
        if (file == null || file.Length == 0) return Error("请选择需要导入的文件", 400);
        if (file.Length > 10 * 1024 * 1024) return Error("导入文件不能超过 10MB", 400);
        await using var stream = file.OpenReadStream();
        return await ExecuteAsync(
            () => _modelConfigurationTransferService.ImportAsync(
                file.FileName,
                stream,
                dashboardCode,
                csvConfigurationType,
                HttpContext.RequestAborted),
            "模型配置导入完成");
    }

    /// <summary>查询一条线体的机械手绑定和状态规则。</summary>
    [HttpGet("bindings")]
    public async Task<IActionResult> GetBindings([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetBindingsAsync(lineCode));

    /// <summary>新增或修改机械手绑定、展示字段以及运行/空闲/故障规则。</summary>
    [HttpPost("bindings")]
    public async Task<IActionResult> SaveBinding([FromBody] EdgeDashboardRobotBindingSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveBindingAsync(dto), "机械手绑定已保存");

    /// <summary>查询一条线体内 Three.js 设备的数据源绑定和展示字段。</summary>
    [HttpGet("device-bindings")]
    public async Task<IActionResult> GetDeviceBindings([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetDeviceBindingsAsync(lineCode));

    /// <summary>新增或修改 Three.js 设备的数据源和展示字段绑定。</summary>
    [HttpPost("device-bindings")]
    public async Task<IActionResult> SaveDeviceBinding([FromBody] EdgeDashboardDeviceBindingSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveDeviceBindingAsync(dto), "设备绑定已保存");

    /// <summary>查询设备模型标识与业务显示名称的映射配置。</summary>
    [HttpGet("model-display-mappings")]
    public async Task<IActionResult> GetModelDisplayMappings([FromQuery] string dashboardCode = "production-dashboard") =>
        await ExecuteAsync(() => _service.GetModelDisplayMappingsAsync(dashboardCode));

    /// <summary>新增或修改设备模型业务名称映射。</summary>
    [HttpPost("model-display-mappings")]
    public async Task<IActionResult> SaveModelDisplayMapping(
        [FromBody] EdgeDashboardModelDisplayMappingSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveModelDisplayMappingAsync(dto), "设备模型映射已保存");

    /// <summary>软删除设备模型业务名称映射。</summary>
    [HttpDelete("model-display-mappings/{id:long}")]
    public async Task<IActionResult> DeleteModelDisplayMapping(long id) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteModelDisplayMappingAsync(id);
            return new { id };
        }, "设备模型映射已删除");

    /// <summary>查询设备卡片配置；未传 lineCode 时读取全部线体。</summary>
    [HttpGet("device-cards")]
    public async Task<IActionResult> GetDeviceCards([FromQuery] string? lineCode) =>
        await ExecuteAsync(() => _service.GetDeviceCardsAsync(lineCode));

    /// <summary>保存设备卡片、展示字段和选择性追溯字段。</summary>
    [HttpPost("device-cards")]
    public async Task<IActionResult> SaveDeviceCard([FromBody] EdgeDashboardDeviceCardSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveDeviceCardAsync(dto), "设备卡片已保存");

    [HttpDelete("device-cards/{id:long}")]
    public async Task<IActionResult> DeleteDeviceCard(long id) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteDeviceCardAsync(id);
            return new { id };
        }, "设备卡片已删除");

    /// <summary>保存指定线体设备卡片每行展示数量。</summary>
    [HttpPost("device-card-board-setting")]
    public async Task<IActionResult> SaveDeviceCardBoardSetting(
        [FromBody] EdgeDashboardDeviceCardBoardSettingDto dto) =>
        await ExecuteAsync(() => _service.SaveDeviceCardBoardSettingAsync(dto), "设备卡片布局已保存");

    /// <summary>一次返回设备卡片、实时状态与顶部汇总。</summary>
    [HttpGet("device-card-runtime")]
    public async Task<IActionResult> GetDeviceCardRuntime([FromQuery] string? lineCode) =>
        await ExecuteAsync(() => _service.GetDeviceCardBoardRuntimeAsync(lineCode));

    /// <summary>分页查询设备卡片字段变更追溯记录。</summary>
    [HttpGet("device-card-change-records")]
    public async Task<IActionResult> GetDeviceCardChangeRecords(
        [FromQuery] EdgeDashboardDeviceCardChangeQueryDto query) =>
        await ExecuteAsync(() => _service.GetDeviceCardChangeRecordsAsync(query));

    /// <summary>分页查询设备卡片数值字段上下限触发的告警及恢复记录。</summary>
    [HttpGet("device-card-alert-records")]
    public async Task<IActionResult> GetDeviceCardAlertRecords(
        [FromQuery] EdgeDashboardDeviceCardAlertQueryDto query) =>
        await ExecuteAsync(() => _service.GetDeviceCardAlertRecordsAsync(query));

    /// <summary>读取一条线体绑定的产量来源设备与报文字段。</summary>
    [HttpGet("line-output-binding")]
    public async Task<IActionResult> GetLineOutputBinding([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetLineOutputBindingAsync(lineCode));

    /// <summary>保存线体产量来源设备和数值字段。</summary>
    [HttpPost("line-output-binding")]
    public async Task<IActionResult> SaveLineOutputBinding([FromBody] EdgeDashboardLineOutputBindingSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveLineOutputBindingAsync(dto), "线体产量绑定已保存");

    /// <summary>读取当前线体根据绑定字段计算出的当日产出。</summary>
    [HttpGet("line-output")]
    public async Task<IActionResult> GetLineOutput([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetLineOutputAsync(lineCode));

    /// <summary>软删除 Three.js 设备绑定。</summary>
    [HttpDelete("device-bindings/{id:long}")]
    public async Task<IActionResult> DeleteDeviceBinding(long id) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteDeviceBindingAsync(id);
            return new { id };
        }, "设备绑定已删除");

    /// <summary>软删除机械手绑定并关闭仍处于活动状态的故障。</summary>
    [HttpDelete("bindings/{id:long}")]
    public async Task<IActionResult> DeleteBinding(long id) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteBindingAsync(id);
            return new { id };
        }, "机械手绑定已删除");

    /// <summary>批量读取线体机械手当前状态和配置的多个字段值。</summary>
    [HttpGet("runtime")]
    public async Task<IActionResult> GetRuntime([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetLineRuntimeAsync(lineCode));

    /// <summary>读取当前仍有效的 AI 建议；模型或实时数据异常时返回空。</summary>
    [HttpGet("ai-insight")]
    public async Task<IActionResult> GetAiInsight(
        [FromQuery] string dashboardCode,
        [FromQuery] string lineCode) =>
        await ExecuteAsync(() => _insightService.GetLatestAsync(dashboardCode, lineCode));

    /// <summary>分页查询机械手每日运行、空闲、故障等累计时长。</summary>
    [HttpGet("daily-statistics")]
    public async Task<IActionResult> GetDailyStatistics(
        [FromQuery] EdgeDashboardDailyStatisticQueryDto query) =>
        Success(await _service.GetDailyStatisticsAsync(query));

    /// <summary>分页查询故障从触发到恢复的生命周期记录。</summary>
    [HttpGet("fault-records")]
    public async Task<IActionResult> GetFaultRecords([FromQuery] EdgeDashboardFaultQueryDto query) =>
        Success(await _service.GetFaultRecordsAsync(query));

    /// <summary>按周月等时间范围汇总故障次数和持续时间。</summary>
    [HttpGet("fault-summary")]
    public async Task<IActionResult> GetFaultSummary(
        [FromQuery] EdgeDashboardFaultQueryDto query,
        [FromQuery] int topN = 10) =>
        Success(await _service.GetFaultSummaryAsync(query, topN));

    /// <summary>查询指定看板和线体的数据库驱动部件配置。</summary>
    [HttpGet("widgets")]
    public async Task<IActionResult> GetWidgets(
        [FromQuery] string dashboardCode,
        [FromQuery] string? lineCode) =>
        await ExecuteAsync(() => _service.GetWidgetsAsync(dashboardCode, lineCode));

    /// <summary>新增或修改通用表格、饼图或柱状图配置。</summary>
    [HttpPost("widgets")]
    public async Task<IActionResult> SaveWidget([FromBody] EdgeDashboardWidgetSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveWidgetAsync(dto), "看板部件已保存");

    /// <summary>软删除一个看板部件配置。</summary>
    [HttpDelete("widgets/{id:long}")]
    public async Task<IActionResult> DeleteWidget(long id) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteWidgetAsync(id);
            return new { id };
        }, "看板部件已删除");

    /// <summary>返回部件配置器允许选择的受控字段目录。</summary>
    [HttpGet("widget-field-catalog")]
    public IActionResult GetWidgetFieldCatalog() => Success(_service.GetWidgetFieldCatalog());

    /// <summary>一次读取看板首屏需要的线体列表和全部 Three.js 场景。</summary>
    [HttpGet("bootstrap")]
    public async Task<IActionResult> GetBootstrap([FromQuery] string dashboardCode) =>
        await ExecuteAsync(() => _service.GetBootstrapAsync(dashboardCode));

    /// <summary>新增或修改一条看板线体主配置。</summary>
    [HttpPost("lines")]
    public async Task<IActionResult> SaveLineConfig(
        [FromBody] EdgeDashboardLineConfigSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveLineConfigAsync(dto), "线体配置已保存");

    /// <summary>复制线体的场景、绑定、图表、产量和设备卡片配置到新线体。</summary>
    [HttpPost("lines/copy")]
    public async Task<IActionResult> CopyLineConfig(
        [FromBody] EdgeDashboardLineCopyDto dto) =>
        await ExecuteAsync(() => _service.CopyLineConfigAsync(dto), "线体配置已复制");

    /// <summary>软删除线体主配置和对应场景，保留历史统计与绑定。</summary>
    [HttpDelete("lines/{lineCode}")]
    public async Task<IActionResult> DeleteLineConfig(
        string lineCode,
        [FromQuery] string dashboardCode) =>
        await ExecuteAsync(async () =>
        {
            await _service.DeleteLineConfigAsync(dashboardCode, lineCode);
            return new { dashboardCode, lineCode };
        }, "线体配置已删除");

    /// <summary>读取指定看板和线体保存在数据库中的 Three.js 场景组态。</summary>
    [HttpGet("scene-layout")]
    public async Task<IActionResult> GetSceneLayout(
        [FromQuery] string dashboardCode,
        [FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetSceneLayoutAsync(dashboardCode, lineCode));

    /// <summary>校验并保存一条线体的完整 Three.js 场景组态。</summary>
    [HttpPost("scene-layout")]
    public async Task<IActionResult> SaveSceneLayout(
        [FromBody] EdgeDashboardSceneLayoutSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveSceneLayoutAsync(dto), "Three.js 场景配置已保存");

    /// <summary>修改线体编码，并同步迁移机械手绑定、统计、故障和图表配置。</summary>
    [HttpPost("line-code")]
    public async Task<IActionResult> ChangeLineCode(
        [FromBody] EdgeDashboardLineCodeChangeDto dto) =>
        await ExecuteAsync(() => _service.ChangeLineCodeAsync(dto), "线体配置已保存");

    /// <summary>执行配置业务并把可预期错误转换成统一响应。</summary>
    private static async Task<IActionResult> ExecuteAsync<T>(
        Func<Task<T>> action,
        string msg = "success")
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

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code) =>
        new JsonResult(new { code, msg, data = new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
}
