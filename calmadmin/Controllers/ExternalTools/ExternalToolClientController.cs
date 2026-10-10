using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.ExternalTools;
using Model.User;
using Service;
using SqlSugar;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace calmadmin.Controllers.ExternalTools;

/// <summary>后台管理外部工具客户端；密钥只在新增或显式轮换时返回一次。</summary>
[Authorize]
[ApiController]
[Route("api/external-tool-clients")]
public sealed class ExternalToolClientController : ControllerBase
{
    private readonly IExternalToolService _service;
    private readonly SqlSugarScope _dbScope;
    private readonly ExternalMcpV2ServerInfoProvider _mcpV2ServerInfoProvider;

    public ExternalToolClientController(
        IExternalToolService service,
        SqlSugarScope dbScope,
        ExternalMcpV2ServerInfoProvider mcpV2ServerInfoProvider)
    {
        _service = service;
        _dbScope = dbScope;
        _mcpV2ServerInfoProvider = mcpV2ServerInfoProvider;
    }

    [HttpGet]
    public Task<IActionResult> GetClients() => ExecuteForAdministratorAsync(_service.GetClientsAsync, "查询成功");

    [HttpPost]
    public async Task<IActionResult> SaveClient([FromBody] ExternalToolClientSaveDto dto)
    {
        try
        {
            if (!await IsCurrentUserAdministratorAsync()) return Error("仅系统管理员可以维护外部 MCP 客户端。", 403);
            return Success(await _service.SaveClientAsync(dto), "客户端已保存");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = 400, message = ex.Message });
        }
    }

    [HttpGet("server-setting")]
    public Task<IActionResult> GetServerSetting() =>
        ExecuteForAdministratorAsync(_service.GetServerSettingAsync, "查询成功");

    [HttpPost("server-setting")]
    public async Task<IActionResult> SaveServerSetting([FromBody] ExternalToolServerSettingSaveDto dto)
    {
        try
        {
            if (!await IsCurrentUserAdministratorAsync()) return Error("仅系统管理员可以修改 MCP 服务配置。", 403);
            if (!ValidateConfigurationPassword(dto.ConfigurationPassword))
                return Error("配置口令错误或已过期，请按服务器当前时间 yyyyMMddHHmm 重新输入。", 400);

            var setting = await _service.SaveServerSettingAsync(dto, GetCurrentUserId());
            _mcpV2ServerInfoProvider.Apply(setting);
            return Success(setting, "MCP V1/V2 服务配置已保存");
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, 400);
        }
    }

    private async Task<IActionResult> ExecuteForAdministratorAsync<T>(Func<Task<T>> action, string message)
    {
        if (!await IsCurrentUserAdministratorAsync()) return Error("仅系统管理员可以维护外部 MCP 客户端。", 403);
        return Success(await action(), message);
    }

    private string GetCurrentUserId()
    {
        var userId = User.FindFirst("Id")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("Name")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value;
        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("无法识别当前登录用户。");
        return userId.Trim();
    }

    private async Task<bool> IsCurrentUserAdministratorAsync()
    {
        var userId = GetCurrentUserId();
        var user = await _dbScope.GetConnectionScope("calmadmin")
            .Queryable<SysUserInfo>()
            .FirstAsync(x => x.UserId == userId && x.Enabled);
        return user?.RoleList.Any(role =>
            role.Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("R_SUPER", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool ValidateConfigurationPassword(string? password)
    {
        var expected = DateTime.Now.ToString("yyyyMMddHHmm");
        var actual = password?.Trim() ?? string.Empty;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }

    private static IActionResult Success(object? data, string message) =>
        new JsonResult(new { code = 200, message, data });

    private static IActionResult Error(string message, int code) =>
        new JsonResult(new { code, message, data = new { } }) { StatusCode = code };
}
