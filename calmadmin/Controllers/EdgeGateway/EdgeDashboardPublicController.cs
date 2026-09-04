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
    private const string PublicDashboardCode = "production-dashboard";

    private readonly IEdgeDashboardService _service;

    /// <summary>通过 IOC 复用看板业务服务，确保公开看板与管理看板的数据口径一致。</summary>
    public EdgeDashboardPublicController(IEdgeDashboardService service)
    {
        _service = service;
    }

    /// <summary>一次读取公开看板首屏所需的线体列表和全部 Three.js 场景。</summary>
    [HttpGet("bootstrap")]
    public async Task<IActionResult> GetBootstrap([FromQuery] string dashboardCode) =>
        await ExecuteAsync(async () =>
        {
            ValidatePublicDashboard(dashboardCode);
            // 公开接口必须是严格只读的，不能因读取旧版数据而触发数据迁移。
            return await _service.GetBootstrapAsync(PublicDashboardCode, migrateLegacyScenes: false);
        });

    /// <summary>批量读取一条线体内机械手的当前状态和展示字段。</summary>
    [HttpGet("runtime")]
    public async Task<IActionResult> GetRuntime([FromQuery] string lineCode) =>
        await ExecuteAsync(async () =>
        {
            await ValidatePublicLineAsync(lineCode);
            return await _service.GetLineRuntimeAsync(lineCode.Trim());
        });

    /// <summary>分页读取公开看板使用的机械手每日状态累计数据。</summary>
    [HttpGet("daily-statistics")]
    public async Task<IActionResult> GetDailyStatistics(
        [FromQuery] EdgeDashboardDailyStatisticQueryDto query) =>
        await ExecuteAsync(async () =>
        {
            await ValidatePublicLineAsync(query.LineCode);
            query.LineCode = query.LineCode!.Trim();
            return await _service.GetDailyStatisticsAsync(query);
        });

    /// <summary>分页读取公开看板使用的故障生命周期记录。</summary>
    [HttpGet("fault-records")]
    public async Task<IActionResult> GetFaultRecords([FromQuery] EdgeDashboardFaultQueryDto query) =>
        await ExecuteAsync(async () =>
        {
            await ValidatePublicLineAsync(query.LineCode);
            query.LineCode = query.LineCode!.Trim();
            return await _service.GetFaultRecordsAsync(query);
        });

    /// <summary>按时间范围读取公开看板使用的故障次数和持续时间汇总。</summary>
    [HttpGet("fault-summary")]
    public async Task<IActionResult> GetFaultSummary(
        [FromQuery] EdgeDashboardFaultQueryDto query,
        [FromQuery] int topN = 10) =>
        await ExecuteAsync(async () =>
        {
            await ValidatePublicLineAsync(query.LineCode);
            query.LineCode = query.LineCode!.Trim();
            return await _service.GetFaultSummaryAsync(query, topN);
        });

    /// <summary>读取公开看板渲染表格、饼图和柱状图所需的部件配置。</summary>
    [HttpGet("widgets")]
    public async Task<IActionResult> GetWidgets(
        [FromQuery] string dashboardCode,
        [FromQuery] string? lineCode) =>
        await ExecuteAsync(async () =>
        {
            ValidatePublicDashboard(dashboardCode);
            if (!string.IsNullOrWhiteSpace(lineCode))
                await ValidatePublicLineAsync(lineCode);
            return await _service.GetWidgetsAsync(PublicDashboardCode, lineCode?.Trim());
        });

    /// <summary>读取公开看板指定线体的 Three.js 场景组态。</summary>
    [HttpGet("scene-layout")]
    public async Task<IActionResult> GetSceneLayout(
        [FromQuery] string dashboardCode,
        [FromQuery] string lineCode) =>
        await ExecuteAsync(async () =>
        {
            ValidatePublicDashboard(dashboardCode);
            await ValidatePublicLineAsync(lineCode);
            return await _service.GetSceneLayoutAsync(PublicDashboardCode, lineCode.Trim());
        });

    private static void ValidatePublicDashboard(string? dashboardCode)
    {
        if (!string.Equals(dashboardCode?.Trim(), PublicDashboardCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仅支持公开访问生产监控看板");
    }

    /// <summary>公开页只允许读取已在生产看板中启用的线体，避免匿名接口成为全量数据查询入口。</summary>
    private async Task ValidatePublicLineAsync(string? lineCode)
    {
        var normalizedLineCode = lineCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLineCode))
            throw new InvalidOperationException("lineCode 不能为空");

        var bootstrap = await _service.GetBootstrapAsync(PublicDashboardCode, migrateLegacyScenes: false);
        var isPublicLine = bootstrap.Lines.Any(line =>
            line.Enabled &&
            string.Equals(line.LineCode, normalizedLineCode, StringComparison.OrdinalIgnoreCase));
        if (!isPublicLine)
            throw new InvalidOperationException("该线体未在公开生产看板中启用");
    }

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
