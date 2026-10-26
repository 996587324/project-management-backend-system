using Interface;
using Microsoft.AspNetCore.Http;
using Model.Agent;
using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>兼容现有 Agent 邮件入口；所有发送都进入统一队列，不直接连接 SMTP。</summary>
public sealed class EmailService(
    IEmailPushService push,
    ISystemApplicationService applications,
    IHttpContextAccessor http,
    SqlSugarScope scope) : IEmailService
{
    public async Task<AgentEmailSendResultDto> SendAsync(AgentEmailSendRequest request)
    {
        ApplicationAccessContext access;
        if (AgentChannelExecutionScope.Current?.ResolveApplicationAccessAsync is { } resolve)
            access = await resolve();
        else if (http.HttpContext?.User.Identity?.IsAuthenticated == true)
            access = await applications.GetAccessAsync(http.HttpContext.User, true);
        else
            throw new UnauthorizedAccessException("邮件发送需要有效的用户与系统授权上下文");
        if (request.UserId != access.UserId) throw new UnauthorizedAccessException("邮件发起人与登录用户不一致");
        using var operation = DatabaseOperationScope.Begin(scope);
        var toIds = request.RecipientUserIds;
        var ccIds = request.CcUserIds;
        // Compatibility for registered legacy plans: resolve addresses only to enabled system users.
        if (request.ToEmails.Count > 0 || request.CcEmails.Count > 0)
        {
            if (toIds.Count > 0 || ccIds.Count > 0) throw new ArgumentException("用户编码和旧版邮箱参数不能混用");
            var addresses = request.ToEmails.Concat(request.CcEmails).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (addresses.Length > 100) throw new ArgumentException("单次最多100个收件用户");
            var users = await DatabaseOperationScope.GetConnection(scope, "calmadmin").Queryable<SysUserInfo>()
                .Where(x => x.Enabled && addresses.Contains(x.Email)).ToListAsync();
            string Resolve(string email)
            {
                return users.FirstOrDefault(x => x.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))?.UserId
                    ?? throw new ArgumentException("收件邮箱未对应到有效系统用户，请先维护邮箱或改用用户编码");
            }
            toIds = request.ToEmails.Select(Resolve).ToList();
            ccIds = request.CcEmails.Select(Resolve).ToList();
        }
        var result = await push.EnqueueAsync(access, new EmailEnqueueDto
        {
            RecipientUserIds = toIds, CcUserIds = ccIds, Subject = request.Subject, Body = request.Body,
            IdempotencyKey = request.IdempotencyKey, Confirm = request.Confirm
        }, "Agent");
        return new AgentEmailSendResultDto
        {
            Success = true, Message = $"{result.Message}，任务编号：{result.TaskId}",
            TaskId = result.TaskId, Status = result.Status
        };
    }
}
