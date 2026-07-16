using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    /// <summary>
    /// 角色实体（匹配数据库+JSON的records项）
    /// </summary>
    [SugarTable("sys_role")]
    public class SysRole
    {
        [SugarColumn(ColumnName = "role_id", IsPrimaryKey = true, IsIdentity = true)]
        public int roleId { get; set; }

        [SugarColumn(ColumnName = "role_name")]
        public string roleName { get; set; }

        [SugarColumn(ColumnName = "role_code")]
        public string roleCode { get; set; }

        [SugarColumn(ColumnName = "description")]
        public string description { get; set; }

        [SugarColumn(ColumnName = "permission_keys", Length = 2048, IsNullable = true)]
        public string permissionKeys { get; set; }

        [SugarColumn(ColumnName = "enabled")]
        public bool enabled { get; set; }

        [SugarColumn(ColumnName = "create_time")]
        public DateTime createTime { get; set; }

        [SugarColumn(ColumnName = "update_time", IsNullable = true)]
        public DateTime? updateTime { get; set; }

        [SugarColumn(IsIgnore = true)]
        public List<string> permissionKeyList =>
            string.IsNullOrEmpty(permissionKeys) ? new List<string>() : permissionKeys.Split(',').ToList();
    }
}
