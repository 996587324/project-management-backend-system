using System.Globalization;
using System.Text.Json;

namespace Service;

/// <summary>
/// Agent 工具参数 JSON Schema 校验器，用于在执行工具前检查模型生成的参数是否合法。
/// </summary>
internal static class AgentJsonSchemaValidator
{
    /// <summary>
    /// 校验工具参数 JSON 是否符合工具注册表中的输入 Schema，返回所有校验错误。
    /// </summary>
    public static List<string> Validate(string schemaJson, string argumentsJson)
    {
        var errors = new List<string>();
        try
        {
            using var schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
            using var value = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            ValidateElement(schema.RootElement, value.RootElement, "$", errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"JSON 格式不正确：{ex.Message}");
        }

        return errors;
    }

    private static void ValidateElement(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            var type = typeElement.GetString();
            if (!MatchesType(type, value))
            {
                errors.Add($"{path} 应为 {ToChineseType(type)}");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
        {
            var raw = value.GetRawText();
            if (!enumElement.EnumerateArray().Any(x => x.GetRawText() == raw))
                errors.Add($"{path} 不在允许值范围内");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, value, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, value, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(schema, value.GetString() ?? string.Empty, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, value, path, errors);
                break;
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var properties = schema.TryGetProperty("properties", out var propertiesElement) &&
                         propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement
            : default;

        if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var required in requiredElement.EnumerateArray())
            {
                var name = required.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                    errors.Add($"{path}.{name} 为必填字段");
            }
        }

        var disallowAdditional = schema.TryGetProperty("additionalProperties", out var additional) &&
                                 additional.ValueKind == JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateElement(propertySchema, property.Value, $"{path}.{property.Name}", errors);
            }
            else if (disallowAdditional)
            {
                errors.Add($"{path}.{property.Name} 不是允许的字段");
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var items = value.EnumerateArray().ToList();
        if (schema.TryGetProperty("minItems", out var minItems) &&
            minItems.TryGetInt32(out var min) && items.Count < min)
        {
            errors.Add($"{path} 至少需要 {min} 项");
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) &&
            maxItems.TryGetInt32(out var max) && items.Count > max)
        {
            errors.Add($"{path} 最多允许 {max} 项");
        }

        if (!schema.TryGetProperty("items", out var itemSchema)) return;
        for (var index = 0; index < items.Count; index++)
            ValidateElement(itemSchema, items[index], $"{path}[{index}]", errors);
    }

    private static void ValidateString(JsonElement schema, string value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minLength", out var minLength) &&
            minLength.TryGetInt32(out var min) && value.Length < min)
        {
            errors.Add($"{path} 长度不能小于 {min}");
        }

        if (schema.TryGetProperty("maxLength", out var maxLength) &&
            maxLength.TryGetInt32(out var max) && value.Length > max)
        {
            errors.Add($"{path} 长度不能大于 {max}");
        }

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "date" &&
            !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{path} 应为有效日期，例如 2026-07-20");
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (!value.TryGetDecimal(out var number)) return;
        if (schema.TryGetProperty("minimum", out var minimum) &&
            minimum.TryGetDecimal(out var min) && number < min)
        {
            errors.Add($"{path} 不能小于 {min}");
        }

        if (schema.TryGetProperty("maximum", out var maximum) &&
            maximum.TryGetDecimal(out var max) && number > max)
        {
            errors.Add($"{path} 不能大于 {max}");
        }
    }

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string ToChineseType(string? type) => type switch
    {
        "object" => "对象",
        "array" => "数组",
        "string" => "字符串",
        "integer" => "整数",
        "number" => "数字",
        "boolean" => "布尔值",
        _ => type ?? "有效值"
    };
}
