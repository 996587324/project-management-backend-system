using Model.EmailPush;
using Model.User;

namespace Service;

public sealed partial class EmailPushService
{
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        RequireReady();
        using var operation = DatabaseOperationScope.Begin(scope);
        var now = DateTime.Now;
        // SMTP cannot guarantee exactly-once delivery after a disconnect. Never automatically resend an expired sending lease.
        await Db.Updateable<EmailPushDeliveryEntity>()
            .SetColumns(x => new EmailPushDeliveryEntity
            {
                Status = "Unknown", ErrorMessage = "发送进程中断或租约过期，结果待核实，未自动重发",
                LeaseExpireTime = null, UpdateTime = now
            }).Where(x => x.Status == "Processing" && x.LeaseExpireTime <= now).ExecuteCommandAsync();
        var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        if (!setting.Enabled || setting.NextSendTime > now || setting.LeaseExpireTime > now) return;
        var delivery = await Db.Queryable<EmailPushDeliveryEntity>()
            .Where(x => (x.Status == "Pending" || x.Status == "Retrying") && x.NextAttemptTime <= now)
            .OrderBy(x => x.Id).FirstAsync();
        if (delivery == null) return;
        var owner = Guid.NewGuid().ToString("N");
        var leaseUntil = now.AddMinutes(5);
        var claimed = await Db.Updateable<EmailPushSettingEntity>()
            .SetColumns(x => new EmailPushSettingEntity { LeaseOwner = owner, LeaseExpireTime = leaseUntil })
            .Where(x => x.Id == 1 && x.Enabled && (x.NextSendTime == null || x.NextSendTime <= now) &&
                (x.LeaseExpireTime == null || x.LeaseExpireTime <= now)).ExecuteCommandAsync();
        if (claimed == 0) return;
        var attempted = false;
        try
        {
            setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
            var task = await Db.Queryable<EmailPushTaskEntity>().InSingleAsync(delivery.TaskId);
            var recipient = await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == delivery.RecipientUserId);
            var external = task != null && task.Source == "MCP" && McpPushAccess.IsExternal(task.AppCode, task.CreatedBy);
            var sender = task == null || external ? null : await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == task.CreatedBy);
            var senderAuthorized = true;
            if (task != null && !external && applications != null)
            {
                try
                {
                    var access = await applications.GetChannelAccessAsync(task.CreatedBy, task.AppCode);
                    senderAuthorized = access.UserId == task.CreatedBy && access.AppCode == task.AppCode &&
                        (access.HasPermission("EmailPush") || access.HasPermission("AgentExecutor"));
                }
                catch (UnauthorizedAccessException)
                {
                    senderAuthorized = false;
                }
            }
            if (!senderAuthorized || task == null || !external && sender?.Enabled != true || recipient?.Enabled != true ||
                !IsValidAddress(recipient.Email) || !recipient.Email.Trim().Equals(delivery.Address, StringComparison.OrdinalIgnoreCase))
            {
                await Db.Updateable<EmailPushDeliveryEntity>()
                    .SetColumns(x => new EmailPushDeliveryEntity
                    {
                        Status = "Cancelled", ErrorMessage = "发起人／接收人已停用、邮箱变更或任务无效，请核对后重新提交",
                        UpdateTime = now
                    }).Where(x => x.Id == delivery.Id && (x.Status == "Pending" || x.Status == "Retrying")).ExecuteCommandAsync();
                return;
            }
            var started = await Db.Updateable<EmailPushDeliveryEntity>()
                .SetColumns(x => new EmailPushDeliveryEntity
                {
                    Status = "Processing", AttemptCount = delivery.AttemptCount + 1,
                    FromAddress = setting.FromAddress, SettingVersion = setting.Version,
                    LeaseExpireTime = leaseUntil, ErrorMessage = null, UpdateTime = now
                }).Where(x => x.Id == delivery.Id && (x.Status == "Pending" || x.Status == "Retrying"))
                .ExecuteCommandAsync();
            if (started == 0) return;
            attempted = true;
            EmailTransportResult result;
            try
            {
                result = await transport.SendAsync(setting, task, delivery, cancellationToken);
            }
            catch
            {
                // Unknown transport failures are conservative: a side effect may already have happened.
                result = new EmailTransportResult { Status = "Unknown", Error = "发送结果不明确，请核实后决定是否重试" };
            }
            var attempts = delivery.AttemptCount + 1;
            var status = result.Status == "Retryable" ? (attempts < setting.MaxAttempts ? "Retrying" : "Failed") : result.Status;
            var completed = DateTime.Now;
            DateTime? sent = status == "Sent" ? completed : null;
            var retryAt = completed.AddSeconds(Math.Max(setting.IntervalSeconds, Math.Min(600, 30 * (1 << Math.Min(attempts, 4)))));
            await Db.Updateable<EmailPushDeliveryEntity>()
                .SetColumns(x => new EmailPushDeliveryEntity
                {
                    Status = status, ErrorMessage = result.Error, SentTime = sent, LeaseExpireTime = null,
                    NextAttemptTime = retryAt, UpdateTime = completed
                }).Where(x => x.Id == delivery.Id && x.Status == "Processing" && x.LeaseExpireTime == leaseUntil)
                .ExecuteCommandAsync();
        }
        finally
        {
            await ReleaseLeaseAsync(owner, attempted);
        }
    }

    private async Task ReleaseLeaseAsync(string owner, bool attempted)
    {
        var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        DateTime? next = attempted ? DateTime.Now.AddSeconds(setting.IntervalSeconds) : setting.NextSendTime;
        await Db.Updateable<EmailPushSettingEntity>()
            .SetColumns(x => new EmailPushSettingEntity { LeaseOwner = null, LeaseExpireTime = null, NextSendTime = next })
            .Where(x => x.Id == 1 && x.LeaseOwner == owner).ExecuteCommandAsync();
    }
}
