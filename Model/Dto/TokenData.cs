using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.Dto
{
    /// <summary>
    /// Token数据模型
    /// </summary>
    public class TokenData
    {
        /// <summary>
        /// 访问Token（AccessToken）
        /// </summary>
        public string token { get; set; } = string.Empty;

        /// <summary>
        /// 刷新Token（RefreshToken）
        /// </summary>
        public string refreshToken { get; set; } = string.Empty;
    }
}
