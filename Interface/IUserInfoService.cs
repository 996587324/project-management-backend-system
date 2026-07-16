using Model.User;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IUserInfoService
    {
        Task<SysUserInfo?> GetUserInfoByUser(string? userKey = null);
        Task<AccountPageData> GetUserPageAsync(AccountQueryDto query);
        Task<AccountPageItem?> GetUserPermissionAsync(string id);
        Task<bool> AddUserAsync(AccountSaveDto input);
        Task<bool> UpdateUserAsync(AccountSaveDto input);
        Task<bool> DeleteUserAsync(string id);
        Task<bool> SaveUserPermissionAsync(AccountSaveDto input);
    }
}
