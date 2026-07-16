using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    [SugarTable("sys_user_info")]
    public class SysUserInfo
    {
        [SugarColumn(ColumnName = "user_id", IsPrimaryKey = true, Length = 64, IsNullable = false)]
        public string UserId { get; set; }

        [SugarColumn(ColumnName = "user_name", Length = 128, IsNullable = false)]
        public string UserName { get; set; }

        [SugarColumn(ColumnName = "password", Length = 128, IsNullable = false, DefaultValue = "123456")]
        public string Password { get; set; }

        [SugarColumn(ColumnName = "nick_name", Length = 128, IsNullable = true)]
        public string NickName { get; set; }

        [SugarColumn(ColumnName = "email", Length = 256, IsNullable = true)]
        public string Email { get; set; }

        [SugarColumn(ColumnName = "roles", Length = 512, IsNullable = true)]
        public string Roles { get; set; }

        [SugarColumn(ColumnName = "permission_keys", Length = 2048, IsNullable = true)]
        public string PermissionKeys { get; set; }

        [SugarColumn(ColumnName = "buttons", Length = 1024, IsNullable = true)]
        public string Buttons { get; set; }

        [SugarColumn(ColumnName = "enabled", IsNullable = false, DefaultValue = "1")]
        public bool Enabled { get; set; } = true;

        [SugarColumn(ColumnName = "create_time", IsNullable = false, DefaultValue = "GETDATE()")]
        public DateTime CreateTime { get; set; }

        [SugarColumn(ColumnName = "update_time", IsNullable = true)]
        public DateTime? UpdateTime { get; set; }

        // 非数据库字段，用于返回给前端的列表格式
        [SugarColumn(IsIgnore = true)]
        public List<string> RoleList =>
            string.IsNullOrEmpty(Roles) ? new List<string>() : Roles.Split(',').ToList();

        [SugarColumn(IsIgnore = true)]
        public List<string> PermissionKeyList =>
            string.IsNullOrEmpty(PermissionKeys) ? new List<string>() : PermissionKeys.Split(',').ToList();

        [SugarColumn(IsIgnore = true)]
        public List<string> ButtonList =>
            string.IsNullOrEmpty(Buttons) ? new List<string>() : Buttons.Split(',').ToList();
    }
}
