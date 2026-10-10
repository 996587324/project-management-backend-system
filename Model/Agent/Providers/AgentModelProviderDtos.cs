namespace Model.Agent;

/// <summary>模型平台分页查询条件。</summary>
public sealed class AgentModelProviderQueryDto
{
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
    public string? Keyword { get; set; }
    public bool? Enabled { get; set; }
}

/// <summary>新增或修改模型平台时提交的配置。</summary>
public sealed class AgentModelProviderSaveDto
{
    public int? Id { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = "OpenAIChatCompletions";
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPath { get; set; } = "/chat/completions";
    public string? ApiKey { get; set; }
    public string ApiKeyHeader { get; set; } = "Authorization";
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 1200;
    public int ClientTimeoutSeconds { get; set; } = 90;
    public int PlatformTimeoutSeconds { get; set; } = 75;
    public int MaxRetries { get; set; } = 1;
    public string? FallbacksJson { get; set; }
    public string? Remark { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>单独切换模型平台启停状态。</summary>
public sealed class AgentModelProviderEnabledDto
{
    public bool Enabled { get; set; }
}

/// <summary>返回前端的模型平台信息，不包含密钥明文和密文。</summary>
public sealed class AgentModelProviderDto
{
    public int Id { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPath { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = string.Empty;
    public string? ApiKeyMask { get; set; }
    public bool HasApiKey { get; set; }
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public int ClientTimeoutSeconds { get; set; }
    public int PlatformTimeoutSeconds { get; set; }
    public int MaxRetries { get; set; }
    public string? FallbacksJson { get; set; }
    public string? Remark { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

/// <summary>模型平台分页结果。</summary>
public sealed class AgentModelProviderPageDto
{
    public List<AgentModelProviderDto> Records { get; set; } = new();
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
}

/// <summary>保存 Agent 与模型平台绑定关系。</summary>
public sealed class AgentModelBindingSaveDto
{
    public string AgentCode { get; set; } = string.Empty;
    public int ProviderId { get; set; }
    public bool IsPrimary { get; set; }
    public int Priority { get; set; } = 100;
    public bool Enabled { get; set; } = true;
}

/// <summary>Agent 当前可见的模型平台绑定。</summary>
public sealed class AgentModelBindingDto
{
    public int? Id { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public int? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public bool IsPrimary { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>模型平台连通性测试结果。</summary>
public sealed class AgentModelProviderTestResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ProviderCode { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>模型调用完成后写入观测日志的数据。</summary>
public sealed class AgentModelInvocationLogWriteDto
{
    public string AgentCode { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public int? ProviderId { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    /// <summary>0 表示模型平台没有返回业务码。</summary>
    public int BusinessCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TraceId { get; set; }
    public string? ActualModel { get; set; }
    public int RetryCount { get; set; }
    public int FallbackCount { get; set; }
    public long DurationMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
}
