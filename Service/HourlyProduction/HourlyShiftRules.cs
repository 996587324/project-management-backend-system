using System.Globalization;

namespace Service;

/// <summary>白夜班连续衔接的时间计算；不依赖数据库或系统当前时区。</summary>
public static class HourlyShiftRules
{
    public sealed record ShiftWindow(string Code, DateTime Start, DateTime End);

    public static (TimeSpan DayStart, TimeSpan NightStart) Validate(string dayStart, string nightStart)
    {
        if (!TimeSpan.TryParseExact(dayStart, @"hh\:mm", CultureInfo.InvariantCulture, out var day) ||
            !TimeSpan.TryParseExact(nightStart, @"hh\:mm", CultureInfo.InvariantCulture, out var night) ||
            day < TimeSpan.Zero || night < TimeSpan.Zero || day >= TimeSpan.FromDays(1) ||
            night >= TimeSpan.FromDays(1) || day >= night)
            throw new ArgumentException("白班开始时间必须早于夜班开始时间，时间格式为 HH:mm");
        return (day, night);
    }

    public static ShiftWindow At(string dayStart, string nightStart, DateTime time)
    {
        var (day, night) = Validate(dayStart, nightStart);
        var date = time.Date;
        if (time.TimeOfDay < day) return new("Night", date.AddDays(-1).Add(night), date.Add(day));
        if (time.TimeOfDay < night) return new("Day", date.Add(day), date.Add(night));
        return new("Night", date.Add(night), date.AddDays(1).Add(day));
    }

    public static ShiftWindow Previous(string dayStart, string nightStart, DateTime time) =>
        At(dayStart, nightStart, At(dayStart, nightStart, time).Start.AddTicks(-1));

    public static IReadOnlyList<(DateTime Start, DateTime End)> Slots(ShiftWindow window)
    {
        var slots = new List<(DateTime, DateTime)>();
        for (var start = window.Start; start < window.End; start = start.AddHours(1))
            slots.Add((start, start.AddHours(1) < window.End ? start.AddHours(1) : window.End));
        return slots;
    }
}
