using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>全局字段推送队列配置与跨进程发送节流租约。</summary>
[SugarTable("EdgeDeviceCardPushQueueSetting")]
public sealed class EdgeDeviceCardPushQueueSettingEntity
{
    // 固定单例配置键，不是自增业务主键。
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(DefaultValue = "5")]
    public int IntervalSeconds { get; set; } = 5;
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? NextSendTime { get; set; }
    [SugarColumn(IsNullable = true, Length = 64)]
    public string? LeaseOwner { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? LeaseExpireTime { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
