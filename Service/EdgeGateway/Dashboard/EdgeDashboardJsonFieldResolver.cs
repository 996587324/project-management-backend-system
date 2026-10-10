using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Service;

/// <summary>使用 JSON Pointer 安全读取完整 HTTP 报文，避免动态字段名称中的点号被误解析。</summary>
internal static class EdgeDashboardJsonFieldResolver
{
    /// <summary>把实时快照数据库列重新组合成配置界面看到的完整报文。</summary>
    public static JObject BuildEnvelope(EdgeRealtimeDeviceSnapshotEntity snapshot)
    {
        var dataList = JsonConvert.DeserializeObject<List<EdgeRealtimeDataItemDto>>(
            snapshot.DataListJson) ?? [];
        var data = dataList.FirstOrDefault()?.Data ?? [];
        return BuildEnvelope(
            snapshot.DeviceCode,
            snapshot.ModelMark,
            snapshot.DataType,
            snapshot.TenantMark,
            snapshot.MessageId,
            snapshot.SourceTimestamp,
            data);
    }

    /// <summary>把新接收样本组合成与原始 HTTP 请求一致的字段树。</summary>
    public static JObject BuildEnvelope(EdgeDashboardTelemetrySampleDto sample) =>
        BuildEnvelope(
            sample.DeviceMark,
            sample.ModelMark,
            sample.DataType,
            sample.TenantMark,
            sample.MessageId,
            sample.SourceTimestamp,
            sample.Data);

    /// <summary>只包含本次明确上报的数据，用于追溯触发和历史字段目录的最后出现时间。</summary>
    public static JObject BuildReportedEnvelope(EdgeDashboardTelemetrySampleDto sample) =>
        BuildEnvelope(
            sample.DeviceMark,
            sample.ModelMark,
            sample.DataType,
            sample.TenantMark,
            sample.MessageId,
            sample.SourceTimestamp,
            sample.ReportedData ?? sample.Data);

    /// <summary>解析 JSON Pointer；不存在时返回 null。</summary>
    public static JToken? Resolve(JObject envelope, string sourcePath)
    {
        var value = ResolveIncludingNull(envelope, sourcePath);
        return value?.Type == JTokenType.Null ? null : value;
    }

    /// <summary>明确 null（包括父对象被清空）返回 JSON null；从未出现的字段才返回 CLR null。</summary>
    public static JToken? ResolveIncludingNull(JObject envelope, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath == "/") return envelope;
        if (!sourcePath.StartsWith('/')) return null;

        JToken current = envelope;
        foreach (var encodedSegment in sourcePath.Split('/').Skip(1))
        {
            if (current.Type == JTokenType.Null) return current;
            var segment = encodedSegment.Replace("~1", "/").Replace("~0", "~");
            if (current is JObject obj)
            {
                var property = obj.Properties()
                    .FirstOrDefault(x => string.Equals(x.Name, segment, StringComparison.Ordinal));
                if (property == null) return null;
                current = property.Value;
                continue;
            }

            if (current is JArray array && int.TryParse(segment, out var index) &&
                index >= 0 && index < array.Count)
            {
                current = array[index];
                continue;
            }

            // 明确替换数组或把对象改为标量时，原子字段被清空；不是未上报。
            if (current is JArray || current is JValue) return JValue.CreateNull();

            return null;
        }
        return current;
    }

    /// <summary>递归展开完整报文中的叶子字段供前端树形选择。</summary>
    public static List<EdgeDashboardSourceFieldOptionDto> Flatten(JObject envelope)
    {
        var result = new List<EdgeDashboardSourceFieldOptionDto>();
        FlattenToken(envelope, string.Empty, result);
        return result;
    }

    /// <summary>把 JSON 值转换为响应中保持类型的 CLR 值。</summary>
    public static object? ToObject(JToken? token) => token switch
    {
        null => null,
        JValue value => value.Value,
        _ => token.ToObject<object>()
    };

    /// <summary>生成适合故障审计保存的紧凑原值。</summary>
    public static string? ToCompactText(JToken? token) => token switch
    {
        null => null,
        JValue value => Convert.ToString(value.Value),
        _ => token.ToString(Formatting.None)
    };

    private static JObject BuildEnvelope(
        string deviceMark,
        string? modelMark,
        int? dataType,
        string? tenantMark,
        string? messageId,
        long? sourceTimestamp,
        Dictionary<string, object?> data) =>
        JObject.FromObject(new
        {
            modelMark,
            data,
            dataType,
            tenantMark,
            messageId,
            time = sourceTimestamp,
            deviceMark
        }, JsonSerializer.Create(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Include
        }));

    private static void FlattenToken(
        JToken token,
        string path,
        ICollection<EdgeDashboardSourceFieldOptionDto> result)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                var segment = property.Name.Replace("~", "~0").Replace("/", "~1");
                FlattenToken(property.Value, $"{path}/{segment}", result);
            }
            return;
        }

        if (token is JArray array)
        {
            for (var index = 0; index < array.Count; index++)
                FlattenToken(array[index], $"{path}/{index}", result);
            return;
        }

        result.Add(new EdgeDashboardSourceFieldOptionDto
        {
            SourcePath = path,
            FieldName = path.Split('/').LastOrDefault() ?? path,
            ValueType = token.Type switch
            {
                JTokenType.Integer => "Integer",
                JTokenType.Float => "Number",
                JTokenType.Boolean => "Boolean",
                JTokenType.Date => "DateTime",
                JTokenType.Null => "Null",
                _ => "String"
            },
            CurrentValue = ToObject(token)
        });
    }
}
