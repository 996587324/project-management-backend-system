using System.Security.Cryptography;
using System.Text;
using Model.ExternalTools;

namespace Service;

/// <summary>独立的外部推送来源，不伪造后台用户或管理员角色。</summary>
public static class McpPushAccess
{
    public const string Application = "MCP";

    public static string RequireOwner(ExternalToolCallerContext caller, string scope)
    {
        if (!caller.IsDirectAccess && (!caller.AllResources || !caller.Scopes.Contains(scope)))
            throw new UnauthorizedAccessException("客户端缺少独立推送权限或推送对象授权");
        var client = caller.IsDirectAccess ? "anonymous-mcp" : caller.ClientId;
        if (string.IsNullOrWhiteSpace(client)) throw new UnauthorizedAccessException("缺少外部调用来源");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client)));
        return "mcp:" + digest[..60];
    }

    public static bool IsExternal(string application, string owner) =>
        application == Application && owner.StartsWith("mcp:", StringComparison.Ordinal) && owner.Length == 64;
}
