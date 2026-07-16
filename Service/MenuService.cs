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
                }
                else
                {
                    // 【子路由】：自动获取父菜单 → 拼接 component
                    var parentPath = menu.ActivePath;
                    var parentMenu = await _db.GetConnection("calmadmin")
                                        .Queryable<sys_menu>()
                                        .Where(x => x.Path == parentPath && x.ParentId == "0")
                                        .FirstAsync();

                    if (parentMenu == null) throw new Exception("父菜单不存在，请先创建父路由");
                    entity.MenuId = parentMenu.MenuId + menu.Sort.ToString("D2");
                    entity.ParentId = parentMenu.MenuId;               // 父ID
                    entity.Path = menu.Path.Split('/').Last();            // 子路由 path：user
                    entity.Component = $"{parentPath}/{menu.Path.Split('/').Last()}"; // /system/user
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
