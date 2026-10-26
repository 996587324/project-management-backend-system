using Interface;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace Service;

/// <summary>邮件专用持久化密钥环，不改变已有微信、模型密钥的加密配置。</summary>
public sealed class EmailSecretProtector : IEmailSecretProtector
{
    private readonly IDataProtector _protector;

    public EmailSecretProtector(IHostEnvironment environment)
    {
        var directory = new DirectoryInfo(Path.Combine(environment.ContentRootPath, "data", "email-keys"));
        var provider = DataProtectionProvider.Create(directory, options =>
        {
            options.SetApplicationName("CalmAdmin.EmailPush");
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector("EmailPush.SmtpPassword.v1");
    }

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);
}
