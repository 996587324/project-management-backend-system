using Model.WeChatClawBot;
using Model.User;
using SqlSugar;

namespace Service;

public sealed partial class WeChatPushQueueService
{
    /// <summary>全局租约控制多实例间隔；外部发送不在数据库事务内，不自动重试不确定结果。</summary>
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        if (!initializer.IsReady) return;
        using var operation = DatabaseOperationScope.Begin(scope);
        var now = DateTime.Now;
        var owner = Guid.NewGuid().ToString("N");
        var lease = now.AddMinutes(10);
        var acquired = await Db.Updateable<WeChatPushQueueSettingEntity>()
            .SetColumns(x => new WeChatPushQueueSettingEntity { LeaseOwner = owner, LeaseExpireTime = lease })
            .Where(x => x.Id == 1 && (x.LeaseExpireTime == null || x.LeaseExpireTime < now) &&
                (x.NextSendTime == null || x.NextSendTime <= now)).ExecuteCommandAsync();
        if (acquired != 1) return;
        var attempted = false;
        try
        {
            await Db.Updateable<WeChatPushQueueEntity>()
                .SetColumns(x => new WeChatPushQueueEntity
                {
                    Status = "Unknown", Error = "发送租约已过期，可能已发送，请人工核实；不会自动重发", UpdateTime = now
                })
                .Where(x => x.Status == "Sending" && x.LeaseExpireTime < now).ExecuteCommandAsync();
            var job = await Db.Queryable<WeChatPushQueueEntity>().Where(x => x.Status == "Pending")
                .OrderBy(x => x.Id).FirstAsync();
            if (job == null) return;
            cancellationToken.ThrowIfCancellationRequested();
            var changed = await Db.Updateable<WeChatPushQueueEntity>()
                .SetColumns(x => new WeChatPushQueueEntity
                {
                    Status = "Sending", Attempt = job.Attempt + 1, LeaseExpireTime = lease, UpdateTime = now
                }).Where(x => x.Id == job.Id && x.Status == "Pending").ExecuteCommandAsync();
            if (changed != 1) return;
            attempted = true;
            job.Attempt++;
            job.Status = "Sending";
            try
            {
                var external = McpPushAccess.IsExternal(job.AppCode, job.CreatedBy);
                if (!external)
                {
                    var access = await applications.GetChannelAccessAsync(job.CreatedBy, job.AppCode);
                    await RequireSenderAsync(access);
                    if (access.UserId != job.CreatedBy || access.AppCode != job.AppCode)
                        throw new UnauthorizedAccessException("发起身份或系统不匹配");
                }
                if (!await Db.Queryable<SysUserInfo>().AnyAsync(x => x.UserId == job.RecipientUserId && x.Enabled))
                    throw new UnauthorizedAccessException("接收账号已停用或不存在");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var delivery = await weChat.SendQueuedMessageAsync(job.RecipientUserId, job.Content,
                    $"push:{job.Id}:{job.Attempt}", job.Id.ToString(), job.BindingIdentity, timeout.Token,
                    external ? "MCP" : "AgentPush");
                job.DeliveryId = delivery.Id;
                job.Status = delivery.Status is "Sent" or "Failed" ? delivery.Status : "Unknown";
                job.Error = delivery.ErrorMessage is { Length: > 1000 } error ? error[..1000] : delivery.ErrorMessage;
                job.SentTime = delivery.SentTime;
            }
            catch (Exception exception)
            {
                job.Status = exception is UnauthorizedAccessException or ArgumentException or InvalidOperationException
                    ? "Cancelled" : "Unknown";
                job.Error = job.Status == "Cancelled" ? "授权或发送条件已变更，本次任务取消，请重新发起" :
                    "未能确认发送结果，请人工核实，不自动重发";
            }
            job.LeaseExpireTime = null;
            job.UpdateTime = DateTime.Now;
            await Db.Updateable(job).Where(x => x.Id == job.Id && x.Status == "Sending").ExecuteCommandAsync();
        }
        finally
        {
            var setting = await Db.Queryable<WeChatPushQueueSettingEntity>().InSingleAsync(1);
            var next = attempted ? DateTime.Now.AddSeconds(setting.IntervalSeconds) : setting.NextSendTime;
            await Db.Updateable<WeChatPushQueueSettingEntity>()
                .SetColumns(x => new WeChatPushQueueSettingEntity { LeaseOwner = null, LeaseExpireTime = null, NextSendTime = next })
                .Where(x => x.Id == 1 && x.LeaseOwner == owner).ExecuteCommandAsync();
        }
    }
}
