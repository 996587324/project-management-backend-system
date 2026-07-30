using AutoMapper;
using Interface;
using Kdbndp.TypeMapping;
using Model.Common;
using Model.Dto;
using Model.Menu;
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
            try
            {
                await EnsureAgentMenuAsync();
                await EnsureProjectManageMenuPermissionsAsync();
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
            catch(Exception ex)
            {
                throw ex;
            }
        }

        private async Task EnsureProjectManageMenuPermissionsAsync()
        {
            var db = _db.GetConnection("calmadmin");
            var menus = await db.Queryable<sys_menu>()
                .Where(x =>
                    x.MenuName == "ProjectLedger" ||
                    x.MenuName == "TaskBoard" ||
                    x.MenuName == "ProjectFile" ||
                    x.Component == "/projectmanage/project" ||
                    x.Component == "/projectmanage/task" ||
                    x.Component == "/projectmanage/file")
                .ToListAsync();

            foreach (var menu in menus)
            {
                var routeName = GetProjectManageRouteName(menu);
                if (string.IsNullOrWhiteSpace(routeName)) continue;

                var requiredAuthList = GetProjectManageAuthList(routeName);

                var mergedJson = MergeAuthList(menu.MetaAuthList, requiredAuthList, out var changed);
                var nameChanged = !string.Equals(menu.MenuName, routeName, StringComparison.OrdinalIgnoreCase);
                if (!changed && !nameChanged) continue;

                menu.MenuName = routeName;
                menu.MetaAuthList = mergedJson;
                await db.Updateable(menu)
                    .UpdateColumns(x => new { x.MenuName, x.MetaAuthList })
                    .ExecuteCommandAsync();
            }
        }

        private static string? GetProjectManageRouteName(sys_menu menu)
        {
            var name = menu.MenuName ?? string.Empty;
            var component = (menu.Component ?? string.Empty).Replace("\\", "/");

            if (name.Equals("ProjectLedger", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("/projectmanage/project", StringComparison.OrdinalIgnoreCase))
            {
                return "ProjectLedger";
            }

            if (name.Equals("TaskBoard", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("/projectmanage/task", StringComparison.OrdinalIgnoreCase))
            {
                return "TaskBoard";
            }

            if (name.Equals("ProjectFile", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("/projectmanage/file", StringComparison.OrdinalIgnoreCase))
            {
                return "ProjectFile";
            }

            return null;
        }

        private static List<MenuAuthItem> GetProjectManageAuthList(string routeName)
        {
            if (routeName.Equals("ProjectLedger", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MenuAuthItem>
                {
                    NewAuth("新增项目", "Create"),
                    NewAuth("批量导入", "Import"),
                    NewAuth("下载模板", "Template"),
                    NewAuth("编辑项目", "Update"),
                    NewAuth("变更状态", "Status"),
                    NewAuth("删除项目", "Delete")
                };
            }

            if (routeName.Equals("TaskBoard", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MenuAuthItem>
                {
                    NewAuth("新增任务", "Create"),
                    NewAuth("编辑任务", "Update"),
                    NewAuth("变更状态", "Status"),
                    NewAuth("删除任务", "Delete")
                };
            }

            if (routeName.Equals("ProjectFile", StringComparison.OrdinalIgnoreCase))
            {
                return new List<MenuAuthItem>
                {
                    NewAuth("上传文件", "Upload"),
                    NewAuth("在线预览", "Preview"),
                    NewAuth("下载文件", "Download"),
                    NewAuth("删除文件", "Delete")
                };
            }

            return new List<MenuAuthItem>();
        }

        private static string MergeAuthList(string? authListJson, List<MenuAuthItem> requiredAuthList, out bool changed)
        {
            changed = false;
            var current = new List<MenuAuthItem>();

            if (!string.IsNullOrWhiteSpace(authListJson))
            {
                try
                {
                    current = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MenuAuthItem>>(authListJson) ?? new List<MenuAuthItem>();
                }
                catch
                {
                    changed = true;
                }
            }

            var existingMarks = new HashSet<string>(
                current
                    .Select(x => x.AuthMark)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in requiredAuthList)
            {
                if (existingMarks.Contains(item.AuthMark)) continue;

                current.Add(item);
                existingMarks.Add(item.AuthMark);
                changed = true;
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(current);
        }

        private static MenuAuthItem NewAuth(string title, string authMark) => new()
        {
            Title = title,
            AuthMark = authMark
        };

        private async Task EnsureAgentMenuAsync()
        {
            var db = _db.GetConnection("calmadmin");
            db.CodeFirst.SetStringDefaultLength(200).InitTables(typeof(sys_menu));

            var exists = await db.Queryable<sys_menu>().AnyAsync(x => x.Path == "/agent" || x.MenuName == "Agent");
            if (exists) return;

            var rootMenus = await db.Queryable<sys_menu>().Where(x => x.ParentId == "0").ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            var rootId = (maxRootId + 1).ToString();
            var now = DateTime.Now;

            await db.Insertable(new sys_menu
            {
                MenuId = rootId,
                ParentId = "0",
                MenuName = "Agent",
                Path = "/agent",
                Component = "/index/index",
                MetaTitle = "Agent 中心",
                MetaIcon = "ri:robot-2-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 98,
                Status = true,
                CreateTime = now
            }).ExecuteCommandAsync();

            await db.Insertable(new sys_menu
            {
                MenuId = $"{rootId}01",
                ParentId = rootId,
                MenuName = "AgentExecutor",
                Path = "executor",
                Component = "/agent/executor",
                MetaTitle = "项目智能助手",
                MetaIcon = "ri:robot-2-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 1,
                Status = true,
                CreateTime = now
            }).ExecuteCommandAsync();
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
            catch(Exception ex)
            {
                throw ex;
            }
           
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
