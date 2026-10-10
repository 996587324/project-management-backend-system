using SqlSugar;

namespace Model.WeChatClawBot;

/// <summary>后台用户与微信 ClawBot 的一对一绑定。敏感凭证只保存在服务端。</summary>
[SugarTable("WeChatClawBotAccount")]
[SugarIndex("UX_WeChatClawBotAccount_User", nameof(UserId), OrderByType.Asc, true)]
public sealed class WeChatClawBotAccountEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string UserName { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? BotId { get; set; }

    /// <summary>iLink 凭证，仅后端调用微信接口时读取，任何 API 都不返回该字段。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? BotToken { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? BaseUrl { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? ILinkUserId { get; set; }

    [SugarColumn(Length = 24, IsNullable = false)]
    public string Status { get; set; } = "Unbound";

    [SugarColumn(IsNullable = false, DefaultValue = "1")]
    public bool McpPushEnabled { get; set; } = true;

    [SugarColumn(IsNullable = false, DefaultValue = "0")]
    public bool AdminDisabled { get; set; }

    /// <summary>未绑定或尚未成功通讯时没有上线时间。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastOnlineTime { get; set; }

    /// <summary>尚未成功发送消息时为空。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastSendTime { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? LastError { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>二维码绑定会话；二维码内容仅在当前登录用户的短期查询中返回。</summary>
[SugarTable("WeChatClawBotBindingSession")]
[SugarIndex("IX_WeChatClawBotBindingSession_UserTime", nameof(UserId), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
public sealed class WeChatClawBotBindingSessionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 512, IsNullable = false)]
    public string Qrcode { get; set; } = string.Empty;

    [SugarColumn(Length = 1024, IsNullable = true)]
    public string? QrcodeUrl { get; set; }

    /// <summary>扫码状态轮询节点，兼容微信返回的机房重定向。</summary>
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? PollBaseUrl { get; set; }

    [SugarColumn(Length = 24, IsNullable = false)]
    public string Status { get; set; } = "Waiting";

    public DateTime ExpireTime { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastPollTime { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? LastError { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>用户独立的微信会话游标和上下文，仅用于后端收发协议维护。</summary>
[SugarTable("WeChatClawBotConversation")]
[SugarIndex("UX_WeChatClawBotConversation_User", nameof(UserId), OrderByType.Asc, true)]
public sealed class WeChatClawBotConversationEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? UpdateCursor { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ContextToken { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastInboundTime { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>调试、MCP 和后续系统消息复用的统一投递审计。</summary>
[SugarTable("WeChatClawBotDelivery")]
[SugarIndex("IX_WeChatClawBotDelivery_UserTime", nameof(UserId), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
[SugarIndex("IX_WeChatClawBotDelivery_StatusTime", nameof(Status), OrderByType.Asc, nameof(CreateTime), OrderByType.Desc, false)]
public sealed class WeChatClawBotDeliveryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    /// <summary>人工或 MCP 重试时关联原投递记录。</summary>
    [SugarColumn(IsNullable = true)]
    public long? RetryOfDeliveryId { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string UserName { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Source { get; set; } = "Debug";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ExternalTaskId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? IdempotencyKey { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Status { get; set; } = "Pending";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ClientMessageId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? WeChatMessageId { get; set; }

    public int AttemptCount { get; set; }
    public long DurationMs { get; set; }

    [SugarColumn(Length = 2000, IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? SentTime { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

/// <summary>当前用户从微信收到的消息，不保存或输出会话鉴权令牌。</summary>
[SugarTable("WeChatClawBotInboundMessage")]
[SugarIndex("UX_WeChatClawBotInboundMessage_UserKey", nameof(UserId), OrderByType.Asc, nameof(MessageKey), OrderByType.Asc, true)]
[SugarIndex("IX_WeChatClawBotInboundMessage_UserTime", nameof(UserId), OrderByType.Asc, nameof(ReceivedTime), OrderByType.Desc, false)]
public sealed class WeChatClawBotInboundMessageEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string UserId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string MessageKey { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? WeChatMessageId { get; set; }

    [SugarColumn(Length = 256, IsNullable = false)]
    public string SenderId { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string MessageType { get; set; } = "Text";

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public DateTime? MessageTime { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime ReceivedTime { get; set; } = DateTime.Now;
}
