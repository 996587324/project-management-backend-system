namespace Model.Agent;

/// <summary>
/// 单次调用模型前整理出来的运行时配置。
/// </summary>
public sealed class RuntimeAgentOptions
{
    public int? ProviderId { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPath { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "Authorization";
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public int TimeoutSeconds { get; set; }
    public int PlatformTimeoutSeconds { get; set; }
    public int MaxRetries { get; set; }
    public string? FallbacksJson { get; set; }
    public bool IsDatabaseConfiguration { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
}

/// <summary>
/// Agent 向模型请求普通回复时使用的输入数据。
/// </summary>
public class AgentModelRequest
{
    public string AgentCode { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ScopeProjectName { get; set; }
    public List<AgentMessageDto> History { get; set; } = new();
    public List<AgentToolResultMessageDto> ToolResults { get; set; } = new();
}

/// <summary>
/// 模型普通回复的统一返回结构，Service 层用它判断是否调用成功。
/// </summary>
public class AgentModelReply
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
