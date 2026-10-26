namespace Model.Agent;

/// <summary>
/// Agent 模块的代码配置。新增业务 Agent 时提供一份该配置即可，无需修改通用注册服务。
/// </summary>
public sealed class AgentModuleSeed
{
    public string AgentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int MaxToolCalls { get; set; } = 4;
    public string Version { get; set; } = "1.0";
    public List<string> Suggestions { get; set; } = new();
}

/// <summary>
/// Agent 工具的代码配置。工具的说明、Schema、权限和风险等级都由业务模块集中声明。
/// </summary>
public sealed class AgentToolSeed
{
    public string AgentCode { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string Route { get; set; } = string.Empty;
    public string InputSchema { get; set; } = "{\"type\":\"object\"}";
    public string OutputSchema { get; set; } = "{\"type\":\"object\"}";
    public bool IsWriteAction { get; set; }
    public bool RequiresConfirmation { get; set; }
    public bool Enabled { get; set; } = true;
    public string? PermissionKey { get; set; }
    public string RiskLevel { get; set; } = "Low";
    public string ServiceName { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string Examples { get; set; } = "{}";
    public string Version { get; set; } = "1.0";
    public int Sort { get; set; }
}

/// <summary>系统内置 Agent 编码，避免前后端业务代码散落魔法字符串。</summary>
public static class AgentCodes
{
    public const string Chat = "chat";
    public const string QueryAssistant = "query-assistant";
    public const string ProjectManagement = "project-management";
    public const string EdgeGateway = "edge-gateway";
    public const string ProductionDashboard = "production-dashboard";
    public const string ProcessBoard = "process-board";
    public const string WeChatPush = "wechat-push";
    public const string EmailPush = "email-push";
}
