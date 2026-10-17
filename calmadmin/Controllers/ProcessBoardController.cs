using calmadmin.HostedServices;
using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Service;
using SqlSugar;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/process-board")]
public sealed class ProcessBoardController(
    ISystemApplicationService applications,
    IProcessBoardService board,
    IProcessFormulaService formulas,
    IEdgeDashboardService dashboard,
    ISharedModelDictionaryService dictionary,
    IEdgeDeviceCardPushService push,
    ProcessBoardInitializationState initialization,
    SqlSugarScope scope) : ControllerBase
{
    private async Task<ApplicationAccessContext> RequireAsync(bool write = false)
    {
        if (!initialization.Ready) throw new InvalidOperationException("工艺看板正在后台准备，请稍后刷新；登录和其他模块不受影响");
        var access = await applications.GetAccessAsync(User, true);
        if (write ? !access.HasPermission("ProcessBoardConfig") :
            !access.HasPermission("ProcessBoard") && !access.HasPermission("ProcessBoardConfig"))
            throw new UnauthorizedAccessException("没有工艺看板权限");
        return access;
    }

    [HttpGet("definitions")]
    public async Task<IActionResult> Definitions() => Success(await board.GetDefinitionsAsync(await RequireAsync()));

    [HttpPost("definitions")]
    public async Task<IActionResult> Save([FromBody] ProcessBoardDefinition definition) =>
        Success(await board.SaveAsync(await RequireAsync(write: true), definition));

    [HttpGet("runtime")]
    public async Task<IActionResult> Runtime([FromQuery] long lineId) => Success(await board.GetRuntimeAsync(await RequireAsync(), lineId));

    [HttpPost("formula-preview")]
    public async Task<IActionResult> Preview([FromBody] ProcessFormulaRequest request, CancellationToken cancellationToken)
    {
        await RequireAsync(write: true);
        return Success(await formulas.EvaluateAsync(request, cancellationToken));
    }

    [HttpGet("setting")]
    public async Task<IActionResult> Setting()
    {
        await RequireAsync();
        return Success(await board.GetSettingAsync());
    }

    [HttpPost("setting")]
    public async Task<IActionResult> SaveSetting([FromBody] ProcessBoardSetting setting)
    {
        await board.SaveSettingAsync(await RequireAsync(write: true), setting);
        return Success(true);
    }

    [HttpGet("sources")]
    public async Task<IActionResult> Sources([FromQuery] string? keyword)
    {
        var access = await RequireAsync();
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var keys = access.SourceKeys.ToArray();
        var query = db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(!access.AllResources, x => keys.Contains(x.TenantMark + "\u001f" + x.DeviceCode));
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(x => x.DeviceCode.Contains(keyword) || x.ModelMark!.Contains(keyword));
        var rows = await query.OrderByDescending(x => x.ReceiveTime).Take(500)
            .Select(x => new { x.TenantMark, deviceMark = x.DeviceCode, x.ModelMark }).ToListAsync();
        var models = await dictionary.GetModelsAsync();
        return Success(rows.Select(x => new
        {
            x.TenantMark, x.deviceMark, x.ModelMark,
            displayName = models.FirstOrDefault(m => m.Enabled && m.ModelMark == x.ModelMark)?.DisplayName ?? x.deviceMark
        }));
    }

    [HttpGet("source-fields")]
    public async Task<IActionResult> SourceFields([FromQuery] string tenantMark, [FromQuery] string deviceMark)
    {
        ProcessBoardService.RequireSource(await RequireAsync(), new ProcessSourceBinding
        {
            TenantMark = tenantMark, DeviceMark = deviceMark
        }, requirePath: false);
        return Success(await dashboard.GetSourceFieldsAsync(tenantMark, deviceMark));
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> Recipients()
    {
        await RequireAsync(write: true);
        return Success(await push.GetRecipientsAsync());
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] long cardId, [FromQuery] string fieldCode,
        [FromQuery] DateTime start, [FromQuery] DateTime end, [FromQuery] int page = 1,
        [FromQuery] int size = 50, [FromQuery] bool events = false) =>
        Success(await board.GetHistoryAsync(await RequireAsync(), cardId, fieldCode, start, end, page, size, events));

    [HttpGet("curve")]
    public async Task<IActionResult> Curve([FromQuery] long cardId, [FromQuery] string fieldCode,
        [FromQuery] DateTime start, [FromQuery] DateTime end, [FromQuery] int points = 600, [FromQuery] bool raw = false) =>
        Success(await board.GetCurveAsync(await RequireAsync(), cardId, fieldCode, start, end, points, raw));

    [HttpGet("push-tasks")]
    public async Task<IActionResult> PushTasks([FromQuery] long lineId, [FromQuery] int page = 1)
    {
        var access = await RequireAsync(write: true);
        return Success(await board.GetPushTasksAsync(access, lineId, page));
    }

    [HttpPost("exports")]
    public async Task<IActionResult> CreateExport([FromBody] ProcessExportRequest request) =>
        Success(await board.CreateExportAsync(await RequireAsync(), request));

    [HttpGet("exports/{id:long}")]
    public async Task<IActionResult> ExportStatus(long id) => Success(await board.GetExportAsync(await RequireAsync(), id));

    [HttpGet("exports/{id:long}/file")]
    public async Task<IActionResult> ExportFile(long id)
    {
        var path = await board.GetExportFileAsync(await RequireAsync(), id);
        return PhysicalFile(path, "text/csv; charset=utf-8", $"工艺参数明细-{id}.csv");
    }

    private static JsonResult Success(object data) => new(new { code = 200, msg = "成功", data },
        new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false }
            },
            NullValueHandling = NullValueHandling.Include
        });
}
