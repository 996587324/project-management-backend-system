namespace Interface;

/// <summary>保护和恢复 Agent 模型平台密钥。</summary>
public interface IAgentSecretProtector
{
    string Protect(string plainText);
    string Unprotect(string cipherText);
}
