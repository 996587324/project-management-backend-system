using Model.EdgeGateway;
using NPOI.SS.UserModel;
using NPOI.XSSF.Streaming;

namespace Service;

/// <summary>导出工作簿所需的线体、每日统计和故障数据快照。</summary>
internal sealed class EdgeDashboardWorkbookData
{
    public required EdgeDashboardLineConfigEntity Line { get; init; }
    public required DateTime ReportDate { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required int ConfiguredRobotCount { get; init; }
    public required List<EdgeDashboardRobotDailyStatisticEntity> Statistics { get; init; }
    public required List<EdgeDashboardRobotFaultRecordEntity> Faults { get; init; }
}

/// <summary>使用项目现有 NPOI 组件生成线体看板日报。</summary>
internal static class EdgeDashboardWorkbookWriter
{
    private const int RowAccessWindowSize = 100;

    /// <summary>把汇总、机械手统计、故障明细和故障原因汇总写入同一个 Excel 工作簿。</summary>
    public static void Write(
        Stream output,
        EdgeDashboardWorkbookData data,
        CancellationToken cancellationToken)
    {
        using var workbook = new SXSSFWorkbook(RowAccessWindowSize)
        {
            CompressTempFiles = true
        };
        var styles = CreateStyles(workbook);
        WriteOverviewSheet(workbook, data, styles);
        cancellationToken.ThrowIfCancellationRequested();
        WriteDailyStatisticsSheet(workbook, data, styles);
        cancellationToken.ThrowIfCancellationRequested();
        WriteFaultRecordsSheet(workbook, data, styles);
        cancellationToken.ThrowIfCancellationRequested();
        WriteFaultSummarySheet(workbook, data, styles);
        cancellationToken.ThrowIfCancellationRequested();
        workbook.Write(output, true);
    }

    /// <summary>生成线体日报首页，并汇总当天所有机械手状态时间。</summary>
    private static void WriteOverviewSheet(
        IWorkbook workbook,
        EdgeDashboardWorkbookData data,
        WorkbookStyles styles)
    {
        var sheet = workbook.CreateSheet("线体日报");
        var statistics = data.Statistics;
        var running = statistics.Sum(x => x.RunningSeconds);
        var idle = statistics.Sum(x => x.IdleSeconds);
        var fault = statistics.Sum(x => x.FaultSeconds);
        var effective = running + idle + fault;
        var availability = effective == 0 ? 0m : Math.Round(running * 100m / effective, 2);
        var rows = new (string Label, object? Value)[]
        {
            ("看板编码", data.Line.DashboardCode),
            ("线体编码", data.Line.LineCode),
            ("线体名称", data.Line.LineName),
            ("区域", data.Line.Area),
            ("统计日期", data.ReportDate.ToString("yyyy-MM-dd")),
            ("生成时间", data.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            ("配置机械手数量", data.ConfiguredRobotCount),
            ("有统计数据机械手数量", statistics.Count),
            ("运行时长", FormatDuration(running)),
            ("空闲时长", FormatDuration(idle)),
            ("故障时长", FormatDuration(fault)),
            ("停止时长", FormatDuration(statistics.Sum(x => x.StoppedSeconds))),
            ("离线时长", FormatDuration(statistics.Sum(x => x.OfflineSeconds))),
            ("未知时长", FormatDuration(statistics.Sum(x => x.UnknownSeconds))),
            ("故障次数", statistics.Sum(x => x.FaultCount)),
            ("样本数量", statistics.Sum(x => x.SampleCount)),
            ("稼动率", $"{availability:0.##}%")
        };

        var titleRow = sheet.CreateRow(0);
        CreateCell(titleRow, 0, $"{data.Line.LineName} 看板日报", styles.Title);
        sheet.AddMergedRegion(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, 1));
        for (var index = 0; index < rows.Length; index++)
        {
            var row = sheet.CreateRow(index + 2);
            CreateCell(row, 0, rows[index].Label, styles.Header);
            CreateCell(row, 1, rows[index].Value, styles.Value);
        }
        sheet.SetColumnWidth(0, 24 * 256);
        sheet.SetColumnWidth(1, 34 * 256);
    }

    /// <summary>逐机械手写入每天各状态累计秒数和稼动率。</summary>
    private static void WriteDailyStatisticsSheet(
        IWorkbook workbook,
        EdgeDashboardWorkbookData data,
        WorkbookStyles styles)
    {
        var sheet = workbook.CreateSheet("机械手每日统计");
        var headers = new[]
        {
            "统计日期", "机械手编码", "机械手名称", "运行秒数", "空闲秒数", "故障秒数",
            "停止秒数", "离线秒数", "未知秒数", "故障次数", "样本数量", "稼动率(%)", "最终状态"
        };
        WriteHeader(sheet, headers, styles.Header);
        var rowIndex = 1;
        foreach (var item in data.Statistics.OrderBy(x => x.RobotCode))
        {
            var row = sheet.CreateRow(rowIndex++);
            var effective = item.RunningSeconds + item.IdleSeconds + item.FaultSeconds;
            var availability = effective == 0 ? 0m : Math.Round(item.RunningSeconds * 100m / effective, 2);
            var values = new object?[]
            {
                item.StatDate.ToString("yyyy-MM-dd"), item.RobotCode, item.RobotName,
                item.RunningSeconds, item.IdleSeconds, item.FaultSeconds, item.StoppedSeconds,
                item.OfflineSeconds, item.UnknownSeconds, item.FaultCount, item.SampleCount,
                availability, item.LastState
            };
            WriteValues(row, values, styles.Value);
        }
        ConfigureTableSheet(sheet, headers.Length, 16);
        sheet.SetColumnWidth(1, 24 * 256);
        sheet.SetColumnWidth(2, 24 * 256);
    }

    /// <summary>写入当天故障生命周期；模型报警原因统一使用报警映射 FaultCode。</summary>
    private static void WriteFaultRecordsSheet(
        IWorkbook workbook,
        EdgeDashboardWorkbookData data,
        WorkbookStyles styles)
    {
        var sheet = workbook.CreateSheet("故障记录");
        var headers = new[]
        {
            "机械手编码", "机械手名称", "设备标识", "故障编码", "故障原因", "等级",
            "开始时间", "结束时间", "持续秒数", "状态"
        };
        WriteHeader(sheet, headers, styles.Header);
        var rowIndex = 1;
        foreach (var item in data.Faults.OrderByDescending(x => x.StartTime))
        {
            var row = sheet.CreateRow(rowIndex++);
            var reason = IsModelRule(item.RuleScope) ? item.FaultCode : item.FaultReason;
            var duration = GetReportDurationSeconds(item, data);
            var values = new object?[]
            {
                item.RobotCode, item.RobotName, item.DeviceMark, item.FaultCode, reason,
                item.Severity, item.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                item.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未恢复", duration, item.Status
            };
            WriteValues(row, values, styles.Value);
        }
        ConfigureTableSheet(sheet, headers.Length, 19);
        sheet.SetColumnWidth(2, 34 * 256);
        sheet.SetColumnWidth(4, 28 * 256);
        sheet.SetColumnWidth(6, 22 * 256);
        sheet.SetColumnWidth(7, 22 * 256);
    }

    /// <summary>按照故障编码和展示原因汇总次数及累计时长。</summary>
    private static void WriteFaultSummarySheet(
        IWorkbook workbook,
        EdgeDashboardWorkbookData data,
        WorkbookStyles styles)
    {
        var sheet = workbook.CreateSheet("故障原因汇总");
        var headers = new[] { "故障编码", "故障原因", "发生次数", "累计秒数", "累计时长" };
        WriteHeader(sheet, headers, styles.Header);
        var summaries = data.Faults
            .Select(x => new
            {
                x.FaultCode,
                FaultReason = IsModelRule(x.RuleScope) ? x.FaultCode : x.FaultReason,
                DurationSeconds = GetReportDurationSeconds(x, data)
            })
            .GroupBy(x => new { x.FaultCode, x.FaultReason })
            .Select(x => new
            {
                x.Key.FaultCode,
                x.Key.FaultReason,
                Count = x.Count(),
                DurationSeconds = x.Sum(item => item.DurationSeconds)
            })
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.DurationSeconds);
        var rowIndex = 1;
        foreach (var item in summaries)
        {
            var row = sheet.CreateRow(rowIndex++);
            WriteValues(row, new object?[]
            {
                item.FaultCode, item.FaultReason, item.Count, item.DurationSeconds,
                FormatDuration(item.DurationSeconds)
            }, styles.Value);
        }
        ConfigureTableSheet(sheet, headers.Length, 22);
        sheet.SetColumnWidth(1, 30 * 256);
    }

    private static WorkbookStyles CreateStyles(IWorkbook workbook)
    {
        var title = workbook.CreateCellStyle();
        var titleFont = workbook.CreateFont();
        titleFont.IsBold = true;
        titleFont.FontHeightInPoints = 16;
        title.SetFont(titleFont);
        title.Alignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;

        var header = workbook.CreateCellStyle();
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerFont.Color = IndexedColors.White.Index;
        header.SetFont(headerFont);
        header.FillForegroundColor = IndexedColors.DarkBlue.Index;
        header.FillPattern = FillPattern.SolidForeground;
        header.Alignment = HorizontalAlignment.Center;
        header.VerticalAlignment = VerticalAlignment.Center;
        SetBorders(header);

        var value = workbook.CreateCellStyle();
        value.VerticalAlignment = VerticalAlignment.Center;
        SetBorders(value);
        return new WorkbookStyles(title, header, value);
    }

    private static void WriteHeader(ISheet sheet, IReadOnlyList<string> headers, ICellStyle style)
    {
        var row = sheet.CreateRow(0);
        row.HeightInPoints = 24;
        for (var index = 0; index < headers.Count; index++)
            CreateCell(row, index, headers[index], style);
    }

    private static void WriteValues(IRow row, IReadOnlyList<object?> values, ICellStyle style)
    {
        for (var index = 0; index < values.Count; index++)
            CreateCell(row, index, values[index], style);
    }

    private static void CreateCell(IRow row, int column, object? value, ICellStyle style)
    {
        var cell = row.CreateCell(column);
        switch (value)
        {
            case int intValue:
                cell.SetCellValue(intValue);
                break;
            case long longValue:
                cell.SetCellValue(longValue);
                break;
            case decimal decimalValue:
                cell.SetCellValue((double)decimalValue);
                break;
            case double doubleValue:
                cell.SetCellValue(doubleValue);
                break;
            default:
                cell.SetCellValue(value?.ToString() ?? string.Empty);
                break;
        }
        cell.CellStyle = style;
    }

    private static void ConfigureTableSheet(ISheet sheet, int columnCount, int defaultWidth)
    {
        sheet.CreateFreezePane(0, 1);
        sheet.SetAutoFilter(new NPOI.SS.Util.CellRangeAddress(0, 0, 0, columnCount - 1));
        for (var index = 0; index < columnCount; index++)
            sheet.SetColumnWidth(index, defaultWidth * 256);
    }

    private static void SetBorders(ICellStyle style)
    {
        style.BorderTop = BorderStyle.Thin;
        style.BorderBottom = BorderStyle.Thin;
        style.BorderLeft = BorderStyle.Thin;
        style.BorderRight = BorderStyle.Thin;
    }

    private static bool IsModelRule(string? ruleScope) =>
        string.Equals(ruleScope, "Model", StringComparison.OrdinalIgnoreCase);

    /// <summary>活动故障最多累计到日报当天结束，避免前一天报表带入第二天持续时间。</summary>
    private static long GetReportDurationSeconds(
        EdgeDashboardRobotFaultRecordEntity fault,
        EdgeDashboardWorkbookData data)
    {
        var reportEnd = data.ReportDate.AddDays(1);
        var effectiveEnd = fault.EndTime ?? data.GeneratedAt;
        if (effectiveEnd > reportEnd) effectiveEnd = reportEnd;
        return (long)Math.Max(0, (effectiveEnd - fault.StartTime).TotalSeconds);
    }

    private static string FormatDuration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        var duration = TimeSpan.FromSeconds(seconds);
        return $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private sealed record WorkbookStyles(ICellStyle Title, ICellStyle Header, ICellStyle Value);
}
