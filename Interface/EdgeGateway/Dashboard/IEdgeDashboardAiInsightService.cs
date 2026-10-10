using Model.EdgeGateway;

namespace Interface;

/// <summary>生产看板 AI 运行建议的读取和定时生成服务。</summary>
public interface IEdgeDashboardAiInsightService
{
    /// <summary>读取线体当前仍有效的建议；数据或模型异常时返回空，不输出占位内容。</summary>
    Task<EdgeDashboardAiInsightOutputDto?> GetLatestAsync(string dashboardCode, string lineCode);

    /// <summary>按启用线体生成建议。没有可靠结论、设备断线或模型失败时会撤销当前建议。</summary>
    Task<int> RefreshInsightsAsync(DateTime now, CancellationToken cancellationToken = default);
}
