using SqlSugar;

namespace Model.EmailPush;

[SugarTable("EmailPushSetting")]
public sealed class EmailPushSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(DefaultValue = "0")]
    public bool Enabled { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(256)", DefaultValue = " ")]
    public string Host { get; set; } = string.Empty;
    [SugarColumn(DefaultValue = "587")]
    public int Port { get; set; } = 587;
    [SugarColumn(Length = 24, DefaultValue = "StartTls")]
    public string Security { get; set; } = "StartTls";
    [SugarColumn(ColumnDataType = "nvarchar(256)", DefaultValue = " ")]
    public string UserName { get; set; } = string.Empty;
    [SugarColumn(Length = 4000, IsNullable = true)]
    public string? ProtectedPassword { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(256)", DefaultValue = " ")]
    public string FromAddress { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(128)", DefaultValue = " ")]
    public string FromName { get; set; } = string.Empty;
    [SugarColumn(DefaultValue = "30")]
    public int TimeoutSeconds { get; set; } = 30;
    [SugarColumn(DefaultValue = "5")]
    public int IntervalSeconds { get; set; } = 5;
    [SugarColumn(DefaultValue = "3")]
    public int MaxAttempts { get; set; } = 3;
    [SugarColumn(DefaultValue = "1")]
    public int Version { get; set; } = 1;
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UpdatedBy { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? UpdateTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? NextSendTime { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? LeaseOwner { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseExpireTime { get; set; }
}

[SugarTable("EmailPushTask")]
[SugarIndex("UX_EmailPushTask_RequestKey", nameof(RequestKey), OrderByType.Asc, true)]
[SugarIndex("IX_EmailPushTask_CreatorTime", nameof(CreatedBy), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
public sealed class EmailPushTaskEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string RequestKey { get; set; } = string.Empty;
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string PayloadHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string CreatedBy { get; set; } = string.Empty;
    [SugarColumn(Length = 16, DefaultValue = " ")]
    public string AppCode { get; set; } = string.Empty;
    [SugarColumn(Length = 32, DefaultValue = "Manual")]
    public string Source { get; set; } = "Manual";
    [SugarColumn(ColumnDataType = "nvarchar(160)", DefaultValue = " ")]
    public string Subject { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", DefaultValue = " ")]
    public string Body { get; set; } = string.Empty;
    [SugarColumn(DefaultValue = "GETDATE()")]
    public DateTime CreateTime { get; set; } = DateTime.Now;
}

[SugarTable("EmailPushDelivery")]
[SugarIndex("IX_EmailPushDelivery_Dispatch", nameof(Status), OrderByType.Asc, nameof(NextAttemptTime), OrderByType.Asc, false)]
[SugarIndex("IX_EmailPushDelivery_Task", nameof(TaskId), OrderByType.Asc, false)]
public sealed class EmailPushDeliveryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long TaskId { get; set; }
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string RecipientUserId { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(256)", DefaultValue = " ")]
    public string Address { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(256)", IsNullable = true)]
    public string? FromAddress { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? SettingVersion { get; set; }
    [SugarColumn(Length = 8, DefaultValue = "To")]
    public string RecipientKind { get; set; } = "To";
    [SugarColumn(Length = 24, DefaultValue = "Pending")]
    public string Status { get; set; } = "Pending";
    [SugarColumn(DefaultValue = "0")]
    public int AttemptCount { get; set; }
    [SugarColumn(DefaultValue = "GETDATE()")]
    public DateTime NextAttemptTime { get; set; } = DateTime.Now;
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseExpireTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? SentTime { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(1000)", IsNullable = true)]
    public string? ErrorMessage { get; set; }
    [SugarColumn(DefaultValue = "GETDATE()")]
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("EmailPushConfigurationAudit")]
public sealed class EmailPushConfigurationAuditEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Action { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(1000)")]
    public string Summary { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
