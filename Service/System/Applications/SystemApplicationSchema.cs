using System.Text.Json;
using Model.Menu;
using Model.SystemApplications;
using Model.User;
using SqlSugar;
using Newtonsoft.Json;

namespace Service;

public static class SystemApplicationSchema
{
    /// <summary>一次性迁移已有用户到 A；后续新增账号必须由管理员显式授权。</summary>
    public static async Task InitializeAsync(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables(typeof(SysApplicationEntity), typeof(SysUserApplicationEntity),
            typeof(SysApplicationMenuEntity), typeof(SysPlatformAdministratorEntity),
            typeof(SysPortalSessionEntity), typeof(SysApplicationLoginTicketEntity),
            typeof(SysApplicationMigrationEntity));

        foreach (var application in new[]
                 {
                     new SysApplicationEntity { AppCode = "A", DisplayName = "智能制造管理系统", Sort = 1 },
                     new SysApplicationEntity { AppCode = "B", DisplayName = "系统 B", HomePath = "/workbench", Sort = 2 }
                 })
        {
            if (!await db.Queryable<SysApplicationEntity>().AnyAsync(x => x.AppCode == application.AppCode))
            {
                application.UpdateTime = DateTime.Now;
                await db.Insertable(application).ExecuteCommandAsync();
            }
        }

        await EnsureMenuAsync(db, "980001", "0", "ApplicationAdministration", "/system-applications",
            "/system-applications/index", "系统入口与授权", "A");
        await EnsureMenuAsync(db, "980002", "0", "SharedModelDictionary", "/shared-data",
            "/shared-data/model-dictionary", "共享模型与字段映射", "A");
        await EnsureMenuAsync(db, "980003", "0", "SystemBWorkbench", "/workbench",
            "/system-b/workbench", "工作台", "B");
        await EnsureMenuAsync(db, "980004", "0", "SystemBSharedModelDictionary", "/shared-data",
            "/shared-data/model-dictionary", "共享模型与字段映射", "B");

        const string version = "multi-system-v1";
        if (await db.Queryable<SysApplicationMigrationEntity>().AnyAsync(x => x.Version == version))
        {
            await InitializeFullBackendAsync(db);
            return;
        }
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            // 一次读取、分批插入，避免升级时按用户串行执行多次查询。
            var users = await db.Queryable<SysUserInfo>().ToListAsync();
            var assignedUsers = (await db.Queryable<SysUserApplicationEntity>().Where(x => x.AppCode == "A").ToListAsync())
                .Select(x => x.UserId).ToHashSet();
            await InsertBatchesAsync(db, users.Where(user => !assignedUsers.Contains(user.UserId))
                .Select(user => new SysUserApplicationEntity
                {
                    UserId = user.UserId, AppCode = "A", Enabled = user.Enabled,
                    UseLegacyPermissions = true, AllResources = true, UpdateTime = DateTime.Now
                }));
            var administratorIds = (await db.Queryable<SysPlatformAdministratorEntity>().ToListAsync())
                .Select(x => x.UserId).ToHashSet();
            // 明确的历史管理员迁为平台管理员；普通账号的通配权限不自动提升。
            await InsertBatchesAsync(db, users.Where(user => !administratorIds.Contains(user.UserId) &&
                    user.RoleList.Any(role => role is "R_ADMIN" or "R_SUPER"))
                .Select(user => new SysPlatformAdministratorEntity { UserId = user.UserId }));
            var assignedMenus = await db.Queryable<SysApplicationMenuEntity>().ToListAsync();
            var bIds = assignedMenus.Where(x => x.AppCode == "B").Select(x => x.MenuId).ToHashSet();
            var aIds = assignedMenus.Where(x => x.AppCode == "A").Select(x => x.MenuId).ToHashSet();
            await InsertBatchesAsync(db, (await db.Queryable<sys_menu>().ToListAsync())
                .Where(menu => !bIds.Contains(menu.MenuId) && !aIds.Contains(menu.MenuId))
                .Select(menu => new SysApplicationMenuEntity { AppCode = "A", MenuId = menu.MenuId }));
            await db.Insertable(new SysApplicationMigrationEntity
            {
                Version = version,
                CompletedAtUtc = DateTime.UtcNow
            }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("多系统迁移失败");
        await InitializeFullBackendAsync(db);
    }

    private static async Task InitializeFullBackendAsync(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables<SysApplicationMenuEntryEntity>();
        const string version = "multi-system-full-backend-v2";
        if (await db.Queryable<SysApplicationMigrationEntity>().AnyAsync(x => x.Version == version)) return;
        var transaction = await db.Ado.UseTranAsync(async () =>
        {
            var source = await db.Queryable<sys_menu>().ToListAsync();
            var aAssignments = (await db.Queryable<SysApplicationMenuEntity>().Where(x => x.AppCode == "A").ToListAsync())
                .Select(x => x.MenuId).ToHashSet();
            foreach (var menu in source.Where(x => !x.MenuName.StartsWith("SystemB") && !aAssignments.Contains(x.MenuId)))
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = menu.MenuId }).ExecuteCommandAsync();

            // B 菜单复制元信息而非关联同一记录；后续编辑 B 不改 A。
            var bSource = source.Where(x => x.MenuName != "SystemBSharedModelDictionary").ToList();
            var existingSources = (await db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == "B").ToListAsync())
                .Where(x => x.SourceMenuId != null).ToDictionary(x => x.SourceMenuId!, x => x.Id);
            foreach (var menu in bSource)
            {
                if (existingSources.ContainsKey(menu.MenuId)) continue;
                var copy = JsonConvert.DeserializeObject<sys_menu>(JsonConvert.SerializeObject(menu))!;
                copy.MetaRoles = string.Empty;
                existingSources[menu.MenuId] = await db.Insertable(new SysApplicationMenuEntryEntity
                {
                    AppCode = "B", SourceMenuId = menu.MenuId, PermissionKey = menu.MenuName,
                    DefinitionJson = JsonConvert.SerializeObject(copy)
                }).ExecuteReturnIdentityAsync();
            }
            foreach (var menu in bSource)
            {
                var id = existingSources[menu.MenuId];
                var parent = existingSources.GetValueOrDefault(menu.ParentId);
                await db.Updateable<SysApplicationMenuEntryEntity>().SetColumns(x => x.ParentId == parent)
                    .Where(x => x.Id == id && x.AppCode == "B").ExecuteCommandAsync();
            }
            var administrators = (await db.Queryable<SysUserInfo>().Where(x => x.Enabled).ToListAsync())
                .Where(x => x.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase) &&
                    x.RoleList.Any(role => role is "R_ADMIN" or "R_SUPER"));
            foreach (var administrator in administrators)
            {
                if (!await db.Queryable<SysPlatformAdministratorEntity>().AnyAsync(x => x.UserId == administrator.UserId))
                    await db.Insertable(new SysPlatformAdministratorEntity { UserId = administrator.UserId }).ExecuteCommandAsync();
                foreach (var appCode in new[] { "A", "B" })
                {
                    var grant = await db.Queryable<SysUserApplicationEntity>().FirstAsync(x => x.UserId == administrator.UserId && x.AppCode == appCode);
                    var isNew = grant == null;
                    grant ??= new SysUserApplicationEntity { UserId = administrator.UserId, AppCode = appCode };
                    grant.Enabled = true;
                    grant.AllResources = true;
                    grant.UseLegacyPermissions = appCode == "A";
                    grant.PermissionsJson = "[\"*\"]";
                    grant.McpScopesJson = "[\"*\"]";
                    grant.UpdateTime = DateTime.Now;
                    if (isNew) await db.Insertable(grant).ExecuteCommandAsync();
                    else await db.Updateable(grant).ExecuteCommandAsync();
                }
            }
            await db.Insertable(new SysApplicationMigrationEntity { Version = version, CompletedAtUtc = DateTime.UtcNow }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("完整后台升级失败");
    }

    private static async Task InsertBatchesAsync<T>(ISqlSugarClient db, IEnumerable<T> rows) where T : class, new()
    {
        foreach (var batch in rows.Chunk(100))
            await db.Insertable(batch).ExecuteCommandAsync();
    }

    private static async Task EnsureMenuAsync(ISqlSugarClient db, string id, string parentId,
        string name, string path, string component, string title, string appCode)
    {
        var menu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == name);
        if (menu == null)
        {
            if (await db.Queryable<sys_menu>().AnyAsync(x => x.MenuId == id))
                throw new InvalidOperationException($"预留菜单编号 {id} 已被使用，请调整编号后重试");
            menu = new sys_menu
            {
                MenuId = id, ParentId = parentId, MenuName = name, Path = path, Component = component,
                MetaTitle = title, MetaIcon = "ri:apps-line", MetaAuthList = "[]", MetaRoles = string.Empty,
                MetaKeepAlive = false, Sort = 100, Status = true, CreateTime = DateTime.Now
            };
            await db.Insertable(menu).ExecuteCommandAsync();
            await db.Insertable(new SysApplicationMenuEntity { AppCode = appCode, MenuId = menu.MenuId }).ExecuteCommandAsync();
        }
    }
}
