namespace Model.Agent;

public class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "TCL项目管理系统";
    public int TimeoutSeconds { get; set; } = 30;
}

public class AgentEmailDraftRequest
{
    public int? ConversationId { get; set; }
    public int? SourceToolCallId { get; set; }
    public string? UserId { get; set; }
    public string? Subject { get; set; }
    public string? Content { get; set; }
}

public class AgentEmailDraftDto
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public int? ConversationId { get; set; }
    public int? SourceToolCallId { get; set; }
}

public class AgentEmailSendRequest
{
    public int? ConversationId { get; set; }
    public int? SourceToolCallId { get; set; }
    public string? UserId { get; set; }
    public List<string> ToEmails { get; set; } = new();
    public List<string> CcEmails { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

public class AgentEmailSendResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> ToEmails { get; set; } = new();
    public List<string> CcEmails { get; set; } = new();
    public DateTime SendTime { get; set; }
}
