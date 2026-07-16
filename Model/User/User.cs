using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    /// <summary>
    /// 用户实体类
    /// </summary>
    public class User
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// 密码（生产环境存储加密后的密码）
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// 角色（如super/admin/normal）
        /// </summary>
        [MaxLength(20)]
        public string Role { get; set; } = "normal";

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreateTime { get; set; } = DateTime.Now;
    }
}
