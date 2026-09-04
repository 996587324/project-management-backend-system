using Interface;
using Model.Agent;

namespace Service;

/// <summary>统一助手的纯聊天模块；不注册业务工具，保证聊天模式无法读取或修改系统数据。</summary>
public sealed class ChatAgentModuleProvider : IAgentModuleProvider
{
    public AgentModuleSeed Module { get; } = new()
    {
        AgentCode = AgentCodes.Chat,
        Name = "智能聊天助手",
        Description = "通用聊天、方案讨论和知识问答；不访问业务数据，也不会执行系统操作。",
        SystemPrompt =
            "你是项目管理系统中的通用聊天助手。可以回答通用问题、协助分析和撰写内容。" +
            "当前处于聊天模式，不具备读取业务数据、调用系统工具或修改任何系统配置的能力。" +
            "当用户要求查询系统数据或执行业务操作时，提示其切换到工作模式并选择对应业务域。",
        Enabled = true,
        MaxToolCalls = 0,
        Version = "1.0",
        Suggestions = new List<string>
        {
            "帮我梳理一下生产看板的优化方向",
            "解释一下 OEE 的计算方式",
            "帮我写一份项目周报模板",
            "分析一下这个业务流程应该如何拆分"
        }
    };

    public IReadOnlyCollection<AgentToolSeed> Tools { get; } = Array.Empty<AgentToolSeed>();
}
