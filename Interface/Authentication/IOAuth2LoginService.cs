using Model.Dto;

namespace Interface;

public interface IOAuth2LoginService
{
    OAuth2StatusDto GetStatus();
    OAuth2AuthorizationResult CreateAuthorization(string callbackUrl, string? returnUrl);
    Task<OAuth2CallbackResult> HandleCallbackAsync(
        string callbackUrl,
        string code,
        string state,
        string protectedState,
        CancellationToken cancellationToken);
    TokenData RedeemTicket(string ticket);
    Task RevokeExternalSessionAsync(string logoutSessionId, CancellationToken cancellationToken);
}
