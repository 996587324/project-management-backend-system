namespace Model.EdgeGateway;

/// <summary>机械手运行状态、规则和看板部件使用的受控枚举值。</summary>
public static class EdgeDashboardConstants
{
    public const string Running = "RUNNING";
    public const string Idle = "IDLE";
    public const string Fault = "FAULT";
    public const string Stopped = "STOPPED";
    public const string Offline = "OFFLINE";
    public const string Unknown = "UNKNOWN";

    public static readonly string[] RequiredStatisticStates = [Running, Idle, Fault];
    public static readonly string[] SupportedStates =
        [Running, Idle, Fault, Stopped, Offline, Unknown];
    public static readonly string[] SupportedOperators =
        ["Equals", "NotEquals", "GreaterThan", "GreaterThanOrEqual", "LessThan",
            "LessThanOrEqual", "Contains", "NotContains", "IsEmpty", "IsNotEmpty"];
    public static readonly string[] SupportedWidgetTypes = ["Table", "Pie", "Bar"];
    public static readonly string[] SupportedDataSourceTypes =
        ["RealtimeRobot", "DailyStatistics", "FaultRecords", "FaultSummary"];
}
