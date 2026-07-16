using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.User;

namespace calmadmin.Controllers
{
    [ApiController]
    [Route("api/[controller]/[action]")]
    public class RoleController : ControllerBase
    {
        private readonly IRoleService _roleService;

        public RoleController(IRoleService roleService)
        {
            _roleService = roleService;
        }

        [HttpGet]
        public async Task<IActionResult> GetRolePage([FromQuery] int current = 1, [FromQuery] int size = 10)
        {
            var result = await _roleService.GetRolePageListAsync(current, size);
            return Ok(new { code = 200, msg = "查询成功", data = result });
        }

        [HttpPost]
        public async Task<IActionResult> CreateRole([FromBody] SysRole role)
        {
            if (string.IsNullOrWhiteSpace(role.roleName) || string.IsNullOrWhiteSpace(role.roleCode))
            {
                return Ok(new { code = 400, msg = "角色名称和角色编码不能为空", data = "" });
            }

            var result = await _roleService.CreateRoleAsync(role);
            return Ok(new { code = 200, msg = "新增成功", data = result });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateRole([FromBody] SysRole role)
        {
            if (role.roleId <= 0)
            {
                return Ok(new { code = 400, msg = "角色ID不能为空", data = "" });
            }

            var result = await _roleService.UpdateRoleAsync(role);
            return Ok(new { code = 200, msg = "修改成功", data = result });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteRole([FromBody] RoleDeleteDto input)
        {
            if (input == null || input.roleId <= 0)
            {
                return Ok(new { code = 400, msg = "角色ID不能为空", data = "" });
            }

            try
            {
                var result = await _roleService.DeleteRoleAsync(input.roleId);
                return Ok(new { code = result ? 200 : 500, msg = result ? "删除成功" : "删除失败", data = "" });
            }
            catch (Exception ex)
            {
                return Ok(new { code = 400, msg = ex.Message, data = "" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetRolePermission([FromQuery] int roleId)
        {
            var result = await _roleService.GetRolePermissionAsync(roleId);
            if (result == null) return Ok(new { code = 404, msg = "角色不存在", data = "" });
            return Ok(new { code = 200, msg = "查询成功", data = result });
        }

        [HttpPost]
        public async Task<IActionResult> SaveRolePermission([FromBody] RolePermissionSaveDto input)
        {
            var result = await _roleService.SaveRolePermissionAsync(input);
            return Ok(new { code = result ? 200 : 500, msg = result ? "保存成功" : "保存失败", data = "" });
        }
    }
}
