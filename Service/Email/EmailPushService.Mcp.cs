using Model.EmailPush;
using Model.ExternalTools;

namespace Service;

public sealed partial class EmailPushService
{
    public async Task<EmailPage<EmailRecipientDto>> GetMcpRecipientsAsync(
        ExternalToolCallerContext caller, string? keyword, int current, int size)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        McpPushAccess.RequireOwner(caller, "email.read");
        return await QueryRecipientsAsync(keyword, current, size);
    }

    public async Task<object> GetMcpAvailabilityAsync(ExternalToolCallerContext caller)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        McpPushAccess.RequireOwner(caller, "email.read");
        if (!initializer.IsReady) return new { ready = false, enabled = false };
        var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        return new { ready = true, enabled = setting?.Enabled == true };
    }

    public async Task<EmailEnqueueResult> EnqueueByMcpAsync(ExternalToolCallerContext caller, EmailEnqueueDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "email.write");
        return await EnqueueCoreAsync(owner, McpPushAccess.Application, dto, "MCP");
    }

    public async Task<EmailPage<EmailRecordDto>> GetMcpRecordsAsync(ExternalToolCallerContext caller, EmailRecordQuery query)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "email.read");
        return await QueryRecordsAsync(query, owner, McpPushAccess.Application);
    }

    public async Task RetryByMcpAsync(ExternalToolCallerContext caller, long deliveryId, bool confirm)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "email.write");
        RequireReady();
        if (!confirm) throw new ArgumentException("重试邮件必须明确确认");
        var record = await QueryRecordsAsync(new EmailRecordQuery { DeliveryId = deliveryId, Size = 1 }, owner, McpPushAccess.Application);
        var row = record.Records.SingleOrDefault();
        if (row?.Status != "Failed") throw new InvalidOperationException("只能重试当前MCP来源明确失败的邮件，Unknown结果必须人工核实");
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            var changed = await Db.Updateable<EmailPushDeliveryEntity>()
                .SetColumns(x => new EmailPushDeliveryEntity
                {
                    Status = "Pending", AttemptCount = 0, NextAttemptTime = now, LeaseExpireTime = null,
                    ErrorMessage = null, UpdateTime = now
                }).Where(x => x.Id == deliveryId && x.Status == "Failed").ExecuteCommandAsync();
            if (changed != 1) throw new InvalidOperationException("投递状态已变化，请刷新后重试");
            await Db.Insertable(new EmailPushConfigurationAuditEntity
            {
                UserId = owner, Action = "McpRetryDelivery",
                Summary = $"DeliveryId={deliveryId}; PreviousAttempts={row.AttemptCount}"
            }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("重试入队失败");
    }
}
