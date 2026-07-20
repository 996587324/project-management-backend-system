using System.Text.Json;
using Interface;
using Model.Agent;

namespace Service;

/// <summary>
/// Agent 邮件业务工具执行器，负责处理 Agent 发起的邮件发送动作。
/// </summary>
public class AgentEmailBusinessToolExecutor : IAgentBusinessToolExecutor
{
    private readonly IEmailService _emailService;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AgentEmailBusinessToolExecutor(IEmailService emailService)
    {
        _emailService = emailService;
    }

    /// <summary>
    /// 判断当前执行器是否负责处理邮件发送工具。
    /// </summary>
    public bool CanExecute(string serviceName, string actionName) =>
        serviceName.Equals("Email", StringComparison.OrdinalIgnoreCase) &&
        actionName.Equals("SendEmail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 执行邮件发送动作，将 Agent 参数反序列化后交给邮件服务处理。
    /// </summary>
    public async Task<AgentBusinessToolResult> ExecuteAsync(
        string actionName,
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Deserialize<AgentEmailSendRequest>(arguments.GetRawText(), JsonOptions)
                      ?? new AgentEmailSendRequest();
        request.ConversationId = context.ConversationId;
        request.UserId = context.UserId;
        var result = await _emailService.SendAsync(request);
        return AgentBusinessToolResult.Ok(result, result.Message);
    }
}
