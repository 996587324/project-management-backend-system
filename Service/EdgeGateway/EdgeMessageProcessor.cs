using AutoMapper;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 边缘报文处理器，负责大小限制、JSON 解析、字段映射、规则执行和标准消息生成。
/// </summary>
public class EdgeMessageProcessor : IEdgeMessageProcessor
{
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly IMapper _mapper;
    private IReadOnlyList<FieldMappingOptions> _mappings;
    private IReadOnlyList<EdgeRuleOptions> _rules;
    private readonly ConcurrentDictionary<string, decimal> _lastNumericValues = new();
    private readonly ConcurrentDictionary<string, (string Hash, DateTime Time)> _deduplicationState = new();
    private readonly ConcurrentDictionary<string, AggregationState> _aggregationStates = new();

    public EdgeMessageProcessor(EdgeGatewayBootstrapOptions bootstrapOptions, IMapper mapper)
    {
        _bootstrapOptions = bootstrapOptions;
        _mapper = mapper;
        // 映射和规则在 Worker 启动后从中心数据表加载，启动参数不再提供业务配置。
        _mappings = new List<FieldMappingOptions>();
        _rules = new List<EdgeRuleOptions>();
    }

    /// <summary>用中心端发布快照原子替换当前内存中的字段映射和规则集合。</summary>
    public void ApplyConfiguration(string configJson, string rulesJson)
    {
        var runtimeConfig = JsonSerializer.Deserialize<RuntimeProcessingConfiguration>(configJson, ConfigurationJsonOptions);
        var mappings = runtimeConfig?.Mappings ?? new List<FieldMappingOptions>();
        var rules = ParsePublishedRules(rulesJson);
        if (rules.Count == 0 && runtimeConfig?.Rules != null) rules = runtimeConfig.Rules;
        _mappings = mappings;
        _rules = rules;
    }

    /// <summary>
    /// 处理一条原始报文。格式错误、超限或命中 Drop 规则时返回丢弃原因。
    /// </summary>
    public ProcessResult Process(SourceEnvelope envelope)
    {
        if (Encoding.UTF8.GetByteCount(envelope.RawPayload) > _bootstrapOptions.MaxPayloadBytes)
            return ProcessResult.Dropped("PayloadTooLarge");

        JsonObject sourceObject;
        try
        {
            sourceObject = JsonNode.Parse(envelope.RawPayload)?.AsObject()
                ?? throw new JsonException("JSON 根节点不是对象");
        }
        catch (Exception)
        {
            return ProcessResult.Dropped("InvalidJson");
        }

        var values = BuildValues(envelope.SourceId, sourceObject);
        var ruleScopeId = string.IsNullOrWhiteSpace(envelope.SourceDeviceCode)
            ? envelope.SourceId
            : $"{envelope.SourceId}:{envelope.SourceDeviceCode}";
        foreach (var rule in _rules.Where(x => x.Enabled).OrderBy(x => x.Code))
        {
            // 同一连接可推送多台业务设备，去重、聚合等有状态规则必须按设备隔离。
            var reason = EvaluateRule(ruleScopeId, values, rule);
            if (reason != null) return ProcessResult.Dropped(reason);
        }

        var timestamp = TryReadTimestamp(sourceObject) ?? envelope.ReceivedAt;
        var message = _mapper.Map<StandardMessage>(envelope);
        message.DeviceId = _bootstrapOptions.DeviceId;
        message.Timestamp = timestamp.ToUniversalTime();
        message.Values = values;
        return ProcessResult.Accepted(message);
    }

    /// <summary>按当前数据源匹配字段映射，并完成枚举转换、倍率和偏移计算。</summary>
    private Dictionary<string, JsonNode?> BuildValues(string sourceId, JsonObject source)
    {
        var mappings = _mappings.Where(x =>
            string.IsNullOrWhiteSpace(x.SourceId) || x.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mappings.Count == 0)
        {
            return source.Where(x => !x.Key.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => CloneNode(x.Value));
        }

        var values = new Dictionary<string, JsonNode?>();
        foreach (var mapping in mappings)
        {
            var value = ResolvePath(source, mapping.SourceField);
            if (value == null) continue;
            var rawText = value.ToString();
            if (mapping.EnumMap.TryGetValue(rawText, out var mappedText))
            {
                values[mapping.TargetField] = JsonValue.Create(mappedText);
                continue;
            }
            if (TryGetDecimal(value, out var number))
            {
                values[mapping.TargetField] = JsonValue.Create(number * mapping.Factor + mapping.Offset);
                continue;
            }
            values[mapping.TargetField] = CloneNode(value);
        }
        return values;
    }

    /// <summary>执行单条边缘规则，返回非空文本表示消息应被丢弃。</summary>
    private string? EvaluateRule(string sourceId, Dictionary<string, JsonNode?> values, EdgeRuleOptions rule)
    {
        if (rule.Type.Equals("Deduplicate", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(values);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            var key = $"{sourceId}:{rule.Code}";
            var now = DateTime.UtcNow;
            if (_deduplicationState.TryGetValue(key, out var previous) && previous.Hash == hash &&
                now - previous.Time < TimeSpan.FromSeconds(Math.Max(rule.WindowSeconds, 1)))
                return rule.Action.Equals("Drop", StringComparison.OrdinalIgnoreCase) ? $"Rule:{rule.Code}" : null;
            _deduplicationState[key] = (hash, now);
            return null;
        }

        if (!values.TryGetValue(rule.Field, out var value) || value == null) return null;
        if (rule.Type.Equals("Range", StringComparison.OrdinalIgnoreCase) && TryGetDecimal(value, out var rangeValue))
        {
            var outside = (rule.Min.HasValue && rangeValue < rule.Min.Value) ||
                          (rule.Max.HasValue && rangeValue > rule.Max.Value);
            return outside && rule.Action.Equals("Drop", StringComparison.OrdinalIgnoreCase) ? $"Rule:{rule.Code}" : null;
        }
        if (rule.Type.Equals("Threshold", StringComparison.OrdinalIgnoreCase) && rule.Threshold.HasValue &&
            TryGetDecimal(value, out var thresholdValue))
        {
            var key = $"{sourceId}:{rule.Field}";
            var hasPrevious = _lastNumericValues.TryGetValue(key, out var previous);
            _lastNumericValues[key] = thresholdValue;
            if (hasPrevious && Math.Abs(thresholdValue - previous) < rule.Threshold.Value &&
                rule.Action.Equals("Drop", StringComparison.OrdinalIgnoreCase)) return $"Rule:{rule.Code}";
            return null;
        }
        if (rule.Type.Equals("Aggregate", StringComparison.OrdinalIgnoreCase) &&
            TryGetDecimal(value, out var aggregateValue))
        {
            var key = $"{sourceId}:{rule.Code}";
            var state = _aggregationStates.GetOrAdd(key, _ => new AggregationState(DateTime.UtcNow));
            lock (state)
            {
                state.Add(aggregateValue);
                if (DateTime.UtcNow - state.StartTime < TimeSpan.FromSeconds(Math.Max(rule.WindowSeconds, 1)))
                    return $"Rule:{rule.Code}:Aggregating";
                values[rule.Field] = JsonValue.Create(state.GetValue(rule.Operator));
                state.Reset(DateTime.UtcNow);
            }
            return null;
        }
        if (rule.Type.Equals("Filter", StringComparison.OrdinalIgnoreCase))
        {
            var matched = Compare(value, rule);
            if (matched && rule.Action.Equals("Drop", StringComparison.OrdinalIgnoreCase)) return $"Rule:{rule.Code}";
        }
        return null;
    }

    /// <summary>根据规则操作符比较当前值与阈值或文本。</summary>
    private static bool Compare(JsonNode value, EdgeRuleOptions rule)
    {
        if (rule.CompareValue.HasValue && TryGetDecimal(value, out var number))
        {
            return rule.Operator.ToLowerInvariant() switch
            {
                "greaterthan" => number > rule.CompareValue.Value,
                "greaterthanorequal" => number >= rule.CompareValue.Value,
                "lessthan" => number < rule.CompareValue.Value,
                "lessthanorequal" => number <= rule.CompareValue.Value,
                "notequal" => number != rule.CompareValue.Value,
                _ => number == rule.CompareValue.Value
            };
        }
        var text = value.ToString();
        return rule.Operator.ToLowerInvariant() switch
        {
            "contains" => text.Contains(rule.CompareText ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "notequal" => !text.Equals(rule.CompareText, StringComparison.OrdinalIgnoreCase),
            _ => text.Equals(rule.CompareText, StringComparison.OrdinalIgnoreCase)
        };
    }

    /// <summary>按点分隔路径从 JSON 节点中读取嵌套字段。</summary>
    private static JsonNode? ResolvePath(JsonNode root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj) return null;
            current = obj.FirstOrDefault(x => x.Key.Equals(segment, StringComparison.OrdinalIgnoreCase)).Value;
        }
        return current;
    }

    /// <summary>尝试从原始报文 timestamp 字段读取业务时间。</summary>
    private static DateTime? TryReadTimestamp(JsonObject source)
    {
        var node = source.FirstOrDefault(x => x.Key.Equals("timestamp", StringComparison.OrdinalIgnoreCase)).Value;
        return node != null && DateTime.TryParse(node.ToString(), out var timestamp) ? timestamp : null;
    }

    /// <summary>尝试把 JSON 节点转换为十进制数值。</summary>
    private static bool TryGetDecimal(JsonNode node, out decimal value) =>
        decimal.TryParse(node.ToString(), out value);

    /// <summary>复制 JSON 节点，避免标准消息与原始解析树共享可变对象。</summary>
    private static JsonNode? CloneNode(JsonNode? node) =>
        node == null ? null : JsonNode.Parse(node.ToJsonString());

    /// <summary>把中心端发布的规则实体快照转换为边缘运行规则。</summary>
    private static List<EdgeRuleOptions> ParsePublishedRules(string rulesJson)
    {
        var result = new List<EdgeRuleOptions>();
        using var document = JsonDocument.Parse(rulesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var ruleJson = ReadString(item, "ruleJson");
            if (string.IsNullOrWhiteSpace(ruleJson)) continue;
            var rule = JsonSerializer.Deserialize<EdgeRuleOptions>(ruleJson, ConfigurationJsonOptions);
            if (rule == null) continue;
            rule.Code = ReadString(item, "ruleCode") ?? rule.Code;
            rule.Name = ReadString(item, "ruleName") ?? rule.Name;
            rule.Type = ReadString(item, "ruleType") ?? rule.Type;
            rule.Enabled = ReadBoolean(item, "enabled", rule.Enabled);
            result.Add(rule);
        }
        return result;
    }

    /// <summary>忽略属性名大小写读取 JSON 文本字段。</summary>
    private static string? ReadString(JsonElement element, string name)
    {
        var property = element.EnumerateObject().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
    }

    /// <summary>忽略属性名大小写读取 JSON 布尔字段。</summary>
    private static bool ReadBoolean(JsonElement element, string name, bool fallback)
    {
        var property = element.EnumerateObject().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.Value.GetBoolean() : fallback;
    }

    /// <summary>采集配置 JSON 在边缘节点反序列化后的运行结构。</summary>
    private sealed class RuntimeProcessingConfiguration
    {
        public List<FieldMappingOptions> Mappings { get; set; } = new();
        public List<EdgeRuleOptions> Rules { get; set; } = new();
    }

    /// <summary>单个聚合规则的内存窗口状态，进程重启后清空。</summary>
    private sealed class AggregationState
    {
        public DateTime StartTime { get; private set; }
        private decimal _sum;
        private decimal _min = decimal.MaxValue;
        private decimal _max = decimal.MinValue;
        private int _count;

        public AggregationState(DateTime startTime) => StartTime = startTime;

        public void Add(decimal value)
        {
            _sum += value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);
            _count++;
        }

        public decimal GetValue(string operation) => operation.ToLowerInvariant() switch
        {
            "sum" => _sum,
            "min" => _min,
            "max" => _max,
            "count" => _count,
            _ => _count == 0 ? 0 : _sum / _count
        };

        public void Reset(DateTime startTime)
        {
            StartTime = startTime;
            _sum = 0;
            _min = decimal.MaxValue;
            _max = decimal.MinValue;
            _count = 0;
        }
    }
}
