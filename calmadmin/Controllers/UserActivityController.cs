using System.Security.Claims;
using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.User;
using SqlSugar;

namespace calmadmin.Controllers;

/// <summary>后台用户会话、登录记录和操作审计接口。</summary>
[ApiController]
[Authorize]
[Route("api/user-activity")]
public sealed class UserActivityController : ControllerBase
{
    private readonly IUserActivityService _service;
    private readonly SqlSugarScope _dbScope;

    public UserActivityController(IUserActivityService service, SqlSugarScope dbScope)
    {
        _service = service;
        _dbScope = dbScope;
    }

    /// <summary>前端定期调用，用于维持当前会话在线状态。</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        await _service.TouchSessionAsync(GetSessionId(), GetUserKey(), HttpContext.RequestAborted);
        return Ok(new { code = 200, msg = "在线状态已更新", data = new { serverTime = DateTime.Now } });
    }

    /// <summary>本系统退出前结束当前会话；不影响 OAuth 单点登出跳转。</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _service.EndSessionAsync(GetSessionId(), GetUserKey(), HttpContext.RequestAborted);
        return Ok(new { code = 200, msg = "会话已结束", data = true });
    }

    /// <summary>记录前端已触发的业务按钮或页面操作，不记录接口请求正文。</summary>
    [HttpPost("operation")]
    public async Task<IActionResult> RecordOperation([FromBody] UserOperationEventSaveDto input)
    {
        var action = input.Action?.Trim();
        if (string.IsNullOrWhiteSpace(action))
            return BadRequest(new { code = 400, msg = "操作名称不能为空", data = "" });

        await _service.RecordOperationAsync(new UserOperationAuditInput
        {
            UserId = GetUserKey(),
            UserName = User.FindFirst("Name")?.Value ?? User.Identity?.Name ?? "未知用户",
            SessionId = GetSessionId(),
            Module = input.Module?.Trim() ?? "未分类模块",
            Action = action,
            HttpMethod = input.OperationType?.Trim() ?? "ButtonClick",
            RequestPath = input.PagePath?.Trim() ?? string.Empty,
            Target = input.Target?.Trim(),
            Success = true,
            StatusCode = StatusCodes.Status200OK,
            DurationMs = 0,
            ClientIp = Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        }, HttpContext.RequestAborted);

        return Ok(new { code = 200, msg = "操作已记录", data = true });
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        return Ok(new { code = 200, msg = "请求成功", data = await _service.GetOverviewAsync(HttpContext.RequestAborted) });
    }

    [HttpGet("login-records")]
    public async Task<IActionResult> GetLoginRecords([FromQuery] UserLoginLogQueryDto query)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        return Ok(new { code = 200, msg = "请求成功", data = await _service.GetLoginPageAsync(query, HttpContext.RequestAborted) });
    }

    [HttpGet("operation-records")]
    public async Task<IActionResult> GetOperationRecords([FromQuery] UserOperationLogQueryDto query)
    {
        if (!await IsAdministratorAsync()) return Forbidden();
        return Ok(new { code = 200, msg = "请求成功", data = await _service.GetOperationPageAsync(query, HttpContext.RequestAborted) });
    }

    private IActionResult Forbidden() => StatusCode(403, new { code = 403, msg = "仅系统管理员可以查看用户审计日志", data = "" });

    private string? GetSessionId() => User.FindFirst("SessionId")?.Value;

    private string? GetUserKey() => User.FindFirst("Id")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("Name")?.Value
        ?? User.FindFirst(ClaimTypes.Name)?.Value;

    private async Task<bool> IsAdministratorAsync()
    {
        var userKey = GetUserKey();
        if (string.IsNullOrWhiteSpace(userKey)) return false;
        var db = _dbScope.GetConnectionScope("calmadmin");
        var user = await db.Queryable<SysUserInfo>()
            .FirstAsync(x => x.Enabled && (x.UserId == userKey || x.UserName == userKey));
        if (user == null) return false;
        return user.RoleList.Any(x => string.Equals(x, "R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(x, "R_SUPER", StringComparison.OrdinalIgnoreCase)) ||
               user.PermissionKeyList.Any(x => x == "*");
    }
}
