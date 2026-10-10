using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.ExternalTools;

namespace calmadmin.Controllers.ExternalTools;

/// <summary>供已登记外部系统以 client_credentials 方式换取短期工具访问令牌。</summary>
[AllowAnonymous]
[ApiController]
[Route("api/external/v1/oauth")]
public sealed class ExternalToolTokenController : ControllerBase
{
    private readonly IExternalToolService _service;

    public ExternalToolTokenController(IExternalToolService service)
    {
        _service = service;
    }

    [HttpPost("token")]
    public async Task<IActionResult> CreateToken([FromBody] ExternalToolTokenRequestDto dto)
    {
        var token = await _service.IssueClientTokenAsync(dto);
        return token == null
            ? Unauthorized(new { error = "invalid_client", error_description = "客户端身份无效、已停用或已过期" })
            : Ok(token);
    }
}
