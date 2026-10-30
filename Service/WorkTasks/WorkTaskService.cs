using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Interface;
using Microsoft.Extensions.Options;
using Model.SystemApplications;
using Model.User;
using Model.WorkTasks;
using SqlSugar;

namespace Service;

public sealed partial class WorkTaskService(SqlSugarScope scope, IWorkTaskInitializer initializer,
    ISystemApplicationService applications, IOptions<WorkTaskOptions> options, IServiceProvider? channelServices = null) : IWorkTaskService
{
    private readonly IServiceProvider? ChannelServices = channelServices;
    private sealed class RequestPermissions
    {
        public Dictionary<string,List<WorkGrant>> Grants { get; } = new();
        public Dictionary<string,List<WorkGroup>> Groups { get; } = new();
    }
    // CopyNew 的连接对象只存在于本次业务操作；缓存不跨请求，撤权下次请求即时生效。
    private static readonly ConditionalWeakTable<ISqlSugarClient,RequestPermissions> PermissionSnapshots=new();
    internal static readonly JsonSerializerOptions Json = WorkTaskJson.Options;
    private void Ready() { if (!initializer.IsReady) throw new InvalidOperationException("工作任务正在初始化，请稍后重试"); }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static async Task LockAsync(ISqlSugarClient db, string key) =>
        await db.Ado.ExecuteCommandAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; IF @r < 0 THROW 51000,'work task lock timeout',1;", new { key });
    private static string Text(string? value, int max, string label, bool required = false)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > max || (required && text.Length == 0)) throw new ArgumentException($"{label}{(required ? "不能为空且" : "")}不能超过{max}字");
        return text;
    }
    private static void Scope(WorkActor actor, string scope)
    {
        if (actor.AppCode is not ("A" or "B") || string.IsNullOrWhiteSpace(actor.UserId)) throw new UnauthorizedAccessException("缺少有效应用身份");
        if (actor.Personal && !actor.Scopes.Split(',').Contains(scope)) throw new UnauthorizedAccessException("个人凭证未允许此操作");
    }
    private static async Task<List<WorkGrant>> Grants(ISqlSugarClient db, WorkActor actor)
    {
        var cache=PermissionSnapshots.GetValue(db,_=>new RequestPermissions());
        var key=actor.AppCode+":"+actor.UserId;
        var now = DateTime.UtcNow;
        if(!cache.Grants.TryGetValue(key,out var grants))
        {
            grants=await db.Queryable<WorkGrant>().Where(x => x.AppCode == actor.AppCode && x.UserId == actor.UserId && x.Enabled && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)).ToListAsync();
            cache.Grants[key]=grants;
        }
        return grants.Where(x=>!x.ExpiresAtUtc.HasValue||x.ExpiresAtUtc>now).ToList();
    }
    private static async Task<bool> Covers(ISqlSugarClient db, WorkActor actor, WorkGrant grant, string groupId, string userId)
    {
        if (grant.Scope == "All") return true;
        if (grant.Scope == "Self") return actor.UserId == userId;
        if (grant.Scope == "User") return grant.TargetId == userId;
        if (grant.Scope == "Group") return grant.TargetId == groupId;
        if (grant.Scope != "GroupTree") return false;
        var cache=PermissionSnapshots.GetValue(db,_=>new RequestPermissions());
        if(!cache.Groups.TryGetValue(actor.AppCode,out var groups))
        {
            groups=await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode).ToListAsync();
            cache.Groups[actor.AppCode]=groups;
        }
        var visited = new HashSet<string>();
        for (var current = groupId; !string.IsNullOrEmpty(current) && visited.Add(current); current = groups.FirstOrDefault(x => x.Id == current)?.ParentId ?? "")
            if (current == grant.TargetId) return true;
        return false;
    }
    private static async Task<bool> Has(ISqlSugarClient db, WorkActor actor, string[] roles, string group, string user)
    {
        foreach (var grant in (await Grants(db, actor)).Where(x => roles.Contains(x.Role)))
            if (await Covers(db, actor, grant, group, user)) return true;
        return false;
    }
    internal static async Task<bool> CanRead(ISqlSugarClient db, WorkActor actor, WorkTask task)
    {
        if (task.AppCode != actor.AppCode) return false;
        if (task.Status == "Draft" || string.IsNullOrEmpty(task.RevisionId)) return (!actor.Personal || actor.Scopes.Split(',').Contains("dispatch")) && task.CreatedBy == actor.UserId && await Has(db, actor, ["dispatcher"], task.GroupId, task.AssigneeId);
        if (actor.Personal)
            return await IsWorker(db, actor, task)
                || actor.Scopes.Split(',').Contains("team_read") && await Has(db, actor, ["dispatcher", "supervisor"], task.GroupId, task.AssigneeId)
                || actor.Scopes.Split(',').Contains("dispatch") && task.CreatedBy == actor.UserId && await Has(db, actor, ["dispatcher"], task.GroupId, task.AssigneeId);
        if (await IsWorker(db, actor, task)) return true;
        return await Has(db, actor, ["dispatcher", "supervisor"], task.GroupId, task.AssigneeId);
    }
    private static async Task<WorkTask> TaskFor(ISqlSugarClient db, WorkActor actor, string id)
    {
        var task = await db.Queryable<WorkTask>().FirstAsync(x => x.Id == id && x.AppCode == actor.AppCode);
        if (task == null || !await CanRead(db, actor, task)) throw new UnauthorizedAccessException("任务不存在或已无权访问");
        return task;
    }
    private static async Task Worker(ISqlSugarClient db, WorkActor actor, WorkTask task, string scope)
    {
        Scope(actor, scope);
        if (task.AssigneeId != actor.UserId || !await Has(db, actor, ["worker"], task.GroupId, actor.UserId)) throw new UnauthorizedAccessException("仅当前主责员工可以办理");
    }
    private static async Task<bool> IsWorker(ISqlSugarClient db, WorkActor actor, WorkTask task) =>
        (task.AssigneeId == actor.UserId || await db.Queryable<WorkTaskParticipant>().AnyAsync(x => x.AppCode == task.AppCode && x.TaskId == task.Id && x.UserId == actor.UserId && x.Enabled))
        && await Has(db, actor, ["worker"], task.GroupId, actor.UserId);
    private static async Task Participant(ISqlSugarClient db, WorkActor actor, WorkTask task, string capability)
    {
        Scope(actor, capability);
        if (!await IsWorker(db, actor, task)) throw new UnauthorizedAccessException("仅当前任务参与员工可以办理");
    }
    private static async Task Dispatcher(ISqlSugarClient db, WorkActor actor, WorkTask task, bool review = false)
    {
        if (actor.Personal) Scope(actor, "dispatch");
        if (!await Has(db, actor, ["dispatcher"], task.GroupId, task.AssigneeId)) throw new UnauthorizedAccessException("没有此任务的管理权限");
        if(review ? task.ReviewerId!=actor.UserId : task.DispatcherId!=actor.UserId&&task.SummaryLeaderId!=actor.UserId)
            throw new UnauthorizedAccessException("不是当前任务的指定负责人，请按授权办理责任交接");
    }
    private static async Task Admin(ISqlSugarClient db, WorkActor actor, string group = "", string user = "")
    {
        Scope(actor, "admin");
        if (actor.Personal || (!actor.Access.PlatformAdministrator && !await Has(db, actor, ["work-admin"], group, user))) throw new UnauthorizedAccessException("没有此范围的工作任务配置权限");
    }
    private static void State(WorkTask task, params string[] states)
    {
        if (!states.Contains(task.Status)) throw new WorkConflictException("任务状态已变化，当前操作不允许，请刷新任务");
    }
    private static async Task Audit(ISqlSugarClient db, WorkActor actor, string taskId, string action, string summary = "") =>
        await db.Insertable(new WorkAudit { AppCode=actor.AppCode, TaskId=taskId, ActorId=actor.UserId, Action=action, Source=actor.Source, Summary=summary }).ExecuteCommandAsync();
    private static async Task ValidatePerson(ISqlSugarClient db, string app, string id)
    {
        if (!await db.Queryable<SysUserInfo>().AnyAsync(x => x.UserId == id && x.Enabled) ||
            !await db.Queryable<SysUserApplicationEntity>().AnyAsync(x => x.AppCode == app && x.UserId == id && x.Enabled))
            throw new ArgumentException("人员不存在、已停用或没有当前应用准入");
    }
    private static async Task ValidateAssignment(ISqlSugarClient db, WorkActor actor, WorkTask task)
    {
        await ValidatePerson(db, actor.AppCode, task.AssigneeId);
        if (!await db.Queryable<WorkGroup>().AnyAsync(x => x.AppCode == actor.AppCode && x.Id == task.GroupId && x.Enabled) ||
            !await db.Queryable<WorkMember>().AnyAsync(x => x.AppCode == actor.AppCode && x.GroupId == task.GroupId && x.UserId == task.AssigneeId && x.Enabled))
            throw new ArgumentException("请选择当前工作组的有效成员作为主责人");
        var assignee = new WorkActor(new ApplicationAccessContext { AppCode=actor.AppCode, UserId=task.AssigneeId });
        if (!await Has(db, assignee, ["worker"], task.GroupId, task.AssigneeId)) throw new ArgumentException("主责人尚未获得执行员工授权");
        await ValidatePerson(db, actor.AppCode, task.SummaryLeaderId);
        var leader = new WorkActor(new ApplicationAccessContext { AppCode=actor.AppCode, UserId=task.SummaryLeaderId });
        if (!await Has(db, leader, ["dispatcher", "supervisor"], task.GroupId, task.AssigneeId)) throw new ArgumentException("汇总负责人没有此任务范围的管理查看授权");
        if (!task.RequiresReview) return;
        if (task.ReviewerId == task.AssigneeId || await db.Queryable<WorkTaskParticipant>().AnyAsync(x => x.AppCode == task.AppCode && x.TaskId == task.Id && x.UserId == task.ReviewerId && x.Enabled)) throw new ArgumentException("验收人不能是任务参与员工");
        await ValidatePerson(db, actor.AppCode, task.ReviewerId);
        var reviewer = new WorkActor(new ApplicationAccessContext { AppCode=actor.AppCode, UserId=task.ReviewerId });
        if (!await Has(db, reviewer, ["dispatcher"], task.GroupId, task.AssigneeId)) throw new ArgumentException("指定验收人没有此范围的验收能力");
    }
    public async Task<object> BootstrapAsync(WorkActor actor)
    {
        Ready(); Scope(actor, "read");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var grants = await Grants(db, actor);
        var groups = await db.Queryable<WorkGroup>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var members = await db.Queryable<WorkMember>().Where(x => x.AppCode == actor.AppCode && x.Enabled).ToListAsync();
        var users = await db.Queryable<SysUserInfo>().Where(x => x.Enabled).Select(x => new { x.UserId, Name=x.NickName ?? x.UserName }).ToListAsync();
        var appUsers=await db.Queryable<SysUserApplicationEntity>().Where(x=>x.AppCode==actor.AppCode&&x.Enabled).Select(x=>x.UserId).ToListAsync();
        var targets = new List<object>();
        var visibleGroups = new HashSet<string>();
        if (!actor.Personal)
            foreach (var member in members)
                if (await Has(db, actor, ["dispatcher", "work-admin", "supervisor"], member.GroupId, member.UserId) || actor.Access.PlatformAdministrator)
                {
                    if(!users.Any(x=>x.UserId==member.UserId)||!appUsers.Contains(member.UserId))continue;
                    visibleGroups.Add(member.GroupId);
                    var targetActor=new WorkActor(new ApplicationAccessContext{UserId=member.UserId,AppCode=actor.AppCode});
                    targets.Add(new { member.GroupId, member.UserId, Name=users.FirstOrDefault(x => x.UserId == member.UserId)?.Name ?? member.UserId,
                        CanAssign=await Has(db,targetActor,["worker"],member.GroupId,member.UserId) });
                }
        foreach (var grant in grants.Where(x => x.TargetId != null && x.Scope is "Group" or "GroupTree")) visibleGroups.Add(grant.TargetId!);
        return new { actor.UserId, actor.AppCode, CanWork=grants.Any(x => x.Role == "worker"), CanDispatch=!actor.Personal && grants.Any(x => x.Role == "dispatcher"),
            CanTeam=!actor.Personal && grants.Any(x => x.Role is "dispatcher" or "supervisor"),
            CanAdmin=!actor.Personal && (actor.Access.PlatformAdministrator || grants.Any(x => x.Role == "work-admin")),
            CanUse=grants.Count > 0, Groups=groups.Where(x => visibleGroups.Contains(x.Id) || actor.Access.PlatformAdministrator), Targets=targets };
    }
    public async Task<object> ListAsync(WorkActor actor, WorkQuery query)
    {
        Ready(); Scope(actor, "read");
        if (query.View is not ("my" or "team" or "review" or "drafts")) throw new ArgumentException("无效工作视图");
        if (actor.Personal && query.View != "my") throw new UnauthorizedAccessException("个人MCP只开放本人任务");
        var keyword = Text(query.Keyword, 100, "搜索内容");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        // SQL 先按应用/视图收紧，再逐条执行配对范围授权；未授权记录不会影响总数。
        var rows = await db.Queryable<WorkTask>().Where(x => x.AppCode == actor.AppCode)
            .WhereIF(query.View == "my", x => x.Status != "Draft" && (x.AssigneeId == actor.UserId || SqlFunc.Subqueryable<WorkTaskParticipant>().Where(p => p.AppCode == x.AppCode && p.TaskId == x.Id && p.UserId == actor.UserId && p.Enabled).Any()))
            .WhereIF(query.View == "review", x => x.ReviewerId == actor.UserId && x.Status == "PendingReview")
            .WhereIF(query.View == "drafts", x => x.CreatedBy == actor.UserId && x.Status == "Draft")
            .WhereIF(!string.IsNullOrEmpty(query.Status), x => x.Status == query.Status)
            .WhereIF(keyword.Length > 0, x => x.Title.Contains(keyword)).OrderByDescending(x => x.UpdatedAtUtc).ToListAsync();
        var allowed = new List<WorkTask>();
        foreach (var task in rows) if (await CanRead(db, actor, task)) allowed.Add(task);
        var page = Math.Max(1, query.Page); var size = Math.Clamp(query.Size, 1, 100);
        return new { Total=allowed.Count, Page=page, Records=allowed.Skip((page - 1) * size).Take(size).Select(x => new
            { x.Id, x.Title, x.Status, x.GroupId, x.AssigneeId, x.Priority, x.Progress, x.Blocked, x.CommittedDueAtUtc, x.OriginalDueAtUtc, x.UpdatedAtUtc, x.Version }) };
    }
    public async Task<object> DetailAsync(WorkActor actor, string id)
    {
        Ready(); Scope(actor, "read");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var task = await TaskFor(db, actor, id);
        var canWork = task.AssigneeId == actor.UserId && await Has(db, actor, ["worker"], task.GroupId, actor.UserId);
        var canParticipate = await IsWorker(db, actor, task);
        var collaboratorIds = await db.Queryable<WorkTaskParticipant>().Where(x => x.AppCode == actor.AppCode && x.TaskId == id && x.Enabled).Select(x=>x.UserId).ToListAsync();
        var participantUserIds = collaboratorIds.Append(task.AssigneeId).Distinct().ToArray();
        var participantNames = await db.Queryable<SysUserInfo>().Where(x => participantUserIds.Contains(x.UserId)).Select(x=>new { x.UserId, Name=x.NickName ?? x.UserName }).ToListAsync();
        var dispatchScope = !actor.Personal && await Has(db, actor, ["dispatcher"], task.GroupId, task.AssigneeId);
        var manage=dispatchScope&&(task.DispatcherId==actor.UserId||task.SummaryLeaderId==actor.UserId);
        var submissions=await db.Queryable<WorkSubmission>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode &&
            ((x.Status != "Draft" && x.Status != "Abandoned") || x.ActorId == actor.UserId)).OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
        var sharedRecords=submissions.Where(x => x.Status is not ("Draft" or "Abandoned")).Select(x => x.Id).ToArray();
        var links=await db.Queryable<WorkAttachmentLink>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).ToListAsync();
        var sharedFiles=links.Where(x => x.Kind == "Report" || sharedRecords.Contains(x.RecordId)).Select(x => x.AttachmentId).ToArray();
        return new { Task=task, CanWork=canWork, CanParticipate=canParticipate, Collaborators=collaboratorIds, ParticipantNames=participantNames, CanManage=manage,
            CanReview=dispatchScope && task.ReviewerId == actor.UserId && task.AssigneeId != actor.UserId,
            Revisions=await db.Queryable<WorkRevision>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).OrderBy(x => x.CreatedAtUtc).ToListAsync(),
            Plans=await db.Queryable<WorkPlan>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(),
            Milestones=await db.Queryable<WorkMilestone>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).OrderBy(x => x.Sort).ToListAsync(),
            Reports=await db.Queryable<WorkReport>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(),
            Submissions=submissions,
            Reviews=await db.Queryable<WorkReview>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).ToListAsync(),
            Attachments=await db.Queryable<WorkAttachment>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode && x.Status == "Ready" && (x.OwnerId == actor.UserId || sharedFiles.Contains(x.Id))).Select(x => new { x.Id, x.Name, x.Size, x.OwnerId, x.CreatedAtUtc }).ToListAsync(),
            AttachmentLinks=links.Where(x => x.Kind == "Report" || sharedRecords.Contains(x.RecordId) || submissions.Any(s => s.Id == x.RecordId && s.ActorId == actor.UserId)),
            Audit=await db.Queryable<WorkAudit>().Where(x => x.TaskId == id && x.AppCode == actor.AppCode).OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync() };
    }
    public async Task<object> OperationAsync(WorkActor actor, string id)
    {
        Ready(); Scope(actor, "read");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var receipt = await db.Queryable<WorkReceipt>().FirstAsync(x => x.Id == id && x.AppCode == actor.AppCode && x.OwnerId == actor.UserId)
            ?? throw new UnauthorizedAccessException("操作记录不存在或无权访问");
        await TaskFor(db, actor, receipt.TaskId);
        return JsonSerializer.Deserialize<WorkCommandResult>(receipt.ResultJson, Json)!;
    }
}
