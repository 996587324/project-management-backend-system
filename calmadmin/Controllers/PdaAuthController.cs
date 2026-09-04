using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.Pda;

namespace calmadmin.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class PdaAuthController : ControllerBase
{
    private readonly IPdaService _pdaService;

    public PdaAuthController(IPdaService pdaService)
    {
        _pdaService = pdaService;
    }

    [HttpPost("pda-login")]
    public async Task<IActionResult> Login([FromBody] PdaLoginRequest request)
    {
        try
        {
            return Success(await _pdaService.LoginAsync(request), "登录成功");
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code, object? data = null) =>
        new JsonResult(new { code, msg, data = data ?? new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
}
