using Model.EmailPush;

namespace Service;

public sealed partial class EmailPushService
{
    public async Task<EmailEnqueueResult> EnqueueWorkTaskAsync(string deliveryId)
    {
        using var operation=DatabaseOperationScope.Begin(scope);
        var (row,body)=await WorkTaskChannelPolicy.ResolveAsync(Db,deliveryId,"Email");
        if (applications == null) throw new InvalidOperationException("工作任务消息授权服务不可用");
        await applications.GetChannelAccessAsync(row.RecipientId,row.AppCode);
        return await EnqueueCoreAsync(WorkTaskChannelPolicy.Prefix + deliveryId,row.AppCode,new EmailEnqueueDto
        { RecipientUserIds=[row.RecipientId], Subject=body.StartsWith("工作任务下派提醒", StringComparison.Ordinal) ? "工作任务下派提醒" : "工作任务日终状态", Body=body, IdempotencyKey=deliveryId, Confirm=true },"WorkTask");
    }
}
