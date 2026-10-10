using Interface;
using Model.Agent;

namespace Service;

/// <summary>生产看板 Agent 的定义。具体业务工具由独立动作类自动注册。</summary>
public sealed class ProductionDashboardAgentModuleProvider : IAgentModuleProvider
{
    public AgentModuleSeed Module { get; } = new()
    {
        AgentCode = AgentCodes.ProductionDashboard,
        Name = "生产看板 Agent 助手",
        Description = "查询生产监控和设备卡片看板数据，并在确认后调整线体、场景、设备、卡片、工位、机械手、产量、型号映射和图表配置。",
        SystemPrompt =
            "你是生产看板 Agent。所有运行状态、统计和故障结论必须优先调用工具，以实际数据为准。" +
            "只能操作 production-dashboard 和 device-card-dashboard 的配置数据；历史统计、故障记录、字段追溯和实时采集数据只能查询，不能修改或删除。" +
            "新增或修改线体、场景组件、机械手或设备绑定、设备卡片、产量、型号映射和图表部件前，先查询现有配置并明确目标对象；仅调整场景物体位置时优先使用专用位置调整工具；名称不唯一时必须要求用户明确。" +
            "写操作必须等待用户确认，不能把待确认计划描述成已完成。" +
            "场景和样式只能使用工具提供的受控字段，绝不生成或执行 JavaScript、CSS、Three.js 代码，也不提供删除操作。" +
            "用简洁中文说明查询结论或将要发生的配置变化。",
        Enabled = true,
        MaxToolCalls = 6,
        Version = "1.0",
        Suggestions = new List<string>
        {
            "查看各线体当前机械手运行状态",
            "查询今天故障次数最多的机械手",
            "查看 W1 线的场景和图表配置",
            "检查 W1 线设备卡片为什么没有实时数据",
            "查看 W1 线设备卡片的今日字段变更",
            "把 CHUANPXX_JQR 映射为穿片下线机器人",
            "查看 W1 线的当前产量和产量来源",
            "把 W1-R-01 的工位名称改为自动上快装工位",
            "在 W1 线新增一个设备组件",
            "把 W1-R-01 移动到坐标 4.5，-1",
            "把 W1 的设备 device-01 绑定到实时数据源",
            "新增一个展示今日故障的图表",
            "新增一张加工中心设备卡片并绑定运行状态字段"
        }
    };

    public IReadOnlyCollection<AgentToolSeed> Tools { get; } = Array.Empty<AgentToolSeed>();
}

/// <summary>生产看板 Agent 工具配置工厂。</summary>
internal static class ProductionDashboardAgentToolSeedFactory
{
    internal const string EmptySchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
    private const string ObjectOutput =
        "{\"type\":\"object\",\"properties\":{\"success\":{\"type\":\"boolean\"},\"message\":{\"type\":\"string\"},\"data\":{}},\"required\":[\"success\",\"message\"]}";

    internal static AgentToolSeed Create(
        string toolName,
        string title,
        string description,
        string actionName,
        string inputSchema,
        int sort,
        string examples,
        bool isWriteAction = false) => new()
    {
        AgentCode = AgentCodes.ProductionDashboard,
        ToolName = toolName,
        Title = title,
        Description = description,
        Method = isWriteAction ? "POST" : "GET",
        Route = $"internal://ProductionDashboard/{actionName}",
        InputSchema = inputSchema,
        OutputSchema = ObjectOutput,
        IsWriteAction = isWriteAction,
        RequiresConfirmation = isWriteAction,
        Enabled = true,
        PermissionKey = isWriteAction ? "AgentExecutor" : "ProjectDashboard",
        RiskLevel = isWriteAction ? "Medium" : "Low",
        ServiceName = "ProductionDashboard",
        ActionName = actionName,
        Examples = examples,
        Version = "1.0",
        Sort = sort
    };
}
