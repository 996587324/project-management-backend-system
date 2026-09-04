using Interface;
using Microsoft.AspNetCore.DataProtection;

namespace Service;

/// <summary>使用 ASP.NET Core Data Protection 加密数据库中的模型平台密钥。</summary>
public sealed class AgentSecretProtector : IAgentSecretProtector
{
    private readonly IDataProtector _protector;

    public AgentSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("CalmAdmin.AgentModelProvider.ApiKey.v1");
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string cipherText) => _protector.Unprotect(cipherText);
}
