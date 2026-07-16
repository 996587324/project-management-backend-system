using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.Other
{
    /// <summary>
    /// JWT配置模型（对应appsettings.json中的JwtSettings）
    /// </summary>
    public class JwtSettings
    {
        /// <summary>
        /// 加密密钥
        /// </summary>
        public string SecretKey { get; set; } = string.Empty;

        /// <summary>
        /// 发行方
        /// </summary>
        public string Issuer { get; set; } = string.Empty;

        /// <summary>
        /// 受众
        /// </summary>
        public string Audience { get; set; } = string.Empty;

        /// <summary>
        /// AccessToken过期分钟数
        /// </summary>
        public int AccessTokenExpireMinutes { get; set; }

        /// <summary>
        /// RefreshToken过期天数
        /// </summary>
        public int RefreshTokenExpireDays { get; set; }
    }
}
