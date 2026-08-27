using Model.EdgeGateway;

namespace Interface;

/// <summary>看板日报手动下载、定时生成及历史文件管理服务。</summary>
public interface IEdgeDashboardExportService
{
    /// <summary>读取数据库中的定时导出配置，不存在时创建默认关闭配置。</summary>
    Task<EdgeDashboardExportSettingOutputDto> GetSettingAsync(string dashboardCode);

    /// <summary>校验并保存每日导出时间及统计日期口径。</summary>
    Task<EdgeDashboardExportSettingOutputDto> SaveSettingAsync(
        EdgeDashboardExportSettingSaveDto dto);

    /// <summary>为当前选定线体生成指定日期的日报下载流。</summary>
    Task<EdgeDashboardExportStreamDto> ExportLineAsync(
        string dashboardCode,
        string lineCode,
        DateTime reportDate,
        CancellationToken cancellationToken = default);

    /// <summary>分页查看后台定时生成的线体日报记录。</summary>
    Task<EdgePagedResultDto<EdgeDashboardExportRecordOutputDto>> GetRecordsAsync(
        EdgeDashboardExportRecordQueryDto query);

    /// <summary>打开一条已完成的定时导出文件。</summary>
    Task<EdgeDashboardExportStreamDto> OpenRecordAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>扫描到期配置并按启用线体分别生成日报。</summary>
    Task<int> RunScheduledExportsAsync(
        DateTime now,
        CancellationToken cancellationToken = default);
}
