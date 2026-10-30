using Microsoft.Extensions.DependencyInjection;
using Model.EmailPush;
using Model.WeChatClawBot;
using Model.WorkTasks;

namespace Service;

public sealed partial class WorkTaskService
{
    private async Task DispatchWorkNotificationsAsync(CancellationToken cancellationToken)
    {
        using var db=scope.GetConnection("calmadmin").CopyNew();
        // 通道只接受可信业务编号。先入现有队列；结果同步不触发二次网络发送。
        foreach (var row in await db.Queryable<WorkDelivery>().Where(x => x.Channel != "Inbox" && x.Status == "Pending").Take(20).ToListAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var enqueue=await db.Ado.UseTranAsync(async () =>
            {
            await LockAsync(db,"work-delivery:"+row.Id);
            if(!await db.Queryable<WorkDelivery>().AnyAsync(x=>x.Id==row.Id&&x.Status=="Pending"))return;
            try
            {
                await WorkTaskChannelPolicy.ResolveAsync(db,row.Id,row.Channel);
                var owner=WorkTaskChannelPolicy.Prefix + row.Id;
                var existing=row.Channel == "Email"
                    ? db.DbMaintenance.IsAnyTable("EmailPushTask",false) && await db.Queryable<EmailPushTaskEntity>().AnyAsync(x => x.CreatedBy == owner && x.Source == "WorkTask")
                    : db.DbMaintenance.IsAnyTable("WeChatPushQueue",false) && await db.Queryable<WeChatPushQueueEntity>().AnyAsync(x => x.CreatedBy == owner && x.SourceLabel == "WorkTask");
                if (!existing)
                {
                    // 由独立适配器解析可信事件，不能用平台管理员的权限代替业务身份。
                    using var operation=DatabaseOperationScope.Begin(scope);
                    if (row.Channel == "Email")
                    {
                        if (ChannelServices == null) throw new InvalidOperationException("邮件通道服务未配置");
                        await ChannelServices.GetRequiredService<Interface.IEmailPushService>().EnqueueWorkTaskAsync(row.Id);
                    }
                    else
                    {
                        if (ChannelServices == null) throw new InvalidOperationException("微信通道服务未配置");
                        await ChannelServices.GetRequiredService<Interface.IWeChatPushQueueService>().EnqueueWorkTaskAsync(row.Id);
                    }
                }
                await db.Updateable<WorkDelivery>().SetColumns(x => new WorkDelivery { Status="Queued", Error=null }).Where(x => x.Id == row.Id && x.Status == "Pending").ExecuteCommandAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error=exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
                await db.Updateable<WorkDelivery>().SetColumns(x => new WorkDelivery { Status="Failed", Error=error }).Where(x => x.Id == row.Id).ExecuteCommandAsync();
            }
            });
            if(!enqueue.IsSuccess)throw enqueue.ErrorException!;
        }
        foreach (var row in await db.Queryable<WorkDelivery>().Where(x => x.Status == "Queued").Take(50).ToListAsync())
        {
            var sync=await db.Ado.UseTranAsync(async()=>
            {
            await LockAsync(db,"work-delivery:"+row.Id);
            if(!await db.Queryable<WorkDelivery>().AnyAsync(x=>x.Id==row.Id&&x.Status=="Queued"))return;
            var owner=WorkTaskChannelPolicy.Prefix + row.Id;
            if (row.Channel == "Email")
            {
                var task=await db.Queryable<EmailPushTaskEntity>().FirstAsync(x => x.CreatedBy == owner && x.Source == "WorkTask");
                if (task == null) return;
                var delivery=await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x => x.TaskId == task.Id);
                if (delivery == null) return;
                row.ChannelReference=delivery.Id.ToString(); row.Error=delivery.ErrorMessage;
                if (delivery.Status is "Sent" or "Failed" or "Unknown" or "Cancelled") row.Status=delivery.Status;
            }
            else
            {
                var delivery=await db.Queryable<WeChatPushQueueEntity>().FirstAsync(x => x.CreatedBy == owner && x.SourceLabel == "WorkTask");
                if (delivery == null) return;
                row.ChannelReference=delivery.Id.ToString(); row.Error=delivery.Error;
                if (delivery.Status is "Sent" or "Failed" or "Unknown" or "Cancelled") row.Status=delivery.Status;
            }
            if(row.Error?.Length>500)row.Error=row.Error[..500];
            await db.Updateable(row).ExecuteCommandAsync();
            });
            if(!sync.IsSuccess)throw sync.ErrorException!;
        }
    }
    public async Task RetryDeliveryAsync(WorkActor actor,string id)
    {
        Ready();using var db=scope.GetConnection("calmadmin").CopyNew();await Admin(db,actor);
        var tx=await db.Ado.UseTranAsync(async()=>
        {
            await LockAsync(db,"work-delivery:"+id);
            var row=await db.Queryable<WorkDelivery>().FirstAsync(x=>x.Id==id&&x.AppCode==actor.AppCode)
                ??throw new UnauthorizedAccessException("投递记录不存在或无权操作");
            if(row.Status!="Failed")throw new WorkConflictException("仅可重试明确失败的投递；结果待核实不能自动重发");
            await WorkTaskChannelPolicy.ResolveAsync(db,id,row.Channel);
            var owner=WorkTaskChannelPolicy.Prefix+id;
            var queued=false;
            if(row.Channel=="Email"&&db.DbMaintenance.IsAnyTable("EmailPushTask",false))
            {
                var task=await db.Queryable<EmailPushTaskEntity>().FirstAsync(x=>x.CreatedBy==owner&&x.Source=="WorkTask");
                if(task!=null)
                {
                    var item=await db.Queryable<EmailPushDeliveryEntity>().FirstAsync(x=>x.TaskId==task.Id);
                    if(item?.Status=="Unknown")throw new WorkConflictException("邮件发送结果待核实，请先核实");
                    if(item?.Status=="Failed")await db.Updateable<EmailPushDeliveryEntity>()
                        .SetColumns(x=>new EmailPushDeliveryEntity{Status="Pending",AttemptCount=0,NextAttemptTime=DateTime.Now,LeaseExpireTime=null,ErrorMessage=null})
                        .Where(x=>x.Id==item.Id&&x.Status=="Failed").ExecuteCommandAsync();
                    queued=true;
                }
            }
            else if(row.Channel=="WeChat"&&db.DbMaintenance.IsAnyTable("WeChatPushQueue",false))
            {
                var item=await db.Queryable<WeChatPushQueueEntity>().FirstAsync(x=>x.CreatedBy==owner&&x.SourceLabel=="WorkTask");
                if(item?.Status=="Unknown")throw new WorkConflictException("微信发送结果待核实，请先核实");
                if(item!=null)
                {
                    if(item.Status=="Failed")await db.Updateable<WeChatPushQueueEntity>().SetColumns(x=>new WeChatPushQueueEntity{Status="Pending",Error=null,UpdateTime=DateTime.Now})
                        .Where(x=>x.Id==item.Id&&x.Status=="Failed").ExecuteCommandAsync();
                    queued=true;
                }
            }
            row.Status=queued?"Queued":"Pending";row.Error=null;
            await db.Updateable(row).ExecuteCommandAsync();await Audit(db,actor,"","notification.retry",id);
        });
        if(!tx.IsSuccess)throw tx.ErrorException!;
    }
}
