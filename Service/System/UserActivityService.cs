using Interface;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>后台用户登录会话和写操作审计服务。</summary>
public sealed class UserActivityService : IUserActivityService
{
    private const int OnlineTimeoutMinutes = 2;
    private const int RetentionDays = 180;
    private static readonly SemaphoreSlim InitializeGate = new(1, 1);
    private static bool _initialized;
    private readonly SqlSugarScope _dbScope;

    public UserActivityService(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
    }

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public async Task<string> StartSessionAsync(
        string userId,
        string userName,
        string loginType,
        string? clientIp,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var now = DateTime.Now;
            var sessionId = Guid.NewGuid().ToString("N");
            await Db.Insertable(new SysUserLoginLogEntity
            {
                SessionId = sessionId,
                UserId = Trim(userId, 64),
                UserName = Trim(userName, 128) ?? "未知用户",
                LoginType = Trim(loginType, 24) ?? "Password",
                Success = true,
                ClientIp = Trim(clientIp, 64),
                UserAgent = Trim(userAgent, 512),
                LoginTime = now,
                LastActiveTime = now,
                Status = "Online"
            }).ExecuteCommandAsync();
            return sessionId;
        }
        catch(Exception ex)
        {
            throw ex;
        }

    }

    public async Task RecordLoginFailureAsync(
        string userName,
        string loginType,
        string reason,
        string? clientIp,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTime.Now;
        await Db.Insertable(new SysUserLoginLogEntity
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserName = Trim(userName, 128) ?? "未知用户",
            LoginType = Trim(loginType, 24) ?? "Password",
            Success = false,
            FailureReason = Trim(reason, 512),
            ClientIp = Trim(clientIp, 64),
            UserAgent = Trim(userAgent, 512),
            LoginTime = now,
            LastActiveTime = now,
            LogoutTime = now,
            Status = "Failed"
        }).ExecuteCommandAsync();
    }

    public async Task TouchSessionAsync(string? sessionId, string? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(userId)) return;
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTime.Now;
        var query = Db.Updateable<SysUserLoginLogEntity>()
            .SetColumns(x => new SysUserLoginLogEntity { LastActiveTime = now });
        if (!string.IsNullOrWhiteSpace(sessionId))
            await query.Where(x => x.SessionId == sessionId && x.LogoutTime == null).ExecuteCommandAsync();
        else
            await query.Where(x => x.UserId == userId && x.LogoutTime == null).ExecuteCommandAsync();
    }

    public async Task EndSessionAsync(string? sessionId, string? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(userId)) return;
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTime.Now;
        var sessions = await Db.Queryable<SysUserLoginLogEntity>()
            .Where(x => x.LogoutTime == null)
            .WhereIF(!string.IsNullOrWhiteSpace(sessionId), x => x.SessionId == sessionId)
            .WhereIF(string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(userId), x => x.UserId == userId)
            .ToListAsync();
        foreach (var session in sessions)
        {
            session.LastActiveTime = now;
            session.LogoutTime = now;
            session.OnlineSeconds = Math.Max(0, (long)(now - session.LoginTime).TotalSeconds);
            session.Status = "Offline";
            await Db.Updateable(session).ExecuteCommandAsync();
        }
    }

    public async Task RecordOperationAsync(UserOperationAuditInput input, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await Db.Insertable(new SysUserOperationLogEntity
        {
            AppCode = Trim(input.AppCode, 32),
            UserId = Trim(input.UserId, 64),
            UserName = Trim(input.UserName, 128) ?? "未知用户",
            SessionId = Trim(input.SessionId, 64),
            Module = Trim(input.Module, 64) ?? "System",
            Action = Trim(input.Action, 128) ?? "Operation",
            HttpMethod = Trim(input.HttpMethod, 12) ?? "POST",
            RequestPath = Trim(input.RequestPath, 512) ?? string.Empty,
            Target = Trim(input.Target, 256),
            Success = input.Success,
            StatusCode = input.StatusCode,
            DurationMs = Math.Max(0, input.DurationMs),
            ClientIp = Trim(input.ClientIp, 64),
            UserAgent = Trim(input.UserAgent, 512),
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
    }

    public async Task<UserActivityOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTime.Now;
        var onlineDeadline = now.AddMinutes(-OnlineTimeoutMinutes);
        var online = await Db.Queryable<SysUserLoginLogEntity>()
            .Where(x => x.Success && x.LogoutTime == null && x.LastActiveTime >= onlineDeadline)
            .OrderByDescending(x => x.LastActiveTime)
            .ToListAsync();
        var today = now.Date;
        var todayLoginCount = await Db.Queryable<SysUserLoginLogEntity>()
            .Where(x => x.Success && x.LoginTime >= today).CountAsync();
        var todayOperationCount = await Db.Queryable<SysUserOperationLogEntity>()
            .Where(x => x.HttpMethod == "ButtonClick" && x.CreateTime >= today)
            .CountAsync();
        return new UserActivityOverviewDto
        {
            OnlineUserCount = online.Select(x => x.UserId ?? x.UserName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            OnlineSessionCount = online.Count,
            TodayLoginCount = todayLoginCount,
            TodayOperationCount = todayOperationCount,
            OnlineUsers = online.Take(100).Select(ToOnlineDto).ToList()
        };
    }

    public async Task<UserActivityPageDto<UserLoginLogDto>> GetLoginPageAsync(
        UserLoginLogQueryDto query,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var current = Math.Max(1, query.Current);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<SysUserLoginLogEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(query.UserName), x => x.UserName.Contains(query.UserName!.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status!.Trim())
            .WhereIF(query.Success.HasValue, x => x.Success == query.Success!.Value)
            .WhereIF(query.StartTime.HasValue, x => x.LoginTime >= query.StartTime!.Value)
            .WhereIF(query.EndTime.HasValue, x => x.LoginTime <= query.EndTime!.Value)
            .OrderByDescending(x => x.LoginTime)
            .ToPageListAsync(current, size, total);
        return new UserActivityPageDto<UserLoginLogDto>
        {
            Records = records.Select(ToLoginDto).ToList(), Current = current, Size = size, Total = total
        };
    }

    public async Task<UserActivityPageDto<UserOperationLogDto>> GetOperationPageAsync(
        UserOperationLogQueryDto query,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var current = Math.Max(1, query.Current);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<SysUserOperationLogEntity>()
            .Where(x => x.HttpMethod == "ButtonClick")
            .WhereIF(!string.IsNullOrWhiteSpace(query.UserName), x => x.UserName.Contains(query.UserName!.Trim()))
            .WhereIF(!string.IsNullOrWhiteSpace(query.AppCode), x => x.AppCode == query.AppCode!.Trim())
            .WhereIF(!string.IsNullOrWhiteSpace(query.Module), x => x.Module == query.Module!.Trim())
            .WhereIF(!string.IsNullOrWhiteSpace(query.Action), x => x.Action.Contains(query.Action!.Trim()))
            .WhereIF(query.Success.HasValue, x => x.Success == query.Success!.Value)
            .WhereIF(query.StartTime.HasValue, x => x.CreateTime >= query.StartTime!.Value)
            .WhereIF(query.EndTime.HasValue, x => x.CreateTime <= query.EndTime!.Value)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(current, size, total);
        return new UserActivityPageDto<UserOperationLogDto>
        {
            Records = records.Select(ToOperationDto).ToList(), Current = current, Size = size, Total = total
        };
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTime.Now;
        var deadline = now.AddMinutes(-OnlineTimeoutMinutes);
        var expired = await Db.Queryable<SysUserLoginLogEntity>()
            .Where(x => x.Success && x.LogoutTime == null && x.LastActiveTime < deadline)
            .ToListAsync();
        foreach (var session in expired)
        {
            session.LogoutTime = session.LastActiveTime;
            session.OnlineSeconds = Math.Max(0, (long)(session.LastActiveTime - session.LoginTime).TotalSeconds);
            session.Status = "Timeout";
            await Db.Updateable(session).ExecuteCommandAsync();
        }
        var cutoff = now.AddDays(-RetentionDays);
        await Db.Deleteable<SysUserLoginLogEntity>().Where(x => x.LoginTime < cutoff).ExecuteCommandAsync();
        await Db.Deleteable<SysUserOperationLogEntity>().Where(x => x.CreateTime < cutoff).ExecuteCommandAsync();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await InitializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Db.CodeFirst.InitTables<SysUserLoginLogEntity, SysUserOperationLogEntity>();
            _initialized = true;
        }
        finally
        {
            InitializeGate.Release();
        }
    }

    private static UserActivityOnlineUserDto ToOnlineDto(SysUserLoginLogEntity entity) => new()
    {
        UserId = entity.UserId ?? string.Empty,
        UserName = entity.UserName,
        LoginType = entity.LoginType,
        LoginTime = entity.LoginTime,
        LastActiveTime = entity.LastActiveTime,
        OnlineSeconds = Math.Max(0, (long)(DateTime.Now - entity.LoginTime).TotalSeconds)
    };

    private static UserLoginLogDto ToLoginDto(SysUserLoginLogEntity entity) => new()
    {
        Id = entity.Id, UserId = entity.UserId, UserName = entity.UserName, LoginType = entity.LoginType,
        Success = entity.Success, FailureReason = entity.FailureReason, ClientIp = entity.ClientIp,
        LoginTime = entity.LoginTime, LastActiveTime = entity.LastActiveTime, LogoutTime = entity.LogoutTime,
        OnlineSeconds = entity.LogoutTime.HasValue
            ? entity.OnlineSeconds
            : Math.Max(entity.OnlineSeconds, (long)(DateTime.Now - entity.LoginTime).TotalSeconds),
        Status = entity.Status
    };

    private static UserOperationLogDto ToOperationDto(SysUserOperationLogEntity entity) => new()
    {
        AppCode = entity.AppCode,
        Id = entity.Id, UserId = entity.UserId, UserName = entity.UserName, Module = entity.Module,
        Action = entity.Action, HttpMethod = entity.HttpMethod, RequestPath = entity.RequestPath,
        Target = entity.Target, Success = entity.Success, StatusCode = entity.StatusCode,
        DurationMs = entity.DurationMs, ClientIp = entity.ClientIp, CreateTime = entity.CreateTime
    };

    private static string? Trim(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
