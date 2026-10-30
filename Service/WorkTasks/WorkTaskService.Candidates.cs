using Model.SystemApplications;
using Model.User;
using Model.WorkTasks;

namespace Service;

public sealed partial class WorkTaskService
{
    private sealed record Candidate(string Value, string Label, string Description, string GroupLabel, bool Disabled, string Reason);

    public async Task<object> DispatchGroupsAsync(WorkActor actor)
    {
        Ready(); Scope(actor, "dispatch");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var members = await db.Queryable<WorkMember>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var visible = new List<object>();
        foreach (var group in groups)
        {
            var allowed = await Has(db, actor, ["dispatcher"], group.Id, "");
            foreach (var member in members.Where(x => x.GroupId == group.Id))
                if (!allowed && await Has(db, actor, ["dispatcher"], group.Id, member.UserId)) allowed = true;
            if (allowed) visible.Add(new { group.Id, group.Name, group.ParentId });
        }
        return visible;
    }

    public async Task<object> CandidatesAsync(WorkActor actor, WorkCandidateQuery query)
    {
        Ready(); Scope(actor, "read");
        if (actor.Personal)
        {
            Scope(actor, "dispatch");
            if (query.Purpose is not ("assignee" or "reviewer" or "leader") || !string.IsNullOrWhiteSpace(query.TaskId)) throw new UnauthorizedAccessException("个人凭证只能查询下派候选人员");
        }
        if (query.Purpose is not ("assignee" or "reviewer" or "leader" or "dispatcher" or "admin-user"))
            throw new ArgumentException("无效人员选择用途");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var own = await Grants(db, actor);
        var isAdmin = actor.Access.PlatformAdministrator || own.Any(x => x.Role == "work-admin");
        var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
        var members = await db.Queryable<WorkMember>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        WorkTask? task = null;
        if (query.Purpose == "admin-user")
        {
            if (!isAdmin) throw new UnauthorizedAccessException("没有人员配置权限");
        }
        else if (!string.IsNullOrWhiteSpace(query.TaskId))
        {
            task = await db.Queryable<WorkTask>().FirstAsync(x => x.Id == query.TaskId && x.AppCode == actor.AppCode)
                ?? throw new UnauthorizedAccessException("任务不存在或无权办理");
            if (query.Purpose == "assignee") await Dispatcher(db, actor, task);
            else if ((task.DispatcherId == actor.UserId || task.SummaryLeaderId == actor.UserId)
                && await Has(db, actor, ["dispatcher"], task.GroupId, task.AssigneeId)) { }
            else await Admin(db, actor, task.GroupId, task.AssigneeId);
            query.GroupId = task.GroupId;
            // 转派时可预览新员工对应的验收/汇总候选；最终保存仍由领域服务校验。
            query.AssigneeId = task.AssigneeId;
        }
        else
        {
            var allowed = await Has(db, actor, ["dispatcher"], query.GroupId, query.AssigneeId);
            foreach (var member in members.Where(x => x.GroupId == query.GroupId))
                if (!allowed && await Has(db, actor, ["dispatcher"], member.GroupId, member.UserId)) allowed = true;
            if (!allowed) throw new UnauthorizedAccessException("请先选择有下派权限的工作组");
        }

        if (query.Purpose != "admin-user")
        {
            if (!groups.Any(x => x.Id == query.GroupId && x.Enabled)) throw new ArgumentException("工作组不存在或已停用");
            if (query.Purpose != "assignee" && (string.IsNullOrWhiteSpace(query.AssigneeId)
                || !await Has(db, actor, ["dispatcher"], query.GroupId, query.AssigneeId)))
            {
                // 配置管理员在管理交接入口只获得候选人的必要标识，不获得任务正文。
                if (task == null || !isAdmin) throw new UnauthorizedAccessException("请先选择授权范围内的主责员工");
                await Admin(db, actor, task.GroupId, task.AssigneeId);
            }
        }

        var keyword = Text(query.Keyword, 128, "搜索词");
        var appUsers = await db.Queryable<SysUserApplicationEntity>().Where(x => x.AppCode == actor.AppCode && x.Enabled).Select(x => x.UserId).ToListAsync();
        // 姓名/账号先在数据库搜索；权限和用途再逐项判断，分页总数只包含可见人员。
        var users = await db.Queryable<SysUserInfo>().Where(x => x.Enabled && appUsers.Contains(x.UserId))
            .WhereIF(keyword.Length > 0, x => x.UserId.Contains(keyword) || x.UserName.Contains(keyword) || x.NickName.Contains(keyword))
            .OrderBy(x => x.UserId).Select(x => new { x.UserId, x.UserName, x.NickName }).ToListAsync();
        var grants = await db.Queryable<WorkGrant>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var permissions = PermissionSnapshots.GetValue(db, _ => new RequestPermissions());
        foreach (var user in users) permissions.Grants[actor.AppCode + ":" + user.UserId] = grants.Where(x => x.UserId == user.UserId).ToList();
        var rows = new List<Candidate>();
        async Task<Candidate?> Resolve(string userId, string userName, string? nickName)
        {
            var memberships = members.Where(x => x.UserId == userId).ToList();
            var groupLabel = string.Join("、", groups.Where(x => memberships.Any(m => m.GroupId == x.Id)).Select(x => x.Name));
            var person = new WorkActor(new ApplicationAccessContext { UserId = userId, AppCode = actor.AppCode });
            var reason = "";
            if (query.Purpose == "admin-user")
            {
                var allowed = actor.Access.PlatformAdministrator || await Has(db, actor, ["work-admin"], "", userId);
                foreach (var member in memberships)
                    if (!allowed && await Has(db, actor, ["work-admin"], member.GroupId, userId)) allowed = true;
                if (!allowed) return null;
            }
            else if (query.Purpose == "assignee")
            {
                if (!memberships.Any(x => x.GroupId == query.GroupId) || !await Has(db, actor, ["dispatcher"], query.GroupId, userId)) return null;
                if (!await Has(db, person, ["worker"], query.GroupId, userId)) reason = "尚未获得执行员工授权";
                if (task != null && task.Status != "Draft" && task.AssigneeId == userId) reason = "当前主责员工，无需重复转派";
            }
            else
            {
                var roles = query.Purpose == "leader" ? new[] { "dispatcher", "supervisor" } : new[] { "dispatcher" };
                if (!await Has(db, person, roles, query.GroupId, query.AssigneeId)) return null;
                if (query.Purpose == "reviewer" && query.AssigneeId == userId) reason = "主责员工不能验收自己的任务";
            }
            return new Candidate(userId, string.IsNullOrWhiteSpace(nickName) ? userName : nickName, userName, groupLabel, reason.Length > 0, reason);
        }
        foreach (var user in users)
        {
            var candidate = await Resolve(user.UserId, user.UserName, user.NickName);
            if (candidate != null) rows.Add(candidate);
        }
        Candidate? selected = rows.FirstOrDefault(x => x.Value == query.SelectedId);
        if (selected == null && !string.IsNullOrWhiteSpace(query.SelectedId))
        {
            var user = await db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == query.SelectedId);
            if (user != null && user.Enabled && appUsers.Contains(user.UserId)) selected = await Resolve(user.UserId, user.UserName, user.NickName);
            if (selected == null && task != null && new[] { task.AssigneeId, task.DispatcherId, task.ReviewerId, task.SummaryLeaderId }.Contains(query.SelectedId))
                selected = new Candidate(query.SelectedId, user?.NickName ?? user?.UserName ?? query.SelectedId, query.SelectedId, "", true, "原负责人已停用、移出工作组或不再符合授权，请重新选择");
        }
        var page = Math.Max(1, query.Page); var size = Math.Clamp(query.Size, 1, 100);
        return new { Total = rows.Count, Page = page, Records = rows.Skip((page - 1) * size).Take(size), Selected = selected };
    }
}
