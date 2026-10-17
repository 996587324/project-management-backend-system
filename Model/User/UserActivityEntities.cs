using SqlSugar;

namespace Model.User;

/// <summary>后台用户的一次登录会话。在线状态以最近一次心跳为准。</summary>
[SugarTable("SysUserLoginLog")]
[SugarIndex("IX_SysUserLoginLog_UserTime", nameof(UserId), OrderByType.Asc, nameof(LoginTime), OrderByType.Desc, false)]
[SugarIndex("IX_SysUserLoginLog_Online", nameof(LogoutTime), OrderByType.Asc, nameof(LastActiveTime), OrderByType.Desc, false)]
public class SysUserLoginLogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64)]
    public string SessionId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserId { get; set; }

    [SugarColumn(Length = 128)]
    public string UserName { get; set; } = string.Empty;

    [SugarColumn(Length = 24)]
    public string LoginType { get; set; } = "Password";

    public bool Success { get; set; }

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? FailureReason { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ClientIp { get; set; }

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? UserAgent { get; set; }

    public DateTime LoginTime { get; set; } = DateTime.Now;
    public DateTime LastActiveTime { get; set; } = DateTime.Now;
    [SugarColumn(IsNullable = true)]
    public DateTime? LogoutTime { get; set; }
    public long OnlineSeconds { get; set; }

    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Online";
}

/// <summary>后台用户写操作审计。请求正文不直接保存，仅保存脱敏后的业务摘要。</summary>
[SugarTable("SysUserOperationLog")]
[SugarIndex("IX_SysUserOperationLog_UserTime", nameof(UserId), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
[SugarIndex("IX_SysUserOperationLog_ModuleTime", nameof(Module), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
public class SysUserOperationLogEntity
{
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? AppCode { get; set; }
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserId { get; set; }

    [SugarColumn(Length = 128)]
    public string UserName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SessionId { get; set; }

    [SugarColumn(Length = 64)]
    public string Module { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(Length = 12)]
    public string HttpMethod { get; set; } = string.Empty;

    [SugarColumn(Length = 512)]
    public string RequestPath { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Target { get; set; }

    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ClientIp { get; set; }

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? UserAgent { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? DetailJson { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
}
