using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Model.SystemApplications;

namespace Service;

/// <summary>资源或权限变更后不再复用旧结果；只记录摘要，不记录凭证。</summary>
public static class PushResultAccessFingerprint
{
    public static string Create(ApplicationAccessContext access)
    {
        static string[] Ordered(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            access.UserId, access.AppCode, access.AllResources,
            permissions = Ordered(access.Permissions), lines = Ordered(access.LineCodes),
            projects = Ordered(access.ProjectCodes), devices = Ordered(access.DeviceCodes), sources = Ordered(access.SourceKeys)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
