using System.Security.Claims;
using Model.SystemApplications;

namespace Interface;

public interface ISystemApplicationService
{
    Task RegisterSessionAsync(string userKey, string sessionId);
    Task<ApplicationAccessContext> GetAccessAsync(ClaimsPrincipal principal, bool requireApplication = false);
    /// <summary>已验证后台渠道按真实用户读取最新系统授权，不创建或冒用网页登录会话。</summary>
    Task<ApplicationAccessContext> GetChannelAccessAsync(string userId, string appCode);
    Task<List<SysApplicationEntity>> GetApplicationsAsync(ClaimsPrincipal principal);
    Task<object> CreateTicketAsync(ClaimsPrincipal principal, ApplicationTicketRequest request);
    Task<object> RedeemTicketAsync(ApplicationTicketRedeemRequest request);
    Task RevokeSessionAsync(ClaimsPrincipal principal);
    Task<SessionActivityResult> GetSessionActivityAsync(ClaimsPrincipal principal, int? idleMilliseconds = null);
    Task SaveApplicationAsync(ClaimsPrincipal principal, SysApplicationEntity application);
    Task<List<ApplicationGrantDto>> GetGrantsAsync(ClaimsPrincipal principal, string userId);
    Task SaveGrantAsync(ClaimsPrincipal principal, ApplicationGrantDto grant);
    Task<object> GetAdministrationAsync(ClaimsPrincipal principal);
    Task SaveMenusAsync(ClaimsPrincipal principal, string appCode, List<string> menuIds);
    Task<string> CreateMcpTokenAsync(ClaimsPrincipal principal);
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken);
}
