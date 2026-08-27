using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;

namespace calmadmin.Controllers;

/// <summary>看板日报手动下载、定时配置和后台生成记录接口。</summary>
[Authorize]
[ApiController]
[Route("api/EdgeGateway/dashboard/exports")]
public sealed class EdgeDashboardExportController : ControllerBase
{
    private readonly IEdgeDashboardExportService _service;

    /// <summary>通过 IOC 注入独立的看板导出业务服务。</summary>
    public EdgeDashboardExportController(IEdgeDashboardExportService service)
    {
        _service = service;
    }

    /// <summary>读取指定看板的每日定时导出配置。</summary>
    [HttpGet("setting")]
    public async Task<IActionResult> GetSetting([FromQuery] string dashboardCode) =>
        await ExecuteAsync(() => _service.GetSettingAsync(dashboardCode));

    /// <summary>保存定时导出启停状态、执行时间及导出日期口径。</summary>
    [HttpPost("setting")]
    public async Task<IActionResult> SaveSetting(
        [FromBody] EdgeDashboardExportSettingSaveDto dto) =>
        await ExecuteAsync(() => _service.SaveSettingAsync(dto), "定时导出配置已保存");

    /// <summary>流式下载当前选定线体的指定日期看板日报。</summary>
    [HttpGet("line")]
    public async Task<IActionResult> DownloadLine(
        [FromQuery] string dashboardCode,
        [FromQuery] string lineCode,
        [FromQuery] DateTime? reportDate)
    {
        try
        {
            var file = await _service.ExportLineAsync(
                dashboardCode,
                lineCode,
                (reportDate ?? DateTime.Today).Date,
                HttpContext.RequestAborted);
            PrepareStreamResponse();
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    /// <summary>分页查询后端按线体生成的日报文件记录。</summary>
    [HttpGet("records")]
    public async Task<IActionResult> GetRecords(
        [FromQuery] EdgeDashboardExportRecordQueryDto query) =>
        await ExecuteAsync(() => _service.GetRecordsAsync(query));

    /// <summary>流式下载一条已经成功生成的定时日报文件。</summary>
    [HttpGet("records/{id:long}/file")]
    public async Task<IActionResult> DownloadRecord(long id)
    {
        try
        {
            var file = await _service.OpenRecordAsync(id, HttpContext.RequestAborted);
            PrepareStreamResponse();
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    /// <summary>关闭 Nginx 响应缓存，让生成后的 Excel 立即传输给浏览器。</summary>
    private void PrepareStreamResponse()
    {
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers.CacheControl = "no-store";
    }

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
        new JsonResult(new
        {
            code = 200,
            msg,
            data,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });

    private static IActionResult Error(string msg, int code) =>
        new JsonResult(new
        {
            code,
            msg,
            data = new { },
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        })
        { StatusCode = code };
}
