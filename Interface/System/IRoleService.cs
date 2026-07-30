using Model.User;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IRoleService
    {
        /// <summary>
        /// 分页查询角色列表（返回完整JSON结构）
        /// </summary>
        Task<RolePageData> GetRolePageListAsync(int current, int size);

        /// <summary>
        /// 修改角色信息（启用状态/名称/描述等）
        /// </summary>
        Task<RolePageData> UpdateRoleAsync(SysRole role);
        Task<RolePageData> CreateRoleAsync(SysRole role);
        Task<bool> DeleteRoleAsync(int roleId);
        Task<SysRole?> GetRolePermissionAsync(int roleId);
        Task<bool> SaveRolePermissionAsync(RolePermissionSaveDto input);
    }
}
