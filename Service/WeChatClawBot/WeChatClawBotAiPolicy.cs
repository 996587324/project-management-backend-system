using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Service;

public static class WeChatClawBotAiPolicy
{
    public static string Day(DateTime utc) => utc.ToUniversalTime().AddHours(8).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    public static string NewSecret() => "wxai_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static bool Verify(string secret, string? hash) => hash?.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(hash));

    public static bool IsConnectionCommand(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed == "连接" || (trimmed.StartsWith("连接", StringComparison.Ordinal) &&
            trimmed.Length > 2 && char.IsWhiteSpace(trimmed[2])) ||
            text.Contains("wxai_", StringComparison.OrdinalIgnoreCase);
    }

    public static string Redact(string text) => IsConnectionCommand(text) ? "[连接密钥验证消息已脱敏]" : text;

    public static string? ReadSecret(string text)
    {
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] == "连接" && parts[1].Length == 69 &&
               parts[1].StartsWith("wxai_", StringComparison.Ordinal) ? parts[1] : null;
    }

    public static bool IsConfirmation(string text) =>
        text.Trim().Replace(" ", string.Empty).ToLowerInvariant() is "确认" or "确认执行" or "同意执行" or "执行" or "yes";
}
