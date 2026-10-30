using Model.User;
using Model.WorkTasks;
using SqlSugar;

namespace Service;

public sealed partial class WorkTaskService
{
    public async Task<object> OverviewAsync(WorkActor actor, WorkOverviewQuery query)
    {
        Ready(); Scope(actor, "read");
        if (actor.Personal) Scope(actor, "team_read");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
        var candidates = await db.Queryable<WorkTask>().Where(x => x.AppCode == actor.AppCode && x.Status != "Draft" && x.RevisionId != "").OrderByDescending(x => x.UpdatedAtUtc).ToListAsync();
        var allowed = new List<WorkTask>();
        foreach (var task in candidates) if (await CanRead(db, actor, task)) allowed.Add(task);
        var taskIds = allowed.Select(x=>x.Id).ToArray();
        var participants = await db.Queryable<WorkTaskParticipant>().Where(x=>x.AppCode==actor.AppCode && x.Enabled && taskIds.Contains(x.TaskId)).ToListAsync();

        // 只暴露任务范围内的组织与人员；已授权的空组也允许用于导航。
        var groupIds = new HashSet<string>(allowed.Select(x => x.GroupId));
        var grants = await Grants(db, actor);
        foreach (var group in groups)
            if (await Has(db, actor, ["dispatcher", "supervisor"], group.Id, "")) groupIds.Add(group.Id);
        var memberships = await db.Queryable<WorkMember>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var memberPairs = allowed.Select(x => (x.GroupId, UserId: x.AssigneeId)).ToHashSet();
        foreach (var p in participants) { var task=allowed.First(x=>x.Id==p.TaskId); memberPairs.Add((task.GroupId,p.UserId)); }
        foreach (var member in memberships)
            if (member.UserId == actor.UserId && grants.Any(x => x.Role == "worker") || await Has(db, actor, ["dispatcher", "supervisor"], member.GroupId, member.UserId))
            {
                groupIds.Add(member.GroupId);
                memberPairs.Add((member.GroupId, member.UserId));
            }
        foreach (var id in groupIds.ToArray())
        {
            var seen = new HashSet<string>();
            for (var current = id; !string.IsNullOrEmpty(current) && seen.Add(current); current = groups.FirstOrDefault(x => x.Id == current)?.ParentId ?? "") groupIds.Add(current);
        }
        var visibleGroups = groups.Where(x => groupIds.Contains(x.Id)).ToList();
        var userIds = memberPairs.Select(x => x.UserId).Distinct().ToArray();
        var users = await db.Queryable<SysUserInfo>().Where(x => userIds.Contains(x.UserId)).Select(x => new { x.UserId, Name = x.NickName ?? x.UserName }).ToListAsync();
        var members = memberPairs.Select(x => new { x.GroupId, x.UserId, Name = users.FirstOrDefault(u => u.UserId == x.UserId)?.Name ?? x.UserId }).ToArray();
        bool InGroup(string groupId, string parentId)
        {
            var seen = new HashSet<string>();
            for (var current = groupId; !string.IsNullOrEmpty(current) && seen.Add(current); current = groups.FirstOrDefault(x => x.Id == current)?.ParentId ?? "")
                if (current == parentId) return true;
            return false;
        }
        var keyword = Text(query.Keyword, 100, "搜索内容");
        var filtered = allowed.Where(x => (string.IsNullOrEmpty(query.GroupId) || groupIds.Contains(query.GroupId) && InGroup(x.GroupId, query.GroupId))
            && (string.IsNullOrEmpty(query.UserId) || x.AssigneeId == query.UserId || participants.Any(p=>p.TaskId==x.Id && p.UserId==query.UserId))
            && (keyword.Length == 0 || x.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToList();
        var now = DateTime.UtcNow;
        var counts = new { Total=filtered.Count, PendingAssessment=filtered.Count(x => x.Status == "PendingAssessment"), InProgress=filtered.Count(x => x.Status == "InProgress"), PendingReview=filtered.Count(x => x.Status == "PendingReview"), Completed=filtered.Count(x => x.Status == "Completed"), Blocked=filtered.Count(x => x.Blocked), Overdue=filtered.Count(x => x.Status is "PendingAssessment" or "InProgress" && x.CommittedDueAtUtc < now) };
        if (!string.IsNullOrEmpty(query.Status)) filtered = filtered.Where(x => x.Status == query.Status).ToList();
        var page = Math.Max(1, query.Page); var size = Math.Clamp(query.Size, 1, 100);
        return new { Groups=visibleGroups.Select(x => new { x.Id, x.Name, x.ParentId }), Members=members, Counts=counts, Total=filtered.Count, Page=page,
            Records=filtered.Skip((page-1)*size).Take(size).Select(x => new { x.Id, x.Title, x.Status, x.GroupId, x.AssigneeId, x.Priority, x.Progress, x.Blocked, x.CommittedDueAtUtc, x.UpdatedAtUtc, x.Version }) };
    }
}
