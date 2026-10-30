using System.Security.Cryptography;
using Model.WorkTasks;
using Model.User;
using SqlSugar;

namespace Service;

public sealed partial class WorkTaskService
{
    public async Task<object> AdministrationAsync(WorkActor actor)
    {
        Ready(); using var db = scope.GetConnection("calmadmin").CopyNew();
        var own = await Grants(db, actor);
        if (actor.Personal || (!actor.Access.PlatformAdministrator && !own.Any(x => x.Role == "work-admin"))) throw new UnauthorizedAccessException("没有工作设置权限");
        var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
        var allowed = new List<WorkGroup>();
        foreach (var group in groups)
            if (actor.Access.PlatformAdministrator || await Has(db, actor, ["work-admin"], group.Id, "")) allowed.Add(group);
        var ids = allowed.Select(x => x.Id).ToArray();
        var members = await db.Queryable<WorkMember>().Where(x => x.AppCode == actor.AppCode && ids.Contains(x.GroupId)).ToListAsync();
        var memberIds=members.Select(x => x.UserId).ToArray();
        var all = actor.Access.PlatformAdministrator || own.Any(x => x.Role == "work-admin" && x.Scope == "All");
        var appUsers=await db.Queryable<Model.SystemApplications.SysUserApplicationEntity>().Where(x=>x.AppCode==actor.AppCode&&x.Enabled).Select(x=>x.UserId).ToListAsync();
        var users = await db.Queryable<SysUserInfo>().Where(x => x.Enabled&&appUsers.Contains(x.UserId)).WhereIF(!all, x => memberIds.Contains(x.UserId))
            .Select(x => new { x.UserId, Name=x.NickName ?? x.UserName }).ToListAsync();
        var grants = await db.Queryable<WorkGrant>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
        var visible = new List<WorkGrant>();
        foreach (var grant in grants)
            if (all || (grant.Scope is "Group" or "GroupTree" && ids.Contains(grant.TargetId)) || (grant.Scope is "Self" or "User" && memberIds.Contains(grant.Scope == "Self" ? grant.UserId : grant.TargetId))) visible.Add(grant);
        object? health=null;
        var handovers=new List<object>();
        foreach(var task in await db.Queryable<WorkTask>().Where(x=>x.AppCode==actor.AppCode&&x.Status!="Draft"&&x.Status!="Completed"&&x.Status!="Cancelled").OrderByDescending(x=>x.UpdatedAtUtc).ToListAsync())
            if(actor.Access.PlatformAdministrator||await Has(db,actor,["work-admin"],task.GroupId,task.AssigneeId))
                handovers.Add(new{TaskId=task.Id,task.GroupId,task.AssigneeId,task.DispatcherId,task.ReviewerId,task.SummaryLeaderId,task.Version,task.Status});
        if(all)
        {
            var oldest=await db.Queryable<WorkEvent>().Where(x=>x.AppCode==actor.AppCode&&!x.Processed).OrderBy(x=>x.CreatedAtUtc).FirstAsync();
            var last=await db.Queryable<WorkDigest>().Where(x=>x.AppCode==actor.AppCode).OrderByDescending(x=>x.CutoffUtc).FirstAsync();
            health=new{PendingEvents=await db.Queryable<WorkEvent>().CountAsync(x=>x.AppCode==actor.AppCode&&!x.Processed),
                PendingDeliveries=await db.Queryable<WorkDelivery>().CountAsync(x=>x.AppCode==actor.AppCode&&(x.Status=="Pending"||x.Status=="Queued")),
                FailedDeliveries=await db.Queryable<WorkDelivery>().CountAsync(x=>x.AppCode==actor.AppCode&&x.Status=="Failed"),
                UnknownDeliveries=await db.Queryable<WorkDelivery>().CountAsync(x=>x.AppCode==actor.AppCode&&x.Status=="Unknown"),
                OldestPendingAtUtc=oldest?.CreatedAtUtc,LastDigestAtUtc=last?.CutoffUtc,
                RegisteredFileBytes=await db.Queryable<WorkAttachment>().Where(x=>x.AppCode==actor.AppCode&&x.Status=="Ready").SumAsync(x=>x.Size)};
        }
        return new { Groups=allowed, Members=members, Grants=visible, Users=users, CanConfigureGlobal=all,
            Health=health,
            Handovers=handovers,
            Rule=all ? await db.Queryable<WorkNotificationRule>().FirstAsync(x => x.AppCode == actor.AppCode) : null,
            Deliveries=all ? await db.Queryable<WorkDelivery>().Where(x => x.AppCode == actor.AppCode).OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync() : [] };
    }
    public async Task<object> SaveGroupAsync(WorkActor actor, WorkGroup group)
    {
        Ready(); using var db = scope.GetConnection("calmadmin").CopyNew();
        var tx = await db.Ado.UseTranAsync(async () =>
        {
            await LockAsync(db, "work-config:" + actor.AppCode);
            var old = await db.Queryable<WorkGroup>().FirstAsync(x => x.Id == group.Id && x.AppCode == actor.AppCode);
            await Admin(db, actor, old?.Id ?? group.ParentId ?? "");
            if (!string.IsNullOrEmpty(group.ParentId))
            {
                await Admin(db, actor, group.ParentId);
                var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
                if (!groups.Any(x => x.Id == group.ParentId && x.Enabled)) throw new ArgumentException("上级组不存在或未启用");
                var visited = new HashSet<string> { group.Id };
                for (var cursor = group.ParentId; cursor != null; cursor=groups.FirstOrDefault(x => x.Id == cursor)?.ParentId)
                    if (!visited.Add(cursor)) throw new ArgumentException("工作组不能形成循环层级");
            }
            group.Name=Text(group.Name, 120, "工作组名称", true); group.AppCode=actor.AppCode;
            if (!string.IsNullOrEmpty(group.LeaderId)) await ValidatePerson(db, actor.AppCode, group.LeaderId);
            if (!group.Enabled && await db.Queryable<WorkTask>().AnyAsync(x => x.AppCode == actor.AppCode && x.GroupId == group.Id && x.Status != "Completed" && x.Status != "Cancelled"))
                throw new WorkConflictException("该组还有未结束任务，请先完成交接");
            group.CreatedAtUtc=old?.CreatedAtUtc ?? DateTime.UtcNow;
            if (old == null) await db.Insertable(group).ExecuteCommandAsync(); else await db.Updateable(group).ExecuteCommandAsync();
            await Audit(db, actor, "", "group.save", System.Text.Json.JsonSerializer.Serialize(new { Before=old, After=group },Json));
        });
        if (!tx.IsSuccess) throw tx.ErrorException!;
        return new { group.Id };
    }
    public async Task<object> SaveMemberAsync(WorkActor actor, WorkMember member)
    {
        Ready(); using var db = scope.GetConnection("calmadmin").CopyNew();
        var tx = await db.Ado.UseTranAsync(async () =>
        {
            await LockAsync(db, "work-config:" + actor.AppCode);
            await Admin(db, actor, member.GroupId, member.UserId);
            if(member.Enabled)await ValidatePerson(db, actor.AppCode, member.UserId);
            if (await db.Queryable<WorkGroup>().Where(x => x.Id == member.GroupId && x.AppCode == actor.AppCode).WhereIF(member.Enabled,x=>x.Enabled).CountAsync()==0) throw new ArgumentException("工作组不存在或已停用");
            var old = await db.Queryable<WorkMember>().FirstAsync(x => x.GroupId == member.GroupId && x.UserId == member.UserId && x.AppCode == actor.AppCode);
            if(!member.Enabled&&old==null)throw new ArgumentException("成员关系不存在");
            if (!member.Enabled && await db.Queryable<WorkTask>().AnyAsync(x => x.AppCode == actor.AppCode && x.GroupId == member.GroupId && (x.AssigneeId == member.UserId || SqlSugar.SqlFunc.Subqueryable<WorkTaskParticipant>().Where(p=>p.AppCode==x.AppCode && p.TaskId==x.Id && p.UserId==member.UserId && p.Enabled).Any()) && x.Status != "Completed" && x.Status != "Cancelled"))
                throw new WorkConflictException("成员还有未结任务，请先转派，历史责任记录会保留");
            member.AppCode=actor.AppCode; member.Id=old?.Id ?? Guid.NewGuid().ToString("N"); member.CreatedAtUtc=old?.CreatedAtUtc ?? DateTime.UtcNow;
            if (old == null) await db.Insertable(member).ExecuteCommandAsync(); else await db.Updateable(member).ExecuteCommandAsync();
            await Audit(db, actor, "", "member.save", System.Text.Json.JsonSerializer.Serialize(new { Before=old, After=member },Json));
        });
        if (!tx.IsSuccess) throw tx.ErrorException!;
        return new { member.Id };
    }
    private static async Task CanDelegate(ISqlSugarClient db, WorkActor actor, WorkGrant target)
    {
        if (actor.Personal) throw new UnauthorizedAccessException("个人凭证不能管理授权");
        if (actor.Access.PlatformAdministrator) return;
        foreach (var mine in (await Grants(db, actor)).Where(x => x.Role == "work-admin"))
        {
            if (mine.ExpiresAtUtc.HasValue && (!target.ExpiresAtUtc.HasValue || target.ExpiresAtUtc > mine.ExpiresAtUtc)) continue;
            if (mine.Scope == "All") return;
            if (target.Scope == "All") continue;
            if (target.Scope == "GroupTree" && mine.Scope != "GroupTree") continue;
            if ((target.Scope is "Group" or "GroupTree") && (mine.Scope is "Group" or "GroupTree") && await Covers(db, actor, mine, target.TargetId ?? "", target.UserId)) return;
            if (target.Scope is "Self" or "User")
            {
                var user = target.Scope == "Self" ? target.UserId : target.TargetId;
                var groups=await db.Queryable<WorkMember>().Where(x => x.UserId == user && x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
                foreach (var group in groups) if (await Covers(db, actor, mine, group.GroupId, user ?? "")) return;
            }
        }
        throw new UnauthorizedAccessException("授权范围或有效期超过自身可委派边界");
    }
    public async Task<object> SaveGrantAsync(WorkActor actor, WorkGrant grant)
    {
        Ready(); using var db = scope.GetConnection("calmadmin").CopyNew();
        if (grant.Role is not ("worker" or "dispatcher" or "supervisor" or "work-admin") || grant.Scope is not ("Self" or "User" or "Group" or "GroupTree" or "All")) throw new ArgumentException("无效业务角色或范围");
        if (grant.Role != "worker" && grant.Scope == "Self") throw new ArgumentException("管理角色需要明确人员或工作组范围");
        var tx = await db.Ado.UseTranAsync(async () =>
        {
            await LockAsync(db, "work-config:" + actor.AppCode);
            var old=await db.Queryable<WorkGrant>().FirstAsync(x => x.Id == grant.Id && x.AppCode == actor.AppCode);
            if (old != null) await CanDelegate(db, actor, old);
            await CanDelegate(db, actor, grant);
            if(grant.Enabled)await ValidatePerson(db, actor.AppCode, grant.UserId);
            else if(old==null)throw new ArgumentException("需要撤销的授权不存在");
            if (grant.Scope is "Group" or "GroupTree")
            {
                if (await db.Queryable<WorkGroup>().Where(x => x.Id == grant.TargetId && x.AppCode == actor.AppCode).WhereIF(grant.Enabled,x=>x.Enabled).CountAsync()==0) throw new ArgumentException("授权工作组不存在");
            }
            else if (grant.Scope == "User") { if(grant.Enabled)await ValidatePerson(db, actor.AppCode, grant.TargetId ?? ""); }
            else grant.TargetId=null;
            if (grant.ExpiresAtUtc.HasValue) grant.ExpiresAtUtc=grant.ExpiresAtUtc.Value.ToUniversalTime();
            if (grant.Enabled && grant.ExpiresAtUtc <= DateTime.UtcNow) throw new ArgumentException("授权到期时间必须晚于现在");
            old ??= await db.Queryable<WorkGrant>().FirstAsync(x => x.AppCode == actor.AppCode && x.UserId == grant.UserId && x.Role == grant.Role && x.Scope == grant.Scope && x.TargetId == grant.TargetId);
            if (old != null) { await CanDelegate(db, actor, old); grant.Id=old.Id; }
            grant.AppCode=actor.AppCode; grant.GrantedBy=actor.UserId; grant.CreatedAtUtc=old?.CreatedAtUtc ?? DateTime.UtcNow;
            if (old == null) await db.Insertable(grant).ExecuteCommandAsync(); else await db.Updateable(grant).ExecuteCommandAsync();
            await Audit(db, actor, "", "grant.save", System.Text.Json.JsonSerializer.Serialize(new { Before=old, After=grant },Json));
        });
        if (!tx.IsSuccess) throw tx.ErrorException!;
        return new { grant.Id };
    }
    public async Task<object> SaveRuleAsync(WorkActor actor, WorkNotificationRule rule)
    {
        Ready(); using var db = scope.GetConnection("calmadmin").CopyNew();
        await Admin(db, actor);
        _ = TimeZoneInfo.FindSystemTimeZoneById(Text(rule.TimeZone, 64, "时区", true));
        rule.IncludedDates=Text(rule.IncludedDates,4000,"额外工作日期");
        rule.ExcludedDates=Text(rule.ExcludedDates,4000,"不生成日期");
        foreach(var calendar in new[]{rule.IncludedDates,rule.ExcludedDates})
            if(calendar.Length>4000||calendar.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Any(x=>!DateOnly.TryParseExact(x,"yyyy-MM-dd",out _)))throw new ArgumentException("日历例外日期使用逗号分隔的yyyy-MM-dd格式");
        if(rule.IncludedDates.Split(',',StringSplitOptions.RemoveEmptyEntries).Intersect(rule.ExcludedDates.Split(',',StringSplitOptions.RemoveEmptyEntries)).Any())throw new ArgumentException("同一天不能同时设为工作日和不生成日");
        if (!TimeOnly.TryParseExact(rule.DailyTime, "HH:mm", out _) || rule.CatchUpMinutes is < 1 or > 1440 ||
            rule.WeekDays.Split(',').Any(x => !int.TryParse(x, out var day) || day is < 0 or > 6) ||
            rule.EmployeeAudience is not ("ActiveOrChanged" or "CompletedToday") ||
            rule.Channels.Split(',').Any(x => x is not ("Inbox" or "Email" or "WeChat"))) throw new ArgumentException("无效日终时间、日期、通道或接收范围");
        var result=await db.Ado.UseTranAsync(async()=>
        {
            await LockAsync(db,"work-config:"+actor.AppCode);
            var old=await db.Queryable<WorkNotificationRule>().FirstAsync(x => x.AppCode == actor.AppCode);
            rule.Id=old?.Id ?? Guid.NewGuid().ToString("N"); rule.AppCode=actor.AppCode; rule.CreatedAtUtc=old?.CreatedAtUtc ?? DateTime.UtcNow;
            if (old == null) await db.Insertable(rule).ExecuteCommandAsync(); else await db.Updateable(rule).ExecuteCommandAsync();
            await Audit(db, actor, "", "rule.save",System.Text.Json.JsonSerializer.Serialize(new{Before=old,After=rule},Json));
        });
        if(!result.IsSuccess)throw result.ErrorException!;
        return new { rule.Id };
    }
    public async Task<object> KeysAsync(WorkActor actor)
    {
        Ready(); Scope(actor, "read"); if (actor.Personal) throw new UnauthorizedAccessException("请登录网页管理个人凭证");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        return await db.Queryable<WorkPersonalKey>().Where(x => x.AppCode == actor.AppCode && x.OwnerId == actor.UserId)
            .Select(x => new { x.Id, x.Label, x.Scopes, x.ExpiresAtUtc, x.RevokedAtUtc, x.LastUsedAtUtc, x.CreatedAtUtc }).ToListAsync();
    }
    public async Task<object> CreateKeyAsync(WorkActor actor, WorkKeyRequest request)
    {
        Ready(); if (actor.Personal) throw new UnauthorizedAccessException("请登录网页创建个人凭证");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var grants = await Grants(db, actor);
        if (!grants.Any(x => x.Role is "worker" or "dispatcher" or "supervisor")) throw new UnauthorizedAccessException("创建个人凭证需要工作任务业务授权");
        var scopes=request.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        if (!scopes.Contains("read") || scopes.Any(x => x is not ("read" or "team_read" or "dispatch" or "assess" or "report" or "submit" or "upload" or "download")) || request.Days is < 1 or > 90) throw new ArgumentException("凭证范围无效或有效期不在1至90天内");
        if (scopes.Contains("team_read") && !grants.Any(x => x.Role is "dispatcher" or "supervisor")) throw new UnauthorizedAccessException("团队查看能力需要工作任务管理查看授权");
        if (scopes.Contains("dispatch") && !grants.Any(x => x.Role == "dispatcher")) throw new UnauthorizedAccessException("任务派发能力需要工作任务下派授权");
        var key = new WorkPersonalKey { AppCode=actor.AppCode, OwnerId=actor.UserId, Label=Text(request.Label, 80, "客户端名称", true),
            Scopes=string.Join(',', scopes), ExpiresAtUtc=DateTime.UtcNow.AddDays(request.Days) };
        var secret="wtk_" + key.Id + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        key.SecretHash=Hash(secret); await db.Insertable(key).ExecuteCommandAsync();
        await Audit(db, actor, "", "key.create", key.Id);
        return new { key.Id, Secret=secret, key.ExpiresAtUtc, key.Scopes };
    }
    public async Task<object> KeyActivityAsync(WorkActor actor)
    {
        Ready();Scope(actor,"read");if(actor.Personal)throw new UnauthorizedAccessException("请在网页查看本人凭证使用记录");
        using var db=scope.GetConnection("calmadmin").CopyNew();
        return await db.Queryable<WorkAudit>().Where(x=>x.AppCode==actor.AppCode&&x.ActorId==actor.UserId&&x.Action=="mcp.call")
            .OrderByDescending(x=>x.CreatedAtUtc).Take(100).Select(x=>new{x.Id,x.CreatedAtUtc,x.Summary}).ToListAsync();
    }
    public async Task RecordMcpUseAsync(WorkActor actor,string tool,bool success)
    {
        Ready();if(!actor.Personal||actor.KeyId==null)throw new UnauthorizedAccessException("缺少个人调用来源");
        using var db=scope.GetConnection("calmadmin").CopyNew();
        var safeTool=tool.StartsWith("worktask.",StringComparison.Ordinal)&&tool.Length<=128&&tool.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_' or '-')?tool:"unknown";
        await Audit(db,actor,"","mcp.call",System.Text.Json.JsonSerializer.Serialize(new{KeyId=actor.KeyId,Tool=safeTool,Success=success},Json));
    }
    public async Task RevokeKeyAsync(WorkActor actor, string id)
    {
        Ready(); if (actor.Personal) throw new UnauthorizedAccessException("请登录网页撤销凭证");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var count=await db.Updateable<WorkPersonalKey>().SetColumns(x => new WorkPersonalKey { RevokedAtUtc=DateTime.UtcNow })
            .Where(x => x.Id == id && x.OwnerId == actor.UserId && x.AppCode == actor.AppCode).ExecuteCommandAsync();
        if (count == 0) throw new UnauthorizedAccessException("凭证不存在或无权操作");
        await Audit(db, actor, "", "key.revoke", id);
    }
    public async Task<WorkActor> AuthenticateKeyAsync(string token)
    {
        Ready(); if (token.Length is < 80 or > 180 || !token.StartsWith("wtk_")) throw new UnauthorizedAccessException("需要有效的个人工作任务凭证");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var hash=Hash(token);
        var key=await db.Queryable<WorkPersonalKey>().FirstAsync(x => x.SecretHash == hash);
        if (key == null || key.RevokedAtUtc.HasValue || key.ExpiresAtUtc <= DateTime.UtcNow) throw new UnauthorizedAccessException("凭证无效、已到期或已撤销");
        var access=await applications.GetChannelAccessAsync(key.OwnerId, key.AppCode);
        var actor=new WorkActor(access, true, key.Id, key.Scopes);
        if (!(await Grants(db, actor)).Any(x => x.Role is "worker" or "dispatcher" or "supervisor")) throw new UnauthorizedAccessException("当前账号没有工作任务业务授权");
        await db.Updateable<WorkPersonalKey>().SetColumns(x => new WorkPersonalKey { LastUsedAtUtc=DateTime.UtcNow }).Where(x => x.Id == key.Id).ExecuteCommandAsync();
        return actor;
    }
}
