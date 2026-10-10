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
        private readonly IUserActivityService _userActivityService;
        public authController(IUserService userService, ICustomJWTService jwtService, IUserActivityService userActivityService)
        {
            _userService = userService;
            _jwtService = jwtService;
            _userActivityService = userActivityService;
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
                await _userActivityService.RecordLoginFailureAsync(login.userName, "Password", "账号不存在或密码错误", GetClientIp(), Request.Headers.UserAgent.ToString(), HttpContext.RequestAborted);
                //  return ResultHelper.Error("账号不存在，用户名或密码错误！");
                return Ok(new { code = 202, msg = "账号不存在，用户名或密码错误！", data = "" });
                 }
              //  return ResultHelper.Success();

                var sessionId = await _userActivityService.StartSessionAsync(
                    user.Id.ToString(), user.Name, "Password", GetClientIp(), Request.Headers.UserAgent.ToString(), HttpContext.RequestAborted);
                return Ok(new { code = 200, msg = "请求成功", data = new TokenData { token = _jwtService.GetToken(user, sessionId), refreshToken = _jwtService.GenerateRefreshToken() } });
          
          
        }

        private string? GetClientIp()
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            return string.IsNullOrWhiteSpace(forwarded) ? HttpContext.Connection.RemoteIpAddress?.ToString() : forwarded.Split(',')[0].Trim();
        }
    }
}
