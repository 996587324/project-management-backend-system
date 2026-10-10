using Interface;
using Model.User;
using SqlSugar;

namespace Service
{
    public class UserInfoService : IUserInfoService
    {
        private readonly SqlSugarScope _db;

        public UserInfoService(SqlSugarScope db)
        {
            _db = db;
        }

        public async Task<SysUserInfo?> GetUserInfoByUser(string? userKey = null)
        {
            var query = _db.GetConnection("calmadmin").Queryable<SysUserInfo>();

            if (!string.IsNullOrWhiteSpace(userKey))
            {
                var current = await query.FirstAsync(x => x.UserId == userKey || x.UserName == userKey);
                if (current != null) return current;
            }

            return null;
        }

        public async Task<AccountPageData> GetUserPageAsync(AccountQueryDto query)
        {
            RefAsync<int> total = 0;
            var dbQuery = _db.GetConnection("calmadmin").Queryable<SysUserInfo>();

            if (!string.IsNullOrWhiteSpace(query.userName))
            {
                dbQuery = dbQuery.Where(x => x.UserName.Contains(query.userName));
            }

            if (!string.IsNullOrWhiteSpace(query.nickName))
            {
                dbQuery = dbQuery.Where(x => x.NickName.Contains(query.nickName));
            }

            if (!string.IsNullOrWhiteSpace(query.keyword))
            {
                dbQuery = dbQuery.Where(x => x.UserName.Contains(query.keyword) || x.NickName.Contains(query.keyword));
            }

            if (!string.IsNullOrWhiteSpace(query.roleCode))
            {
                dbQuery = dbQuery.Where(x => x.Roles.Contains(query.roleCode));
            }

            if (query.enabled.HasValue)
            {
                dbQuery = dbQuery.Where(x => x.Enabled == query.enabled.Value);
            }

            var users = await dbQuery.OrderByDescending(x => x.CreateTime)
                .ToPageListAsync(query.current, query.size, total);
            var roles = await GetRoleNameMapAsync();

            return new AccountPageData
            {
                records = users.Select(x => ToAccountItem(x, roles)).ToList(),
                current = query.current,
                size = query.size,
                total = total
            };
        }

        public async Task<AccountPageItem?> GetUserPermissionAsync(string id)
        {
            var user = await _db.GetConnection("calmadmin").Queryable<SysUserInfo>()
                .FirstAsync(x => x.UserId == id);
            if (user == null) return null;

            return ToAccountItem(user, await GetRoleNameMapAsync());
        }

        public async Task<bool> AddUserAsync(AccountSaveDto input)
        {
            var roles = NormalizeRoles(input.roleCodes ?? input.roles);
            var userId = string.IsNullOrWhiteSpace(input.userId)
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                : input.userId!;

            var exists = await _db.GetConnection("calmadmin").Queryable<SysUserInfo>()
                .AnyAsync(x => x.UserName == input.userName || x.UserId == userId);
            if (exists) throw new Exception("登录账号已存在");

            var entity = new SysUserInfo
            {
                UserId = userId,
                UserName = input.userName,
                Password = string.IsNullOrWhiteSpace(input.password) ? "123456" : input.password!,
                NickName = input.nickName ?? input.userName,
                Email = input.email ?? input.userEmail ?? string.Empty,
                Roles = string.Join(",", roles),
                PermissionKeys = string.Join(",", input.permissionKeys ?? new List<string>()),
                Buttons = string.Empty,
                Enabled = input.enabled,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };

            return await _db.GetConnection("calmadmin").Insertable(entity).ExecuteCommandAsync() > 0;
        }

        public async Task<bool> UpdateUserAsync(AccountSaveDto input)
        {
            var id = input.userId ?? input.id;
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("用户ID不能为空");

            var entity = await _db.GetConnection("calmadmin").Queryable<SysUserInfo>()
                .FirstAsync(x => x.UserId == id);
            if (entity == null) throw new Exception("用户不存在");

            entity.UserName = input.userName;
            if (!string.IsNullOrWhiteSpace(input.password)) entity.Password = input.password!;
            entity.NickName = input.nickName ?? entity.NickName;
            entity.Email = input.email ?? input.userEmail ?? entity.Email;
            entity.Roles = string.Join(",", NormalizeRoles(input.roleCodes ?? input.roles));
            if (input.permissionKeys != null) entity.PermissionKeys = string.Join(",", input.permissionKeys);
            entity.Enabled = input.enabled;
            entity.UpdateTime = DateTime.Now;

            return await _db.GetConnection("calmadmin").Updateable(entity).ExecuteCommandAsync() > 0;
        }

        public async Task<bool> DeleteUserAsync(string id)
        {
            return await _db.GetConnection("calmadmin").Deleteable<SysUserInfo>()
                .Where(x => x.UserId == id)
                .ExecuteCommandAsync() > 0;
        }

        public async Task<bool> SaveUserPermissionAsync(AccountSaveDto input)
        {
            var id = input.userId ?? input.id;
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("用户ID不能为空");

            var entity = await _db.GetConnection("calmadmin").Queryable<SysUserInfo>()
                .FirstAsync(x => x.UserId == id);
            if (entity == null) throw new Exception("用户不存在");

            entity.PermissionKeys = string.Join(",", input.permissionKeys ?? new List<string>());
            entity.Roles = string.Join(",", NormalizeRoles(input.roleCodes ?? input.roles));
            entity.UpdateTime = DateTime.Now;
            return await _db.GetConnection("calmadmin").Updateable(entity).ExecuteCommandAsync() > 0;
        }

        private async Task<Dictionary<string, string>> GetRoleNameMapAsync()
        {
            var roles = await _db.GetConnection("calmadmin").Queryable<SysRole>().ToListAsync();
            return roles.ToDictionary(x => x.roleCode, x => x.roleName);
        }

        private static List<string> NormalizeRoles(IEnumerable<string>? roles)
        {
            var normalized = roles?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>();
            return normalized.Count > 0 ? normalized : new List<string> { "R_USER" };
        }

        private static AccountPageItem ToAccountItem(SysUserInfo user, Dictionary<string, string> roleNames)
        {
            var roles = user.RoleList;
            return new AccountPageItem
            {
                id = user.UserId,
                userId = user.UserId,
                userName = user.UserName,
                nickName = user.NickName,
                email = user.Email,
                userEmail = user.Email,
                roles = roles,
                roleNames = string.Join("、", roles.Select(x => roleNames.TryGetValue(x, out var name) ? name : x)),
                permissionKeys = user.PermissionKeyList,
                enabled = user.Enabled,
                createTime = user.CreateTime,
                updateTime = user.UpdateTime
            };
        }
    }
}
