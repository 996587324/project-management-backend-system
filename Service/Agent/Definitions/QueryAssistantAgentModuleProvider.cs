using Interface;
using Model.Agent;

namespace Service;

/// <summary>
/// 悬浮窗使用的跨业务查询助手。工具由注册服务从各业务 Agent 的只读工具自动汇总，
/// 因而不会注册新增、修改、删除或其他写操作。
/// </summary>
public sealed class QueryAssistantAgentModuleProvider : IAgentModuleProvider
{
    public AgentModuleSeed Module { get; } = new()
    {
        AgentCode = AgentCodes.QueryAssistant,
        Name = "全局查询助手",
        Description = "跨项目管理、边缘计算、生产监控及产线工艺看板查询业务信息；只读，不执行任何写操作。",
        SystemPrompt =
            "你是项目管理系统的全局查询助手。只能使用已提供的只读查询工具获取项目管理、边缘计算、生产监控和产线工艺看板的数据。" +
            "不得新增、修改、删除、发送邮件、执行命令或生成待确认计划。" +
            "用户提出非查询请求时，明确说明悬浮助手仅支持查询，并引导其进入 Agent 中心的工作模式。" +
            "所有业务结论必须以工具返回数据为准，使用简洁中文回答。",
        Enabled = true,
        MaxToolCalls = 6,
        Version = "1.0",
        Suggestions = new List<string>
        {
            "查看当前项目概况",
            "查询边缘计算中心运行概况",
            "查看生产看板各线体实时状态",
            "查询今天故障次数最多的机械手"
        }
    };

    public IReadOnlyCollection<AgentToolSeed> Tools { get; } = Array.Empty<AgentToolSeed>();
}
