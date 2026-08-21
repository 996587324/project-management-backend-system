using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;

namespace calmadmin.Controllers;

/// <summary>
/// 独立 HTTP 实时数据暂存接口；接收动态 data 参数并按租户和设备覆盖最新值。
/// </summary>
[ApiController]
[Route("api/EdgeGateway/realtime-data")]
public sealed class EdgeRealtimeDataController : ControllerBase
{
    private readonly IEdgeRealtimeDataService _service;
    private readonly string? _machineApiKey;

    /// <summary>通过 IOC 注入实时数据服务，并复用现有 EdgeGateway 机器认证密钥。</summary>
    public EdgeRealtimeDataController(
        IEdgeRealtimeDataService service,
        IConfiguration configuration)
    {
        _service = service;
        _machineApiKey = configuration["EdgeGateway:ApiKey"];
    }

    /// <summary>接收对方标准信封，deviceMark 对应订阅子设备，data 内字段完全动态。</summary>
    [HttpPost]
    public async Task<IActionResult> Receive(
        [FromBody] EdgeRealtimeDataReceiveDto dto,
        CancellationToken cancellationToken)
    {
        var unauthorized = ValidateMachineKey();
        if (unauthorized != null) return unauthorized;
        return await ExecuteAsync(
            () => _service.ReceiveAsync(dto, cancellationToken),
            "实时数据已接收");
    }

    /// <summary>分页查询所有设备的当前快照。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPage([FromQuery] EdgeRealtimeSnapshotQueryDto query) =>
        Success(await _service.GetPageAsync(query));

    /// <summary>根据租户和设备标识查询当前暂存的最新数据。</summary>
    [HttpGet("{deviceMark}")]
    public async Task<IActionResult> GetLatest(
        string deviceMark,
        [FromQuery] string tenantMark)
    {
        try
        {
            var snapshot = await _service.GetLatestAsync(tenantMark, deviceMark);
            return snapshot == null ? Error("未找到实时数据", 404) : Success(snapshot);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    /// <summary>校验请求携带的 X-Edge-Api-Key。</summary>
    private IActionResult? ValidateMachineKey()
    {
        if (string.IsNullOrWhiteSpace(_machineApiKey)) return null;
        return Request.Headers.TryGetValue("X-Edge-Api-Key", out var value) && value == _machineApiKey
            ? null
            : Error("边缘设备认证失败", 401);
    }

    /// <summary>执行实时数据业务并把可预期的参数错误转换为统一响应。</summary>
    private static async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action, string msg)
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
