using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interface;
using Model.EmailPush;
using Model.SystemApplications;
using Model.User;
using SqlSugar;

namespace Service;

public sealed partial class EmailPushService(
    SqlSugarScope scope,
    IEmailPushDatabaseInitializer initializer,
    IEmailSecretProtector secrets,
    IEmailTransport transport,
    ISystemApplicationService? applications = null) : IEmailPushService
{
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    private void RequireReady()
    {
        if (!initializer.IsReady) throw new InvalidOperationException("邮件服务正在初始化，请稍后重试");
    }

    private async Task<SysUserInfo> RequireSenderAsync(ApplicationAccessContext access)
    {
        if (access.AppCode is not ("A" or "B") ||
            !(access.HasPermission("EmailPush") || access.HasPermission("AgentExecutor")))
            throw new UnauthorizedAccessException("没有邮件发送权限");
        return await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == access.UserId && x.Enabled)
            ?? throw new UnauthorizedAccessException("当前账号不存在或已停用");
    }

    public async Task<bool> IsAdministratorAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var user = await RequireSenderAsync(access);
        return access.HasPermission("EmailPush") && user.RoleList.Any(x => x.Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
                                      x.Equals("R_SUPER", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RequireAdministratorAsync(ApplicationAccessContext access)
    {
        if (!access.HasPermission("EmailPush") || !await IsAdministratorAsync(access))
            throw new UnauthorizedAccessException("只有具备邮件功能权限的管理员角色可以修改邮件服务配置");
    }

    public async Task<object> GetAvailabilityAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireSenderAsync(access);
        if (!initializer.IsReady) return new { ready = false, enabled = false };
        var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        return new { ready = true, enabled = setting?.Enabled == true };
    }

    public async Task<EmailSettingDto> GetSettingAsync(ApplicationAccessContext access)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireAdministratorAsync(access);
        RequireReady();
        return ToSetting(await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1));
    }

    public async Task<EmailSettingDto> SaveSettingAsync(ApplicationAccessContext access, EmailSettingSaveDto dto)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireAdministratorAsync(access);
        RequireReady();
        ValidateSetting(dto);
        var existing = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
        var passwordChanged = !string.IsNullOrEmpty(dto.Password);
        // Changing the SMTP identity must not accidentally reuse another account's password.
        if (!passwordChanged && !string.Equals(existing.UserName, dto.UserName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(existing.ProtectedPassword))
            throw new InvalidOperationException("更换邮件登录账户时必须重新填写密码或授权码");
        var protectedPassword = passwordChanged ? secrets.Protect(dto.Password!) : existing.ProtectedPassword;
        if (dto.Enabled && (string.IsNullOrWhiteSpace(dto.Host) || string.IsNullOrWhiteSpace(dto.FromAddress) ||
                            string.IsNullOrWhiteSpace(dto.UserName) || string.IsNullOrEmpty(protectedPassword)))
            throw new InvalidOperationException("启用前必须配置 SMTP 地址、登录账户、授权码和发件邮箱");
        var now = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            var affected = await Db.Updateable<EmailPushSettingEntity>()
                .SetColumns(x => new EmailPushSettingEntity
                {
                    Enabled = dto.Enabled, Host = dto.Host.Trim(), Port = dto.Port, Security = dto.Security,
                    UserName = dto.UserName.Trim(), ProtectedPassword = protectedPassword,
                    FromAddress = dto.FromAddress.Trim(), FromName = dto.FromName.Trim(),
                    TimeoutSeconds = dto.TimeoutSeconds, IntervalSeconds = dto.IntervalSeconds,
                    MaxAttempts = dto.MaxAttempts, Version = dto.Version + 1, UpdatedBy = access.UserId, UpdateTime = now
                })
                .Where(x => x.Id == 1 && x.Version == dto.Version &&
                    (x.LeaseExpireTime == null || x.LeaseExpireTime <= now)).ExecuteCommandAsync();
            if (affected != 1) throw new InvalidOperationException("配置已更新或正在发送／测试，请刷新后稍后保存");
            await Db.Insertable(new EmailPushConfigurationAuditEntity
            {
                UserId = access.UserId, Action = "SaveSetting",
                Summary = $"Enabled={dto.Enabled}; Security={dto.Security}; Port={dto.Port}; PasswordChanged={passwordChanged}",
                CreateTime = now
            }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException ?? new InvalidOperationException("邮件配置保存失败");
        return ToSetting(await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1));
    }

    public async Task TestConnectionAsync(ApplicationAccessContext access, CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        await RequireAdministratorAsync(access);
        RequireReady();
        var owner = Guid.NewGuid().ToString("N");
        var now = DateTime.Now;
        var claimed = await Db.Updateable<EmailPushSettingEntity>()
            .SetColumns(x => new EmailPushSettingEntity { LeaseOwner = owner, LeaseExpireTime = now.AddMinutes(5) })
            .Where(x => x.Id == 1 && (x.LeaseExpireTime == null || x.LeaseExpireTime <= now)).ExecuteCommandAsync();
        if (claimed == 0) throw new InvalidOperationException("正在发送或测试邮件，请稍后再试");
        try
        {
            var setting = await Db.Queryable<EmailPushSettingEntity>().InSingleAsync(1);
            if (string.IsNullOrWhiteSpace(setting.Host) || string.IsNullOrEmpty(setting.ProtectedPassword))
                throw new InvalidOperationException("请先保存完整的发件账户配置");
            await Db.Insertable(new EmailPushConfigurationAuditEntity
            {
                UserId = access.UserId, Action = "TestConnection", Summary = "检查 SMTP 连接及认证，不发送邮件"
            }).ExecuteCommandAsync();
            await transport.TestAsync(setting, cancellationToken);
        }
        finally
        {
            await ReleaseLeaseAsync(owner, false);
        }
    }

    private static EmailSettingDto ToSetting(EmailPushSettingEntity setting)
    {
        return new EmailSettingDto
        {
            Enabled = setting.Enabled, Host = setting.Host, Port = setting.Port, Security = setting.Security,
            UserName = setting.UserName, FromAddress = setting.FromAddress, FromName = setting.FromName,
            TimeoutSeconds = setting.TimeoutSeconds, IntervalSeconds = setting.IntervalSeconds,
            MaxAttempts = setting.MaxAttempts, Version = setting.Version,
            HasPassword = !string.IsNullOrEmpty(setting.ProtectedPassword), UpdateTime = setting.UpdateTime
        };
    }

    private static void ValidateSetting(EmailSettingSaveDto dto)
    {
        if (dto.Port is < 1 or > 65535 || dto.TimeoutSeconds is < 5 or > 120 ||
            dto.IntervalSeconds is < 1 or > 86400 || dto.MaxAttempts is < 1 or > 5)
            throw new ArgumentException("端口、超时时间、发送间隔或最大尝试次数不合法");
        if (dto.Security is not ("StartTls" or "SslOnConnect"))
            throw new ArgumentException("连接方式仅支持 STARTTLS 或 SSL/TLS，禁止明文传输凭据");
        if (dto.Host.Length > 256 || dto.UserName.Length > 256 || dto.FromAddress.Length > 256 ||
            dto.FromName.Length > 128 || dto.Password?.Length > 512)
            throw new ArgumentException("邮件配置字段过长");
        if (dto.Host.Contains('/') || dto.Host.Contains(':') || dto.Host.Any(char.IsWhiteSpace))
            throw new ArgumentException("SMTP 地址请填写主机名或 IP，不要包含协议、端口或路径");
        if (!string.IsNullOrWhiteSpace(dto.FromAddress) && !IsValidAddress(dto.FromAddress))
            throw new ArgumentException("发件邮箱格式不正确");
    }

    private static bool IsValidAddress(string? address)
    {
        return !string.IsNullOrWhiteSpace(address) && address.Length <= 256 &&
               MailAddress.TryCreate(address.Trim(), out var parsed) &&
               parsed.Address.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
