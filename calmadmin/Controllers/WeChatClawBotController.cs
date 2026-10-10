using System.Security.Claims;
using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.User;
using Model.WeChatClawBot;
using SqlSugar;

namespace calmadmin.Controllers;

/// <summary>微信 ClawBot 的用户绑定、管理员调试和投递记录接口。</summary>
[ApiController]
[Authorize]
[Route("api/wechat-clawbot")]
public sealed class WeChatClawBotController : ControllerBase
{
    private readonly IWeChatClawBotService _service;
    private readonly SqlSugarScope _dbScope;

    public WeChatClawBotController(IWeChatClawBotService service, SqlSugarScope dbScope)
    {
        _service = service;
        _dbScope = dbScope;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyAccount()
    {
        var user = await ResolveCurrentUserAsync();
        return Ok(new { code = 200, message = "查询成功", data = await _service.GetMyAccountAsync(user.UserId, user.UserName, HttpContext.RequestAborted) });
    }

    [HttpPost("binding")]
    public async Task<IActionResult> CreateBinding()
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            return Ok(new { code = 200, message = "二维码已生成", data = await _service.CreateBindingAsync(user.UserId, user.UserName, HttpContext.RequestAborted) });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpGet("binding/{sessionId:long}")]
    public async Task<IActionResult> GetBindingStatus(long sessionId)
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            return Ok(new { code = 200, message = "查询成功", data = await _service.GetBindingStatusAsync(user.UserId, sessionId, HttpContext.RequestAborted) });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpDelete("binding")]
    public async Task<IActionResult> Unbind()
    {
        var user = await ResolveCurrentUserAsync();
        await _service.UnbindAsync(user.UserId, HttpContext.RequestAborted);
        return Ok(new { code = 200, message = "已解绑微信 ClawBot", data = true });
    }

    [HttpPost("binding/{sessionId:long}/verify")]
    public async Task<IActionResult> VerifyBinding(long sessionId, [FromBody] WeChatClawBotVerifyRequest request)
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            var result = await _service.GetBindingStatusAsync(
                user.UserId, sessionId, HttpContext.RequestAborted, request.VerifyCode.Trim());
            return Ok(new { code = 200, message = "扫码状态已更新", data = result });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpGet("me/messages")]
    public async Task<IActionResult> GetMyMessages([FromQuery] int current = 1, [FromQuery] int size = 20)
    {
        var user = await ResolveCurrentUserAsync();
        var result = await _service.GetMyInboundMessagesAsync(user.UserId, current, size, HttpContext.RequestAborted);
        return Ok(new { code = 200, message = "查询成功", data = result });
    }

    [HttpPost("me/test-send")]
    public async Task<IActionResult> SendMyTest([FromBody] WeChatClawBotMySendRequest request)
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            var result = await _service.SendMyTestAsync(user.UserId, request.Content, HttpContext.RequestAborted);
            return Ok(new
            {
                code = 200,
                message = result.Status == "Sent" ? "测试消息已发送" : "测试消息发送失败，请查看记录",
                data = result
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpPost("me/mcp-push-enabled")]
    public async Task<IActionResult> SetMyMcpPushEnabled([FromBody] WeChatClawBotPushEnabledRequest request)
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            await _service.SetMcpPushEnabledAsync(user.UserId, request.Enabled, HttpContext.RequestAborted);
            return Ok(new { code = 200, message = "推送授权已更新", data = true });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpGet("me/deliveries")]
    public async Task<IActionResult> GetMyDeliveries([FromQuery] WeChatClawBotDeliveryQueryDto query)
    {
        var user = await ResolveCurrentUserAsync();
        return Ok(new { code = 200, message = "查询成功", data = await _service.GetDeliveryPageAsync(query, true, user.UserId, HttpContext.RequestAborted) });
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts([FromQuery] string? userName, [FromQuery] int current = 1, [FromQuery] int size = 20)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        return Ok(new { code = 200, message = "查询成功", data = await _service.GetAccountsAsync(userName, current, size, HttpContext.RequestAborted) });
    }

    [HttpPost("accounts/{userId}/admin-disabled")]
    public async Task<IActionResult> SetAdminDisabled(string userId, [FromBody] WeChatClawBotAdminDisabledRequest request)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        try
        {
            await _service.SetAdminDisabledAsync(userId, request.Disabled, HttpContext.RequestAborted);
            return Ok(new { code = 200, message = request.Disabled ? "已禁用该用户推送" : "已恢复该用户推送", data = true });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    [HttpGet("deliveries")]
    public async Task<IActionResult> GetDeliveries([FromQuery] WeChatClawBotDeliveryQueryDto query)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        return Ok(new { code = 200, message = "查询成功", data = await _service.GetDeliveryPageAsync(query, false, null, HttpContext.RequestAborted) });
    }

    [HttpPost("debug/send")]
    public async Task<IActionResult> SendDebug([FromBody] WeChatClawBotSendDto dto)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        try
        {
            var result = await _service.SendDebugAsync(dto, HttpContext.RequestAborted);
            return Ok(new { code = 200, message = result.Status == "Sent" ? "测试消息已发送" : "测试消息发送失败，请查看记录", data = result });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message, data = new { } });
        }
    }

    private async Task<SysUserInfo> ResolveCurrentUserAsync()
    {
        var userKey = User.FindFirst("Id")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("Name")?.Value
            ?? User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userKey)) throw new InvalidOperationException("无法识别当前登录用户。");
        var user = await _dbScope.GetConnectionScope("calmadmin").Queryable<SysUserInfo>()
            .FirstAsync(x => x.Enabled && (x.UserId == userKey || x.UserName == userKey));
        return user ?? throw new InvalidOperationException("当前登录用户不存在或已停用。");
    }

    private async Task<bool> IsAdministratorAsync()
    {
        try
        {
            var user = await ResolveCurrentUserAsync();
            return user.RoleList.Any(x => x.Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) || x.Equals("R_SUPER", StringComparison.OrdinalIgnoreCase)) ||
                   user.PermissionKeyList.Any(x => x == "*");
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private IActionResult Forbidden() => StatusCode(403, new { code = 403, message = "仅系统管理员可以执行该操作", data = new { } });
}

public sealed class WeChatClawBotPushEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class WeChatClawBotAdminDisabledRequest
{
    public bool Disabled { get; set; }
}
