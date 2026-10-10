using SqlSugar;

namespace Model.Agent;

/// <summary>统一 Agent 模型配置使用的系统级归属标识及支持的平台编码。</summary>
public static class AgentModelConfigurationScope
{
    public const string SystemOwnerUserId = "__system__";

    public static bool IsSupportedProvider(string providerCode) =>
        providerCode.Equals("turing", StringComparison.OrdinalIgnoreCase) ||
        providerCode.Equals("deepseek", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Agent 模型平台配置。密钥只保存加密文本，任何查询接口都不能返回明文。
/// 当前运行配置统一归属 <see cref="AgentModelConfigurationScope.SystemOwnerUserId"/>，不按用户区分。
/// </summary>
[SugarTable("AgentModelProvider")]
public sealed class AgentModelProviderEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>配置所属用户；模型密钥与平台配置只允许该用户使用。</summary>
    [SugarColumn(Length = 64)]
    public string OwnerUserId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string ProviderCode { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string ProviderName { get; set; } = string.Empty;

    [SugarColumn(Length = 48)]
    public string ProtocolType { get; set; } = "OpenAIChatCompletions";

    [SugarColumn(Length = 512)]
    public string BaseUrl { get; set; } = string.Empty;

    [SugarColumn(Length = 256)]
    public string EndpointPath { get; set; } = "/chat/completions";

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ApiKeyCipherText { get; set; }

    [SugarColumn(Length = 32, IsNullable = true)]
    public string? ApiKeyMask { get; set; }

    [SugarColumn(Length = 64)]
    public string ApiKeyHeader { get; set; } = "Authorization";

    [SugarColumn(Length = 128)]
    public string Model { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 1200;
    public int ClientTimeoutSeconds { get; set; } = 90;
    public int PlatformTimeoutSeconds { get; set; } = 75;
    public int MaxRetries { get; set; } = 1;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? FallbacksJson { get; set; }

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? Remark { get; set; }

    public bool Enabled { get; set; }
    public int IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// Agent 与模型平台的绑定关系。优先级越小越先调用，主配置仅用于管理界面标识。
/// </summary>
[SugarTable("AgentModelBinding")]
public sealed class AgentModelBindingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    /// <summary>绑定所属用户；同一个 Agent 可以被不同用户绑定到不同平台。</summary>
    [SugarColumn(Length = 64)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int ProviderId { get; set; }
    public bool IsPrimary { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public int IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>
/// 模型平台调用观测日志，只记录链路、耗时和用量，不重复保存提示词与回复正文。
/// </summary>
[SugarTable("AgentModelInvocationLog")]
public sealed class AgentModelInvocationLogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string OwnerUserId { get; set; } = string.Empty;

    public int? ProviderId { get; set; }

    [SugarColumn(Length = 64)]
    public string ProviderCode { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string Stage { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Model { get; set; } = string.Empty;

    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    /// <summary>模型平台业务码；0 表示该次调用未返回业务码，兼容历史非空数据库列。</summary>
    public int BusinessCode { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? TraceId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ActualModel { get; set; }

    public int RetryCount { get; set; }
    public int FallbackCount { get; set; }
    public long DurationMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    [SugarColumn(Length = 64)]
    public string ClientRequestId { get; set; } = string.Empty;

    public DateTime CreateTime { get; set; } = DateTime.Now;
}
