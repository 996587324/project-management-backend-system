using SqlSugar;

namespace Model.ExternalTools;

/// <summary>可调用外部数据工具的系统客户端；密钥仅保存不可逆摘要。</summary>
[SugarTable("ExternalToolClient")]
public sealed class ExternalToolClientEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ClientName { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>以空格分隔的最小权限集合，例如 dashboard.read device-card.read。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string Scopes { get; set; } = string.Empty;

    /// <summary>以逗号分隔的线体编码；为空代表该客户端未获任何线体数据授权。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string AllowedLineCodes { get; set; } = string.Empty;

    /// <summary>以逗号分隔的项目编号；为空代表该客户端未获项目数据授权。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string AllowedProjectCodes { get; set; } = string.Empty;

    /// <summary>以逗号分隔的边缘设备编码；为空代表该客户端未获设备级数据授权。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string AllowedDeviceCodes { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, DefaultValue = "1")]
    public bool Enabled { get; set; } = true;

    [SugarColumn(IsNullable = true)]
    public DateTime? ExpireTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>外部工具调用审计记录；仅保存参数摘要，不落原始业务数据或密钥。</summary>
[SugarTable("ExternalToolAccessLog")]
public sealed class ExternalToolAccessLogEntity
{
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserId { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? AppCode { get; set; }
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string ClientId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ToolName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string RequestId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ArgumentHash { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Status { get; set; } = "Success";

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Message { get; set; }

    public int ResultCount { get; set; }
    public long DurationMs { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? RemoteIp { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
}

/// <summary>对外 MCP 服务的展示标识；固定保存为单条系统级配置。</summary>
[SugarTable("ExternalToolServerSetting")]
public sealed class ExternalToolServerSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string ServerName { get; set; } = "calmadmin-external-tools";

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Version { get; set; } = "1.0.0";

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdatedBy { get; set; }

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
