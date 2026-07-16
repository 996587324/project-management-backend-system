using Model;
using Model.User;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IJwtService
    {
        /// <summary>
        /// 生成AccessToken
        /// </summary>
        /// <param name="userName">用户名</param>
        /// <param name="roles">用户角色（可扩展）</param>
        /// <returns>有效的JWT Token字符串</returns>
        string GenerateAccessToken(string userName, string roles = "super");

        /// <summary>
        /// 生成RefreshToken
        /// </summary>
        /// <returns>RefreshToken字符串</returns>
        string GenerateRefreshToken();
    }
}
