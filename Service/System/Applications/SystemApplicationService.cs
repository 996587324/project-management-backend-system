using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Model.Menu;
using Model.Other;
using Model.SystemApplications;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>统一会话、系统票据与授权。授权每次读取数据库，不把权限永久写死在 JWT 中。</summary>
public sealed class SystemApplicationService : ISystemApplicationService
{
    public const string McpAudience = "calmadmin-external-tools";
    private readonly SqlSugarScope _scope;
    private readonly JWTTokenOptions _jwt;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ISystemApplicationMenuService? _menus;
    private readonly TimeProvider _clock;
    private const int IdleTimeoutMinutes = 20;

    public SystemApplicationService(SqlSugarScope scope, IOptionsMonitor<JWTTokenOptions> options,
        IHttpContextAccessor? httpContextAccessor = null, ISystemApplicationMenuService? menus = null,
        TimeProvider? clock = null)
    {
        _scope = scope;
        _jwt = options.CurrentValue;
        _httpContextAccessor = httpContextAccessor;
        _menus = menus;
        _clock = clock ?? TimeProvider.System;
    }

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_scope, "calmadmin");

    public async Task RegisterSessionAsync(string userKey, string sessionId)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var user = await FindUserAsync(userKey);
        await Db.Insertable(new SysPortalSessionEntity
        {
            SessionId = Require(sessionId, 64, "会话编号"),
            UserId = user.UserId,
            ExpiresAtUtc = _clock.GetUtcNow().UtcDateTime.AddMinutes(IdleTimeoutMinutes)
        }).ExecuteCommandAsync();
    }

    public async Task<ApplicationAccessContext> GetAccessAsync(ClaimsPrincipal principal, bool requireApplication = false)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        if (principal.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("请先登录");
        var httpContext = _httpContextAccessor?.HttpContext;
        // 只在同一 HTTP 请求中复用认证阶段的授权结果；下一次请求仍读取数据库，撤权即时生效。
        if (httpContext != null && !httpContext.WebSockets.IsWebSocketRequest && ReferenceEquals(httpContext.User, principal) &&
            httpContext.Items.TryGetValue(typeof(ApplicationAccessContext), out var cached) && cached is ApplicationAccessContext existing)
        {
            if (requireApplication && string.IsNullOrEmpty(existing.AppCode))
                throw new UnauthorizedAccessException("请先在控制台选择系统");
            return existing;
        }
        var sessionId = principal.FindFirst("SessionId")?.Value ?? string.Empty;
        var session = await Db.Queryable<SysPortalSessionEntity>().FirstAsync(x => x.SessionId == sessionId);
        if (session == null || session.RevokedAtUtc != null || session.ExpiresAtUtc <= _clock.GetUtcNow().UtcDateTime)
            throw new UnauthorizedAccessException("统一登录会话已失效，请重新登录");
        var user = await FindUserAsync(session.UserId);
        // 主体和会话必须一致，不能通过提交任意 SessionId 使用他人的授权。
        var tokenUser = principal.FindFirst("Id")?.Value;
        var tokenName = principal.FindFirst("Name")?.Value;
        if (tokenUser != user.UserId && tokenName != user.UserName)
            throw new UnauthorizedAccessException("令牌与会话用户不一致");
        var platformAdministrator = await Db.Queryable<SysPlatformAdministratorEntity>()
            .AnyAsync(x => x.UserId == user.UserId);
        var appCode = principal.FindFirst("AppCode")?.Value ?? string.Empty;
        SysUserApplicationEntity? grant = null;
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(appCode))
        {
            await GetApplicationAsync(appCode);
            grant = await Db.Queryable<SysUserApplicationEntity>()
                .FirstAsync(x => x.UserId == user.UserId && x.AppCode == appCode && x.Enabled);
            if (grant == null) throw new UnauthorizedAccessException("当前系统授权已被撤销");
            permissions = Parse(grant.PermissionsJson);
            if (appCode == "A" && grant.UseLegacyPermissions)
            {
                permissions.UnionWith(user.PermissionKeyList);
                var roles = await Db.Queryable<SysRole>().Where(x => x.enabled).ToListAsync();
                foreach (var role in roles.Where(x => user.RoleList.Contains(x.roleCode)))
                    permissions.UnionWith(role.permissionKeyList);
            }
        }
        else if (requireApplication)
        {
            throw new UnauthorizedAccessException("请先在控制台选择系统");
        }

        return new ApplicationAccessContext
        {
            UserId = user.UserId, UserName = user.UserName, SessionId = sessionId, AppCode = appCode,
            PlatformAdministrator = platformAdministrator,
            AllResources = grant?.AllResources ?? false,
            Permissions = permissions,
            McpScopes = Parse(grant?.McpScopesJson),
            LineCodes = Parse(grant?.LineCodesJson),
            ProjectCodes = Parse(grant?.ProjectCodesJson),
            DeviceCodes = Parse(grant?.DeviceCodesJson),
            SourceKeys = Parse(grant?.SourceKeysJson, StringComparer.Ordinal)
        };
    }

    public async Task<ApplicationAccessContext> GetChannelAccessAsync(string userId, string appCode)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var user = await FindUserAsync(userId);
        await GetApplicationAsync(appCode);
        var grant = await Db.Queryable<SysUserApplicationEntity>()
            .FirstAsync(x => x.UserId == user.UserId && x.AppCode == appCode && x.Enabled)
            ?? throw new UnauthorizedAccessException("该用户没有当前系统授权");
        var permissions = Parse(grant.PermissionsJson);
        if (appCode == "A" && grant.UseLegacyPermissions)
        {
            permissions.UnionWith(user.PermissionKeyList);
            var roles = await Db.Queryable<SysRole>().Where(x => x.enabled).ToListAsync();
            foreach (var role in roles.Where(x => user.RoleList.Contains(x.roleCode)))
                permissions.UnionWith(role.permissionKeyList);
        }
        return new ApplicationAccessContext
        {
            UserId = user.UserId, UserName = user.UserName, AppCode = appCode,
            AllResources = grant.AllResources, Permissions = permissions,
            McpScopes = Parse(grant.McpScopesJson), LineCodes = Parse(grant.LineCodesJson),
            ProjectCodes = Parse(grant.ProjectCodesJson), DeviceCodes = Parse(grant.DeviceCodesJson),
            SourceKeys = Parse(grant.SourceKeysJson, StringComparer.Ordinal),
            PlatformAdministrator = await Db.Queryable<SysPlatformAdministratorEntity>().AnyAsync(x => x.UserId == user.UserId)
        };
    }

    public async Task<List<SysApplicationEntity>> GetApplicationsAsync(ClaimsPrincipal principal)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var access = await GetAccessAsync(principal);
        var grants = await Db.Queryable<SysUserApplicationEntity>()
            .Where(x => x.UserId == access.UserId && x.Enabled).ToListAsync();
        var codes = grants.Select(x => x.AppCode).ToHashSet();
        return (await Db.Queryable<SysApplicationEntity>().Where(x => x.Enabled).OrderBy(x => x.Sort).ToListAsync())
            .Where(x => codes.Contains(x.AppCode)).ToList();
    }

    public async Task<object> CreateTicketAsync(ClaimsPrincipal principal, ApplicationTicketRequest request)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var access = await GetAccessAsync(principal);
        var application = await GetApplicationAsync(request.AppCode);
        if (!await Db.Queryable<SysUserApplicationEntity>()
                .AnyAsync(x => x.UserId == access.UserId && x.AppCode == application.AppCode && x.Enabled))
            throw new UnauthorizedAccessException("没有该系统的入口权限");
        if (string.IsNullOrEmpty(request.CodeChallenge) || request.CodeChallenge.Length != 43 || request.CodeChallenge.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("只接受 S256 PKCE challenge");
        var ticket = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await Db.Insertable(new SysApplicationLoginTicketEntity
        {
            TicketHash = Hash(ticket), SessionId = access.SessionId, AppCode = application.AppCode,
            CodeChallenge = request.CodeChallenge, State = Require(request.State, 128, "state"),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(60)
        }).ExecuteCommandAsync();
        // 不接受客户端提供 redirect URL；跳转目标只能来自管理员登记的固定 EntryUrl。
        return new { ticket, application.EntryUrl, application.HomePath, application.AppCode, expiresIn = 60 };
    }

    public async Task<object> RedeemTicketAsync(ApplicationTicketRedeemRequest request)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        if (string.IsNullOrEmpty(request.CodeVerifier) || request.CodeVerifier.Length is < 43 or > 128 ||
            request.CodeVerifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~'))
            throw new ArgumentException("无效的 PKCE verifier");
        var hash = Hash(Require(request.Ticket, 128, "票据"));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));
        var now = DateTime.UtcNow;
        // 条件更新同时校验系统、state、PKCE、有效期；并发兑换只能有一个成功。
        var consumed = await Db.Updateable<SysApplicationLoginTicketEntity>()
            .SetColumns(x => x.ConsumedAtUtc == now)
            .Where(x => x.TicketHash == hash && x.AppCode == request.AppCode && x.State == request.State &&
                        x.CodeChallenge == challenge && x.ConsumedAtUtc == null && x.ExpiresAtUtc > now)
            .ExecuteCommandAsync();
        if (consumed != 1) throw new UnauthorizedAccessException("系统票据无效、已使用或已过期");
        var row = await Db.Queryable<SysApplicationLoginTicketEntity>().FirstAsync(x => x.TicketHash == hash);
        var session = await Db.Queryable<SysPortalSessionEntity>().FirstAsync(x => x.SessionId == row.SessionId);
        if (session == null || session.RevokedAtUtc != null || session.ExpiresAtUtc <= now)
            throw new UnauthorizedAccessException("统一登录会话已失效");
        var user = await FindUserAsync(session.UserId);
        var application = await GetApplicationAsync(row.AppCode);
        if (!await Db.Queryable<SysUserApplicationEntity>().AnyAsync(x => x.UserId == user.UserId && x.AppCode == row.AppCode && x.Enabled))
            throw new UnauthorizedAccessException("系统授权已被撤销");
        return new
        {
            token = CreateToken(user, session, row.AppCode, "app", _jwt.Audience),
            refreshToken = string.Empty, application.AppCode, application.DisplayName, application.HomePath
        };
    }

    public async Task RevokeSessionAsync(ClaimsPrincipal principal)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var access = await GetAccessAsync(principal);
        var now = DateTime.UtcNow;
        await Db.Updateable<SysPortalSessionEntity>().SetColumns(x => x.RevokedAtUtc == now)
            .Where(x => x.SessionId == access.SessionId && x.RevokedAtUtc == null).ExecuteCommandAsync();
    }

    /// <summary>
    /// GET 只读取会话；POST 按最近真实操作距今的时长续期。普通 API、在线心跳不会续期。
    /// 更新条件同时检查撤销和过期，旧标签页不能恢复已失效的会话，也不能缩短其他页面的有效期。
    /// </summary>
    public async Task<SessionActivityResult> GetSessionActivityAsync(ClaimsPrincipal principal, int? idleMilliseconds = null)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var purpose = principal.FindFirst("TokenPurpose")?.Value;
        if (purpose is not ("portal" or "app")) throw new UnauthorizedAccessException("请使用统一入口重新登录");
        var timeoutMilliseconds = IdleTimeoutMinutes * 60 * 1000;
        if (idleMilliseconds is < 0 || idleMilliseconds >= timeoutMilliseconds)
            throw new ArgumentException("操作时间已失效或不合法");
        var access = await GetAccessAsync(principal, purpose == "app");
        var now = _clock.GetUtcNow().UtcDateTime;
        if (idleMilliseconds.HasValue)
        {
            var deadline = now.AddMilliseconds(timeoutMilliseconds - idleMilliseconds.Value);
            await Db.Updateable<SysPortalSessionEntity>()
                .SetColumns(x => x.ExpiresAtUtc == deadline)
                .Where(x => x.SessionId == access.SessionId && x.RevokedAtUtc == null &&
                    x.ExpiresAtUtc > now && x.ExpiresAtUtc < deadline).ExecuteCommandAsync();
        }
        var session = await Db.Queryable<SysPortalSessionEntity>().FirstAsync(x => x.SessionId == access.SessionId);
        if (session == null || session.RevokedAtUtc != null || session.ExpiresAtUtc <= now)
            throw new UnauthorizedAccessException("统一登录会话已失效，请重新登录");
        var expiryClaim = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        // 提前更新即将过期的令牌；读取状态不会改变会话截止时间。
        var renewToken = !long.TryParse(expiryClaim, out var expiry) ||
            expiry <= new DateTimeOffset(now).ToUnixTimeSeconds() + 10 * 60;
        var user = renewToken ? await FindUserAsync(access.UserId) : null;
        return new SessionActivityResult
        {
            SessionId = session.SessionId, ServerTimeUtc = now.ToString("O"),
            ExpiresAtUtc = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc).ToString("O"),
            Token = user == null ? null : CreateToken(user, session, access.AppCode, purpose, _jwt.Audience),
            PortalToken = user == null ? null : CreateToken(user, session, "", "portal", _jwt.Audience)
        };
    }

    public async Task SaveApplicationAsync(ClaimsPrincipal principal, SysApplicationEntity application)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        await RequireAdministratorAsync(principal);
        var current = await Db.Queryable<SysApplicationEntity>().FirstAsync(x => x.AppCode == application.AppCode);
        if (current == null) throw new ArgumentException("系统编码不存在，不能通过重命名创建或改变编码");
        application.AppCode = current.AppCode;
        if (application.AppCode == "A" && !application.Enabled)
            throw new ArgumentException("首期统一控制台依赖 A，不能停用控制台；可以撤销普通账号的 A 入口授权");
        application.DisplayName = Require(application.DisplayName, 128, "系统名称");
        application.HomePath = Require(application.HomePath, 255, "首页路径");
        if (!application.HomePath.StartsWith('/') || application.HomePath.StartsWith("//")) throw new ArgumentException("首页必须为站内路径");
        application.EntryUrl = (application.EntryUrl ?? string.Empty).Trim();
        if (application.EntryUrl.Length > 2048 || (!string.IsNullOrEmpty(application.EntryUrl) &&
            (!Uri.TryCreate(application.EntryUrl, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
             !string.IsNullOrEmpty(uri.Query) || uri.Fragment.Contains('?'))))
            throw new ArgumentException("入口需为固定 HTTP/HTTPS 地址，不能带凭据或查询参数");
        application.UpdateTime = DateTime.Now;
        await Db.Updateable(application).ExecuteCommandAsync();
    }

    public async Task<List<ApplicationGrantDto>> GetGrantsAsync(ClaimsPrincipal principal, string userId)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        await RequireAdministratorAsync(principal);
        return (await Db.Queryable<SysUserApplicationEntity>().Where(x => x.UserId == userId).ToListAsync())
            .Select(ToDto).ToList();
    }

    public async Task SaveGrantAsync(ClaimsPrincipal principal, ApplicationGrantDto grant)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        await RequireAdministratorAsync(principal);
        await FindUserAsync(grant.UserId, allowDisabled: true);
        var application = await Db.Queryable<SysApplicationEntity>().FirstAsync(x => x.AppCode == grant.AppCode);
        if (application == null) throw new ArgumentException("系统不存在");
        grant.AppCode = application.AppCode;
        var actor = await GetAccessAsync(principal);
        if (grant.UserId == actor.UserId && grant.AppCode == "A" && !grant.Enabled)
            throw new ArgumentException("不能撤销自己用于管理统一授权的 A 入口");
        if (grant.UseLegacyPermissions && grant.AppCode != "A") throw new ArgumentException("B 不能继承 A 的历史角色权限");
        var existing = await Db.Queryable<SysUserApplicationEntity>().FirstAsync(x => x.UserId == grant.UserId && x.AppCode == grant.AppCode);
        var entity = new SysUserApplicationEntity
        {
            Id = existing?.Id ?? 0, UserId = grant.UserId, AppCode = grant.AppCode,
            Enabled = grant.Enabled, UseLegacyPermissions = grant.UseLegacyPermissions,
            AllResources = grant.AllResources,
            PermissionsJson = Encode(grant.Permissions), McpScopesJson = Encode(grant.McpScopes),
            LineCodesJson = Encode(grant.LineCodes), ProjectCodesJson = Encode(grant.ProjectCodes),
            DeviceCodesJson = Encode(grant.DeviceCodes), SourceKeysJson = EncodeSources(grant.SourceKeys),
            UpdateTime = DateTime.Now
        };
        if (existing == null) await Db.Insertable(entity).ExecuteCommandAsync();
        else await Db.Updateable(entity).ExecuteCommandAsync();
    }

    public async Task<object> GetAdministrationAsync(ClaimsPrincipal principal)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        await RequireAdministratorAsync(principal);
        return new
        {
            applications = await Db.Queryable<SysApplicationEntity>().OrderBy(x => x.Sort).ToListAsync(),
            users = (await Db.Queryable<SysUserInfo>().ToListAsync()).Select(x => new { x.UserId, x.UserName, x.Enabled }),
            menus = await Db.Queryable<sys_menu>().OrderBy(x => x.Sort).ToListAsync(),
            applicationMenus = (await Db.Queryable<SysApplicationMenuEntity>().Where(x => x.AppCode == "A").ToListAsync())
                .Concat((await Db.Queryable<SysApplicationMenuEntryEntity>().Where(x => x.AppCode == "B" && x.SourceMenuId != null).ToListAsync())
                    .Select(x => new SysApplicationMenuEntity { AppCode = "B", MenuId = x.SourceMenuId! })).ToList()
        };
    }

    public async Task SaveMenusAsync(ClaimsPrincipal principal, string appCode, List<string> menuIds)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        await RequireAdministratorAsync(principal);
        if (!await Db.Queryable<SysApplicationEntity>().AnyAsync(x => x.AppCode == appCode)) throw new ArgumentException("系统不存在");
        if (appCode == "B")
        {
            if (_menus == null) throw new InvalidOperationException("系统菜单服务未注册");
            await _menus.ConfigureSourcesAsync(appCode, menuIds);
            return;
        }
        var selected = menuIds.Distinct(StringComparer.Ordinal).ToList();
        var all = await Db.Queryable<sys_menu>().ToListAsync();
        if (selected.Any(id => !all.Any(x => x.MenuId == id))) throw new ArgumentException("菜单不存在");
        if (appCode == "A" && !all.Any(x => x.MenuName == "ApplicationAdministration" && selected.Contains(x.MenuId)))
            throw new ArgumentException("A 必须保留系统入口与授权菜单，防止平台管理员无法管理授权");
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            await Db.Deleteable<SysApplicationMenuEntity>().Where(x => x.AppCode == appCode).ExecuteCommandAsync();
            foreach (var id in selected)
                await Db.Insertable(new SysApplicationMenuEntity { AppCode = appCode, MenuId = id }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("保存系统菜单失败");
    }

    public async Task<string> CreateMcpTokenAsync(ClaimsPrincipal principal)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        var access = await GetAccessAsync(principal, true);
        if (access.McpScopes.Count == 0) throw new UnauthorizedAccessException("当前系统未配置 MCP 工具授权");
        var user = await FindUserAsync(access.UserId);
        var session = await Db.Queryable<SysPortalSessionEntity>().FirstAsync(x => x.SessionId == access.SessionId);
        return CreateToken(user, session, access.AppCode, "mcp", McpAudience);
    }

    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        cancellationToken.ThrowIfCancellationRequested();
        // 分批清理过期临时凭据；延后一天删除会话，保留故障排查窗口。
        var ticketCutoff = DateTime.UtcNow.AddMinutes(-5);
        var sessionCutoff = DateTime.UtcNow.AddDays(-1);
        var tickets = await Db.Queryable<SysApplicationLoginTicketEntity>()
            .Where(x => x.ExpiresAtUtc < ticketCutoff).OrderBy(x => x.ExpiresAtUtc)
            .Take(500).Select(x => x.TicketHash).ToListAsync();
        if (tickets.Count > 0)
            await Db.Deleteable<SysApplicationLoginTicketEntity>().Where(x => tickets.Contains(x.TicketHash)).ExecuteCommandAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = await Db.Queryable<SysPortalSessionEntity>()
            .Where(x => x.ExpiresAtUtc < sessionCutoff).OrderBy(x => x.ExpiresAtUtc)
            .Take(500).Select(x => x.SessionId).ToListAsync();
        if (sessions.Count > 0)
            await Db.Deleteable<SysPortalSessionEntity>().Where(x => sessions.Contains(x.SessionId)).ExecuteCommandAsync();
    }

    private string CreateToken(SysUserInfo user, SysPortalSessionEntity session, string appCode, string purpose, string audience)
    {
        var claims = new[]
        {
            new Claim("Id", user.UserId), new Claim("Name", user.UserName),
            new Claim("SessionId", session.SessionId), new Claim("AppCode", appCode),
            new Claim("TokenPurpose", purpose), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(_jwt.Issuer, audience, claims,
            // SQL Server datetime 不保留 Kind；该列约定存储 UTC，不能再次按本地时区换算。
            // 浏览器令牌略晚于会话过期，便于多页面在有效会话内换新；鉴权仍每次检查会话。
            expires: DateTime.SpecifyKind(purpose is "app" or "portal"
                ? session.ExpiresAtUtc.AddMinutes(5) : session.ExpiresAtUtc, DateTimeKind.Utc),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecurityKey)), SecurityAlgorithms.HmacSha256)));
    }

    private async Task<SysUserInfo> FindUserAsync(string key, bool allowDisabled = false)
    {
        var user = await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == key || x.UserName == key);
        if (user == null || (!allowDisabled && !user.Enabled)) throw new UnauthorizedAccessException("账号不存在或已停用");
        return user;
    }

    private async Task<SysApplicationEntity> GetApplicationAsync(string code)
    {
        var application = await Db.Queryable<SysApplicationEntity>().FirstAsync(x => x.AppCode == code && x.Enabled);
        return application ?? throw new UnauthorizedAccessException("系统不存在或已停用");
    }

    private async Task RequireAdministratorAsync(ClaimsPrincipal principal)
    {
        if (!(await GetAccessAsync(principal)).PlatformAdministrator) throw new UnauthorizedAccessException("仅平台管理员可以配置入口与授权");
    }

    public static HashSet<string> Parse(string? json, StringComparer? comparer = null) =>
        (JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [])
        .ToHashSet(comparer ?? StringComparer.OrdinalIgnoreCase);

    private static string Encode(IEnumerable<string> values)
    {
        var rows = values.Select(x => Require(x, 512, "授权编码")).Distinct(StringComparer.Ordinal).ToList();
        if (rows.Count > 10000) throw new ArgumentException("单类授权最多 10000 条");
        return JsonSerializer.Serialize(rows);
    }

    private static string EncodeSources(IEnumerable<string> values)
    {
        var keys = values.Select(value =>
        {
            var normalized = Require(value, 512, "数据源授权").Replace('|', '\u001f');
            var parts = normalized.Split('\u001f');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]) ||
                parts[0].Length > 64 || parts[1].Length > 128)
                throw new ArgumentException("数据源授权需为 租户|设备，两部分均不能为空");
            return parts[0].Trim() + "\u001f" + parts[1].Trim();
        });
        return Encode(keys);
    }

    private static string Require(string? value, int length, string name)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > length) throw new ArgumentException($"{name}不能为空且不能超过 {length} 字符");
        return value;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ApplicationGrantDto ToDto(SysUserApplicationEntity row) => new()
    {
        UserId = row.UserId, AppCode = row.AppCode, Enabled = row.Enabled,
        UseLegacyPermissions = row.UseLegacyPermissions, AllResources = row.AllResources,
        Permissions = Parse(row.PermissionsJson).ToList(), McpScopes = Parse(row.McpScopesJson).ToList(),
        LineCodes = Parse(row.LineCodesJson).ToList(), ProjectCodes = Parse(row.ProjectCodesJson).ToList(),
        DeviceCodes = Parse(row.DeviceCodesJson).ToList(), SourceKeys = Parse(row.SourceKeysJson).ToList()
    };
}
