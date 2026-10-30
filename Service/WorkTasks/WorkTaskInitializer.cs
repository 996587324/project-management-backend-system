using Interface;
using Model.Menu;
using Model.SystemApplications;
using Model.WorkTasks;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

public sealed class WorkTaskInitializer(SqlSugarScope scope) : IWorkTaskInitializer
{
    private readonly SemaphoreSlim gate = new(1);
    public bool IsReady { get; private set; }
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsReady) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady) return;
            using var db = scope.GetConnection("calmadmin").CopyNew();
            var result = await db.Ado.UseTranAsync(async () =>
            {
                await WorkTaskService.LockAsync(db, "work-task-schema-v1");
                await db.Ado.ExecuteCommandAsync("IF OBJECT_ID('work_task_outbox','U') IS NOT NULL AND COL_LENGTH('work_task_outbox','PushRequested') IS NULL ALTER TABLE [work_task_outbox] ADD [PushRequested] bit NOT NULL CONSTRAINT [DF_work_task_outbox_PushRequested] DEFAULT (0) WITH VALUES;");
                db.CodeFirst.InitTables(typeof(WorkGroup), typeof(WorkMember), typeof(WorkGrant), typeof(WorkTask),
                    typeof(WorkTaskParticipant), typeof(WorkRevision), typeof(WorkPlan), typeof(WorkMilestone), typeof(WorkReport),
                    typeof(WorkSubmission), typeof(WorkReview), typeof(WorkAttachment), typeof(WorkAttachmentLink),
                    typeof(WorkAudit), typeof(WorkPersonalKey), typeof(WorkReceipt), typeof(WorkEvent),
                    typeof(WorkDigest), typeof(WorkDelivery), typeof(WorkNotificationRule));
                foreach (var index in new[]
                {
                    (Table: "work_task", Name: "IX_work_task_assignee", Columns: "[AppCode],[AssigneeId],[Status],[CommittedDueAtUtc]", Unique: false),
                    (Table: "work_task_participant", Name: "UX_work_task_participant", Columns: "[AppCode],[TaskId],[UserId]", Unique: true),
                    (Table: "work_role_grant", Name: "IX_work_grant_user", Columns: "[AppCode],[UserId],[Enabled]", Unique: false),
                    (Table: "work_group_member", Name: "UX_work_member", Columns: "[AppCode],[GroupId],[UserId]", Unique: true),
                    (Table: "work_task_report", Name: "IX_work_report_task", Columns: "[AppCode],[TaskId],[CreatedAtUtc]", Unique: false),
                    (Table: "work_task_attachment_link", Name: "UX_work_attachment_link", Columns: "[AppCode],[RecordId],[AttachmentId]", Unique: true),
                    (Table: "work_personal_key", Name: "UX_work_key_hash", Columns: "[SecretHash]", Unique: true),
                    (Table: "work_task_outbox", Name: "IX_work_outbox_pending", Columns: "[Processed],[CreatedAtUtc]", Unique: false),
                    (Table: "work_task_digest", Name: "UX_work_digest", Columns: "[AppCode],[BusinessDate],[RecipientId],[Perspective]", Unique: true),
                    (Table: "work_notification_delivery", Name: "UX_work_delivery", Columns: "[AppCode],[OriginId],[RecipientId],[Perspective],[Channel]", Unique: true),
                    (Table: "work_notification_rule", Name: "UX_work_rule", Columns: "[AppCode]", Unique: true)
                })
                    await db.Ado.ExecuteCommandAsync($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='{index.Name}' AND object_id=OBJECT_ID('{index.Table}')) CREATE {(index.Unique ? "UNIQUE " : "")}INDEX [{index.Name}] ON [{index.Table}] ({index.Columns});");
            });
            if (!result.IsSuccess) throw result.ErrorException ?? new InvalidOperationException("工作任务结构初始化失败");
            IsReady = true;
        }
        finally { gate.Release(); }
    }
}

public static class WorkTaskMenus
{
    public static async Task SeedAsync(ISqlSugarClient db)
    {
        foreach (var item in new[]
        {
            (Id:"940000000", Parent:"0", Name:"WorkTasks", Path:"/work-tasks", Component:"/index/index", Title:"工作任务", Sort:7),
            (Id:"940000001", Parent:"940000000", Name:"WorkTasksMy", Path:"my", Component:"/work-tasks/my", Title:"我的工作", Sort:1),
            (Id:"940000002", Parent:"940000000", Name:"WorkTasksTeam", Path:"team", Component:"/work-tasks/team", Title:"团队工作", Sort:2),
            (Id:"940000003", Parent:"940000000", Name:"WorkTasksSettings", Path:"settings", Component:"/work-tasks/settings", Title:"工作设置", Sort:3),
            (Id:"940000004", Parent:"940000000", Name:"WorkTasksKeys", Path:"keys", Component:"/work-tasks/keys", Title:"个人MCP连接", Sort:4),
            (Id:"940000005", Parent:"940000000", Name:"WorkTasksOverview", Path:"overview", Component:"/work-tasks/overview", Title:"任务总览", Sort:0)
        })
        {
            var menu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == item.Name);
            if (menu == null)
            {
                var menuId = item.Id;
                if (await db.Queryable<sys_menu>().AnyAsync(x => x.MenuId == menuId))
                {
                    var used = (await db.Queryable<sys_menu>().Select(x => x.MenuId).ToListAsync()).ToHashSet();
                    var candidate = 1000000000;
                    while (used.Contains(candidate.ToString())) candidate++;
                    menuId = candidate.ToString();
                }
                var parentId = item.Parent == "0" ? "0" : (await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "WorkTasks"))?.MenuId
                    ?? throw new InvalidOperationException("工作任务父菜单不存在");
                menu = new sys_menu { MenuId=menuId, ParentId=parentId, MenuName=item.Name, Path=item.Path,
                    Component=item.Component, MetaTitle=item.Title, MetaIcon="ri:task-line", MetaRoles="", MetaAuthList="[]", Status=true, Sort=item.Sort, CreateTime=DateTime.Now };
                await db.Insertable(menu).ExecuteCommandAsync();
            }
            if (!await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == menu.MenuId))
                await db.Insertable(new SysApplicationMenuEntity { AppCode="A", MenuId=menu.MenuId }).ExecuteCommandAsync();
        }
        // B 保持独立菜单实例和业务授权；仅补缺失入口，不覆盖已有配置。
        var sources = await db.Queryable<sys_menu>().Where(x => x.MenuName.StartsWith("WorkTasks")).ToListAsync();
        var entries = await db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == "B").ToListAsync();
        var rootSource = sources.FirstOrDefault(x => x.MenuName == "WorkTasks");
        if (rootSource == null) return;
        var root = entries.FirstOrDefault(x => x.PermissionKey == "WorkTasks");
        if (root == null)
        {
            if (entries.Any(x => SystemApplicationMenuService.Read(x).Path == "/work-tasks" && x.ParentId == 0)) return;
            root = new SysApplicationMenuEntryEntity { AppCode="B", ParentId=0, SourceMenuId=rootSource.MenuId, PermissionKey="WorkTasks", DefinitionJson=JsonConvert.SerializeObject(rootSource) };
            root.Id = await db.Insertable(root).ExecuteReturnIdentityAsync();
            entries.Add(root);
        }
        foreach (var source in sources.Where(x => x.ParentId == rootSource.MenuId))
        {
            if (entries.Any(x => x.PermissionKey == source.MenuName || x.ParentId == root.Id && SystemApplicationMenuService.Read(x).Path == source.Path)) continue;
            var entry = new SysApplicationMenuEntryEntity { AppCode="B", ParentId=root.Id, SourceMenuId=source.MenuId, PermissionKey=source.MenuName!, DefinitionJson=JsonConvert.SerializeObject(source) };
            entry.Id = await db.Insertable(entry).ExecuteReturnIdentityAsync();
            entries.Add(entry);
        }
    }
}

public static class WorkTaskAccessProjection
{
    public static async Task ApplyAsync(ISqlSugarClient db, HashSet<string> permissions, string userId, string appCode, bool platformAdmin)
    {
        if (appCode is not ("A" or "B")) return;
        foreach (var key in new[] { "WorkTasks", "WorkTasksMy", "WorkTasksTeam", "WorkTasksSettings", "WorkTasksKeys", "WorkTasksOverview" }) permissions.Remove(key);
        if (platformAdmin) { permissions.Add("WorkTasks"); permissions.Add("WorkTasksSettings"); }
        if (!db.DbMaintenance.IsAnyTable("work_role_grant", false)) return;
        var now = DateTime.UtcNow;
        var grants = await db.Queryable<WorkGrant>().Where(x => x.AppCode == appCode && x.UserId == userId && x.Enabled && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)).ToListAsync();
        if (grants.Count == 0) return;
        permissions.Add("WorkTasks");
        if (grants.Any(x => x.Role is "worker" or "dispatcher" or "supervisor")) permissions.Add("WorkTasksOverview");
        if (grants.Any(x => x.Role == "worker")) permissions.UnionWith(["WorkTasksMy", "WorkTasksKeys"]);
        if (grants.Any(x => x.Role is "dispatcher" or "supervisor")) permissions.UnionWith(["WorkTasksTeam", "WorkTasksKeys"]);
        if (grants.Any(x => x.Role == "work-admin")) permissions.Add("WorkTasksSettings");
    }
}
