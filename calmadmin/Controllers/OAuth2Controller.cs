using Interface;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Model.Dto;
using Model.Other;

namespace calmadmin.Controllers;

[ApiController]
[Route("api/auth/oauth2")]
public sealed class OAuth2Controller : ControllerBase
{
    private const string StateCookieName = "calmadmin_oauth2_state";
    private const string LogoutSessionCookieName = "calmadmin_oauth2_logout_session";
    private readonly IOAuth2LoginService _oauth2LoginService;
    private readonly OAuth2Options _options;
    private readonly ILogger<OAuth2Controller> _logger;

    public OAuth2Controller(
        IOAuth2LoginService oauth2LoginService,
        IOptionsMonitor<OAuth2Options> options,
        ILogger<OAuth2Controller> logger)
    {
        _oauth2LoginService = oauth2LoginService;
        _options = options.CurrentValue;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult Status() => Success(_oauth2LoginService.GetStatus());

    [HttpGet("authorize")]
    public IActionResult Authorize([FromQuery] string? returnUrl = null)
    {
        try
        {
            var callbackUrl = ResolveCallbackUrl();
            var result = _oauth2LoginService.CreateAuthorization(callbackUrl, returnUrl);
            Response.Cookies.Append(StateCookieName, result.protectedState, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromSeconds(result.stateLifetimeSeconds),
                Path = "/api/auth/oauth2"
            });
            return Redirect(result.authorizationUrl);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription)
    {
        if (!string.IsNullOrWhiteSpace(error))
            return RedirectToFrontendError(errorDescription ?? error);

        if (!Request.Cookies.TryGetValue(StateCookieName, out var protectedState))
            return RedirectToFrontendError("OAuth 登录状态已丢失，请重新发起登录");

        Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/api/auth/oauth2" });
        try
        {
            var result = await _oauth2LoginService.HandleCallbackAsync(
                ResolveCallbackUrl(),
                code ?? string.Empty,
                state ?? string.Empty,
                protectedState,
                HttpContext.RequestAborted);
            Response.Cookies.Append(LogoutSessionCookieName, result.logoutSessionId, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromHours(8),
                Path = "/api/auth/oauth2"
            });
            return Redirect(BuildFrontendLoginUrl(new Dictionary<string, string?>
            {
                ["oauthTicket"] = result.ticket,
                ["redirect"] = result.returnUrl
            }));
        }
        catch (InvalidOperationException ex)
        {
            return RedirectToFrontendError(ex.Message);
        }
    }

    [HttpPost("complete")]
    public IActionResult Complete([FromBody] OAuth2CompleteRequest request)
    {
        try
        {
            return Success(_oauth2LoginService.RedeemTicket(request.ticket), "登录成功");
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    /// <summary>
    /// 主动退出时同时结束 TCL SIAM 浏览器会话，以便用户能切换统一身份账号。
    /// 回跳地址固定为本系统登录页，避免由前端参数造成开放重定向。
    /// </summary>
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = "/api/auth/oauth2" });
        if (Request.Cookies.TryGetValue(LogoutSessionCookieName, out var logoutSessionId))
        {
            try
            {
                await _oauth2LoginService.RevokeExternalSessionAsync(logoutSessionId, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                // 即使票据注销接口暂时异常，也继续执行浏览器级登出，避免本系统退出被阻断。
                _logger.LogWarning(ex, "SIAM TGT 注销失败，将继续执行浏览器单点登出");
            }
        }
        Response.Cookies.Delete(LogoutSessionCookieName, new CookieOptions { Path = "/api/auth/oauth2" });
        var frontendLoginUrl = BuildFrontendLoginUrl(new Dictionary<string, string?>());
        if (!Uri.TryCreate(_options.LogoutEndpoint, UriKind.Absolute, out _))
            return Redirect(frontendLoginUrl);

        var parameterName = string.IsNullOrWhiteSpace(_options.LogoutReturnUrlParameter)
            ? "service"
            : _options.LogoutReturnUrlParameter.Trim();
        return Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            _options.LogoutEndpoint.Trim(),
            parameterName,
            frontendLoginUrl));
    }

    private string ResolveCallbackUrl()
    {
        if (!string.IsNullOrWhiteSpace(_options.CallbackUrl)) return _options.CallbackUrl.Trim();
        return $"{Request.Scheme}://{Request.Host}/api/auth/oauth2/callback";
    }

    private IActionResult RedirectToFrontendError(string message) =>
        Redirect(BuildFrontendLoginUrl(new Dictionary<string, string?> { ["oauthError"] = message }));

    private string BuildFrontendLoginUrl(IReadOnlyDictionary<string, string?> parameters)
    {
        if (!Uri.TryCreate(_options.FrontendLoginUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("OAuth 前端登录地址未配置");
        var separator = _options.FrontendLoginUrl.Contains('?') ? "&" : "?";
        var query = string.Join("&", parameters
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
        return string.IsNullOrEmpty(query) ? _options.FrontendLoginUrl : _options.FrontendLoginUrl + separator + query;
    }

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code) =>
        new JsonResult(new { code, msg, data = new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
}
