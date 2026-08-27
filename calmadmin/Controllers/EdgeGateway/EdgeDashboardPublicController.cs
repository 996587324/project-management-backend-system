using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;

namespace calmadmin.Controllers;

/// <summary>无需登录的生产看板只读接口，仅公开页面展示所需的数据。</summary>
[AllowAnonymous]
[ApiController]
[Route("api/EdgeGateway/dashboard/public")]
public sealed class EdgeDashboardPublicController : ControllerBase
{
    private readonly IEdgeDashboardService _service;

    /// <summary>通过 IOC 复用看板业务服务，确保公开看板与管理看板的数据口径一致。</summary>
    public EdgeDashboardPublicController(IEdgeDashboardService service)
    {
        _service = service;
    }

    /// <summary>一次读取公开看板首屏所需的线体列表和全部 Three.js 场景。</summary>
    [HttpGet("bootstrap")]
    public async Task<IActionResult> GetBootstrap([FromQuery] string dashboardCode) =>
        await ExecuteAsync(() => _service.GetBootstrapAsync(dashboardCode));

    /// <summary>批量读取一条线体内机械手的当前状态和展示字段。</summary>
    [HttpGet("runtime")]
    public async Task<IActionResult> GetRuntime([FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetLineRuntimeAsync(lineCode));

    /// <summary>分页读取公开看板使用的机械手每日状态累计数据。</summary>
    [HttpGet("daily-statistics")]
    public async Task<IActionResult> GetDailyStatistics(
        [FromQuery] EdgeDashboardDailyStatisticQueryDto query) =>
        Success(await _service.GetDailyStatisticsAsync(query));

    /// <summary>分页读取公开看板使用的故障生命周期记录。</summary>
    [HttpGet("fault-records")]
    public async Task<IActionResult> GetFaultRecords([FromQuery] EdgeDashboardFaultQueryDto query) =>
        Success(await _service.GetFaultRecordsAsync(query));

    /// <summary>按时间范围读取公开看板使用的故障次数和持续时间汇总。</summary>
    [HttpGet("fault-summary")]
    public async Task<IActionResult> GetFaultSummary(
        [FromQuery] EdgeDashboardFaultQueryDto query,
        [FromQuery] int topN = 10) =>
        Success(await _service.GetFaultSummaryAsync(query, topN));

    /// <summary>读取公开看板渲染表格、饼图和柱状图所需的部件配置。</summary>
    [HttpGet("widgets")]
    public async Task<IActionResult> GetWidgets(
        [FromQuery] string dashboardCode,
        [FromQuery] string? lineCode) =>
        await ExecuteAsync(() => _service.GetWidgetsAsync(dashboardCode, lineCode));

    /// <summary>读取公开看板指定线体的 Three.js 场景组态。</summary>
    [HttpGet("scene-layout")]
    public async Task<IActionResult> GetSceneLayout(
        [FromQuery] string dashboardCode,
        [FromQuery] string lineCode) =>
        await ExecuteAsync(() => _service.GetSceneLayoutAsync(dashboardCode, lineCode));

    /// <summary>执行只读查询并把可预期错误转换成统一响应。</summary>
    private static async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Success(await action());
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
