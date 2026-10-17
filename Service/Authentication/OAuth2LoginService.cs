using Interface;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Model.Dto;
using Model.Other;
using Model.User;
using SqlSugar;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenData = Model.Dto.TokenData;

namespace Service;

public sealed class OAuth2LoginService : IOAuth2LoginService
{
    private const string DbConfigId = "calmadmin";
    private const string TicketCachePrefix = "oauth2-login-ticket:";
    private const string LogoutTicketCachePrefix = "oauth2-logout-ticket:";
    private readonly SqlSugarScope _db;
    private readonly ICustomJWTService _jwtService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IDataProtector _stateProtector;
    private readonly OAuth2Options _options;
    private readonly IUserActivityService _userActivityService;
    private readonly ISystemApplicationService _applications;

    public OAuth2LoginService(
        SqlSugarScope db,
        ICustomJWTService jwtService,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IDataProtectionProvider dataProtectionProvider,
        IOptionsMonitor<OAuth2Options> options,
        IUserActivityService userActivityService,
        ISystemApplicationService applications)
    {
        _db = db;
        _jwtService = jwtService;
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _stateProtector = dataProtectionProvider.CreateProtector("CalmAdmin.OAuth2.State.v1");
        _options = options.CurrentValue;
        _userActivityService = userActivityService;
        _applications = applications;
    }

    private ISqlSugarClient Db => _db.GetConnection(DbConfigId);

    public OAuth2StatusDto GetStatus() => new()
    {
        enabled = _options.Enabled && HasRequiredConfiguration(includeSecret: true),
        displayName = string.IsNullOrWhiteSpace(_options.DisplayName)
            ? "统一身份认证"
            : _options.DisplayName.Trim()
    };

    public OAuth2AuthorizationResult CreateAuthorization(string callbackUrl, string? returnUrl)
    {
        EnsureEnabled();
        var state = CreateRandomUrlToken(32);
        var verifier = _options.UsePkce ? CreateRandomUrlToken(64) : string.Empty;
        var payload = new OAuth2StatePayload
        {
            State = state,
            CodeVerifier = verifier,
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            CreatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var protectedState = _stateProtector.Protect(JsonSerializer.Serialize(payload));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId.Trim(),
            ["redirect_uri"] = callbackUrl,
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(_options.Scope)) parameters["scope"] = _options.Scope.Trim();
        if (_options.UsePkce)
        {
            parameters["code_challenge"] = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            parameters["code_challenge_method"] = "S256";
        }

        return new OAuth2AuthorizationResult
        {
            authorizationUrl = QueryHelpers.AddQueryString(_options.AuthorizationEndpoint.Trim(), parameters),
            protectedState = protectedState,
            stateLifetimeSeconds = Math.Clamp(_options.StateLifetimeMinutes, 1, 30) * 60
        };
    }

    public async Task<OAuth2CallbackResult> HandleCallbackAsync(
        string callbackUrl,
        string code,
        string state,
        string protectedState,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(protectedState))
            throw new InvalidOperationException("OAuth 回调参数不完整");

        OAuth2StatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<OAuth2StatePayload>(_stateProtector.Unprotect(protectedState))
                ?? throw new InvalidOperationException("OAuth 登录状态无效");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidOperationException("OAuth 登录状态无效或已被篡改");
        }

        if (!FixedTimeEquals(payload.State, state))
            throw new InvalidOperationException("OAuth 登录状态校验失败");

        var stateLifetime = TimeSpan.FromMinutes(Math.Clamp(_options.StateLifetimeMinutes, 1, 30));
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(payload.CreatedAtUnixSeconds);
        if (DateTimeOffset.UtcNow - createdAt > stateLifetime)
            throw new InvalidOperationException("OAuth 登录请求已过期，请重新发起登录");

        var accessToken = await ExchangeCodeAsync(callbackUrl, code.Trim(), payload.CodeVerifier, cancellationToken);
        var externalUser = await GetExternalUserAsync(accessToken, cancellationToken);
        var account = await SynchronizeAccountAsync(externalUser);
        if (!account.Enabled)
            throw new InvalidOperationException("本系统账号已停用，无法登录");

        var user = new UserRes
        {
            Id = long.TryParse(account.UserId, out var id) ? id : 0,
            Name = account.UserName,
            Password = string.Empty,
            UserType = 0,
            IsEnable = account.Enabled,
            Description = account.NickName,
            CreateDate = account.CreateTime
        };
        var sessionId = await _userActivityService.StartSessionAsync(
            account.UserId,
            account.UserName,
            "OAuth2",
            null,
            null,
            cancellationToken);
        await _applications.RegisterSessionAsync(account.UserId, sessionId);
        var tokenData = new TokenData
        {
            token = _jwtService.GetToken(user, sessionId),
            refreshToken = _jwtService.GenerateRefreshToken()
        };
        var ticket = CreateRandomUrlToken(32);
        _memoryCache.Set(
            TicketCachePrefix + ticket,
            tokenData,
            TimeSpan.FromSeconds(Math.Clamp(_options.TicketLifetimeSeconds, 30, 180)));

        // SIAM 的 OAuth access_token 实际为 TGT。仅将随机会话标识写入浏览器 Cookie，
        // 原始票据始终保留在服务端缓存中，供用户主动退出时注销。
        var logoutSessionId = CreateRandomUrlToken(32);
        _memoryCache.Set(
            LogoutTicketCachePrefix + logoutSessionId,
            accessToken,
            TimeSpan.FromHours(8));

        return new OAuth2CallbackResult
        {
            ticket = ticket,
            returnUrl = payload.ReturnUrl,
            logoutSessionId = logoutSessionId
        };
    }

    public TokenData RedeemTicket(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("OAuth 登录票据不能为空");

        var cacheKey = TicketCachePrefix + ticket.Trim();
        if (!_memoryCache.TryGetValue<TokenData>(cacheKey, out var tokenData) || tokenData == null)
            throw new InvalidOperationException("OAuth 登录票据无效或已过期");

        _memoryCache.Remove(cacheKey);
        return tokenData;
    }

    public async Task RevokeExternalSessionAsync(string logoutSessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logoutSessionId)) return;

        var cacheKey = LogoutTicketCachePrefix + logoutSessionId.Trim();
        if (!_memoryCache.TryGetValue<string>(cacheKey, out var tgt) || string.IsNullOrWhiteSpace(tgt)) return;
        _memoryCache.Remove(cacheKey);

        if (!Uri.TryCreate(_options.TicketLogoutEndpoint, UriKind.Absolute, out _)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TicketLogoutEndpoint.Trim())
        {
            Content = JsonContent.Create(new
            {
                ticketValue = tgt,
                ticketName = "SIAMTGT",
                ticketType = "TGT"
            })
        };
        var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SIAM TGT 注销失败（HTTP {(int)response.StatusCode}）");
    }

    private async Task<string> ExchangeCodeAsync(
        string callbackUrl,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint.Trim());
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = callbackUrl,
            ["client_id"] = _options.ClientId.Trim()
        };
        if (_options.UsePkce && !string.IsNullOrWhiteSpace(codeVerifier)) form["code_verifier"] = codeVerifier;
        if (string.Equals(_options.ClientAuthenticationMethod, "client_secret_basic", StringComparison.OrdinalIgnoreCase))
        {
            var credential = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ClientId.Trim()}:{_options.ClientSecret.Trim()}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
        }
        else
        {
            form["client_secret"] = _options.ClientSecret.Trim();
        }
        request.Content = new FormUrlEncodedContent(form);

        var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth Token 交换失败（HTTP {(int)response.StatusCode}）");

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseAccessToken(content);
    }

    private async Task<ExternalOAuthUser> GetExternalUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var requestMethod = string.Equals(_options.UserInfoRequestMethod, "POST", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;
        var endpoint = _options.UserInfoEndpoint.Trim();
        using var request = new HttpRequestMessage(requestMethod, endpoint);
        var tokenTransport = _options.UserInfoTokenTransport.Trim().ToLowerInvariant();
        switch (tokenTransport)
        {
            case "form":
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["access_token"] = accessToken
                });
                break;
            case "query":
                request.RequestUri = new Uri(QueryHelpers.AddQueryString(endpoint, "access_token", accessToken));
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                break;
        }
        var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth 用户信息查询失败（HTTP {(int)response.StatusCode}）");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var userNameClaim = string.IsNullOrWhiteSpace(_options.UserNameClaim)
            ? "preferred_username"
            : _options.UserNameClaim.Trim();
        if (!TryGetClaimValue(document.RootElement, userNameClaim, out var userName) || string.IsNullOrWhiteSpace(userName))
            throw new InvalidOperationException($"OAuth 用户信息缺少账号字段 {userNameClaim}");

        var password = string.Empty;
        var passwordClaim = string.IsNullOrWhiteSpace(_options.PasswordClaim) ? "password" : _options.PasswordClaim.Trim();
        if (_options.SynchronizePassword &&
            (!TryGetClaimValue(document.RootElement, passwordClaim, out password) || string.IsNullOrWhiteSpace(password)))
            throw new InvalidOperationException($"OAuth 用户信息缺少密码字段 {passwordClaim}");

        var nickName = GetOptionalClaim(document.RootElement, _options.NickNameClaim);
        var email = GetOptionalClaim(document.RootElement, _options.EmailClaim);
        return new ExternalOAuthUser
        {
            UserName = ValidateClaimLength(userName.Trim(), userNameClaim, 128),
            Password = string.IsNullOrWhiteSpace(password)
                ? string.Empty
                : ValidateClaimLength(password, passwordClaim, 128),
            NickName = ValidateOptionalClaimLength(nickName, _options.NickNameClaim, 128),
            Email = ValidateOptionalClaimLength(email, _options.EmailClaim, 256)
        };
    }

    private async Task<SysUserInfo> SynchronizeAccountAsync(ExternalOAuthUser externalUser)
    {
        var now = DateTime.Now;
        var account = await Db.Queryable<SysUserInfo>()
            .FirstAsync(x => x.UserName == externalUser.UserName);

        if (account != null)
        {
            if (_options.SynchronizePassword && !string.IsNullOrWhiteSpace(externalUser.Password))
                account.Password = externalUser.Password;
            if (!string.IsNullOrWhiteSpace(externalUser.NickName)) account.NickName = externalUser.NickName;
            if (!string.IsNullOrWhiteSpace(externalUser.Email)) account.Email = externalUser.Email;
            account.UpdateTime = now;
            await Db.Updateable(account).IgnoreColumns(x => x.AvatarFileKey).ExecuteCommandAsync();
            return account;
        }

        account = new SysUserInfo
        {
            UserId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            UserName = externalUser.UserName,
            Password = string.IsNullOrWhiteSpace(externalUser.Password)
                ? CreateRandomLocalPassword()
                : externalUser.Password,
            NickName = string.IsNullOrWhiteSpace(externalUser.NickName)
                ? externalUser.UserName
                : externalUser.NickName,
            Email = externalUser.Email,
            Roles = NormalizeCsv(_options.DefaultRoles, "R_USER"),
            PermissionKeys = NormalizeCsv(_options.DefaultPermissionKeys, "Dashboard"),
            Buttons = string.Empty,
            Enabled = true,
            CreateTime = now,
            UpdateTime = now
        };

        try
        {
            await Db.Insertable(account).ExecuteCommandAsync();
            return account;
        }
        catch
        {
            // 并发回调可能在查询后由另一个请求创建同名账号，重新查询并按“已存在”规则同步。
            var concurrentAccount = await Db.Queryable<SysUserInfo>()
                .FirstAsync(x => x.UserName == externalUser.UserName);
            if (concurrentAccount == null) throw;
            if (_options.SynchronizePassword && !string.IsNullOrWhiteSpace(externalUser.Password))
                concurrentAccount.Password = externalUser.Password;
            if (!string.IsNullOrWhiteSpace(externalUser.NickName)) concurrentAccount.NickName = externalUser.NickName;
            if (!string.IsNullOrWhiteSpace(externalUser.Email)) concurrentAccount.Email = externalUser.Email;
            concurrentAccount.UpdateTime = now;
            await Db.Updateable(concurrentAccount).IgnoreColumns(x => x.AvatarFileKey).ExecuteCommandAsync();
            return concurrentAccount;
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw new InvalidOperationException("OAuth 2.0 登录尚未启用");
        if (!HasRequiredConfiguration(includeSecret: true))
            throw new InvalidOperationException("OAuth 2.0 登录配置不完整");
    }

    private bool HasRequiredConfiguration(bool includeSecret) =>
        !string.IsNullOrWhiteSpace(_options.ClientId) &&
        (!includeSecret || !string.IsNullOrWhiteSpace(_options.ClientSecret)) &&
        Uri.TryCreate(_options.AuthorizationEndpoint, UriKind.Absolute, out _) &&
        Uri.TryCreate(_options.TokenEndpoint, UriKind.Absolute, out _) &&
        Uri.TryCreate(_options.UserInfoEndpoint, UriKind.Absolute, out _) &&
        Uri.TryCreate(_options.FrontendLoginUrl, UriKind.Absolute, out _);

    private string ParseAccessToken(string content)
    {
        if (string.Equals(_options.TokenResponseFormat, "form_urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var values = QueryHelpers.ParseQuery(content.TrimStart('?', '&'));
            if (values.TryGetValue("access_token", out var accessToken) && !string.IsNullOrWhiteSpace(accessToken))
                return accessToken.ToString();
        }
        else
        {
            using var document = JsonDocument.Parse(content);
            if (TryGetString(document.RootElement, "access_token", out var accessToken) && !string.IsNullOrWhiteSpace(accessToken))
                return accessToken;
        }

        throw new InvalidOperationException("OAuth Token 响应缺少 access_token");
    }

    private bool TryGetClaimValue(JsonElement element, string claimName, out string value)
    {
        if (TryGetString(element, claimName, out value)) return true;

        if (string.IsNullOrWhiteSpace(_options.ProfileAttributesClaim) ||
            !element.TryGetProperty(_options.ProfileAttributesClaim.Trim(), out var attributes) ||
            attributes.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in attributes.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && TryGetString(item, claimName, out value)) return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private string GetOptionalClaim(JsonElement element, string claimName) =>
        !string.IsNullOrWhiteSpace(claimName) && TryGetClaimValue(element, claimName.Trim(), out var value)
            ? value.Trim()
            : string.Empty;

    private static string ValidateClaimLength(string value, string claimName, int maxLength)
    {
        if (value.Length > maxLength)
            throw new InvalidOperationException($"OAuth 用户信息字段 {claimName} 超过最大长度 {maxLength}");
        return value;
    }

    private static string ValidateOptionalClaimLength(string value, string claimName, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ValidateClaimLength(value, string.IsNullOrWhiteSpace(claimName) ? "未命名字段" : claimName, maxLength);

    private static string NormalizeCsv(string value, string fallback)
    {
        var normalized = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = string.Join(',', normalized);
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string NormalizeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static string CreateRandomUrlToken(int byteLength)
    {
        var bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    private static string CreateRandomLocalPassword() => CreateRandomUrlToken(32);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class OAuth2StatePayload
    {
        public string State { get; set; } = string.Empty;
        public string CodeVerifier { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = "/";
        public long CreatedAtUnixSeconds { get; set; }
    }

    private sealed class ExternalOAuthUser
    {
        public string UserName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string NickName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }
}
