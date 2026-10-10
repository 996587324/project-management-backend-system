namespace Model.User;

public class UserActivityOverviewDto
{
    public int OnlineUserCount { get; set; }
    public int OnlineSessionCount { get; set; }
    public int TodayLoginCount { get; set; }
    public int TodayOperationCount { get; set; }
    public List<UserActivityOnlineUserDto> OnlineUsers { get; set; } = new();
}

public class UserActivityOnlineUserDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string LoginType { get; set; } = string.Empty;
    public DateTime LoginTime { get; set; }
    public DateTime LastActiveTime { get; set; }
    public long OnlineSeconds { get; set; }
}

public class UserLoginLogQueryDto
{
    public string? UserName { get; set; }
    public string? Status { get; set; }
    public bool? Success { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

public class UserLoginLogDto
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string LoginType { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string? ClientIp { get; set; }
    public DateTime LoginTime { get; set; }
    public DateTime LastActiveTime { get; set; }
    public DateTime? LogoutTime { get; set; }
    public long OnlineSeconds { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class UserOperationLogQueryDto
{
    public string? UserName { get; set; }
    public string? Module { get; set; }
    public string? Action { get; set; }
    public bool? Success { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>前端上报的业务操作事件；用户身份由服务端 JWT 获取，不接受前端传入。</summary>
public class UserOperationEventSaveDto
{
    public string? Module { get; set; }
    public string? Action { get; set; }
    public string? PagePath { get; set; }
    public string? Target { get; set; }
    public string? OperationType { get; set; }
}

public class UserOperationLogDto
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string? Target { get; set; }
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ClientIp { get; set; }
    public DateTime CreateTime { get; set; }
}

public class UserActivityPageDto<T>
{
    public List<T> Records { get; set; } = new();
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
}
