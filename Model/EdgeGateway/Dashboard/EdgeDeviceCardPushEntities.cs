using SqlSugar;

namespace Model.EdgeGateway;

[SugarTable("EdgeDeviceCardPushRule")]
[SugarIndex("UX_EdgeDeviceCardPushRule_Field", nameof(CardId), OrderByType.Asc, nameof(FieldCode), OrderByType.Asc, true)]
public sealed class EdgeDeviceCardPushRuleEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string SourceIdentity { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [SugarColumn(Length = 24)]
    public string Channel { get; set; } = "WeChat";
    public bool OnChange { get; set; }
    public bool OnLimit { get; set; } = true;
    public bool OnRecovery { get; set; }
    [SugarColumn(ColumnDataType = "decimal(18,6)", IsNullable = true)]
    public decimal? ChangeThreshold { get; set; }
    public int ChangeIntervalSeconds { get; set; } = 300;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? MessageTemplate { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeDeviceCardPushRecipient")]
[SugarIndex("UX_EdgeDeviceCardPushRecipient_User", nameof(RuleId), OrderByType.Asc, nameof(UserId), OrderByType.Asc, true)]
public sealed class EdgeDeviceCardPushRecipientEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RuleId { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
}

[SugarTable("EdgeDeviceCardPushState")]
[SugarIndex("UX_EdgeDeviceCardPushState_Rule", nameof(RuleId), OrderByType.Asc, true)]
public sealed class EdgeDeviceCardPushStateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RuleId { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ModelMark { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string LastValueJson { get; set; } = "null";
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ChangeBaselineJson { get; set; } = "null";
    [SugarColumn(Length = 16)]
    public string LimitState { get; set; } = "Unknown";
    [SugarColumn(IsNullable = true)]
    public long? SourceTimestamp { get; set; }
    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime DataTime { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? LastQueuedTime { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("EdgeDeviceCardPushTask")]
[SugarIndex("UX_EdgeDeviceCardPushTask_EventRecipient", nameof(EventKey), OrderByType.Asc, nameof(UserId), OrderByType.Asc, true)]
[SugarIndex("IX_EdgeDeviceCardPushTask_Dispatch", nameof(Status), OrderByType.Asc, nameof(NextAttemptTime), OrderByType.Asc, false)]
[SugarIndex("IX_EdgeDeviceCardPushTask_Card", nameof(CardId), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
public sealed class EdgeDeviceCardPushTaskEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long RuleId { get; set; }
    public long CardId { get; set; }
    [SugarColumn(Length = 64)]
    public string EventKey { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string LineCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DeviceMark { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string FieldCode { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? UserName { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BindingIdentity { get; set; }
    [SugarColumn(Length = 32)]
    public string EventType { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string Content { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    [SugarColumn(IsNullable = true)]
    public long? DeliveryId { get; set; }
    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }
    [SugarColumn(ColumnDataType = "datetime2")]
    public DateTime DataTime { get; set; }
    public DateTime NextAttemptTime { get; set; } = DateTime.Now;
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseExpireTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? SentTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
