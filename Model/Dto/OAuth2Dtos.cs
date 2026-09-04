namespace Model.Dto;

public sealed class OAuth2StatusDto
{
    public bool enabled { get; set; }
    public string displayName { get; set; } = string.Empty;
}

public sealed class OAuth2CompleteRequest
{
    public string ticket { get; set; } = string.Empty;
}

public sealed class OAuth2AuthorizationResult
{
    public string authorizationUrl { get; set; } = string.Empty;
    public string protectedState { get; set; } = string.Empty;
    public int stateLifetimeSeconds { get; set; }
}

public sealed class OAuth2CallbackResult
{
    public string ticket { get; set; } = string.Empty;
    public string returnUrl { get; set; } = "/";
    /// <summary>仅用于后端保存 SIAM TGT，不会回传给前端。</summary>
    public string logoutSessionId { get; set; } = string.Empty;
}
