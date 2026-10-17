using Interface;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Model.Other;
using Model.User;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class CustomHSJWTService : ICustomJWTService
    {
        private readonly JWTTokenOptions _JWTTokenOptions;
        public CustomHSJWTService(IOptionsMonitor<JWTTokenOptions> jwtTokenOptions)
        {
            _JWTTokenOptions = jwtTokenOptions.CurrentValue;
        }
        /// <summary>
        /// 获取token
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public string GetToken(UserRes user, string? sessionId = null)
        {
            #region 有效载荷
            var claims = new List<Claim>
            {
                new("Id", user.Id.ToString()),
                new("Name", user.Name),
                new("UserType", user.UserType.ToString()),
                new("TokenPurpose", "portal"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };
            if (!string.IsNullOrWhiteSpace(sessionId)) claims.Add(new Claim("SessionId", sessionId));

            //需要加密：需要加密key:
            //Nuget引入：Microsoft.IdentityModel.Tokens
            SymmetricSecurityKey key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_JWTTokenOptions.SecurityKey));

            SigningCredentials creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            //Nuget引入：System.IdentityModel.Tokens.Jwt
            JwtSecurityToken token = new JwtSecurityToken(
             issuer: _JWTTokenOptions.Issuer,
             audience: _JWTTokenOptions.Audience,
             claims: claims,
             expires: DateTime.Now.AddMinutes(20),//20分钟有效期
             signingCredentials: creds);

            string returnToken = new JwtSecurityTokenHandler().WriteToken(token);
            return returnToken;
            #endregion
        }

        /// <summary>
        /// 生成RefreshToken
        /// </summary>
        public string GenerateRefreshToken()
        {
            var randomNumber = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }


    }
}
