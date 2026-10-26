using Model.EmailPush;
using Model.ExternalTools;
using Model.WeChatClawBot;

namespace Service;

public sealed partial class WeChatPushQueueService
{
    public async Task<object> EnqueueByMcpAsync(ExternalToolCallerContext caller, WeChatPushEnqueueDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "clawbot.write");
        return await EnqueueCoreAsync(owner, McpPushAccess.Application, dto);
    }

    public async Task<EmailPage<WeChatPushQueueRecordDto>> GetMcpRecordsAsync(ExternalToolCallerContext caller, WeChatPushQueueQuery query)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "clawbot.read");
        return await QueryRecordsAsync(owner, McpPushAccess.Application, query);
    }

    public async Task RetryByMcpAsync(ExternalToolCallerContext caller, long? taskId, long? deliveryId, bool confirm)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var owner = McpPushAccess.RequireOwner(caller, "clawbot.write");
        if (!confirm) throw new ArgumentException("重试微信必须明确确认");
        if (taskId.HasValue == deliveryId.HasValue || taskId is <= 0 || deliveryId is <= 0)
            throw new ArgumentException("请提供taskId或deliveryId中的一个有效编号，不可同时提供");
        var records = await QueryRecordsAsync(owner, McpPushAccess.Application,
            new WeChatPushQueueQuery { TaskId = taskId, DeliveryId = deliveryId, Size = 1 });
        var row = records.Records.SingleOrDefault();
        if (row?.Status != "Failed")
            throw new InvalidOperationException("仅支持当前MCP来源队列中明确失败的任务；Unknown或旧版未入队记录不能直接重试");
        var changed = await Db.Updateable<WeChatPushQueueEntity>()
            .SetColumns(x => new WeChatPushQueueEntity { Status = "Pending", Error = null, UpdateTime = DateTime.Now })
            .Where(x => x.Id == row.Id && x.CreatedBy == owner && x.AppCode == McpPushAccess.Application && x.Status == "Failed")
            .ExecuteCommandAsync();
        if (changed != 1) throw new InvalidOperationException("任务状态已变化，请查询后重试");
    }
}
