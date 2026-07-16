using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.User;
using System.Security.Claims;

namespace calmadmin.Controllers
{
    [ApiController]
    [Route("api/[controller]/[action]")]
    public class UserInfoController : ControllerBase
    {
        private readonly IUserInfoService _userInfoService;
        private readonly IRoleService _roleService;

        public UserInfoController(IUserInfoService userInfoService, IRoleService roleService)
        {
            _userInfoService = userInfoService;
            _roleService = roleService;
        }

        [Authorize]
        [HttpGet]
        public async Task<IActionResult> GetUserInfo()
        {
            var claimUserKey = User.FindFirst("Id")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("Name")?.Value
                ?? User.FindFirst(ClaimTypes.Name)?.Value;

            if (string.IsNullOrWhiteSpace(claimUserKey))
            {
                return Unauthorized(new { code = 401, msg = "登录状态已失效，请重新登录", data = "" });
            }

            var user = await _userInfoService.GetUserInfoByUser(claimUserKey);
            if (user == null)
            {
                return Unauthorized(new { code = 401, msg = "当前登录用户不存在或已停用", data = "" });
            }

            var permissionKeys = await GetPermissionKeys(user);
            return Ok(new
            {
                code = 200,
                msg = "请求成功",
                data = new
                {
                    userId = user.UserId,
                    userName = user.UserName,
                    roles = user.RoleList,
                    buttons = permissionKeys.Where(x => x.Contains("_")).ToList(),
                    permissionKeys,
                    email = user.Email
                }
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetUserPage([FromQuery] AccountQueryDto query)
        {
            var result = await _userInfoService.GetUserPageAsync(query);
            return Ok(new { code = 200, msg = "查询成功", data = result });
        }

        [HttpGet]
        public async Task<IActionResult> GetUserPermission([FromQuery] string id)
        {
            var result = await _userInfoService.GetUserPermissionAsync(id);
            if (result == null) return Ok(new { code = 404, msg = "用户不存在", data = "" });
            return Ok(new { code = 200, msg = "查询成功", data = result });
        }

        [HttpPost]
        public async Task<IActionResult> AddUser([FromBody] AccountSaveDto input)
        {
            if (string.IsNullOrWhiteSpace(input.userName))
            {
                return Ok(new { code = 400, msg = "登录账号不能为空", data = "" });
            }

            var result = await _userInfoService.AddUserAsync(input);
            return Ok(new { code = result ? 200 : 500, msg = result ? "新增成功" : "新增失败", data = "" });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateUser([FromBody] AccountSaveDto input)
        {
            var result = await _userInfoService.UpdateUserAsync(input);
            return Ok(new { code = result ? 200 : 500, msg = result ? "修改成功" : "修改失败", data = "" });
        }

        [HttpPost]
        public async Task<IActionResult> DeleteUser([FromBody] AccountSaveDto input)
        {
            var id = input.userId ?? input.id;
            if (string.IsNullOrWhiteSpace(id))
            {
                return Ok(new { code = 400, msg = "用户ID不能为空", data = "" });
            }

            var result = await _userInfoService.DeleteUserAsync(id);
            return Ok(new { code = result ? 200 : 500, msg = result ? "删除成功" : "删除失败", data = "" });
        }

        [HttpPost]
        public async Task<IActionResult> SaveUserPermission([FromBody] AccountSaveDto input)
        {
            var result = await _userInfoService.SaveUserPermissionAsync(input);
            return Ok(new { code = result ? 200 : 500, msg = result ? "保存成功" : "保存失败", data = "" });
        }

        private async Task<List<string>> GetPermissionKeys(SysUserInfo user)
        {
            if (user.RoleList.Contains("R_ADMIN") || user.PermissionKeyList.Contains("*"))
            {
                return new List<string> { "*" };
            }

            var rolePage = await _roleService.GetRolePageListAsync(1, 1000);
            var roleKeys = rolePage.records
                .Where(x => user.RoleList.Contains(x.roleCode) && x.enabled)
                .SelectMany(x => x.permissionKeyList);

            return roleKeys.Concat(user.PermissionKeyList)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
        }
    }
}
