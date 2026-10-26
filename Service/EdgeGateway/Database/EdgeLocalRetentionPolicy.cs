namespace Service;

/// <summary>本地日库按 UTC 日期计算工作日年龄；只跳过周末，不套用节假日或调休日历。</summary>
public static class EdgeLocalRetentionPolicy
{
    public const int RetentionWorkdays = 4;

    /// <summary>
    /// 刚满四个工作日的文件保留，早于返回日期的文件才删除。
    /// 周末以最近的周五为计算基准，因此周六、周日不会额外缩短保留时间。
    /// </summary>
    public static DateTime GetOldestRetainedDate(DateTime utcNow)
    {
        var date = utcNow.Date;
        while (!IsWorkday(date))
        {
            date = date.AddDays(-1);
        }

        var elapsed = 0;
        while (elapsed < RetentionWorkdays)
        {
            date = date.AddDays(-1);
            if (IsWorkday(date)) elapsed++;
        }
        return date;
    }

    private static bool IsWorkday(DateTime date)
    {
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
