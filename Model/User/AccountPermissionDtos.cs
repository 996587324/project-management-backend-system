using System;
using System.Collections.Generic;

namespace Model.User
{
    public class AccountQueryDto
    {
        public int current { get; set; } = 1;
        public int size { get; set; } = 20;
        public string? userName { get; set; }
        public string? nickName { get; set; }
        public string? keyword { get; set; }
        public string? roleCode { get; set; }
        public bool? enabled { get; set; }
        public string? startTime { get; set; }
        public string? endTime { get; set; }
    }

    public class AccountSaveDto
    {
        public string? id { get; set; }
        public string? userId { get; set; }
        public string userName { get; set; } = string.Empty;
        public string? password { get; set; }
        public string? nickName { get; set; }
        public string? email { get; set; }
        public string? userEmail { get; set; }
        public List<string> roles { get; set; } = new();
        public List<string>? roleCodes { get; set; }
        public List<string>? permissionKeys { get; set; }
        public bool enabled { get; set; } = true;
    }

    public class AccountPageItem
    {
        public string id { get; set; } = string.Empty;
        public string userId { get; set; } = string.Empty;
        public string userName { get; set; } = string.Empty;
        public string? nickName { get; set; }
        public string? email { get; set; }
        public string? userEmail { get; set; }
        public List<string> roles { get; set; } = new();
        public string roleNames { get; set; } = string.Empty;
        public List<string> permissionKeys { get; set; } = new();
        public bool enabled { get; set; }
        public DateTime createTime { get; set; }
        public DateTime? updateTime { get; set; }
    }

    public class AccountPageData
    {
        public List<AccountPageItem> records { get; set; } = new();
        public int current { get; set; }
        public int size { get; set; }
        public int total { get; set; }
    }

    public class RolePermissionSaveDto
    {
        public int roleId { get; set; }
        public string roleCode { get; set; } = string.Empty;
        public List<string> permissionKeys { get; set; } = new();
    }

    public class RoleDeleteDto
    {
        public int roleId { get; set; }
    }
}
