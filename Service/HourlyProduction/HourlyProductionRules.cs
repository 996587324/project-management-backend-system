using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Model.HourlyProduction;

namespace Service;

/// <summary>确定性统计规则，与数据库、HTTP 和模型调用无关，可独立验证跨夜班及乱序补传。</summary>
public static class HourlyProductionRules
{
    public static DateTime Now => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "China Standard Time");

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static (DateTime Start, DateTime End) Window(HourlyBoardDefinition board, DateTime time)
    {
        var start = TimeSpan.ParseExact(board.StartTime, @"hh\:mm", CultureInfo.InvariantCulture);
        var end = TimeSpan.ParseExact(board.EndTime, @"hh\:mm", CultureInfo.InvariantCulture);
        var date = time.Date;
        // 仅跨夜班在凌晨沿用上一个生产日；日班在开班前展示当天“待开始”。
        if (end <= start && time.TimeOfDay < end) date = date.AddDays(-1);
        return (date.Add(start), date.Add(end).AddDays(end <= start ? 1 : 0));
    }

    public static List<(DateTime Start, DateTime End)> Slots(HourlyBoardDefinition board, DateTime time)
    {
        var window = Window(board, time);
        var result = new List<(DateTime, DateTime)>();
        for (var start = window.Start; start < window.End; start = start.AddHours(1))
            result.Add((start, start.AddHours(1) < window.End ? start.AddHours(1) : window.End));
        return result;
    }

    /// <summary>按事件时间重放同 SN 的有效过站；与上一次有效过站比较，抖动报文不会延长抑制窗口。</summary>
    public static void Classify(HourlyProcessDefinition process, List<HourlyProductionPassEntity> events)
    {
        HourlyProductionPassEntity? lastEffective = null;
        foreach (var item in events.OrderBy(x => x.OccurredAt).ThenBy(x => x.EventKey, StringComparer.Ordinal))
        {
            item.Output = 0;
            item.Rework = 0;
            if (process.Mode != "DistinctSn")
            {
                item.Rework = item.ExplicitRework ? item.Quantity : 0;
                item.Output = !item.ExplicitRework || process.IncludeReworkInOutput ? item.Quantity : 0;
                continue;
            }
            if (lastEffective == null)
            {
                item.Rework = item.ExplicitRework ? 1 : 0;
                item.Output = !item.ExplicitRework || process.IncludeReworkInOutput ? 1 : 0;
                lastEffective = item;
                continue;
            }
            if ((item.OccurredAt - lastEffective.OccurredAt).TotalMinutes <= process.ReworkMinutes) continue;
            if (!item.ExplicitRework && process.ReworkRequiresDifferentHour && item.SlotStart == lastEffective.SlotStart) continue;
            item.Rework = 1;
            item.Output = process.IncludeReworkInOutput ? 1 : 0;
            lastEffective = item;
        }
    }
}
