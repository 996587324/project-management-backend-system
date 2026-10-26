using System.Text.Json;
using Interface;
using Model.Agent;
using Microsoft.AspNetCore.Http;

namespace Service;

/// <summary>
/// Agent 邮件业务工具执行器，负责处理 Agent 发起的邮件发送动作。
/// </summary>
public class AgentEmailBusinessToolExecutor : IAgentBusinessToolExecutor
{
    private readonly IEmailService _emailService;
    private readonly IEmailPushService _push;
    private readonly ISystemApplicationService _applications;
    private readonly IHttpContextAccessor _http;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AgentEmailBusinessToolExecutor(IEmailService emailService, IEmailPushService push,
        ISystemApplicationService applications, IHttpContextAccessor http)
    {
        _emailService = emailService;
        _push = push;
        _applications = applications;
        _http = http;
    }

    /// <summary>
    /// 判断当前执行器是否负责处理邮件发送工具。
    /// </summary>
    public bool CanExecute(string serviceName, string actionName) =>
        serviceName.Equals("Email", StringComparison.OrdinalIgnoreCase) &&
        (actionName.Equals("SendEmail", StringComparison.OrdinalIgnoreCase) ||
         actionName.Equals("GetEmailRecipients", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 执行邮件发送动作，将 Agent 参数反序列化后交给邮件服务处理。
    /// </summary>
    public async Task<AgentBusinessToolResult> ExecuteAsync(
        string actionName,
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (actionName.Equals("GetEmailRecipients", StringComparison.OrdinalIgnoreCase))
        {
            var access = AgentChannelExecutionScope.Current?.ResolveApplicationAccessAsync is { } resolve
                ? await resolve()
                : await _applications.GetAccessAsync(_http.HttpContext?.User
                    ?? throw new UnauthorizedAccessException("缺少当前用户上下文"), true);
            if (access.UserId != context.UserId) throw new UnauthorizedAccessException("邮件查询用户与执行用户不一致");
            var keyword = arguments.TryGetProperty("keyword", out var term) ? term.GetString() : null;
            var current = arguments.TryGetProperty("current", out var page) ? page.GetInt32() : 1;
            var size = arguments.TryGetProperty("size", out var count) ? count.GetInt32() : 20;
            return AgentBusinessToolResult.Ok(await _push.GetRecipientsAsync(access, keyword, current, size), "邮件接收用户查询完成");
        }
        var request = JsonSerializer.Deserialize<AgentEmailSendRequest>(arguments.GetRawText(), JsonOptions)
                      ?? new AgentEmailSendRequest();
        request.ConversationId = context.ConversationId;
        request.UserId = context.UserId;
        request.Confirm = true; // Reached only after the Agent execution-plan confirmation gate.
        request.IdempotencyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{context.ConversationId}:{context.CorrelationId}:{arguments.GetRawText()}")));
        var result = await _emailService.SendAsync(request);
        return AgentBusinessToolResult.Ok(result, result.Message);
    }
}
