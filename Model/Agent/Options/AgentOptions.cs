namespace Model.Agent;

/// <summary>
/// Agent 模块在 appsettings 中绑定的基础配置，例如模型地址、密钥和默认提示词。
/// </summary>
public class AgentOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "OpenAICompatible";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string EndpointPath { get; set; } = "/chat/completions";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "Authorization";
    public string Model { get; set; } = "gpt-4o-mini";
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 1200;
    public int TimeoutSeconds { get; set; } = 60;
    public string SystemPrompt { get; set; } =
        "你是TCL项目管理系统里的项目助手。请根据工具注册表自主选择工具，并基于工具实际返回的数据回答，" +
        "不要编造不存在的数据。新增、修改等写操作必须先生成待确认计划，确认后才能执行；系统不向 Agent 开放删除能力。";
}

/// <summary>
/// 单次调用模型前整理出来的运行时配置，包含默认值和 appsettings 覆盖后的最终值。
/// </summary>
public sealed class RuntimeAgentOptions
{
    public string Provider { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string EndpointPath { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "Authorization";
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public int TimeoutSeconds { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
}

/// <summary>
/// Agent 向模型请求普通回复时使用的输入数据。
/// </summary>
public class AgentModelRequest
{
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
