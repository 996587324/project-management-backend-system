namespace Model.Other;

/// <summary>后台管理系统 OAuth 2.0 授权码登录配置。</summary>
public sealed class OAuth2Options
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "统一身份认证";
    /// <summary>协议适配器：generic 或 tcl_siam。</summary>
    public string Provider { get; set; } = "generic";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string UserInfoEndpoint { get; set; } = string.Empty;
    /// <summary>TCL 统一身份平台的浏览器单点登出地址。</summary>
    public string LogoutEndpoint { get; set; } = string.Empty;
    /// <summary>单点登出完成后回跳地址的参数名，TCL SIAM 使用 service。</summary>
    public string LogoutReturnUrlParameter { get; set; } = "service";
    /// <summary>TCL SIAM 的 TGT 票据注销接口。</summary>
    public string TicketLogoutEndpoint { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public string FrontendLoginUrl { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile";
    /// <summary>统一身份平台确认支持后再启用；TCL SIAM v3.0 文档示例未包含 PKCE 参数。</summary>
    public bool UsePkce { get; set; } = true;
    /// <summary>Token 响应格式：json 或 form_urlencoded。</summary>
    public string TokenResponseFormat { get; set; } = "json";
    /// <summary>用户信息请求方法：GET 或 POST。</summary>
    public string UserInfoRequestMethod { get; set; } = "GET";
    /// <summary>用户信息 Token 传递方式：bearer、form 或 query。</summary>
    public string UserInfoTokenTransport { get; set; } = "bearer";
    public string UserNameClaim { get; set; } = "preferred_username";
    public string PasswordClaim { get; set; } = "password";
    public string NickNameClaim { get; set; } = "name";
    public string EmailClaim { get; set; } = "email";
    /// <summary>Profile 内属性数组字段，例如 TCL SIAM 的 attributes。</summary>
    public string ProfileAttributesClaim { get; set; } = "attributes";
    /// <summary>Profile 未提供密码时设为 false：保留已有密码，新用户生成随机密码。</summary>
    public bool SynchronizePassword { get; set; } = true;
    public string DefaultRoles { get; set; } = "R_USER";
    public string DefaultPermissionKeys { get; set; } = "Dashboard";
    public string ClientAuthenticationMethod { get; set; } = "client_secret_post";
    public int StateLifetimeMinutes { get; set; } = 10;
    public int TicketLifetimeSeconds { get; set; } = 60;
}
