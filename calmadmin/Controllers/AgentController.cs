using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.Agent;
using System.Security.Claims;

namespace calmadmin.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AgentController : ControllerBase
{
    private readonly IAgentService _service;

    public AgentController(IAgentService service)
    {
        _service = service;
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations([FromQuery] string? userId, [FromQuery] int current = 1, [FromQuery] int size = 20)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        return Success(await _service.GetConversationPageAsync(currentUserId, current, size));
    }

    [HttpGet("conversations/{id:int}")]
    public async Task<IActionResult> GetConversation(int id)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        var data = await _service.GetConversationAsync(id, currentUserId);
        if (data == null)
            return Error("会话不存在", 404);
        return Success(data);
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation([FromBody] AgentStartRequest request)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        request.UserId = currentUserId;
        var res = await _service.CreateConversationAsync(request);
        return Success(res, "创建成功");
    }

    [HttpPost("conversations/delete")]
    public async Task<IActionResult> DeleteConversation([FromBody] AgentDeleteConversationRequest request)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        request.UserId = currentUserId;
        var deleted = await _service.DeleteConversationAsync(request);
        return deleted ? Success(true, "删除成功") : Error("会话不存在", 404);
    }

    [HttpGet("tools")]
    public async Task<IActionResult> GetTools([FromQuery] string? agentCode)
    {
        return Success(await _service.GetToolCatalogAsync(agentCode));
    }

    [HttpPost("email/draft")]
    public async Task<IActionResult> CreateEmailDraft([FromBody] AgentEmailDraftRequest request)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        request.UserId = currentUserId;
        var draft = await _service.CreateEmailDraftAsync(request);
        return draft == null ? Error("没有可发送的 Agent 结果", 404) : Success(draft);
    }

    [HttpPost("email/send")]
    public async Task<IActionResult> SendEmail([FromBody] AgentEmailSendRequest request)
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        request.UserId = currentUserId;
        var result = await _service.SendEmailAsync(request);
        return Success(result, result.Message);
    }

    [HttpPost("tool-calls/{id:int}/confirm")]
    public async Task<IActionResult> ConfirmToolCall(int id, [FromBody] AgentConfirmRequest request)
    {
        request.ToolCallId = id;
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(currentUserId)) return Error("未登录", 401);
        request.UserId = currentUserId;
        var data = await _service.ConfirmToolCallAsync(request);
        return data == null ? Error("工具调用不存在", 404) : Success(data, "已确认");
    }

    private static IActionResult Success(object? data, string msg = "success") =>
        new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code) =>
        new JsonResult(new { code, msg, data = new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private string GetCurrentUserId()
    {
        var id = User.FindFirst("Id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(id) && id != "0") return id;
        return User.FindFirst("Name")?.Value
               ?? User.FindFirst(ClaimTypes.Name)?.Value
               ?? string.Empty;
    }
}
