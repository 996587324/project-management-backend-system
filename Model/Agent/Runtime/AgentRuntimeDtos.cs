namespace Model.Agent;

public class AgentToolSelectionRequest
{
    public string AgentCode { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string? ScopeProjectName { get; set; }
    public List<AgentMessageDto> History { get; set; } = new();
    public List<AgentToolDefinitionDto> Tools { get; set; } = new();
    public int MaxToolCalls { get; set; } = 4;
    public int? MaxOutputTokens { get; set; }
}

public class AgentToolSelectionReply
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<AgentSelectedToolCallDto> ToolCalls { get; set; } = new();
}

public class AgentSelectedToolCallDto
{
    public string CallId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
}

public class AgentToolResultMessageDto
{
    public string ToolName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
}

public class AgentToolDispatchRequest
{
    public int ConversationId { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public string? UserId { get; set; }
}

public class AgentBusinessToolContext
{
    public int ConversationId { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class AgentBusinessToolResult
{
    public bool Success { get; set; }
    public bool PartialSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public string? ErrorMessage { get; set; }

    public static AgentBusinessToolResult Ok(object? data, string message = "执行成功") => new()
    {
        Success = true,
        Message = message,
        Data = data
    };

    public static AgentBusinessToolResult Fail(string message) => new()
    {
        Success = false,
        Message = message,
        ErrorMessage = message
    };
}
