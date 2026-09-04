using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.Pda;
using System.Security.Claims;

namespace calmadmin.Controllers;

[ApiController]
[Route("api/pda")]
[Authorize]
public sealed class PdaController : ControllerBase
{
    private readonly IPdaService _pdaService;

    public PdaController(IPdaService pdaService)
    {
        _pdaService = pdaService;
    }

    [HttpPost("scan/query")]
    public async Task<IActionResult> Query([FromBody] PdaScanQueryRequest request)
    {
        try
        {
            var result = await _pdaService.QueryProductBarcodeAsync(
                GetCurrentUserId(),
                GetCurrentUserName(),
                GetCurrentDeviceCode(),
                request,
                HttpContext.RequestAborted);
            return Success(result, result.message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    [HttpGet("scan/recent")]
    public async Task<IActionResult> Recent([FromQuery] int current = 1, [FromQuery] int size = 20) =>
        Success(await _pdaService.GetRecentScanLogsAsync(GetCurrentUserId(), GetCurrentDeviceCode(), current, size));

    [HttpGet("scan/logs")]
    public async Task<IActionResult> Logs([FromQuery] PdaScanLogQueryDto query) =>
        Success(await _pdaService.GetScanLogsAsync(query));

    [HttpGet("scan/logs/{id:long}")]
    public async Task<IActionResult> LogDetail(long id)
    {
        if (id <= 0) return Error("扫码记录 ID 缺失或无效", 400);
        var data = await _pdaService.GetScanLogAsync(id);
        return data == null ? Error("扫码记录不存在", 404) : Success(data);
    }

    [HttpGet("devices")]
    public async Task<IActionResult> Devices([FromQuery] PdaDeviceQueryDto query) =>
        Success(await _pdaService.GetDevicesAsync(query));

    [HttpPost("devices/save")]
    public async Task<IActionResult> SaveDevice([FromBody] PdaDeviceSaveDto dto) =>
        await ExecuteAsync(() => _pdaService.SaveDeviceAsync(dto), "保存成功");

    [HttpPost("devices/delete/{id:long}")]
    public async Task<IActionResult> DeleteDevice(long id)
    {
        if (id <= 0) return Error("设备 ID 缺失或无效", 400);
        return await ExecuteAsync(async () => await _pdaService.DeleteDeviceAsync(id) ? id : throw new InvalidOperationException("设备不存在"), "删除成功");
    }

    [HttpGet("barcode-rules")]
    public async Task<IActionResult> BarcodeRules([FromQuery] PdaBarcodeRuleQueryDto query) =>
        Success(await _pdaService.GetBarcodeRulesAsync(query));

    [HttpPost("barcode-rules/save")]
    public async Task<IActionResult> SaveBarcodeRule([FromBody] PdaBarcodeRuleSaveDto dto) =>
        await ExecuteAsync(() => _pdaService.SaveBarcodeRuleAsync(dto), "保存成功");

    [HttpPost("barcode-rules/delete/{id:long}")]
    public async Task<IActionResult> DeleteBarcodeRule(long id)
    {
        if (id <= 0) return Error("规则 ID 缺失或无效", 400);
        return await ExecuteAsync(async () => await _pdaService.DeleteBarcodeRuleAsync(id) ? id : throw new InvalidOperationException("规则不存在"), "删除成功");
    }

    private static async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action, string message)
    {
        try
        {
            return Success(await action(), message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    private string GetCurrentUserId()
    {
        var userId = User.FindFirst("Id")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("Name")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("无法识别当前登录用户");
        return userId.Trim();
    }

    private string GetCurrentUserName() =>
        (User.FindFirst("Name")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty).Trim();

    private string GetCurrentDeviceCode()
    {
        var deviceCode = User.FindFirst("DeviceCode")?.Value;
        if (string.IsNullOrWhiteSpace(deviceCode)) throw new InvalidOperationException("当前登录令牌不是 PDA 令牌");
        return deviceCode.Trim();
    }

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code, object? data = null) =>
        new JsonResult(new { code, msg, data = data ?? new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
}
