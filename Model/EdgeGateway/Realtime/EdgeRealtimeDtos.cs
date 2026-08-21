using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Model.EdgeGateway;

/// <summary>对方固定 dataList 外层结构中的单个动态数据块。</summary>
public class EdgeRealtimeDataItemDto
{
    /// <summary>允许任意参数名以及字符串、数字、布尔值、对象、数组或 null。</summary>
    [JsonConverter(typeof(EdgeRealtimeDynamicDataJsonConverter))]
    public Dictionary<string, object?> Data { get; set; } = new();
}

/// <summary>
/// 对方实时数据上报信封；data 内参数动态，同时保留 dataList 用于兼容已接入的旧格式。
/// </summary>
public class EdgeRealtimeDataReceiveDto
{
    /// <summary>对方设备型号标识。</summary>
    public string? ModelMark { get; set; }

    /// <summary>设备动态参数；字段和值类型均不固定，并按对方原始 JSON 类型解析。</summary>
    [JsonConverter(typeof(EdgeRealtimeDynamicDataJsonConverter))]
    public Dictionary<string, object?>? Data { get; set; }
    /// <summary>对方数据类型标识。</summary>
    public int? DataType { get; set; }

    /// <summary>对方租户标识。</summary>
    public string? TenantMark { get; set; }

    /// <summary>对方消息唯一编号，同时作为中心端请求编号。</summary>
    public string? MessageId { get; set; }

    /// <summary>对方数据产生时间，Unix 毫秒时间戳。</summary>
    public long? Time { get; set; }

    /// <summary>业务设备标识，与 tenantMark 共同确定一条最新暂存数据。</summary>
    public string? DeviceMark { get; set; }

    /// <summary>兼容旧版实时接收接口的 dataList 格式。</summary>
    public List<EdgeRealtimeDataItemDto>? DataList { get; set; }
}

/// <summary>
/// 解析动态参数时关闭 Newtonsoft 的日期猜测，避免 ISO 时间字符串在接收后被自动改写。
/// 该转换器仅标记在实时数据的 data 属性上，不改变项目其他接口的 JSON 行为。
/// </summary>
public sealed class EdgeRealtimeDynamicDataJsonConverter : JsonConverter
{
    public override bool CanWrite => false;

    public override bool CanConvert(Type objectType) =>
        objectType == typeof(Dictionary<string, object?>);

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;

        var originalDateParseHandling = reader.DateParseHandling;
        try
        {
            reader.DateParseHandling = DateParseHandling.None;
            var token = JToken.Load(reader);
            if (token.Type != JTokenType.Object)
                throw new JsonSerializationException("实时数据的 data 必须是 JSON 对象。");

            return token.ToObject<Dictionary<string, object?>>(serializer) ?? new();
        }
        finally
        {
            reader.DateParseHandling = originalDateParseHandling;
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException("实时动态数据使用 Newtonsoft 默认序列化行为。");
}

/// <summary>用于 AutoMapper 生成最新设备快照实体的标准化写入模型。</summary>
public class EdgeRealtimeSnapshotWriteModel
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public int? DataType { get; set; }
    public string? TenantMark { get; set; }
    public string? MessageId { get; set; }
    public long? SourceTimestamp { get; set; }
    public List<EdgeRealtimeDataItemDto> DataList { get; set; } = new();
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
    public string? LastRequestId { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>实时数据覆盖写入结果。</summary>
public class EdgeRealtimeReceiveResultDto
{
    public long SnapshotId { get; set; }
    public int ReceivedCount { get; set; } = 1;
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int IgnoredOlderCount { get; set; }
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
}

/// <summary>实时设备快照分页筛选条件。</summary>
public class EdgeRealtimeSnapshotQueryDto : EdgePageQueryDto
{
    public string? TenantMark { get; set; }
    public string? ModelMark { get; set; }
    public string? DeviceCode { get; set; }
    public bool? DataOnline { get; set; }
}

/// <summary>实时设备最新值查询结果。</summary>
public class EdgeRealtimeSnapshotOutputDto
{
    public long Id { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceMark { get; set; } = string.Empty;
    public string? ModelMark { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public int? DataType { get; set; }
    public string? TenantMark { get; set; }
    public string? MessageId { get; set; }
    public long? Time { get; set; }
    public List<EdgeRealtimeDataItemDto> DataList { get; set; } = new();
    public DateTime DataTime { get; set; }
    public DateTime ReceiveTime { get; set; }
    public string? LastRequestId { get; set; }
    public bool DataOnline { get; set; }
    public string DataStatus { get; set; } = EdgeRealtimeDataReception.Interrupted;
    public DateTime CreateTime { get; set; }
    public DateTime UpdateTime { get; set; }
}

/// <summary>三秒级实时数据的接收状态定义。</summary>
public static class EdgeRealtimeDataReception
{
    /// <summary>允许连续丢失约五次三秒推送，超过该窗口后显示数据中断。</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);
    public const string Receiving = "Receiving";
    public const string Interrupted = "Interrupted";
}
