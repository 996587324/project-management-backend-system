using Interface;
using Model.Agent;

namespace Service;

/// <summary>
/// 边缘计算中心 Agent 配置。业务工具由独立 IAgentToolAction 实现并通过 IOC 自动加入本模块。
/// </summary>
public sealed class EdgeGatewayAgentModuleProvider : IAgentModuleProvider
{
    public AgentModuleSeed Module { get; } = new()
    {
        AgentCode = AgentCodes.EdgeGateway,
        Name = "边缘计算中心 Agent 助手",
        Description = "查询边缘设备、连接、实时数据、队列、转发和事件状态，并执行受控的重连及队列操作。",
        SystemPrompt =
            "你是边缘计算中心运维 Agent。查询运行状态时必须优先调用工具，以数据库实际结果为准。" +
            "需要定位故障时，应结合连接接收状态、消息队列、上传记录和事件日志给出判断，并明确区分事实与推断。" +
            "重新连接、队列重试和立即转发属于受控操作，必须等待用户确认，不能声称待确认操作已经执行。" +
            "工具没有提供新增、修改或删除配置的能力，不得编造或尝试执行这些操作。" +
            "回答使用清晰、简洁的中文，避免直接倾倒大段 JSON。",
        Enabled = true,
        MaxToolCalls = 5,
        Version = "1.0",
        Suggestions = new List<string>
        {
            "查看边缘计算中心运行概况",
            "哪些连接最近没有收到数据",
            "查询最近失败的转发记录",
            "查看最近的错误事件"
        }
    };

    // 边缘工具配置由各 IAgentToolAction 自带，新增功能时不需要修改此列表。
    public IReadOnlyCollection<AgentToolSeed> Tools { get; } = Array.Empty<AgentToolSeed>();
}

/// <summary>边缘 Agent 工具配置工厂，统一内部路由、权限、输出协议和版本。</summary>
internal static class EdgeGatewayAgentToolSeedFactory
{
    internal const string EmptySchema = """{"type":"object","properties":{},"additionalProperties":false}""";
    private const string ObjectOutput = """
        {"type":"object","properties":{"success":{"type":"boolean"},"message":{"type":"string"},"data":{}},"required":["success","message"]}
        """;

    internal static AgentToolSeed Create(
        string toolName,
        string title,
        string description,
        string actionName,
        string inputSchema,
        int sort,
        string examples,
        bool isWriteAction = false,
        bool requiresConfirmation = false,
        string riskLevel = "Low") => new()
    {
        AgentCode = AgentCodes.EdgeGateway,
        ToolName = toolName,
        Title = title,
        Description = description,
        Method = isWriteAction ? "POST" : "GET",
        Route = $"internal://EdgeGateway/{actionName}",
        InputSchema = inputSchema,
        OutputSchema = ObjectOutput,
        IsWriteAction = isWriteAction,
        RequiresConfirmation = requiresConfirmation,
        Enabled = true,
        PermissionKey = "AgentExecutor",
        RiskLevel = riskLevel,
        ServiceName = "EdgeGateway",
        ActionName = actionName,
        Examples = examples,
        Version = "1.0",
        Sort = sort
    };
}
