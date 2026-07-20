using Model.ProjectManage;

namespace Model.Agent;

public class AgentStartRequest
{
    public string? AgentCode { get; set; }
    public string? UserId { get; set; }
    public string? Title { get; set; }
    public string? ScopeProjectName { get; set; }
    public string? Mode { get; set; }
}

public class AgentSendRequest
{
    public string? AgentCode { get; set; }
    public int? ConversationId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? ScopeProjectName { get; set; }
    public string? Mode { get; set; }
    public bool AutoExecute { get; set; }
    public string? AttachmentType { get; set; }
    public string? AttachmentName { get; set; }
    public string? AttachmentContent { get; set; }
}

public class AgentProgressDto
{
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? ConversationId { get; set; }
}

public class AgentConversationDto
{
    public int Id { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ScopeProjectName { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
}

public class AgentMessageDto
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
}

public class AgentPlanStepDto
{
    public int Sort { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
}

public class AgentToolCallDto
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public int ToolId { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ToolTitle { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool RequiresConfirmation { get; set; }
    public bool Confirmed { get; set; }
    public DateTime CreateTime { get; set; }
}

public class AgentToolDefinitionDto
{
    public int Id { get; set; }
    public string AgentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsWriteAction { get; set; }
    public bool RequiresConfirmation { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string PermissionKey { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string InputSchema { get; set; } = string.Empty;
    public string OutputSchema { get; set; } = string.Empty;
    public string Examples { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public class AgentResponseDto
{
    public int ConversationId { get; set; }
    public string Reply { get; set; } = string.Empty;
    public List<AgentPlanStepDto> Plan { get; set; } = new();
    public List<AgentToolCallDto> ToolCalls { get; set; } = new();
    public bool RequiresConfirmation { get; set; }
    public List<string> Suggestions { get; set; } = new();
}

public class AgentConversationDetailDto
{
    public AgentConversationDto Conversation { get; set; } = new();
    public List<AgentMessageDto> Messages { get; set; } = new();
    public List<AgentToolCallDto> ToolCalls { get; set; } = new();
}

public class AgentProjectSnapshotDto
{
    public List<ProjectOutputDto> Projects { get; set; } = new();
    public List<TaskOutputDto> DelayedTasks { get; set; } = new();
    public List<ProjectFileOutputDto> RecentFiles { get; set; } = new();
    public DashboardStatsDto Dashboard { get; set; } = new();
}

public class AgentConfirmRequest
{
    public int ToolCallId { get; set; }
    public string? UserId { get; set; }
    public string? Remark { get; set; }
}

public class AgentDeleteConversationRequest
{
    public int Id { get; set; }
    public string? UserId { get; set; }
}
