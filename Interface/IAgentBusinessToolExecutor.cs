using System.Text.Json;
using Model.Agent;

namespace Interface;

/// <summary>
/// Agent 业务工具执行器接口。每个实现负责一个业务域，例如项目管理或邮件发送。
/// </summary>
public interface IAgentBusinessToolExecutor
{
    /// <summary>
    /// 判断当前执行器是否能处理指定服务名称和动作名称。
    /// </summary>
    bool CanExecute(string serviceName, string actionName);

    /// <summary>
    /// 执行具体业务动作，并返回统一的 Agent 工具执行结果。
    /// </summary>
    Task<AgentBusinessToolResult> ExecuteAsync(
        string actionName,
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default);
}
