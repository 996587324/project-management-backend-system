using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.WeChatClawBot;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/wechat-clawbot/ai")]
public sealed class WeChatClawBotAiController(
    IWeChatClawBotAiService service,
    ISystemApplicationService applications) : ControllerBase
{
    private async Task<string> UserIdAsync()
    {
        var access = await applications.GetAccessAsync(User, true);
        if (!access.HasPermission("WeChatClawBot") && !access.HasPermission("WeChatClawBotManagement"))
            throw new UnauthorizedAccessException("没有微信 ClawBot 功能权限");
        return access.UserId;
    }

    [HttpGet]
    public async Task<IActionResult> GetState() => Success(await service.GetStateAsync(await UserIdAsync()));

    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Success(await service.GetOptionsAsync(await UserIdAsync()));

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] WeChatClawBotAiSaveDto dto)
    {
        await service.SaveAsync(await UserIdAsync(), dto);
        return Success(true, "配置已保存，请在微信重新验证连接密钥");
    }

    [HttpPost("secret/reset")]
    public async Task<IActionResult> ResetSecret()
    {
        var secret = await service.ResetSecretAsync(await UserIdAsync());
        Response.Headers.CacheControl = "no-store";
        return Success(new { secret }, "密钥仅展示本次，请妥善保存");
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await service.DisconnectAsync(await UserIdAsync());
        return Success(true, "连接授权已撤销");
    }

    [HttpPost("context/reset")]
    public async Task<IActionResult> ClearContext()
    {
        await service.ClearContextAsync(await UserIdAsync());
        return Success(true, "已清空会话关联，请重新验证连接");
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs([FromQuery] int current = 1, [FromQuery] int size = 20) =>
        Success(await service.GetJobsAsync(await UserIdAsync(), current, size));

    private IActionResult Success(object? data, string message = "查询成功") =>
        Ok(new { code = 200, message, data });
}
