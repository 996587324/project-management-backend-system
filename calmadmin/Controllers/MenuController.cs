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

        /// <summary>
        /// 构造函数注入业务层接口
        /// </summary>
        public MenuController(IMenuService menuService)
        {
            _menuService = menuService;
        }

        /// <summary>
        /// 获取菜单树形结构（GET接口）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMenuTree()
        {
            var menus = await _menuService.GetFrontMenuTreeAsync();
            return Ok(new { code = 200, msg = "请求成功", data = menus });
        }

        /// <summary>
        /// 根据ID查询单个菜单（GET接口）
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMenuById(string menuId)
        {
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
