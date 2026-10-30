using Model.WeChatClawBot;

namespace Service;

public sealed partial class WeChatPushQueueService
{
    public async Task<object> EnqueueWorkTaskAsync(string deliveryId)
    {
        using var operation=DatabaseOperationScope.Begin(scope);
        var (row,body)=await WorkTaskChannelPolicy.ResolveAsync(Db,deliveryId,"WeChat");
        await applications.GetChannelAccessAsync(row.RecipientId,row.AppCode);
        return await EnqueueCoreAsync(WorkTaskChannelPolicy.Prefix + deliveryId,row.AppCode,new WeChatPushEnqueueDto
        { RecipientUserIds=[row.RecipientId], Content=body, IdempotencyKey=deliveryId, Confirm=true, SourceLabel="WorkTask", ExternalTaskId=deliveryId });
    }
}
