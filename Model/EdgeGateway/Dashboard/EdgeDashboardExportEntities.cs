using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>看板日报定时导出配置；每个看板保存一条数据库配置。</summary>
[SugarTable("EdgeDashboardExportSetting")]
public sealed class EdgeDashboardExportSettingEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    public bool Enabled { get; set; }
    [SugarColumn(Length = 5)]
    public string ExportTime { get; set; } = "00:10";
    public int ExportDayOffset { get; set; } = 1;
    [SugarColumn(IsNullable = true)]
    public DateTime? LastScheduledRunDate { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdatedBy { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>后台按线体生成的日报文件记录，供前端查看状态和再次下载。</summary>
[SugarTable("EdgeDashboardExportRecord")]
public sealed class EdgeDashboardExportRecordEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string DashboardCode { get; set; } = "production-dashboard";
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string LineName { get; set; } = string.Empty;
    public DateTime ReportDate { get; set; }
    [SugarColumn(Length = 24)]
    public string TriggerType { get; set; } = "Scheduled";
    [SugarColumn(Length = 260)]
    public string FileName { get; set; } = string.Empty;
    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? RelativePath { get; set; }
    public long FileSize { get; set; }
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ErrorMessage { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? CompleteTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
