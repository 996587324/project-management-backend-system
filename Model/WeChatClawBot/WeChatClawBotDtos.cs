namespace Model.WeChatClawBot;

public sealed class WeChatClawBotAccountDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Status { get; set; } = "Unbound";
    public bool Bound { get; set; }
    public bool McpPushEnabled { get; set; }
    public bool AdminDisabled { get; set; }
    public DateTime? LastOnlineTime { get; set; }
    public DateTime? LastSendTime { get; set; }
    public string? LastError { get; set; }
    public DateTime? UpdateTime { get; set; }
    public bool HasConversationContext { get; set; }
    public DateTime? LastInboundTime { get; set; }
}

public sealed class WeChatClawBotBindingDto
{
    public long SessionId { get; set; }
    public string Status { get; set; } = "Waiting";
    public string? Qrcode { get; set; }
    public string? QrcodeUrl { get; set; }
    public DateTime? ExpireTime { get; set; }
    public string? Message { get; set; }
}

public sealed class WeChatClawBotDeliveryQueryDto
{
    public string? UserName { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

public sealed class WeChatClawBotDeliveryDto
{
    public long Id { get; set; }
    public long? RetryOfDeliveryId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? ExternalTaskId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? WeChatMessageId { get; set; }
    public int AttemptCount { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? SentTime { get; set; }
    public DateTime CreateTime { get; set; }
}

public sealed class WeChatClawBotSendDto
{
    public string UserId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class WeChatClawBotVerifyRequest
{
    public string VerifyCode { get; set; } = string.Empty;
}

public sealed class WeChatClawBotMySendRequest
{
    public string Content { get; set; } = string.Empty;
}

public sealed class WeChatClawBotInboundMessageDto
{
    public long Id { get; set; }
    public string? WeChatMessageId { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime? MessageTime { get; set; }
    public DateTime ReceivedTime { get; set; }
}

public sealed class WeChatClawBotMcpSendDto
{
    public List<string> RecipientCodes { get; set; } = [];
    public string Content { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ExternalTaskId { get; set; }
}

public sealed class WeChatClawBotRecipientDto
{
    public string RecipientCode { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class WeChatClawBotOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://ilinkai.weixin.qq.com";
    /// <summary>iLink-App-ClientVersion，十进制编码值；2.4.8 对应 132104。</summary>
    public string AppVersion { get; set; } = "132104";
    public string ChannelVersion { get; set; } = "2.4.8";
    public string BotAgent { get; set; } = "IntelligentManufacturing";
    public int QrcodeExpireMinutes { get; set; } = 5;
    public int HttpTimeoutSeconds { get; set; } = 45;
    public bool MonitorEnabled { get; set; } = true;
    public int MonitorInitialDelaySeconds { get; set; } = 30;
    public int MonitorIdleDelaySeconds { get; set; } = 5;
}
