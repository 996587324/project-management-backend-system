namespace Model.EdgeGateway;

public sealed class EdgeDeviceCardPushQueueSettingDto
{
    public int IntervalSeconds { get; set; } = 5;
}

public sealed class EdgeDeviceCardPushConfigDto
{
    public bool Enabled { get; set; }
    public string Channel { get; set; } = "WeChat";
    public List<string> RecipientUserIds { get; set; } = [];
    public bool OnChange { get; set; }
    public bool OnLimit { get; set; } = true;
    public bool OnRecovery { get; set; }
    public decimal? ChangeThreshold { get; set; }
    public int ChangeIntervalSeconds { get; set; } = 300;
    public string? MessageTemplate { get; set; }
}

public sealed class EdgeDeviceCardPushRecipientDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool Selectable { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class EdgeDeviceCardPushTestDto
{
    public long CardId { get; set; }
    public string FieldCode { get; set; } = string.Empty;
    public List<string> RecipientUserIds { get; set; } = [];
    public string? MessageTemplate { get; set; }
}

public sealed class EdgeDeviceCardPushTaskQueryDto : EdgePageQueryDto
{
    public string? LineCode { get; set; }
    public long? CardId { get; set; }
    public string? Status { get; set; }
    public string? UserId { get; set; }
}
