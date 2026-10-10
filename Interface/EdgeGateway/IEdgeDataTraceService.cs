using Model.EdgeGateway;

namespace Interface;

public interface IEdgeDataTraceService
{
    Task<EdgeDataTraceSettingDto> GetSettingAsync();
    Task<EdgeDataTraceSettingDto> SaveSettingAsync(EdgeDataTraceSettingSaveDto dto, string? operatorName);
    Task<List<EdgeDataTraceRuleDto>> GetRulesAsync();
    Task<EdgeDataTraceRuleDto> SaveRuleAsync(EdgeDataTraceRuleSaveDto dto, string? operatorName);
    Task<bool> DeleteRuleAsync(int id, string? operatorName);
    Task<List<EdgeDataTraceTreeNodeDto>> GetTreeAsync();
    Task<List<EdgeDataTraceTrendSeriesDto>> GetTrendAsync(EdgeDataTraceTrendQueryDto query);
    Task<EdgeDataTraceDetailPageDto> GetDetailAsync(EdgeDataTraceDetailQueryDto query);
    Task<List<EdgeDataTraceSummaryDto>> GetSummariesAsync(EdgeDataTraceDetailQueryDto query);
    Task<EdgeDataTraceExportTaskDto> CreateExportTaskAsync(EdgeDataTraceExportRequestDto dto, string? operatorName);
    Task<EdgeDataTraceExportTaskDto?> GetExportTaskAsync(long id);
    Task<EdgeDataTraceExportFileDto?> OpenExportFileAsync(long id, string? operatorName, bool isAdministrator);
    Task ProcessPendingExportsAsync(CancellationToken cancellationToken);
    Task<int> CleanupAsync(CancellationToken cancellationToken);
    Task CaptureAsync(EdgeStandardMessageDto message, string edgeDeviceCode, CancellationToken cancellationToken);
}
