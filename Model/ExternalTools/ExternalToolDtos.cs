using System.Text.Json;

namespace Model.ExternalTools;

public sealed class ExternalToolClientSaveDto
{
    public long? Id { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public string AllowedLineCodes { get; set; } = string.Empty;
    public string AllowedProjectCodes { get; set; } = string.Empty;
    public string AllowedDeviceCodes { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime? ExpireTime { get; set; }
    /// <summary>仅新增或轮换密钥时填写；为空不会改变既有密钥。</summary>
    public bool RotateSecret { get; set; }
}

public sealed class ExternalToolClientOutputDto
{
    public long Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Scopes { get; set; } = string.Empty;
    public string AllowedLineCodes { get; set; } = string.Empty;
    public string AllowedProjectCodes { get; set; } = string.Empty;
    public string AllowedDeviceCodes { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime? ExpireTime { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>创建或轮换时仅返回一次明文密钥，数据库不会保存该值。</summary>
public sealed class ExternalToolClientSecretResultDto
{
    public ExternalToolClientOutputDto Client { get; set; } = new();
    public string? ClientSecret { get; set; }
}

public sealed class ExternalToolTokenRequestDto
{
    public string GrantType { get; set; } = "client_credentials";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class ExternalToolTokenResponseDto
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public int ExpiresIn { get; set; }
    public string Scope { get; set; } = string.Empty;
}

public sealed class ExternalToolDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequiredScope { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; }
}

public sealed class ExternalToolCallerContext
{
    /// <summary>
    /// 内网直连模式：不校验客户端凭据、权限范围或资源编码白名单。
    /// 仍保留该标记，以便执行记录明确标识此次调用来源。
    /// </summary>
    public bool IsDirectAccess { get; set; }
    public bool AllResources { get; set; }
    public string? UserId { get; set; }
    public string? AppCode { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public HashSet<string> Scopes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedLineCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedProjectCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowedDeviceCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? RemoteIp { get; set; }
}

public sealed class ExternalToolExecutionResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

public sealed class ExternalToolServerSettingDto
{
    public string ServerName { get; set; } = "calmadmin-external-tools";
    public string Version { get; set; } = "1.0.0";
    public string? UpdatedBy { get; set; }
    public DateTime UpdateTime { get; set; }
}

public sealed class ExternalToolServerSettingSaveDto
{
    public string ServerName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    /// <summary>不入库；必须等于服务器当前 yyyyMMddHHmm。</summary>
    public string ConfigurationPassword { get; set; } = string.Empty;
}
