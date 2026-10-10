using Model.EdgeGateway;

namespace Interface;

/// <summary>模型映射、字段展示映射与状态优先级的批量导入导出。</summary>
public interface IEdgeDashboardModelConfigurationTransferService
{
    Task<EdgeDashboardModelConfigurationExportDto> ExportAsync(
        string dashboardCode,
        bool includeData,
        CancellationToken cancellationToken = default);

    Task<EdgeDashboardModelConfigurationImportResultDto> ImportAsync(
        string fileName,
        Stream stream,
        string dashboardCode,
        string? csvConfigurationType,
        CancellationToken cancellationToken = default);
}
