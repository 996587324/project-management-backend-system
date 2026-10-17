using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Microsoft.AspNetCore.Http;
using Model.Agent;
using Model.ProcessBoard;
using Model.SystemApplications;
using SqlSugar;

namespace Service;

/// <summary>
/// 工艺看板工具执行入口。只调用受控业务方法，不把用户输入拼接成SQL或代码。
/// 网页与微信共用入口，确认执行时重新读取当前系统及资源权限。
/// </summary>
public sealed partial class ProcessBoardAgentExecutor(
    SqlSugarScope scope,
    ISystemApplicationService applications,
    IHttpContextAccessor httpContext,
    IProcessBoardService board,
    IProcessFormulaService formulas,
    IEdgeDashboardService dashboard,
    ISharedModelDictionaryService dictionary,
    IEdgeDeviceCardPushService push) : IAgentBusinessToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    public bool CanExecute(string serviceName, string actionName)
    {
        return serviceName.Equals("ProcessBoard", StringComparison.OrdinalIgnoreCase) &&
            ProcessBoardAgentToolCatalog.Definitions.Any(tool => tool.ActionName == actionName);
    }

    public async Task<AgentBusinessToolResult> ExecuteAsync(
        string actionName,
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = ProcessBoardAgentToolCatalog.Definitions.FirstOrDefault(tool => tool.ActionName == actionName)
            ?? throw new ArgumentException("未开放的工艺看板动作");
        var errors = AgentJsonSchemaValidator.Validate(tool.InputSchema, arguments.GetRawText());
        if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors.Take(8)));
        var access = await RequireAccessAsync(context, tool.IsWriteAction && actionName != "export-create" ||
            actionName is "formula-preview" or "recipients" or "push-tasks");
        using var operation = DatabaseOperationScope.Begin(scope);
        if (!Db.DbMaintenance.IsAnyTable("ProcessBoardConfig", false))
            throw new InvalidOperationException("工艺看板正在后台准备，请稍后再试");

        var data = tool.IsWriteAction
            ? await WriteAsync(actionName, arguments, access, cancellationToken)
            : await ReadAsync(actionName, arguments, access, cancellationToken);
        if (data is ProcessFormulaResult { Success: false } formulaFailure)
            return AgentBusinessToolResult.Fail("公式试算失败：" + formulaFailure.Error);
        return AgentBusinessToolResult.Ok(data, tool.Title + "成功");
    }

    private async Task<ApplicationAccessContext> RequireAccessAsync(AgentBusinessToolContext context, bool write)
    {
        ApplicationAccessContext access;
        if (AgentChannelExecutionScope.Current is { } channel)
        {
            channel.CancellationToken.ThrowIfCancellationRequested();
            access = channel.ResolveApplicationAccessAsync == null
                ? throw new UnauthorizedAccessException("该后台渠道没有提供真实系统授权上下文")
                : await channel.ResolveApplicationAccessAsync();
        }
        else
        {
            var principal = httpContext.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
                throw new UnauthorizedAccessException("工艺看板Agent需要已登录的系统会话或已验证微信渠道");
            access = await applications.GetAccessAsync(principal, true);
        }

        if (string.IsNullOrWhiteSpace(context.UserId) ||
            !access.UserId.Equals(context.UserId, StringComparison.OrdinalIgnoreCase) ||
            access.AppCode is not ("A" or "B"))
            throw new UnauthorizedAccessException("Agent调用用户与当前系统授权不一致");
        if (write ? !access.HasPermission("ProcessBoardConfig") :
            !access.HasPermission("ProcessBoard") && !access.HasPermission("ProcessBoardConfig"))
            throw new UnauthorizedAccessException(write ? "没有工艺看板配置权限" : "没有工艺看板查看权限");
        return access;
    }

    private async Task<ProcessBoardDefinition> GetDefinitionAsync(ApplicationAccessContext access, long id)
    {
        var definition = (await board.GetDefinitionsAsync(access)).FirstOrDefault(item => item.Id == id)
            ?? throw new UnauthorizedAccessException("配置不存在或不在当前授权范围内");
        return definition;
    }

    private static long Id(JsonElement args, string key = "id")
    {
        return args.GetProperty(key).GetInt64();
    }

    private static int Int(JsonElement args, string key, int fallback)
    {
        return args.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
    }

    private static string Text(JsonElement args, string key, string fallback = "")
    {
        return args.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
    }

    private static bool Flag(JsonElement args, string key)
    {
        return args.TryGetProperty(key, out var value) && value.GetBoolean();
    }

    private static DateTime Date(JsonElement args, string key)
    {
        return DateTime.Parse(Text(args, key), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Page<T>(IEnumerable<T> records, JsonElement args)
    {
        var rows = records.ToList();
        var page = Math.Clamp(Int(args, "page", 1), 1, 100000);
        var size = Math.Clamp(Int(args, "size", 20), 1, 50);
        return new { total = rows.Count, page, size, records = rows.Skip((page - 1) * size).Take(size).ToArray() };
    }

    private static T Deserialize<T>(JsonElement args) where T : class
    {
        return JsonSerializer.Deserialize<T>(args.GetRawText(), JsonOptions) ?? throw new ArgumentException("参数不能为空");
    }

    /// <summary>只合并明确提交的属性，省略不覆盖；Source绑定也保留省略的租户/设备信息。</summary>
    private static T ApplyPatch<T>(T current, JsonElement patch) where T : class
    {
        var result = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();
        foreach (var property in patch.EnumerateObject())
        {
            if (property.Name == "source" && property.Value.ValueKind == JsonValueKind.Object && result["source"] is JsonObject source)
            {
                foreach (var part in property.Value.EnumerateObject())
                    source[part.Name] = JsonNode.Parse(part.Value.GetRawText());
            }
            else
            {
                result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        return result.Deserialize<T>(JsonOptions) ?? throw new ArgumentException("补丁无效");
    }
}
