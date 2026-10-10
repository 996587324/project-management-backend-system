using Model.User;

namespace Interface;

public interface IUserActivityService
{
    Task<string> StartSessionAsync(string userId, string userName, string loginType, string? clientIp, string? userAgent, CancellationToken cancellationToken = default);
    Task RecordLoginFailureAsync(string userName, string loginType, string reason, string? clientIp, string? userAgent, CancellationToken cancellationToken = default);
    Task TouchSessionAsync(string? sessionId, string? userId, CancellationToken cancellationToken = default);
    Task EndSessionAsync(string? sessionId, string? userId, CancellationToken cancellationToken = default);
    Task RecordOperationAsync(UserOperationAuditInput input, CancellationToken cancellationToken = default);
    Task<UserActivityOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<UserActivityPageDto<UserLoginLogDto>> GetLoginPageAsync(UserLoginLogQueryDto query, CancellationToken cancellationToken = default);
    Task<UserActivityPageDto<UserOperationLogDto>> GetOperationPageAsync(UserOperationLogQueryDto query, CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public class UserOperationAuditInput
{
    public string? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string? Target { get; set; }
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
}
