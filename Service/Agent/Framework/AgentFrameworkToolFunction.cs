using System.Text.Json;
using Microsoft.Extensions.AI;
using Model.Agent;

namespace Service;

/// <summary>
/// 将本项目动态工具注册表转换为 Microsoft.Extensions.AI 可调用函数。
/// 工具执行仍统一落到 AgentToolDispatcher，因此权限、审计和确认机制保持不变。
/// </summary>
internal sealed class AgentFrameworkToolFunction : AIFunction
{
    private readonly AgentToolDefinitionDto _definition;
    private readonly AgentFrameworkExecutionContext _context;
    private readonly JsonElement _schema;

    public AgentFrameworkToolFunction(
        AgentToolDefinitionDto definition,
        AgentFrameworkExecutionContext context)
    {
        _definition = definition;
        _context = context;
        _schema = ParseSchema(definition.InputSchema);
    }

    public override string Name => _definition.Name;

    public override string Description => string.IsNullOrWhiteSpace(_definition.Description)
        ? _definition.Title
        : $"{_definition.Title}：{_definition.Description}";

    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        _context.InvokeToolAsync(_definition.Name, arguments, cancellationToken);

    private static JsonElement ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        }
    }
}
