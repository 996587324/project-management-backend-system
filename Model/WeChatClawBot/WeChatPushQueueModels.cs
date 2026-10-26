using SqlSugar;

namespace Model.WeChatClawBot;

[SugarTable("WeChatPushQueueSetting")]
public sealed class WeChatPushQueueSettingEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = 1;
    [SugarColumn(DefaultValue = "5")]
    public int IntervalSeconds { get; set; } = 5;
    [SugarColumn(IsNullable = true)]
    public DateTime? NextSendTime { get; set; }
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? LeaseOwner { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseExpireTime { get; set; }
}

/// <summary>通用微信发送出站队列；身份与绑定在提交时固定，发送前再次校验。</summary>
[SugarTable("WeChatPushQueue")]
[SugarIndex("UX_WeChatPushQueue_KeyRecipient", nameof(RequestKey), OrderByType.Asc, nameof(RecipientUserId), OrderByType.Asc, true)]
[SugarIndex("IX_WeChatPushQueue_StatusTime", nameof(Status), OrderByType.Asc, nameof(CreateTime), OrderByType.Asc, false)]
public sealed class WeChatPushQueueEntity
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
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string RecipientUserId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, DefaultValue = " ")]
    public string BindingIdentity { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", DefaultValue = " ")]
    public string Content { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(128)", IsNullable = true)]
    public string? ExternalTaskId { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(32)", IsNullable = true)]
    public string? SourceLabel { get; set; }
    [SugarColumn(Length = 24, DefaultValue = "Pending")]
    public string Status { get; set; } = "Pending";
    [SugarColumn(DefaultValue = "0")]
    public int Attempt { get; set; }
    [SugarColumn(IsNullable = true)]
    public long? DeliveryId { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(1000)", IsNullable = true)]
    public string? Error { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseExpireTime { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? SentTime { get; set; }
    [SugarColumn(DefaultValue = "GETDATE()")]
    public DateTime CreateTime { get; set; } = DateTime.Now;
    [SugarColumn(DefaultValue = "GETDATE()")]
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

public sealed class WeChatPushEnqueueDto
{
    public string? ExternalTaskId { get; set; }
    public string? SourceLabel { get; set; }
    public List<string> RecipientUserIds { get; set; } = [];
    public string Content { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class WeChatPushQueueRecordDto
{
    public string? ExternalTaskId { get; set; }
    public string? SourceLabel { get; set; }
    public long Id { get; set; }
    public string RecipientUserId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long? DeliveryId { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? SentTime { get; set; }
}

public sealed class WeChatPushQueueQuery
{
    public long? TaskId { get; set; }
    public long? DeliveryId { get; set; }
    public string? RecipientCode { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? Status { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}
