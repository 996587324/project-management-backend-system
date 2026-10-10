using Interface;
using Model.User;
using SqlSugar;

public class RoleService : IRoleService
{
    private readonly SqlSugarScope _db;

    public RoleService(SqlSugarScope db)
    {
        _db = db;
    }

    public async Task<RolePageData> GetRolePageListAsync(int current, int size)
    {
        RefAsync<int> total = 0;
        var records = await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .OrderBy(x => x.roleId)
            .ToPageListAsync(current, size, total);

        return new RolePageData
        {
            records = records,
            current = current,
            size = size,
            total = total,
            _debug = new RoleDebugInfo
            {
                totalPages = (int)Math.Ceiling((double)total / Math.Max(size, 1)),
                isLastPage = current >= (int)Math.Ceiling((double)total / Math.Max(size, 1)),
                currentPageSize = records.Count,
                startIndex = (current - 1) * size
            }
        };
    }

    public async Task<RolePageData> CreateRoleAsync(SysRole role)
    {
        var exists = await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .AnyAsync(x => x.roleCode == role.roleCode);
        if (exists) throw new Exception("角色编码已存在");

        role.createTime = DateTime.Now;
        role.updateTime = DateTime.Now;
        role.permissionKeys ??= string.Empty;
        await _db.GetConnection("calmadmin").Insertable(role).ExecuteCommandAsync();
        return await GetRolePageListAsync(1, 20);
    }

    public async Task<RolePageData> UpdateRoleAsync(SysRole role)
    {
        var existRole = await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .Where(r => r.roleId == role.roleId)
            .FirstAsync();
        if (existRole == null) throw new Exception("角色不存在");

        existRole.roleName = role.roleName;
        existRole.roleCode = role.roleCode;
        existRole.description = role.description;
        existRole.enabled = role.enabled;
        if (role.permissionKeys != null) existRole.permissionKeys = role.permissionKeys;
        existRole.updateTime = DateTime.Now;

        await _db.GetConnection("calmadmin").Updateable(existRole).ExecuteCommandAsync();
        return await GetRolePageListAsync(1, 20);
    }

    public async Task<bool> DeleteRoleAsync(int roleId)
    {
        var role = await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .FirstAsync(x => x.roleId == roleId);
        if (role == null) return false;
        if (new[] { "R_ADMIN", "R_ENGINEER", "R_USER" }.Contains(role.roleCode))
        {
            throw new Exception("默认角色不允许删除");
        }

        return await _db.GetConnection("calmadmin").Deleteable<SysRole>()
            .Where(x => x.roleId == roleId)
            .ExecuteCommandAsync() > 0;
    }

    public async Task<SysRole?> GetRolePermissionAsync(int roleId)
    {
        return await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .FirstAsync(x => x.roleId == roleId);
    }

    public async Task<bool> SaveRolePermissionAsync(RolePermissionSaveDto input)
    {
        var role = await _db.GetConnection("calmadmin").Queryable<SysRole>()
            .FirstAsync(x => x.roleId == input.roleId || x.roleCode == input.roleCode);
        if (role == null) throw new Exception("角色不存在");

        role.permissionKeys = string.Join(",", input.permissionKeys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        role.updateTime = DateTime.Now;
        return await _db.GetConnection("calmadmin").Updateable(role).ExecuteCommandAsync() > 0;
    }

}
