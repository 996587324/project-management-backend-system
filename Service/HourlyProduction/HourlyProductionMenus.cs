using Model.Menu;
using Model.SystemApplications;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

public static class HourlyProductionMenus
{
    public static async Task SeedAsync(ISqlSugarClient db)
    {
        var root = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == "ProductionDashboard");
        if (root == null) return;
        foreach (var item in new[]
        {
            (Name: "HourlyProduction", Path: "hourly-production", Component: "/hourly-production/board", Title: "小时产量看板", Sort: 8),
            (Name: "HourlyProductionConfig", Path: "hourly-production-config", Component: "/hourly-production/config", Title: "小时产量配置", Sort: 9)
        })
        {
            var menu = await db.Queryable<sys_menu>().FirstAsync(x => x.MenuName == item.Name);
            if (menu == null)
            {
                var ids = (await db.Queryable<sys_menu>().Select(x => x.MenuId).ToListAsync()).ToHashSet();
                var id = 930000000;
                while (ids.Contains(id.ToString())) id++;
                menu = new sys_menu
                {
                    MenuId = id.ToString(), ParentId = root.MenuId, MenuName = item.Name, Path = item.Path,
                    Component = item.Component, MetaTitle = item.Title, MetaIcon = "ri:bar-chart-grouped-line",
                    MetaRoles = "", MetaAuthList = "[]", Status = true, Sort = item.Sort, CreateTime = DateTime.Now
                };
                await db.Insertable(menu).ExecuteCommandAsync();
            }
            if (!await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == menu.MenuId))
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = menu.MenuId }).ExecuteCommandAsync();
            // 尊重 B 系统独立菜单；没有产线目录时仅进入可选组件目录，不擅自授权。
            var parent = await db.Queryable<SysApplicationMenuEntryEntity>()
                .FirstAsync(x => x.AppCode == "B" && x.SourceMenuId == root.MenuId);
            if (parent != null && !await db.Queryable<SysApplicationMenuEntryEntity>()
                    .AnyAsync(x => x.AppCode == "B" && x.PermissionKey == item.Name))
                await db.Insertable(new SysApplicationMenuEntryEntity
                {
                    AppCode = "B", ParentId = parent.Id, SourceMenuId = menu.MenuId,
                    PermissionKey = item.Name, DefinitionJson = JsonConvert.SerializeObject(menu), UpdateTime = DateTime.Now
                }).ExecuteCommandAsync();
        }
    }
}
