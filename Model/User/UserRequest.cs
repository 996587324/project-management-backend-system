using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    public class UserRequest
    {
        /// <summary>
        /// 用户名
        /// </summary>
        [Required(ErrorMessage = "用户名不能为空")]
        [MaxLength(50, ErrorMessage = "用户名最长50个字符")]
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// 密码
        /// </summary>
        [Required(ErrorMessage = "密码不能为空")]
        [MaxLength(100, ErrorMessage = "密码最长100个字符")]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// 角色
        /// </summary>
        [MaxLength(20, ErrorMessage = "角色最长20个字符")]
        public string Role { get; set; } = "normal";
    }
}
