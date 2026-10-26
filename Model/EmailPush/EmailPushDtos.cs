namespace Model.EmailPush;

public sealed class EmailSettingDto
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string UserName { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int IntervalSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
    public int Version { get; set; }
    public bool HasPassword { get; set; }
    public DateTime? UpdateTime { get; set; }
}

public sealed class EmailSettingSaveDto
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string UserName { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int IntervalSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 3;
    public int Version { get; set; }
}

public sealed class EmailEnqueueDto
{
    public List<string> RecipientUserIds { get; set; } = [];
    public List<string> CcUserIds { get; set; } = [];
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool Confirm { get; set; }
}

public sealed class EmailRecipientDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? NickName { get; set; }
    public string? Email { get; set; }
    public bool Available { get; set; }
}

public sealed class EmailPage<T>
{
    public List<T> Records { get; set; } = [];
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
}

public sealed class EmailEnqueueResult
{
    public long TaskId { get; set; }
    public bool Duplicate { get; set; }
    public string Status { get; set; } = "Pending";
    public string Message { get; set; } = "已加入邮件发送队列";
}

public sealed class EmailRecordDto
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string AppCode { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string RecipientUserId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string RecipientKind { get; set; } = string.Empty;
    public string? FromAddress { get; set; }
    public int? SettingVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? SentTime { get; set; }
}

public sealed class EmailRecordQuery
{
    public string? CreatedBy { get; set; }
    public string? AppCode { get; set; }
    public long? TaskId { get; set; }
    public long? DeliveryId { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
    public string? Keyword { get; set; }
    public string? Status { get; set; }
    public string? Source { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public sealed class EmailRetryDto
{
    public bool Confirm { get; set; }
    public bool AcknowledgeDuplicateRisk { get; set; }
}

public sealed class EmailTransportResult
{
    public string Status { get; init; } = "Failed";
    public string? Error { get; init; }
}
