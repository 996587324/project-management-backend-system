using Model.Menu;
using Model.SystemApplications;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>只补缺失菜单，已有名称、排序、启停及 B 独立菜单均不覆盖。</summary>
public static class ProcessBoardMenus
{
    public static async Task SeedAsync(ISqlSugarClient db)
    {
        var root = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "ProductionDashboard");
        if (root == null) return;
        foreach (var item in new[]
        {
            (Name: "ProcessBoard", Path: "process-board", Component: "/process-board/board", Title: "产线工艺看板", Sort: 6),
            (Name: "ProcessBoardConfig", Path: "process-config", Component: "/process-board/config", Title: "工艺看板配置", Sort: 7)
        })
        {
            var menu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == item.Name);
            if (menu == null)
            {
                // A 的编辑 DTO 使用 int，不能生成只能显示但无法编辑的 GUID 菜单编号。
                var existingIds = (await db.Queryable<sys_menu>().Select(x => x.MenuId).ToListAsync()).ToHashSet();
                var preferred = root.MenuId + item.Sort.ToString("D2");
                var candidate = int.TryParse(preferred, out var parsed) && parsed > 0 && !existingIds.Contains(preferred)
                    ? parsed : 920000000;
                while (existingIds.Contains(candidate.ToString())) candidate++;
                menu = new sys_menu
                {
                    MenuId = candidate.ToString(), ParentId = root.MenuId, MenuName = item.Name,
                    Path = item.Path, Component = item.Component, MetaTitle = item.Title, MetaIcon = "ri:flow-chart",
                    MetaRoles = "", MetaAuthList = "[]", Status = true, Sort = item.Sort, CreateTime = DateTime.Now
                };
                await db.Insertable(menu).ExecuteCommandAsync();
            }
            if (!await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == menu.MenuId))
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = menu.MenuId }).ExecuteCommandAsync();
            if (!await db.Queryable<SysApplicationMenuEntryEntity>().AnyAsync(x => x.AppCode == "B" && x.PermissionKey == item.Name))
            {
                var parent = await db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.AppCode == "B" && x.SourceMenuId == root.MenuId);
                // B 未开放产线目录时不擅自开放整套模块，管理员可从业务组件目录自行添加。
                if (parent == null) continue;
                await db.Insertable(new SysApplicationMenuEntryEntity
                {
                    AppCode = "B", ParentId = parent.Id, SourceMenuId = menu.MenuId,
                    PermissionKey = item.Name, DefinitionJson = JsonConvert.SerializeObject(menu), UpdateTime = DateTime.Now
                }).ExecuteCommandAsync();
            }
        }
    }
}
