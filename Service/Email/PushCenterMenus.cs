using Model.Menu;
using Model.SystemApplications;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>一次性整理导航，保留菜单主键、权限标识、旧地址及各系统已有授权。</summary>
public static class PushCenterMenus
{
    public static async Task SeedAsync(ISqlSugarClient db)
    {
        const string version = "push-center-email-v1";
        if (await db.Queryable<SysApplicationMigrationEntity>().AnyAsync(x => x.Version == version)) return;
        var result = await db.Ado.UseTranAsync(async () =>
        {
            var source = await db.Queryable<sys_menu>().ToListAsync();
            var root = source.FirstOrDefault(x => x.MenuName == "WeChatClawBotRoot");
            if (root == null) throw new InvalidOperationException("推送中心需要已有微信目录，请先完成系统菜单初始化");
            if (root.MetaTitle is "微信推送" or "微信 ClawBot")
            {
                var title = "推送中心";
                root.MetaTitle = title;
                await db.Updateable<sys_menu>().SetColumns(x => x.MetaTitle == title)
                    .Where(x => x.MenuId == root.MenuId).ExecuteCommandAsync();
            }
            var manager = source.FirstOrDefault(x => x.MenuName == "WeChatClawBotManagement");
            var managerWasMissing = manager == null;
            if (manager == null)
            {
                manager = Create(source, root.MenuId, "WeChatClawBotManagement", "/agent/clawbot-management",
                    "/agent/clawbot-management", "微信管理与调试", "R_SUPER,R_ADMIN", 3);
                await db.Insertable(manager).ExecuteCommandAsync();
                source.Add(manager);
            }
            else
            {
                var oldPath = FullPath(manager, source);
                manager.ParentId = root.MenuId;
                manager.Path = oldPath;
                await db.Updateable<sys_menu>().SetColumns(x => new sys_menu { ParentId = root.MenuId, Path = oldPath })
                    .Where(x => x.MenuId == manager.MenuId).ExecuteCommandAsync();
            }
            var email = source.FirstOrDefault(x => x.MenuName == "EmailPush");
            if (email == null)
            {
                email = Create(source, root.MenuId, "EmailPush", "email", "/push-center/email", "邮件推送", "", 2);
                await db.Insertable(email).ExecuteCommandAsync();
                source.Add(email);
            }
            var aRoot = await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == root.MenuId);
            if (aRoot && !await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == email.MenuId))
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = email.MenuId }).ExecuteCommandAsync();
            if (aRoot && managerWasMissing && !await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == manager.MenuId))
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = manager.MenuId }).ExecuteCommandAsync();

            var bRows = await db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == "B").ToListAsync();
            var bRoot = bRows.FirstOrDefault(x => x.SourceMenuId == root.MenuId);
            var bManager = bRows.FirstOrDefault(x => x.PermissionKey == "WeChatClawBotManagement");
            // Do not open a previously absent module in B.
            if (bRoot == null && bManager != null)
            {
                bRoot = new SysApplicationMenuEntryEntity
                {
                    AppCode = "B", SourceMenuId = root.MenuId, PermissionKey = root.MenuName,
                    DefinitionJson = JsonConvert.SerializeObject(root)
                };
                bRoot.Id = await db.Insertable(bRoot).ExecuteReturnIdentityAsync();
            }
            if (bRoot != null)
            {
                var definition = JsonConvert.DeserializeObject<sys_menu>(bRoot.DefinitionJson)!;
                if (definition.MetaTitle is "微信推送" or "微信 ClawBot")
                {
                    definition.MetaTitle = "推送中心";
                    bRoot.DefinitionJson = JsonConvert.SerializeObject(definition);
                    await db.Updateable(bRoot).UpdateColumns(x => x.DefinitionJson).ExecuteCommandAsync();
                }
                if (bManager != null)
                {
                    var managerDefinition = JsonConvert.DeserializeObject<sys_menu>(bManager.DefinitionJson)!;
                    managerDefinition.Path = FullBPath(bManager, bRows);
                    bManager.ParentId = bRoot.Id;
                    bManager.DefinitionJson = JsonConvert.SerializeObject(managerDefinition);
                    await db.Updateable(bManager).UpdateColumns(x => new { x.ParentId, x.DefinitionJson }).ExecuteCommandAsync();
                }
                else if (managerWasMissing)
                {
                    await db.Insertable(new SysApplicationMenuEntryEntity
                    {
                        AppCode = "B", ParentId = bRoot.Id, SourceMenuId = manager.MenuId,
                        PermissionKey = manager.MenuName, DefinitionJson = JsonConvert.SerializeObject(manager)
                    }).ExecuteCommandAsync();
                }
                if (!bRows.Any(x => x.PermissionKey == "EmailPush"))
                {
                    await db.Insertable(new SysApplicationMenuEntryEntity
                    {
                        AppCode = "B", ParentId = bRoot.Id, SourceMenuId = email.MenuId,
                        PermissionKey = "EmailPush", DefinitionJson = JsonConvert.SerializeObject(email)
                    }).ExecuteCommandAsync();
                }
            }
            await db.Insertable(new SysApplicationMigrationEntity { Version = version, CompletedAtUtc = DateTime.UtcNow })
                .ExecuteCommandAsync();
        });
        if (!result.IsSuccess) throw result.ErrorException ?? new InvalidOperationException("推送中心菜单整理失败");
    }

    private static sys_menu Create(List<sys_menu> source, string parentId, string name, string path,
        string component, string title, string roles, int sort)
    {
        var id = 930000000;
        var existing = source.Select(x => x.MenuId).ToHashSet();
        while (existing.Contains(id.ToString())) id++;
        return new sys_menu
        {
            MenuId = id.ToString(), ParentId = parentId, MenuName = name, Path = path, Component = component,
            MetaTitle = title, MetaIcon = "ri:mail-send-line", MetaRoles = roles, MetaAuthList = "[]",
            MetaKeepAlive = true, Status = true, Sort = sort, CreateTime = DateTime.Now
        };
    }

    private static string FullPath(sys_menu row, List<sys_menu> rows)
    {
        if (row.Path.StartsWith('/')) return row.Path;
        var parent = rows.FirstOrDefault(x => x.MenuId == row.ParentId);
        return parent == null ? "/" + row.Path : FullPath(parent, rows).TrimEnd('/') + "/" + row.Path;
    }

    private static string FullBPath(SysApplicationMenuEntryEntity row, List<SysApplicationMenuEntryEntity> rows)
    {
        var definition = JsonConvert.DeserializeObject<sys_menu>(row.DefinitionJson)!;
        if (definition.Path.StartsWith('/')) return definition.Path;
        var parent = rows.FirstOrDefault(x => x.Id == row.ParentId);
        return parent == null ? "/" + definition.Path : FullBPath(parent, rows).TrimEnd('/') + "/" + definition.Path;
    }
}
