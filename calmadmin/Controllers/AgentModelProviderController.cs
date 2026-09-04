using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.Agent;
using Model.User;
using SqlSugar;
using System.Security.Claims;

namespace calmadmin.Controllers;

/// <summary>系统统一模型平台配置接口。仅系统管理员可以维护密钥和 Agent 平台绑定。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class AgentModelProviderController : ControllerBase
{
    private readonly IAgentModelProviderService _providerService;
    private readonly IAgentModelClient _modelClient;
    private readonly SqlSugarScope _dbScope;

    public AgentModelProviderController(
        IAgentModelProviderService providerService,
        IAgentModelClient modelClient,
        SqlSugarScope dbScope)
    {
        _providerService = providerService;
        _modelClient = modelClient;
        _dbScope = dbScope;
    }

    /// <summary>分页查询当前系统的模型平台。</summary>
    [HttpGet("page")]
    public Task<IActionResult> GetPage([FromQuery] AgentModelProviderQueryDto query) =>
        ExecuteForAdministratorAsync(() => _providerService.GetPageAsync(AgentModelConfigurationScope.SystemOwnerUserId, query), "查询成功");

    /// <summary>查询 Agent 绑定下拉框使用的全部平台。</summary>
    [HttpGet("options")]
    public Task<IActionResult> GetOptions() =>
        ExecuteForAdministratorAsync(() => _providerService.GetOptionsAsync(AgentModelConfigurationScope.SystemOwnerUserId), "查询成功");

    /// <summary>查询单个平台详情，不返回密钥明文或密文。</summary>
    [HttpGet("{id:int}")]
    public Task<IActionResult> Get(int id) =>
        ExecuteForAdministratorAsync(() => _providerService.GetAsync(AgentModelConfigurationScope.SystemOwnerUserId, id), "查询成功");

    /// <summary>新增或修改平台；修改时密钥留空表示保留原密钥。</summary>
    [HttpPost("save")]
    public Task<IActionResult> Save([FromBody] AgentModelProviderSaveDto dto) =>
        ExecuteForAdministratorAsync(() => _providerService.SaveAsync(AgentModelConfigurationScope.SystemOwnerUserId, dto), "模型平台已保存");

    /// <summary>单独启用或停用模型平台。</summary>
    [HttpPost("{id:int}/enabled")]
    public Task<IActionResult> SetEnabled(int id, [FromBody] AgentModelProviderEnabledDto dto) =>
        ExecuteForAdministratorAsync(() => _providerService.SetEnabledAsync(AgentModelConfigurationScope.SystemOwnerUserId, id, dto.Enabled), dto.Enabled ? "模型平台已启用" : "模型平台已停用");

    /// <summary>软删除平台并停用其 Agent 绑定。</summary>
    [HttpPost("delete/{id:int}")]
    public Task<IActionResult> Delete(int id) =>
        ExecuteForAdministratorAsync(() => _providerService.DeleteAsync(AgentModelConfigurationScope.SystemOwnerUserId, id), "模型平台已删除");

    /// <summary>查询所有代码注册 Agent 的平台绑定。</summary>
    [HttpGet("bindings")]
    public Task<IActionResult> GetBindings() =>
        ExecuteForAdministratorAsync(() => _providerService.GetBindingsAsync(AgentModelConfigurationScope.SystemOwnerUserId), "查询成功");

    /// <summary>保存 Agent 与平台的主备优先级绑定。</summary>
    [HttpPost("bindings/save")]
    public Task<IActionResult> SaveBinding([FromBody] AgentModelBindingSaveDto dto) =>
        ExecuteForAdministratorAsync(() => _providerService.SaveBindingAsync(AgentModelConfigurationScope.SystemOwnerUserId, dto), "Agent 平台绑定已保存");

    /// <summary>发送最小模型请求验证已保存平台；该测试会产生少量 Token 用量。</summary>
    [HttpPost("{id:int}/test")]
    public Task<IActionResult> Test(int id) =>
        ExecuteForAdministratorAsync(() => _modelClient.TestConnectionAsync(id, AgentModelConfigurationScope.SystemOwnerUserId), "测试完成");

    private async Task<IActionResult> ExecuteForAdministratorAsync<T>(Func<Task<T>> action, string message)
    {
        try
        {
            if (!await IsCurrentUserAdministratorAsync())
                return Error("仅系统管理员可以维护 Agent 模型平台配置。", 403);
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
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("无法识别当前登录用户。");

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

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code, object? data = null) =>
        new JsonResult(new { code, msg, data = data ?? new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
}
