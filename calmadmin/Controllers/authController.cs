using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.User;
using Model;
using 上传MES.Config;
using Model.Dto;
using System;

namespace 上传MES.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class authController : ControllerBase
    {
        private IUserService _userService;
        private ICustomJWTService _jwtService;
        public authController(IUserService userService, ICustomJWTService jwtService)
        {
            _userService = userService;
            _jwtService = jwtService;
        }
        [HttpPost]
        public async Task<IActionResult> login(LoginParams login)
        {
           
                if (string.IsNullOrEmpty(login.userName) || string.IsNullOrEmpty(login.password))
                {
                //  return ResultHelper.Error("参数不能为空");
                    return Ok(new { code = 201, msg = "参数不能为空", data = "" });
                }
                UserRes user =  _userService.GetUser(login.userName, login.password);
                if (string.IsNullOrEmpty(user.Name))
                {
                //  return ResultHelper.Error("账号不存在，用户名或密码错误！");
                return Ok(new { code = 202, msg = "账号不存在，用户名或密码错误！", data = "" });
                 }
              //  return ResultHelper.Success();

                return Ok(new { code = 200, msg = "请求成功", data = new TokenData { token = _jwtService.GetToken(user), refreshToken = _jwtService.GenerateRefreshToken() } });
          
          
        }
    }
}
