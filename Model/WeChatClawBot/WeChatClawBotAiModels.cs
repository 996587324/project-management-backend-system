using SqlSugar;

namespace Model.WeChatClawBot;

/// <summary>独立于网页 JWT 的微信连接授权。密钥仅保存随机高熵密钥的 SHA256。</summary>
[SugarTable("WeChatClawBotAiConnection")]
public sealed class WeChatClawBotAiConnectionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string UserId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [SugarColumn(Length = 8)]
    public string AppCode { get; set; } = "A";
    [SugarColumn(Length = 8)]
    public string Mode { get; set; } = "Chat";
    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = "chat";
    /// <summary>NULL 兼容旧版单选 AgentCode；新配置保存显式允许列表。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? AllowedAgentCodesJson { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? AgentConversationsJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ActiveAgentCode { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? SecretHash { get; set; }
    [SugarColumn(Length = 32)]
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? BindingIdentity { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? AuthorizedUntilUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LockedUntilUtc { get; set; }
    public int FailedAttempts { get; set; }
    public int ValidHours { get; set; } = 24;
    public int DailyLimit { get; set; } = 50;
    /// <summary>同一机器人所有发送来源的每日总预算；0 表示不启用本地总预算。</summary>
    public int BotDailyLimit { get; set; }
    public int SendIntervalSeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 120;
    [SugarColumn(Length = 500)]
    public string LimitMessage { get; set; } = "今日 AI 对话次数已用完，请明日再试。";
    [SugarColumn(IsNullable = true)]
    public int? ConversationId { get; set; }
    public DateTime UpdateTimeUtc { get; set; } = DateTime.UtcNow;
}

[SugarTable("WeChatClawBotAiDay")]
public sealed class WeChatClawBotAiDayEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(IsPrimaryKey = true, Length = 8)]
    public string Day { get; set; } = string.Empty;
    public int Used { get; set; }
    public bool LimitNoticeQueued { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LastControlReplyUtc { get; set; }
}

[SugarTable("WeChatClawBotAiJob")]
[SugarIndex("UX_ClawBotAiJob_Inbound", nameof(InboundId), OrderByType.Asc, true)]
[SugarIndex("IX_ClawBotAiJob_State", nameof(Status), OrderByType.Asc, nameof(CreateTimeUtc), OrderByType.Asc, false)]
public sealed class WeChatClawBotAiJobEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }
    public long InboundId { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 32)]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string BindingIdentity { get; set; } = string.Empty;
    [SugarColumn(Length = 8)]
    public string Day { get; set; } = string.Empty;
    [SugarColumn(Length = 24)]
    public string Status { get; set; } = "Pending";
    [SugarColumn(Length = 24)]
    public string Kind { get; set; } = "Agent";
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? AgentCode { get; set; }
    public bool QuotaReserved { get; set; }
    public bool RequiresConfirmation { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ResultAccessHash { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Reply { get; set; }
    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? Error { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? ConversationId { get; set; }
    [SugarColumn(IsNullable = true)]
    public long? DeliveryId { get; set; }
    public DateTime CreateTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdateTimeUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>所有发送来源共享的机器人级节流和每日发送预算。</summary>
[SugarTable("WeChatClawBotSendBudget")]
public sealed class WeChatClawBotSendBudgetEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string BotKey { get; set; } = string.Empty;
    [SugarColumn(Length = 8)]
    public string Day { get; set; } = string.Empty;
    public int Used { get; set; }
    public DateTime NextSendUtc { get; set; } = new(2000, 1, 1);
}

public sealed class WeChatClawBotAiSaveDto
{
    public bool Enabled { get; set; }
    public string AppCode { get; set; } = "A";
    public string Mode { get; set; } = "Chat";
    public string AgentCode { get; set; } = "chat";
    /// <summary>省略时兼容旧版 AgentCode；显式空列表在工作模式下不允许保存。</summary>
    public List<string>? AgentCodes { get; set; }
    public int ValidHours { get; set; } = 24;
    public int DailyLimit { get; set; } = 50;
    public int BotDailyLimit { get; set; }
    public int SendIntervalSeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 120;
    public string LimitMessage { get; set; } = "今日 AI 对话次数已用完，请明日再试。";
}

public sealed class WeChatClawBotAiStateDto
{
    public WeChatClawBotAiSaveDto Configuration { get; set; } = new();
    public bool HasSecret { get; set; }
    public string State { get; set; } = "Unverified";
    public DateTime? AuthorizedUntilUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
    public bool DailyLimited { get; set; }
    public string Day { get; set; } = string.Empty;
    public int? ConversationId { get; set; }
    public string? ActiveAgentCode { get; set; }
}
