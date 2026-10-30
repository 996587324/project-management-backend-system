using AutoMapper;
using Interface;
using Model.Dto;
using Model.Menu;
using Model.SystemApplications;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

/// <summary>B 使用与 A 相同的菜单 DTO、布局和组件加载器，菜单实例在独立表中维护。</summary>
public sealed class SystemApplicationMenuService(SqlSugarScope scope, IMapper mapper) : ISystemApplicationMenuService
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    public async Task<List<FrontMenuDto>> GetTreeAsync(ApplicationAccessContext access, bool includeDisabled = false)
    {
        if (includeDisabled) RequireManager(access);
        using var operation = DatabaseOperationScope.Begin(scope);
        var rows = await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == access.AppCode).ToListAsync();
        return BuildTree(rows, access, 0, new HashSet<int>(), includeDisabled);
    }

    private List<FrontMenuDto> BuildTree(List<SysApplicationMenuEntryEntity> rows, ApplicationAccessContext access,
        int parentId, HashSet<int> ancestors, bool includeDisabled)
    {
        var result = new List<FrontMenuDto>();
        foreach (var row in rows.Where(x => x.ParentId == parentId).OrderBy(x => Read(x).Sort))
        {
            if (!ancestors.Add(row.Id)) throw new InvalidOperationException("系统菜单存在循环父级");
            var menu = Read(row);
            var children = BuildTree(rows, access, row.Id, ancestors, includeDisabled);
            ancestors.Remove(row.Id);
            if (menu.Status != true && !includeDisabled) continue;
            var allowed = access.HasPermission(row.PermissionKey);
            if (row.PermissionKey.StartsWith("WorkTasks", StringComparison.Ordinal)) allowed = access.Permissions.Contains(row.PermissionKey);
            if (row.PermissionKey == "ApplicationAdministration") allowed = access.PlatformAdministrator;
            if (menu.Component == "/shared-data/model-dictionary")
                allowed = access.PlatformAdministrator || access.HasPermission("shared-dictionary.read");
            if (includeDisabled || allowed || children.Count > 0)
            {
                var dto = mapper.Map<FrontMenuDto>(menu);
                dto.meta.isEnable = menu.Status == true;
                dto.meta.sort = menu.Sort ?? 0;
                dto.meta.permissionKey = row.PermissionKey;
                dto.children = children;
                result.Add(dto);
            }
        }
        return result;
    }

    public async Task<sys_menu?> GetMenuAsync(ApplicationAccessContext access, int id)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        RequireManager(access);
        var row = await Db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.Id == id && x.AppCode == access.AppCode);
        return row == null ? null : Read(row);
    }

    public async Task SaveMenuAsync(ApplicationAccessContext access, BackMenuDto dto)
    {
        RequireManager(access);
        await WriteGate.WaitAsync();
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var rows = await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == access.AppCode).ToListAsync();
            var existing = dto.Id > 0 ? rows.FirstOrDefault(x => x.Id == dto.Id) : null;
            if (dto.Id > 0 && existing == null) throw new ArgumentException("菜单不属于当前系统");
            if (dto.MenuType == "button")
            {
                if (existing == null) throw new ArgumentException("请选择按钮所属菜单");
                var definition = Read(existing);
                var buttons = definition.GetMetaAuthList();
                var mark = Text(dto.AuthLabel, 128, "权限标识");
                var button = buttons.FirstOrDefault(x => x.AuthMark == mark);
                if (button == null) buttons.Add(new MenuAuthItem { AuthMark = mark, Title = Text(dto.AuthName, 128, "权限名称") });
                else button.Title = Text(dto.AuthName, 128, "权限名称");
                definition.MetaAuthList = JsonConvert.SerializeObject(buttons);
                existing.DefinitionJson = JsonConvert.SerializeObject(definition);
                existing.UpdateTime = DateTime.Now;
                await Db.Updateable(existing).ExecuteCommandAsync();
                return;
            }
            var name = Text(dto.Name, 128, "路由名称");
            var path = Text(dto.Path, 255, "路由地址");
            var title = Text(dto.Label, 128, "菜单名称");
            if (path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Contains("://") || path.StartsWith("//"))
                throw new ArgumentException("只允许站内路由地址");
            if (rows.Any(x => x.Id != dto.Id && Read(x).MenuName == name)) throw new ArgumentException("当前系统的路由名称不能重复");
            var parent = dto.ParentId.HasValue
                ? rows.FirstOrDefault(x => x.Id == dto.ParentId.Value)
                : !string.IsNullOrWhiteSpace(dto.ActivePath)
                ? rows.FirstOrDefault(x => FullPath(x, rows) == dto.ActivePath)
                : null;
            if (((dto.ParentId.HasValue && dto.ParentId.Value != 0) || (!dto.ParentId.HasValue && !string.IsNullOrWhiteSpace(dto.ActivePath))) && parent == null)
                throw new ArgumentException("当前系统的父菜单不存在");
            for (var cursor = parent; cursor != null; cursor = rows.FirstOrDefault(x => x.Id == cursor.ParentId))
                if (cursor.Id == dto.Id) throw new ArgumentException("不能把菜单移动到自身或子菜单下面");
            var source = await Db.Queryable<sys_menu>().FirstAsync(x => x.Component == dto.Component);
            if (source == null && !string.IsNullOrWhiteSpace(dto.Component)) throw new ArgumentException("组件不在可复用业务组件目录中");
            var parentId = parent?.Id ?? 0;
            if (parentId == 0 && !path.StartsWith('/')) throw new ArgumentException("一级路由须以 / 开头");
            if (parentId != 0 && path.StartsWith('/')) throw new ArgumentException("子菜单路由请填写相对路径");
            if (parentId == 0 && (path is "/portal" or "/403" or "/500" or "/outside" || path.StartsWith("/auth") || path.StartsWith("/public")))
                throw new ArgumentException("路由地址与系统保留入口冲突");
            if (rows.Any(x => x.Id != dto.Id && x.ParentId == parentId && Read(x).Path == path))
                throw new ArgumentException("当前目录的路由地址不能重复");
            var menu = new sys_menu
            {
                MenuName = name, Path = path, Component = source?.Component ?? string.Empty, ParentId = parentId.ToString(),
                MetaTitle = title, MetaIcon = dto.Icon ?? string.Empty, Sort = dto.Sort, Status = dto.IsEnable,
                MetaKeepAlive = dto.KeepAlive, MetaIsHide = dto.IsHide, MetaIsHideTab = dto.IsHideTab,
                MetaFixedTab = dto.FixedTab, MetaIsFullPage = dto.IsFullPage, MetaRoles = string.Empty,
                MetaAuthList = existing != null && Read(existing).Component == dto.Component
                    ? Read(existing).MetaAuthList : source?.MetaAuthList ?? "[]",
                CreateTime = existing == null ? DateTime.Now : Read(existing).CreateTime
            };
            var entity = new SysApplicationMenuEntryEntity
            {
                Id = existing?.Id ?? -1, AppCode = access.AppCode, ParentId = parentId,
                SourceMenuId = existing?.SourceMenuId,
                PermissionKey = existing != null && Read(existing).Component == dto.Component
                    ? existing.PermissionKey : source?.MenuName ?? "__directory__",
                DefinitionJson = JsonConvert.SerializeObject(menu), UpdateTime = DateTime.Now
            };
            var candidateRows = rows.Where(x => x.Id != dto.Id).Append(entity).ToList();
            if (candidateRows.GroupBy(x => FullPath(x, candidateRows), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                throw new ArgumentException("当前系统的完整路由地址不能重复");
            if (existing == null)
            {
                entity.Id = 0;
                await Db.Insertable(entity).ExecuteCommandAsync();
            }
            else await Db.Updateable(entity).ExecuteCommandAsync();
        }
        finally { WriteGate.Release(); }
    }

    public async Task DeleteMenuAsync(ApplicationAccessContext access, int id)
    {
        RequireManager(access);
        await WriteGate.WaitAsync();
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var rows = await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == access.AppCode).ToListAsync();
            var targets = new HashSet<int> { id };
            bool changed;
            do
            {
                changed = false;
                foreach (var row in rows.Where(x => targets.Contains(x.ParentId))) changed |= targets.Add(row.Id);
            } while (changed);
            await Db.Deleteable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == access.AppCode && targets.Contains(x.Id)).ExecuteCommandAsync();
        }
        finally { WriteGate.Release(); }
    }

    public async Task ConfigureSourcesAsync(string appCode, List<string> sourceMenuIds)
    {
        await WriteGate.WaitAsync();
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var source = await Db.Queryable<sys_menu>().ToListAsync();
            var selected = sourceMenuIds.ToHashSet();
            if (selected.Any(id => !source.Any(x => x.MenuId == id))) throw new ArgumentException("菜单不存在");
            foreach (var menu in source.Where(x => selected.Contains(x.MenuId)).ToList())
            {
                for (var parent = menu.ParentId; parent != "0";)
                {
                    var ancestor = source.FirstOrDefault(x => x.MenuId == parent) ?? throw new ArgumentException("菜单父级不存在");
                    if (!selected.Add(parent)) break;
                    parent = ancestor.ParentId;
                }
            }
            var transaction = await Db.Ado.UseTranAsync(async () =>
            {
                var existing = await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == appCode).ToListAsync();
                var removed = existing.Where(x => x.SourceMenuId != null && !selected.Contains(x.SourceMenuId)).Select(x => x.Id).ToArray();
                if (existing.Any(x => !removed.Contains(x.Id) && removed.Contains(x.ParentId)))
                    throw new ArgumentException("待移除目录下还有自定义菜单，请先移动或移除这些菜单");
                if (removed.Length > 0) await Db.Deleteable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == appCode && removed.Contains(x.Id)).ExecuteCommandAsync();
                var ids = existing.Where(x => x.SourceMenuId != null && selected.Contains(x.SourceMenuId))
                    .ToDictionary(x => x.SourceMenuId!, x => x.Id);
                var added = new HashSet<string>();
                foreach (var menu in source.Where(x => selected.Contains(x.MenuId)))
                {
                    if (ids.ContainsKey(menu.MenuId)) continue;
                    var copy = JsonConvert.DeserializeObject<sys_menu>(JsonConvert.SerializeObject(menu))!;
                    copy.MetaRoles = string.Empty;
                    if (existing.Where(x => !removed.Contains(x.Id)).Any(x => Read(x).MenuName == copy.MenuName || FullPath(x, existing) == SourceFullPath(menu, source)))
                        throw new ArgumentException("所选业务组件与 B 已有菜单的名称或路由地址重复");
                    var row = new SysApplicationMenuEntryEntity
                    {
                        AppCode = appCode, SourceMenuId = menu.MenuId, PermissionKey = menu.MenuName,
                        DefinitionJson = JsonConvert.SerializeObject(copy)
                    };
                    ids[menu.MenuId] = await Db.Insertable(row).ExecuteReturnIdentityAsync();
                    added.Add(menu.MenuId);
                }
                foreach (var menu in source.Where(x => added.Contains(x.MenuId)))
                {
                    var id = ids[menu.MenuId];
                    var parentId = ids.GetValueOrDefault(menu.ParentId);
                    await Db.Updateable<SysApplicationMenuEntryEntity>().SetColumns(x => x.ParentId == parentId)
                        .Where(x => x.Id == id && x.AppCode == appCode).ExecuteCommandAsync();
                }
                var finalRows = await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == appCode).ToListAsync();
                if (finalRows.GroupBy(x => Read(x).MenuName).Any(x => x.Count() > 1) ||
                    finalRows.GroupBy(x => FullPath(x, finalRows), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                    throw new ArgumentException("所选组件存在重复路由名称或完整路径，请修改配置后重试");
            });
            if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("保存系统组件菜单失败");
        }
        finally { WriteGate.Release(); }
    }

    public async Task DeleteButtonAsync(ApplicationAccessContext access, int menuId, string authMark)
    {
        RequireManager(access);
        await WriteGate.WaitAsync();
        try
        {
            using var operation = DatabaseOperationScope.Begin(scope);
            var row = await Db.Queryable<SysApplicationMenuEntryEntity>().FirstAsync(x => x.Id == menuId && x.AppCode == access.AppCode);
            if (row == null) throw new ArgumentException("菜单不属于当前系统");
            var definition = Read(row);
            var buttons = definition.GetMetaAuthList();
            buttons.RemoveAll(x => x.AuthMark == authMark);
            definition.MetaAuthList = JsonConvert.SerializeObject(buttons);
            row.DefinitionJson = JsonConvert.SerializeObject(definition);
            row.UpdateTime = DateTime.Now;
            await Db.Updateable(row).ExecuteCommandAsync();
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public static sys_menu Read(SysApplicationMenuEntryEntity row)
    {
        var menu = JsonConvert.DeserializeObject<sys_menu>(row.DefinitionJson) ?? throw new InvalidOperationException("菜单定义损坏");
        menu.MenuId = row.Id.ToString();
        menu.ParentId = row.ParentId.ToString();
        menu.Children = [];
        return menu;
    }

    private static void RequireManager(ApplicationAccessContext access)
    {
        // 修改组件可能改变接口能力映射，仅平台管理员管理；不能借菜单权限扩大自己的业务授权。
        if (!access.PlatformAdministrator || access.AppCode != "B") throw new UnauthorizedAccessException("仅平台管理员可以管理 B 的组件菜单");
    }

    private static string FullPath(SysApplicationMenuEntryEntity row, List<SysApplicationMenuEntryEntity> rows)
    {
        var segments = new List<string>();
        var visited = new HashSet<int>();
        for (var current = row; current != null; current = rows.FirstOrDefault(x => x.Id == current.ParentId))
        {
            if (!visited.Add(current.Id)) throw new ArgumentException("菜单存在循环父级");
            segments.Insert(0, Read(current).Path.Trim('/'));
        }
        return "/" + string.Join('/', segments.Where(x => x.Length > 0));
    }

    private static string SourceFullPath(sys_menu row, List<sys_menu> rows)
    {
        var segments = new List<string>();
        var visited = new HashSet<string>();
        for (var current = row; current != null; current = rows.FirstOrDefault(x => x.MenuId == current.ParentId))
        {
            if (!visited.Add(current.MenuId)) throw new ArgumentException("源菜单存在循环父级");
            segments.Insert(0, current.Path.Trim('/'));
        }
        return "/" + string.Join('/', segments.Where(x => x.Length > 0));
    }

    private static string Text(string? value, int max, string label)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > max) throw new ArgumentException($"{label}不能为空且不能超过 {max} 字符");
        return value;
    }
}
