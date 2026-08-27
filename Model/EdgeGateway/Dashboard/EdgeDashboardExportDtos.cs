namespace Model.EdgeGateway;

/// <summary>保存看板日报定时导出配置。</summary>
public class EdgeDashboardExportSettingSaveDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public bool Enabled { get; set; }
    public string ExportTime { get; set; } = "00:10";
    public int ExportDayOffset { get; set; } = 1;
    public string? UpdatedBy { get; set; }
}

/// <summary>看板日报定时配置及最近执行日期。</summary>
public sealed class EdgeDashboardExportSettingOutputDto : EdgeDashboardExportSettingSaveDto
{
    public long Id { get; set; }
    public DateTime? LastScheduledRunDate { get; set; }
    public DateTime? NextScheduledRunTime { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>定时导出记录分页条件。</summary>
public sealed class EdgeDashboardExportRecordQueryDto : EdgePageQueryDto
{
    public string DashboardCode { get; set; } = "production-dashboard";
    public string? LineCode { get; set; }
    public string? Status { get; set; }
}

/// <summary>一条线体日报导出结果。</summary>
public sealed class EdgeDashboardExportRecordOutputDto
{
    public long Id { get; set; }
    public string DashboardCode { get; set; } = string.Empty;
    public string LineCode { get; set; } = string.Empty;
    public string LineName { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime? CompleteTime { get; set; }
    public DateTime CreateTime { get; set; }
}

/// <summary>控制器用于流式返回 Excel 文件的描述。</summary>
public sealed class EdgeDashboardExportStreamDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public Stream Content { get; set; } = Stream.Null;
}
