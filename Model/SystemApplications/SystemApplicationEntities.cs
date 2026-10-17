using SqlSugar;

namespace Model.SystemApplications;

/// <summary>稳定系统编码与可修改的显示信息；名称不是权限标识。</summary>
[SugarTable("SysApplication")]
public sealed class SysApplicationEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 32)]
    public string AppCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 2048)]
    public string EntryUrl { get; set; } = string.Empty;
    [SugarColumn(Length = 255)]
    public string HomePath { get; set; } = "/";
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>账号的系统授权。空集合默认拒绝，旧角色仅可显式用于 A 的兼容授权。</summary>
[SugarTable("SysUserApplication")]
[SugarIndex("UX_SysUserApplication_User_App", nameof(UserId), OrderByType.Asc, nameof(AppCode), OrderByType.Asc, true)]
public sealed class SysUserApplicationEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string AppCode { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool UseLegacyPermissions { get; set; }
    public bool AllResources { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PermissionsJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string McpScopesJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string LineCodesJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ProjectCodesJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string DeviceCodesJson { get; set; } = "[]";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string SourceKeysJson { get; set; } = "[]";
    public DateTime UpdateTime { get; set; }
}

[SugarTable("SysApplicationMenu")]
public sealed class SysApplicationMenuEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 32)]
    public string AppCode { get; set; } = string.Empty;
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string MenuId { get; set; } = string.Empty;
}

[SugarTable("SysPlatformAdministrator")]
public sealed class SysPlatformAdministratorEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string UserId { get; set; } = string.Empty;
}

/// <summary>授权会话不依赖在线统计状态；退出后所有派生系统令牌均失效。</summary>
[SugarTable("SysPortalSession")]
[SugarIndex("IX_SysPortalSession_ExpiresAt", nameof(ExpiresAtUtc), OrderByType.Asc, false)]
public sealed class SysPortalSessionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string SessionId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? RevokedAtUtc { get; set; }
}

/// <summary>数据库原子消费的短期票据。仅保存哈希，明确绑定接收系统、会话及 PKCE。</summary>
[SugarTable("SysApplicationLoginTicket")]
[SugarIndex("IX_SysApplicationLoginTicket_ExpiresAt", nameof(ExpiresAtUtc), OrderByType.Asc, false)]
public sealed class SysApplicationLoginTicketEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string TicketHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SessionId { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string AppCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string CodeChallenge { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string State { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? ConsumedAtUtc { get; set; }
}

[SugarTable("SysApplicationMigration")]
public sealed class SysApplicationMigrationEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Version { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; }
}
