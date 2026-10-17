using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Model.Menu;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>统一初始化账号、角色和菜单，业务请求不再执行 CodeFirst 或默认数据写入。</summary>
public sealed class SystemDatabaseInitializer
{
    private readonly SqlSugarScope _dbScope;
    private readonly ILogger<SystemDatabaseInitializer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SystemDatabaseInitializer(SqlSugarScope dbScope, ILogger<SystemDatabaseInitializer> logger)
    {
        _dbScope = dbScope;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            using var db = _dbScope.GetConnection("calmadmin").CopyNew();
            cancellationToken.ThrowIfCancellationRequested();
            db.CodeFirst.SetStringDefaultLength(200)
                .InitTables(typeof(SysUserInfo), typeof(SysRole), typeof(sys_menu));
            _logger.LogInformation("System security schema ready in {ElapsedMilliseconds} ms.", stopwatch.ElapsedMilliseconds);

            await SeedAccountsAsync(db, cancellationToken);
            await new SystemMenuInitializer(db, _logger).SeedAsync(cancellationToken);
            await SystemApplicationSchema.InitializeAsync(db);
            await ProcessBoardMenus.SeedAsync(db);
            _initialized = true;
            _logger.LogInformation("System accounts and menus initialized in {ElapsedMilliseconds} ms.", stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task SeedAccountsAsync(ISqlSugarClient db, CancellationToken cancellationToken)
    {
        var roles = new[]
        {
            new SysRole { roleName = "管理员", roleCode = "R_ADMIN", description = "系统管理员，拥有全部权限", permissionKeys = "*", enabled = true },
            new SysRole { roleName = "工程师", roleCode = "R_ENGINEER", description = "工程师角色，后续可扩展为工艺/设备/品质工程师", permissionKeys = "Dashboard,ProjectDashboard,ProjectLedger,TaskBoard,ProjectFile,Agent,AgentExecutor,AgentModelProvider,Pda,PdaScan,PdaScanLog,PdaDevice,PdaBarcodeRule", enabled = true },
            new SysRole { roleName = "普通用户", roleCode = "R_USER", description = "普通用户，仅保留基础工作台和个人权限", permissionKeys = "Dashboard", enabled = true }
        };

        foreach (var role in roles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await db.Queryable<SysRole>().AnyAsync(x => x.roleCode == role.roleCode))
            {
                continue;
            }

            role.createTime = DateTime.Now;
            role.updateTime = role.createTime;
            await db.Insertable(role).ExecuteCommandAsync();
        }

        // 延续空库首次初始化行为；已有用户时不创建、改写账号或密码。
        if (!await db.Queryable<SysUserInfo>().AnyAsync())
        {
            await db.Insertable(new SysUserInfo
            {
                UserId = "1",
                UserName = "admin",
                Password = "123456",
                NickName = "系统管理员",
                Email = "admin@example.com",
                Roles = "R_ADMIN",
                PermissionKeys = "*",
                Buttons = string.Empty,
                Enabled = true,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
    }
}
