using Model.Agent;

namespace Service;

/// <summary>失败时只反馈已知执行状态；不重跑工具，也不将模型异常正文发送给用户。</summary>
public static class AgentReplyFailureFeedback
{
    public static bool MayHaveWriteEffects(
        IReadOnlyList<AgentToolCallDto> calls,
        IReadOnlyList<AgentToolDefinitionDto> tools)
    {
        return calls.Any(call =>
            call.Status is not ("PendingConfirm" or "PendingConfirmation" or "PermissionDenied") &&
            // 无法识别的工具按可能写入处理，不能错误地承诺未修改数据。
            tools.FirstOrDefault(tool => tool.Name.Equals(call.ToolName, StringComparison.OrdinalIgnoreCase))
                ?.IsWriteAction != false);
    }

    public static string Build(
        AgentReplyFailureKind failureKind,
        IReadOnlyList<AgentToolCallDto> calls,
        bool mayHaveWriteEffects)
    {
        var reason = failureKind switch
        {
            AgentReplyFailureKind.OutputLimit => "本次回答达到模型输出长度限制，未能生成完整结果。",
            AgentReplyFailureKind.EmptyResponse => "AI 未返回有效回答，可能是本次数据范围较大。",
            AgentReplyFailureKind.InvalidResponse => "AI 返回的数据格式异常，暂时无法生成回答。",
            _ => "AI 暂时无法生成完整回答。"
        };
        if (mayHaveWriteEffects)
        {
            return reason + "\n已有操作可能执行，请先到后台核对工具执行记录，不要重复提交；系统不会自动重试操作。";
        }

        var execution = calls.Count == 0
            ? "本次未执行业务操作。"
            : calls.All(call => call.Status is "Succeeded" or "Success")
                ? "数据查询已完成，本次未修改业务数据。"
                : "请查看后台查询执行记录，本次未执行写操作。";
        return reason + "\n" + execution +
            "\n建议缩小查询范围：指定一条线体、某台设备或更短的时间段，或一次只查询一个指标后重新提问。";
    }
}
