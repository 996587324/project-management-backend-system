using System.Text.Json;
using Model.SystemApplications;
using Model.WorkTasks;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>仅接受落库的业务投递编号；不能由调用方指定正文、接收人或冒用网页登录身份。</summary>
public static class WorkTaskChannelPolicy
{
    public const string Prefix="work:";
    public static async Task<(WorkDelivery Delivery, string Body)> ResolveAsync(ISqlSugarClient db, string id, string channel)
    {
        var row=await db.Queryable<WorkDelivery>().FirstAsync(x => x.Id == id && x.Channel == channel)
            ?? throw new UnauthorizedAccessException("工作任务通知来源不存在");
        if (!await db.Queryable<SysUserInfo>().AnyAsync(x => x.UserId == row.RecipientId && x.Enabled) ||
            !await db.Queryable<SysUserApplicationEntity>().AnyAsync(x => x.AppCode == row.AppCode && x.UserId == row.RecipientId && x.Enabled))
            throw new UnauthorizedAccessException("通知接收账号或应用准入已停用");
        var rule=await db.Queryable<WorkNotificationRule>().FirstAsync(x => x.AppCode == row.AppCode);
        if (rule == null || !rule.Channels.Split(',',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(channel)) throw new UnauthorizedAccessException("工作任务通知通道已关闭");
        var actor=new WorkActor(new ApplicationAccessContext { UserId=row.RecipientId, AppCode=row.AppCode });
        var item=await db.Queryable<WorkEvent>().FirstAsync(x => x.Id == row.OriginId && x.AppCode == row.AppCode);
        if (item != null)
        {
            var snapshot=JsonSerializer.Deserialize<WorkTask>(item.SnapshotJson,WorkTaskService.Json);
            var current=await db.Queryable<WorkTask>().FirstAsync(x => x.Id == item.TaskId && x.AppCode == row.AppCode);
            var participant = current != null && (current.AssigneeId == row.RecipientId || await db.Queryable<WorkTaskParticipant>().AnyAsync(x=>x.AppCode==row.AppCode && x.TaskId==current.Id && x.UserId==row.RecipientId && x.Enabled && x.CreatedAtUtc<=item.CreatedAtUtc));
            if (item.Action != "dispatch" || !item.PushRequested || row.Perspective != "Employee" || snapshot == null || current == null || snapshot.AssigneeId != current.AssigneeId || !participant || current.Status == "Cancelled" || !await WorkTaskService.CanRead(db,actor,current))
                throw new UnauthorizedAccessException("任务接收人或权限已变化，不能发送下派提醒");
            return (row,"工作任务下派提醒\n你有一项新下派任务，请登录系统查看任务要求并进行评估。");
        }
        var digest=await db.Queryable<WorkDigest>().FirstAsync(x => x.Id == row.OriginId && x.RecipientId == row.RecipientId && x.AppCode == row.AppCode)
            ?? throw new UnauthorizedAccessException("工作任务摘要来源不匹配");
        if (!rule.Enabled) throw new UnauthorizedAccessException("工作任务日终通知已关闭");
        var snapshots=JsonSerializer.Deserialize<List<WorkTask>>(digest.SnapshotJson,WorkTaskService.Json) ?? [];
        foreach (var snapshot in snapshots)
        {
            var current=await db.Queryable<WorkTask>().FirstAsync(x => x.Id == snapshot.Id && x.AppCode == row.AppCode);
            if (current == null || !await WorkTaskService.CanRead(db, actor, current) || (row.Perspective == "Employee" && current.AssigneeId != row.RecipientId && !await db.Queryable<WorkTaskParticipant>().AnyAsync(x=>x.AppCode==row.AppCode && x.TaskId==current.Id && x.UserId==row.RecipientId && x.Enabled))) throw new UnauthorizedAccessException("收件人任务范围已变化，旧摘要投递已停止");
        }
        // 外部通道只发送状态数量，任务标题、成果和详细材料留在受控站内摘要。
        var perspective=row.Perspective == "Leader" ? "团队工作日终状态" : "个人工作日终状态";
        var body=$"{digest.BusinessDate} {perspective}\n待评估：{snapshots.Count(x => x.Status == "PendingAssessment")}；执行中：{snapshots.Count(x => x.Status == "InProgress")}；待验收：{snapshots.Count(x => x.Status == "PendingReview")}；已完成：{snapshots.Count(x => x.Status == "Completed")}；阻塞：{snapshots.Count(x => x.Blocked)}。\n请登录系统，在“工作任务”中查看本人有权限的任务、节点与日终详情。";
        return (row,body);
    }
    public static async Task<bool> AuthorizeAsync(ISqlSugarClient db, string owner, string app, string user, string channel)
    {
        if (!owner.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var (row,_)=await ResolveAsync(db,owner[Prefix.Length..],channel);
            return row.AppCode == app && row.RecipientId == user;
        }
        catch (UnauthorizedAccessException) { return false; }
    }
}
