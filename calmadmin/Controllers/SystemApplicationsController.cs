using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.SystemApplications;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/system-applications")]
public sealed class SystemApplicationsController(ISystemApplicationService applications) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("entry-information")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> EntryInformation([FromServices] SqlSugar.SqlSugarScope scope,
        [FromServices] IConfiguration configuration, [FromQuery] string appCode = "A")
    {
        appCode = appCode.Trim().ToUpperInvariant();
        if (appCode is not ("A" or "B"))
            return BadRequest(new { code = 400, msg = "无效的系统编码", data = new { } });
        var db = scope.GetConnectionScope("calmadmin");
        var application = await db.Queryable<SysApplicationEntity>().FirstAsync(x => x.AppCode == "A" && x.Enabled);
        var target = appCode == "A" ? application
            : await db.Queryable<SysApplicationEntity>().FirstAsync(x => x.AppCode == appCode && x.Enabled);
        if (target == null)
            return NotFound(new { code = 404, msg = "系统不存在或已停用", data = new { } });
        var url = application?.EntryUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var loginUrl = configuration["OAuth2:FrontendLoginUrl"];
            if (Uri.TryCreate(loginUrl, UriKind.Absolute, out var login))
                url = new UriBuilder(login) { Fragment = "/portal", Query = string.Empty }.Uri.AbsoluteUri;
        }
        // 控制台始终在 A；名称取请求的系统，不返回授权、账号或业务配置信息。
        return Success(new { appCode, portalUrl = url ?? string.Empty, displayName = target.DisplayName });
    }
    [HttpGet]
    public async Task<IActionResult> List() => Success(await applications.GetApplicationsAsync(User));

    [HttpGet("context")]
    public async Task<IActionResult> Context() => Success(await applications.GetAccessAsync(User));

    [HttpGet("session")]
    [Authorize(AuthenticationSchemes = "SessionStatus")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Session() => Success(await applications.GetSessionActivityAsync(User));

    [HttpPost("activity")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Activity([FromBody] SessionActivityRequest request) =>
        Success(await applications.GetSessionActivityAsync(User, request.IdleMilliseconds));

    [HttpPost("ticket")]
    public async Task<IActionResult> Ticket([FromBody] ApplicationTicketRequest request) =>
        Success(await applications.CreateTicketAsync(User, request));

    [AllowAnonymous]
    [HttpPost("redeem")]
    public async Task<IActionResult> Redeem([FromBody] ApplicationTicketRedeemRequest request) =>
        Success(await applications.RedeemTicketAsync(request));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await applications.RevokeSessionAsync(User);
        return Success(true);
    }

    [HttpGet("administration")]
    public async Task<IActionResult> Administration() => Success(await applications.GetAdministrationAsync(User));

    [HttpPost("configuration")]
    public async Task<IActionResult> Configuration([FromBody] SysApplicationEntity request)
    {
        await applications.SaveApplicationAsync(User, request);
        return Success(true);
    }

    [HttpGet("grants/{userId}")]
    public async Task<IActionResult> Grants(string userId) => Success(await applications.GetGrantsAsync(User, userId));

    [HttpPost("grants")]
    public async Task<IActionResult> SaveGrant([FromBody] ApplicationGrantDto request)
    {
        await applications.SaveGrantAsync(User, request);
        return Success(true);
    }

    [HttpPost("menus/{appCode}")]
    public async Task<IActionResult> Menus(string appCode, [FromBody] List<string> menuIds)
    {
        await applications.SaveMenusAsync(User, appCode, menuIds);
        return Success(true);
    }

    [HttpPost("mcp-token")]
    public async Task<IActionResult> McpToken() => Success(new
    {
        token = await applications.CreateMcpTokenAsync(User),
        endpoint = "/mcp/v2"
    });

    private static IActionResult Success(object data) => new JsonResult(new { code = 200, msg = "操作成功", data });
}
