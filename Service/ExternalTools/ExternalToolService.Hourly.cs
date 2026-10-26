using System.Text.Json;
using Model.ExternalTools;

namespace Service;

public sealed partial class ExternalToolService
{
    private async Task<object> ExecuteHourlyAsync(string name, JsonElement args, ExternalToolCallerContext caller)
    {
        // 内网匿名MCP仅获得显式发布的统计；不借用管理员、不暴露配置写入口。
        if (!caller.IsDirectAccess && !caller.AllResources)
            throw new UnauthorizedAccessException("小时看板工具当前仅向直连或全资源授权客户端开放已发布数据");
        var service = _hourlyProduction ?? throw new InvalidOperationException("小时统计服务未注册");
        if (name == "hourly.boards") return await service.PublishedBoardsAsync();
        var code = GetRequiredString(args, "boardCode");
        if (name == "hourly.runtime") return await service.RuntimeAsync(null, code, publicRead: true);
        return await service.PublishedReportsAsync(code, GetBoundedInt(args, "current", 1, 1, 10000));
    }

    private const string HourlyRuntimeSchema = """
        {"type":"object","properties":{"boardCode":{"type":"string","minLength":1,"maxLength":64}},"required":["boardCode"],"additionalProperties":false}
        """;
    private const string HourlyReportSchema = """
        {"type":"object","properties":{"boardCode":{"type":"string","minLength":1,"maxLength":64},"current":{"type":"integer","minimum":1,"maximum":10000}},"required":["boardCode"],"additionalProperties":false}
        """;
}
