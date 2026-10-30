using AutoMapper;
using Interface;
using Kdbndp.TypeMapping;
using Model.Common;
using Model.Dto;
using Model.Menu;
using Model.SystemApplications;
using Model.User;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class MenuService: IMenuService
    {
        private readonly SqlSugarScope _db;
        private readonly IMapper _mapper;
        public MenuService(SqlSugarScope dbFactory, IMapper mapper)
        {
            this._db = dbFactory;
            this._mapper = mapper;
        }

        /// <summary>
        /// 查询菜单树形结构
        /// </summary>
        public async Task<List<sys_menu>> GetMenuTreeAsync()
        {
            // 请求路径只查询数据，不建表、不补菜单、不修改角色权限。
            var allMenus = await _db.GetConnection("calmadmin").Queryable<sys_menu>()
                .Where(m => m.Status == true)
                .OrderBy(m => m.Sort)
                .ToListAsync();

            var rootMenus = allMenus.Where(m => m.ParentId == "0").ToList();
            foreach (var root in rootMenus)
            {
                root.Children = BuildChildMenu(root.MenuId, allMenus);
            }

            return rootMenus;
        }

        /// <summary>
        /// 递归构建子菜单
        /// </summary>
        private List<sys_menu> BuildChildMenu(string parentId, List<sys_menu> allMenus)
        {
            var children = allMenus.Where(c => c.ParentId == parentId).ToList();
            foreach (var child in children)
            {
                child.Children = BuildChildMenu(child.MenuId, allMenus);
            }
            return children;
        }
        /// <summary>
        /// 转换为前端DTO
        /// </summary>
        public async Task<List<FrontMenuDto>> GetFrontMenuTreeAsync()
        {
            // 1. 查询数据库实体
            var dbMenuTree = await GetMenuTreeAsync();
            // 2. AutoMapper自动映射：List<SysMenu> → List<FrontMenuDto>
            var frontMenuTree = _mapper.Map<List<FrontMenuDto>>(dbMenuTree);
            return frontMenuTree;
        }

        /// <summary>
        /// 新增菜单
        /// </summary>
        public async Task<bool> AddMenuAsync(BackMenuDto menu)
        {
            if (menu.MenuType == "menu")
                return await AddRouteMenuAsync(menu);
            try
            {
                var entity = new sys_menu();
                entity.CreateTime = DateTime.Now;

                if (menu.Component == "/index/index")
                {       // 父菜单：取最大ID +1
                    var max = await _db.GetConnection("calmadmin").Queryable<sys_menu>()
                        .Where(x => x.ParentId == "0").ToListAsync();
                    var maxindex = max.Select(x => Convert.ToInt32(x.MenuId)).Max();
                    entity.MenuId = (maxindex + 1).ToString(); // 主键ID
                    entity.ParentId = "0";
                    entity.Path = menu.Path;                  // 父路由 path：/system
                    entity.Component = "/index/index";       // 固定组件
                    entity.MetaTitle = $"menus.{menu.Label}.title";
                }
                else
                {
                    // 【子路由】：自动获取父菜单 → 拼接 component
                    var parentPath = menu.ActivePath;
                    var parentPathname = menu.ActivePath.Split('/').Last();
                    var parentMenu = await _db.GetConnection("calmadmin")
                                        .Queryable<sys_menu>()
                                        .Where(x => x.Path == parentPath && x.ParentId == "0")
                                        .FirstAsync();

                    if (parentMenu == null) throw new Exception("父菜单不存在，请先创建父路由");
                    entity.MenuId = parentMenu.MenuId + menu.Sort.ToString("D2");
                    entity.ParentId = parentMenu.MenuId;               // 父ID
                    entity.Path = menu.Path.Split('/').Last();            // 子路由 path：user
                    entity.Component = $"{parentPath}/{menu.Path.Split('/').Last()}"; // /system/user
                    entity.MetaTitle = $"menus.{parentPath}.{entity.Path}";
                }

                // 基础字段映射
                entity.MenuName = menu.Label;
       
                entity.MetaIcon = menu.Icon;
                entity.Sort = menu.Sort;
                entity.Status = menu.IsEnable;
                entity.MetaKeepAlive = menu.KeepAlive;
                entity.MetaIsHide = menu.IsHide;
                entity.MetaIsHideTab = menu.IsHideTab;
                entity.MetaFixedTab = menu.FixedTab;
                entity.MetaIsFullPage = menu.IsFullPage;
                entity.MetaRoles = menu.Roles != null && menu.Roles.Any() ? string.Join(",", menu.Roles) : null;

                // 插入
                var result = await _db.GetConnection("calmadmin").Insertable(entity).ExecuteCommandAsync();
                return result > 0;
            }
            catch
            {
                throw;
            }
           
        }

        private async Task<bool> AddRouteMenuAsync(BackMenuDto menu)
        {
            static string Required(string? value, int max, string label)
            {
                var text = value?.Trim() ?? string.Empty;
                if (text.Length == 0 || text.Length > max) throw new ArgumentException($"{label}不能为空且不能超过{max}个字符");
                return text;
            }
            var name = Required(menu.Name, 128, "菜单标识");
            var title = Required(menu.Label, 128, "菜单名称");
            var path = Required(menu.Path, 255, "路由地址");
            var component = menu.Component?.Trim() ?? string.Empty;
            var icon = menu.Icon?.Trim() ?? string.Empty;
            var roles = string.Join(",", menu.Roles ?? []);
            if (component.Length > 255 || icon.Length > 64 || roles.Length > 512)
                throw new ArgumentException("组件路径、图标或角色权限超过允许长度");
            if (path.Contains('?') || path.Contains('#') || path.Contains("..") || path.Contains("://") || path.StartsWith("//"))
                throw new ArgumentException("只允许站内路由地址");

            using var db = _db.GetConnection("calmadmin").CopyNew();
            var transaction = await db.Ado.UseTranAsync(async () =>
            {
                // 菜单编号和 A 系统关联一起提交；跨实例串行分配，避免与已有根菜单或子菜单冲突。
                await db.Ado.ExecuteCommandAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='calmadmin:menu:create', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @r < 0 THROW 51000, 'menu create lock timeout', 1;");
                var rows = await db.Queryable<sys_menu>().ToListAsync();
                var byId = rows.ToDictionary(x => x.MenuId, StringComparer.OrdinalIgnoreCase);
                string FullPath(sys_menu item)
                {
                    var segments = new Stack<string>();
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var current = item; current != null && visited.Add(current.MenuId);)
                    {
                        segments.Push(current.Path.Trim('/'));
                        if (current.ParentId == "0" || !byId.TryGetValue(current.ParentId, out var parent)) break;
                        current = parent;
                    }
                    return "/" + string.Join('/', segments.Where(x => x.Length > 0));
                }
                sys_menu? parentMenu = null;
                if (menu.ParentId.GetValueOrDefault() > 0)
                    byId.TryGetValue(menu.ParentId!.Value.ToString(), out parentMenu);
                else if (!string.IsNullOrWhiteSpace(menu.ActivePath))
                    parentMenu = rows.FirstOrDefault(x => string.Equals(FullPath(x), menu.ActivePath.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if ((menu.ParentId.GetValueOrDefault() > 0 || !string.IsNullOrWhiteSpace(menu.ActivePath)) && parentMenu == null)
                    throw new ArgumentException("父菜单不存在，请选择或填写有效的父路径");
                if (parentMenu != null && !await db.Queryable<SysApplicationMenuEntity>().AnyAsync(x => x.AppCode == "A" && x.MenuId == parentMenu.MenuId))
                    throw new ArgumentException("父菜单尚未加入当前系统，不能在其下新增菜单");
                if (parentMenu == null && !path.StartsWith('/')) throw new ArgumentException("一级路由地址必须以 / 开头");
                if (parentMenu != null && path.StartsWith('/'))
                {
                    var prefix = FullPath(parentMenu) + "/";
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("子菜单路由请填写相对路径或父路径下的完整地址");
                    path = path[prefix.Length..];
                }
                if (string.IsNullOrWhiteSpace(path.Trim('/'))) throw new ArgumentException("路由地址不能为空");
                var fullPath = parentMenu == null ? path.TrimEnd('/') : FullPath(parentMenu).TrimEnd('/') + "/" + path.Trim('/');
                if (rows.Any(x => string.Equals(x.MenuName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("菜单标识已存在，请使用其他标识");
                if (rows.Any(x => string.Equals(FullPath(x), fullPath, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("路由地址已存在，请使用其他地址");

                var usedIds = rows.Select(x => x.MenuId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var candidate = 1_000_000_000;
                while (usedIds.Contains(candidate.ToString()))
                {
                    if (candidate == int.MaxValue) throw new InvalidOperationException("可用菜单编号不足");
                    candidate++;
                }
                var entity = new sys_menu
                {
                    MenuId = candidate.ToString(), ParentId = parentMenu?.MenuId ?? "0", MenuName = name,
                    Path = path, Component = component, MetaTitle = title, MetaIcon = icon, Sort = menu.Sort,
                    Status = menu.IsEnable, MetaKeepAlive = menu.KeepAlive, MetaIsHide = menu.IsHide,
                    MetaIsHideTab = menu.IsHideTab, MetaFixedTab = menu.FixedTab, MetaIsFullPage = menu.IsFullPage,
                    MetaRoles = roles, MetaAuthList = "[]", CreateTime = DateTime.Now
                };
                await db.Insertable(entity).ExecuteCommandAsync();
                await db.Insertable(new SysApplicationMenuEntity { AppCode = "A", MenuId = entity.MenuId }).ExecuteCommandAsync();
            });
            if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("新增菜单失败");
            return true;
        }
        /// <summary>
        /// 修改菜单
        /// </summary>
        public async Task<bool> UpdateMenuAsync(BackMenuDto menu)
        {
            var entity = await _db.GetConnection("calmadmin")
                                      .Queryable<sys_menu>()
                                   .FirstAsync(x => x.MenuId == menu.Id.ToString());

            if (entity == null) throw new Exception("菜单不存在");

            // ====================== 编辑也自动更新父子结构 ======================
            if (menu.Component == "/index/index")
            {
                entity.ParentId = "0";
                entity.Path = menu.Path;
                entity.Component = "/index/index";
            }
            else
            {
                var parentPath = menu.ActivePath;
                var parentMenu = await _db.GetConnection("calmadmin")
                                    .Queryable<sys_menu>()
                                .Where(x => x.Path == parentPath && x.ParentId == "0")
                                .FirstAsync();

                if (parentMenu == null) throw new Exception("父菜单不存在");

                entity.ParentId = parentMenu.MenuId;
                entity.Path = menu.Path.Split('/').Last();
                entity.Component = $"{parentPath}/{menu.Path.Split('/').Last()}";
            }

            // 基础字段映射
            entity.MenuName = menu.Name;
            entity.MetaTitle = menu.Label;
            entity.MetaIcon = menu.Icon;
            entity.Sort = menu.Sort;
            entity.Status = menu.IsEnable;
            entity.MetaKeepAlive = menu.KeepAlive;
            entity.MetaIsHide = menu.IsHide;
            entity.MetaIsHideTab = menu.IsHideTab;
            entity.MetaFixedTab = menu.FixedTab;
            entity.MetaIsFullPage = menu.IsFullPage;
            entity.MetaRoles = menu.Roles != null && menu.Roles.Any() ? string.Join(",", menu.Roles) : null;
            var result = await _db.GetConnection("calmadmin").Updateable(entity).ExecuteCommandAsync();
            return result > 0;
        }

        /// <summary>
        /// 删除菜单（级联删除子菜单）
        /// </summary>
        public async Task<bool> DeleteMenuAsync(string menuId)
        {
            if (string.IsNullOrWhiteSpace(menuId))
            {
                return false;
            }

            // 1. 查询所有子菜单ID
            var allMenus = await _db.GetConnection("calmadmin").Queryable<sys_menu>().ToListAsync();
            var childIds = GetAllChildMenuIds(menuId, allMenus);
            childIds.Add(menuId); // 包含自身

            // 2. 批量删除
            var result = await _db.GetConnection("calmadmin").Deleteable<sys_menu>()
                                  .In(childIds)
                                  .ExecuteCommandAsync();
            return result > 0;
        }

        /// <summary>
        /// 递归获取所有子菜单ID
        /// </summary>
        private List<string> GetAllChildMenuIds(string parentId, List<sys_menu> allMenus)
        {
            var childIds = allMenus.Where(c => c.ParentId == parentId).Select(c => c.MenuId).ToList();
            var allChildIds = new List<string>();
            foreach (var id in childIds)
            {
                allChildIds.Add(id);
                allChildIds.AddRange(GetAllChildMenuIds(id, allMenus));
            }
            return allChildIds;
        }

        /// <summary>
        /// 根据ID查询单个菜单
        /// </summary>
        public async Task<sys_menu> GetMenuByIdAsync(string menuId)
        {
            return await _db.GetConnection("calmadmin").Queryable<sys_menu>()
                            .Where(m => m.MenuId == menuId)
                            .FirstAsync();
        }
    }
}
