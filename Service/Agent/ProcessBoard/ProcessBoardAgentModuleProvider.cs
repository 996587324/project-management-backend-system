using Interface;
using Model.Agent;

namespace Service;

/// <summary>工艺看板独立业务域；业务执行复用看板服务的权限、版本和审计。</summary>
public sealed class ProcessBoardAgentModuleProvider : IAgentModuleProvider
{
    public AgentModuleSeed Module { get; } = new()
    {
        AgentCode = AgentCodes.ProcessBoard,
        Name = "产线工艺看板 Agent",
        Description = "查询及配置区域、工艺线体、设备绑定、参数卡片、公式、上下限、追溯、曲线、流程图、自由布局、微信推送及导出。",
        SystemPrompt = "你是产线工艺看板助手，业务域为 process-board，不是三维生产监控看板。" +
            "所有数据必须通过工具查询，不能虚构设备、ID、字段路径、参数值、收件人或执行结果。" +
            "先查询区域/线体/卡片列表，再按ID读取完整定义，名称重名时必须追问。" +
            "写操作必须等待用户回复确认；未确认只能描述计划，不能宣称成功。" +
            "修改必须使用刚读取的 version；配置冲突时重新读取并告知用户，禁止自动覆盖。" +
            "update只改基本信息，layout-update改布局，field-upsert配置单个参数。字段补丁省略的属性必须保留，清除上下限或固定文本用null。" +
            "绑定前查询数据源和完整历史字段路径，继承共享中文映射；不能凭名称编造字段路径。" +
            "公式使用既有mXparser；先用formula-preview做数学试算，用户必须自行确认真实授权声明，不能代签授权。" +
            "flows-upsert合并节点和连线，devices-bind合并设备，均不删除已有配置。删除请到人工配置页。" +
            "不修改采集实时值、历史采样、事件及审计，不执行脚本或任意SQL，不提供删除工具。" +
            "新建父级后必须等待确认并取得真实ID，再创建其子级，不猜测新ID。" +
            "大范围查询先概览再分页缩小到线体、卡片、字段和时间段，曲线使用有限聚合点，明细大批量使用异步导出。" +
            "推送只配置绑定人和触发条件，不主动群发或重复发送；系统级采样及保留时间仅管理员可改。用简洁中文回复。",
        MaxToolCalls = 6,
        Suggestions =
        [
            "查看各区域工艺线体和卡片数量",
            "查询某条工艺线体的当前参数和超限情况",
            "给参数卡片新增温度字段并配置上下限和追溯",
            "把温度显示公式改成 x/10 并试算",
            "查询最近一小时温度曲线和超限事件",
            "把卡片改成每行显示两组参数",
            "给温度超限配置微信接收人",
            "查看工艺流程和最近配置变更记录"
        ]
    };

    public IReadOnlyCollection<AgentToolSeed> Tools => ProcessBoardAgentToolCatalog.Definitions;
}
