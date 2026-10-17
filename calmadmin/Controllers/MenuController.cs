using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.Dto;
using Model.Menu;

namespace calmadmin.Controllers
{
    [ApiController]
    [Route("api/[controller]/[action]")]
    public class MenuController : ControllerBase
    {
        private readonly IMenuService _menuService;
        private readonly ISystemApplicationService _applications;

        /// <summary>
        /// 构造函数注入业务层接口
        /// </summary>
        public MenuController(IMenuService menuService, ISystemApplicationService applications)
        {
            _menuService = menuService;
            _applications = applications;
        }

        /// <summary>
        /// 获取菜单树形结构（GET接口）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMenuTree()
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (access.AppCode == "B")
            {
                var tree = await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().GetTreeAsync(access);
                return Ok(new { code = 200, msg = "请求成功", data = tree });
            }
            var menus = await _menuService.GetFrontMenuTreeAsync();
            var db = HttpContext.RequestServices.GetRequiredService<SqlSugar.SqlSugarScope>().GetConnectionScope("calmadmin");
            var assigned = (await db.Queryable<Model.SystemApplications.SysApplicationMenuEntity>()
                .Where(x => x.AppCode == access.AppCode).ToListAsync()).Select(x => x.MenuId).ToHashSet();
            var filtered = FilterMenus(menus, assigned, access);
            return Ok(new { code = 200, msg = "请求成功", data = filtered });
        }

        [HttpGet]
        public async Task<IActionResult> GetManagementMenuTree()
        {
            var access = await _applications.GetAccessAsync(User, true);
            var tree = await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().GetTreeAsync(access, true);
            return Ok(new { code = 200, msg = "请求成功", data = tree });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteMenuButton([FromQuery] int menuId, [FromQuery] string authMark)
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (menuId <= 0 || string.IsNullOrWhiteSpace(authMark)) throw new ArgumentException("菜单和权限标识不能为空");
            await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().DeleteButtonAsync(access, menuId, authMark);
            return Ok(new { code = 200, msg = "删除成功", data = true });
        }

        private static List<FrontMenuDto> FilterMenus(List<FrontMenuDto> menus, HashSet<string> assigned,
            Model.SystemApplications.ApplicationAccessContext access)
        {
            var result = new List<FrontMenuDto>();
            foreach (var menu in menus)
            {
                menu.children = FilterMenus(menu.children, assigned, access);
                var allowed = assigned.Contains(menu.id) && access.HasPermission(menu.name);
                if (menu.name == "ApplicationAdministration") allowed = assigned.Contains(menu.id) && access.PlatformAdministrator;
                if (menu.name is "SharedModelDictionary" or "SystemBSharedModelDictionary")
                    allowed = assigned.Contains(menu.id) && (access.PlatformAdministrator || access.HasPermission("shared-dictionary.read"));
                if (allowed || (assigned.Contains(menu.id) && menu.children.Count > 0))
                    result.Add(menu);
            }
            return result;
        }

        /// <summary>
        /// 根据ID查询单个菜单（GET接口）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMenuById(string menuId)
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (access.AppCode == "B")
            {
                if (!int.TryParse(menuId, out var id) || id <= 0)
                    return BadRequest(new { code = 400, msg = "菜单编号无效", data = "" });
                return Ok(new { code = 200, msg = "请求成功", data = await HttpContext.RequestServices
                    .GetRequiredService<ISystemApplicationMenuService>().GetMenuAsync(access, id) });
            }
            var menu = await _menuService.GetMenuByIdAsync(menuId);
            if (menu == null)
            {
                return Ok(new { code = 404, msg = "菜单不存在", data = "" });
            }
            return Ok(new { code = 200, msg = "请求成功", data = menu });
        }

        /// <summary>
        /// 新增菜单（POST接口）
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddMenu([FromBody] BackMenuDto menu)
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (access.AppCode == "B")
            {
                if (menu.MenuType != "button") menu.Id = 0;
                await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().SaveMenuAsync(access, menu);
                return Ok(new { code = 200, msg = "新增成功", data = true });
            }
            if (!ModelState.IsValid)
            {
                return Ok(new { code = 400, msg = "参数错误", data = "" });
            }
            var result = await _menuService.AddMenuAsync(menu);
            if (result)
            {
                return Ok(new { code = 200, msg = "新增成功", data = menu });
            }
            return Ok(new { code = 500, msg = "新增失败", data = "" });
        }

        /// <summary>
        /// 修改菜单（PUT接口）
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UpdateMenu([FromBody] BackMenuDto menu)
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (access.AppCode == "B")
            {
                await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().SaveMenuAsync(access, menu);
                return Ok(new { code = 200, msg = "修改成功", data = true });
            }
            var result = await _menuService.UpdateMenuAsync(menu);
            if (result)
            {
                return Ok(new { code = 200, msg = "修改成功", data = menu });
            }
            return Ok(new { code = 500, msg = "修改失败", data = "" });
        }

        /// <summary>
        /// 删除菜单（DELETE接口）
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> DeleteMenu([FromQuery] string menuId)
        {
            var access = await _applications.GetAccessAsync(User, true);
            if (access.AppCode == "B")
            {
                if (!int.TryParse(menuId, out var id) || id <= 0)
                    return BadRequest(new { code = 400, msg = "菜单编号无效", data = "" });
                await HttpContext.RequestServices.GetRequiredService<ISystemApplicationMenuService>().DeleteMenuAsync(access, id);
                return Ok(new { code = 200, msg = "删除成功", data = true });
            }
            if (string.IsNullOrWhiteSpace(menuId))
            {
                return Ok(new { code = 400, msg = "菜单ID不能为空", data = "" });
            }

            var result = await _menuService.DeleteMenuAsync(menuId);
            if (result)
            {
                return Ok(new { code = 200, msg = "删除成功", data = "" });
            }
            return Ok(new { code = 500, msg = "删除失败", data = "" });
        }
    }
}
