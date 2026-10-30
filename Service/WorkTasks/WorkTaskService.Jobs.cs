using System.Data;
using System.Text.Json;
using Model.WorkTasks;
using Model.SystemApplications;
using SqlSugar;

namespace Service;

public sealed partial class WorkTaskService
{
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        Ready(); cancellationToken.ThrowIfCancellationRequested();
        using var db=scope.GetConnection("calmadmin").CopyNew();
        // SERIALIZABLE 保证日报基础数据的一致读取；网络投递不进入此事务。
        db.Ado.BeginTran(IsolationLevel.Serializable);
        try
        {
            await LockAsync(db, "work-task-jobs-v1");
            var events=await db.Queryable<WorkEvent>().Where(x => !x.Processed).OrderBy(x => x.CreatedAtUtc).Take(100).ToListAsync();
            foreach (var item in events)
            {
                var task=await db.Queryable<WorkTask>().FirstAsync(x => x.Id == item.TaskId && x.AppCode == item.AppCode);
                if (task != null && task.Status != "Draft")
                {
                    var collaborators=await db.Queryable<WorkTaskParticipant>().Where(x=>x.AppCode==task.AppCode && x.TaskId==task.Id && x.Enabled && x.CreatedAtUtc<=item.CreatedAtUtc).Select(x=>x.UserId).ToListAsync();
                    foreach (var (recipient, perspective) in new[] { (task.AssigneeId,"Employee"), (task.SummaryLeaderId,"Leader"), (task.ReviewerId,"Leader") }.Concat(collaborators.Select(x=>(x,"Employee"))).Distinct())
                    {
                        if (string.IsNullOrEmpty(recipient)) continue;
                        var actor=new WorkActor(new ApplicationAccessContext { UserId=recipient, AppCode=task.AppCode });
                        if (!await CanRead(db, actor, task)) continue;
                        await AddDelivery(db, item.AppCode, item.Id, recipient, perspective, "Inbox");
                        var snapshot= item.Action == "dispatch" ? JsonSerializer.Deserialize<WorkTask>(item.SnapshotJson,Json) : null;
                        if (item.Action == "dispatch" && item.PushRequested && perspective == "Employee" && snapshot?.AssigneeId == task.AssigneeId)
                        {
                            var rule = await db.Queryable<WorkNotificationRule>().FirstAsync(x => x.AppCode == item.AppCode);
                            foreach (var channel in (rule?.Channels ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(x => x is "Email" or "WeChat").Distinct())
                                await AddDelivery(db, item.AppCode, item.Id, recipient, perspective, channel);
                        }
                    }
                }
                item.Processed=true; await db.Updateable(item).ExecuteCommandAsync();
            }
            var now=DateTime.UtcNow;
            foreach (var rule in await db.Queryable<WorkNotificationRule>().Where(x => x.Enabled).ToListAsync())
            {
                var zone=TimeZoneInfo.FindSystemTimeZoneById(rule.TimeZone);
                var local=TimeZoneInfo.ConvertTimeFromUtc(now, zone);
                var date=local.ToString("yyyy-MM-dd");
                if(rule.ExcludedDates.Split(',',StringSplitOptions.TrimEntries).Contains(date))continue;
                if (!rule.IncludedDates.Split(',',StringSplitOptions.TrimEntries).Contains(date)&&!rule.WeekDays.Split(',').Contains(((int)local.DayOfWeek).ToString())) continue;
                var time=TimeOnly.ParseExact(rule.DailyTime,"HH:mm");
                var slot=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date + time.ToTimeSpan(), DateTimeKind.Unspecified), zone);
                if (now < slot || now > slot.AddMinutes(rule.CatchUpMinutes)) continue;
                // 同一应用/业务日已有快照即复用，普通重启/补发不重新计算。
                if (await db.Queryable<WorkDigest>().AnyAsync(x => x.AppCode == rule.AppCode && x.BusinessDate == date)) continue;
                var start=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), zone);
                var tasks=await db.Queryable<WorkTask>().Where(x => x.AppCode == rule.AppCode && x.Status != "Draft").ToListAsync();
                var relevant=tasks.Where(x => x.Status is not ("Completed" or "Cancelled") || x.UpdatedAtUtc >= start).ToList();
                var grants=await db.Queryable<WorkGrant>().Where(x => x.AppCode == rule.AppCode && x.Enabled && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)).ToListAsync();
                foreach (var user in grants.Select(x => x.UserId).Distinct())
                {
                    try { await applications.GetChannelAccessAsync(user, rule.AppCode); } catch (UnauthorizedAccessException) { continue; }
                    var actor=new WorkActor(new ApplicationAccessContext { UserId=user, AppCode=rule.AppCode });
                    var participation=await db.Queryable<WorkTaskParticipant>().Where(x=>x.AppCode==rule.AppCode && x.UserId==user && x.Enabled).Select(x=>x.TaskId).ToListAsync();
                    var mine=relevant.Where(x => (x.AssigneeId == user || participation.Contains(x.Id)) && (rule.EmployeeAudience != "CompletedToday" || x.CompletedAtUtc >= start)).ToList();
                    var own=new List<WorkTask>();
                    foreach (var task in mine) if (await CanRead(db, actor, task)) own.Add(task);
                    if (own.Count > 0) await Digest(db, rule, user, "Employee", date, now, own);
                    if (!grants.Any(x => x.UserId == user && x.Role is "dispatcher" or "supervisor")) continue;
                    var team=new List<WorkTask>();
                    foreach (var task in relevant)
                        if (await Has(db, actor, ["dispatcher","supervisor"], task.GroupId, task.AssigneeId)) team.Add(task);
                    if (team.Count > 0) await Digest(db, rule, user, "Leader", date, now, team);
                }
            }
            await db.Ado.CommitTranAsync();
        }
        catch { await db.Ado.RollbackTranAsync(); throw; }
        await DispatchWorkNotificationsAsync(cancellationToken);
        var unlinkedBefore=DateTime.UtcNow.AddHours(-Math.Clamp(options.Value.UnlinkedFileHours,1,168));
        var candidates=await db.Queryable<WorkAttachment>().Where(x=>x.Status=="Ready"&&x.CreatedAtUtc<unlinkedBefore&&
            !SqlFunc.Subqueryable<WorkAttachmentLink>().Where(link=>link.AttachmentId==x.Id).Any()).Take(100).ToListAsync();
        foreach(var file in candidates)
        {
            var cleanup=await db.Ado.UseTranAsync(async()=>
            {
                await LockAsync(db,"work-file:"+file.Id);
                if(await db.Queryable<WorkAttachmentLink>().AnyAsync(x=>x.AttachmentId==file.Id))return;
                await db.Updateable<WorkAttachment>().SetColumns(x=>new WorkAttachment{Status="Expired",TicketHash=""}).Where(x=>x.Id==file.Id&&x.Status=="Ready").ExecuteCommandAsync();
            });
            if(!cleanup.IsSuccess)throw cleanup.ErrorException!;
        }
        // 临时文件只清理未归档且已过期的上传；已Ready成果不自动删除。
        var stale=await db.Queryable<WorkAttachment>().Where(x => x.Status != "Ready" && x.ExpiresAtUtc < DateTime.UtcNow).Take(50).ToListAsync();
        foreach (var file in stale)
        {
            try { var path=FilePath(file.StorageKey); if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { continue; }
            await db.Deleteable<WorkAttachment>().Where(x => x.Id == file.Id && x.Status != "Ready").ExecuteCommandAsync();
        }
    }
    private static async Task AddDelivery(ISqlSugarClient db, string app, string origin, string user, string perspective, string channel)
    {
        var id=Hash($"{app}:{origin}:{user}:{perspective}:{channel}")[..32];
        if (!await db.Queryable<WorkDelivery>().AnyAsync(x => x.Id == id))
            await db.Insertable(new WorkDelivery { Id=id, AppCode=app, OriginId=origin, RecipientId=user, Perspective=perspective, Channel=channel, Status=channel == "Inbox" ? "Available" : "Pending" }).ExecuteCommandAsync();
    }
    private static async Task Digest(ISqlSugarClient db, WorkNotificationRule rule, string user, string perspective, string date, DateTime now, List<WorkTask> tasks)
    {
        var row=new WorkDigest { AppCode=rule.AppCode, RecipientId=user, Perspective=perspective, BusinessDate=date,
            CutoffUtc=now, SnapshotJson=JsonSerializer.Serialize(tasks, Json) };
        await db.Insertable(row).ExecuteCommandAsync();
        foreach (var channel in rule.Channels.Split(',').Append("Inbox").Distinct())
            await AddDelivery(db, rule.AppCode, row.Id, user, perspective, channel);
    }
    public async Task<object> InboxAsync(WorkActor actor)
    {
        Ready(); Scope(actor,"read"); using var db=scope.GetConnection("calmadmin").CopyNew();
        var rows=await db.Queryable<WorkDelivery>().Where(x => x.AppCode == actor.AppCode && x.RecipientId == actor.UserId && x.Channel == "Inbox")
            .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync();
        var result=new List<object>();
        foreach (var row in rows)
        {
            if (actor.Personal && row.Perspective != "Employee") continue;
            var digest=await db.Queryable<WorkDigest>().FirstAsync(x => x.Id == row.OriginId && x.AppCode == actor.AppCode);
            if (digest != null)
            {
                var tasks=JsonSerializer.Deserialize<List<WorkTask>>(digest.SnapshotJson,Json) ?? [];
                var visible=new List<WorkTask>();
                foreach (var snapshot in tasks)
                {
                    var current=await db.Queryable<WorkTask>().FirstAsync(x => x.Id == snapshot.Id && x.AppCode == actor.AppCode);
                    if (current != null && await CanRead(db, actor,current) && (row.Perspective != "Employee" || await IsWorker(db,actor,current))) visible.Add(snapshot);
                }
                if (visible.Count > 0) result.Add(new { row.Id, row.Perspective, row.ReadAtUtc, row.CreatedAtUtc, Kind="Digest", digest.BusinessDate, digest.CutoffUtc, Tasks=visible });
            }
            else
            {
                var item=await db.Queryable<WorkEvent>().FirstAsync(x => x.Id == row.OriginId && x.AppCode == actor.AppCode);
                if (item == null) continue;
                var task=await db.Queryable<WorkTask>().FirstAsync(x => x.Id == item.TaskId && x.AppCode == actor.AppCode);
                if (task != null && await CanRead(db, actor, task)) result.Add(new { row.Id, row.Perspective, row.ReadAtUtc, row.CreatedAtUtc, Kind="Event", item.Action, Task=task });
            }
        }
        return result;
    }
    public async Task MarkReadAsync(WorkActor actor, string id)
    {
        Ready(); Scope(actor,"read"); using var db=scope.GetConnection("calmadmin").CopyNew();
        await db.Updateable<WorkDelivery>().SetColumns(x => new WorkDelivery { ReadAtUtc=DateTime.UtcNow })
            .Where(x => x.Id == id && x.AppCode == actor.AppCode && x.RecipientId == actor.UserId && x.Channel == "Inbox").ExecuteCommandAsync();
    }
}
