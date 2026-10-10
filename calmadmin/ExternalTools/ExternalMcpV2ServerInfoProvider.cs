using Interface;
using Model.ExternalTools;
using ModelContextProtocol.Protocol;

namespace calmadmin.Controllers.ExternalTools;

/// <summary>
/// 让标准 MCP V2 与后台服务名称配置使用同一份持久化数据。
/// ServerInfo 保持同一对象实例，供 SDK 在每次无状态请求创建服务时读取。
/// </summary>
public sealed class ExternalMcpV2ServerInfoProvider
{
    private readonly object _syncRoot = new();

    public Implementation ServerInfo { get; } = new()
    {
        Name = "calmadmin-external-tools-v2",
        Title = "智能制造管理系统 MCP",
        Version = "2.0.0"
    };

    public async Task RefreshAsync(IExternalToolService service)
    {
        var setting = await service.GetServerSettingAsync();
        Apply(setting);
    }

    public void Apply(ExternalToolServerSettingDto setting)
    {
        lock (_syncRoot)
        {
            ServerInfo.Name = setting.ServerName;
            ServerInfo.Title = setting.ServerName;
            ServerInfo.Version = setting.Version;
        }
    }
}
