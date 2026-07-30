using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interface;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 边缘节点调用中心端 HTTP API 的实现，统一附加设备 API Key 并解析通用响应。
/// </summary>
public class CenterApiClient : ICenterApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public CenterApiClient(EdgeGatewayBootstrapOptions bootstrapOptions)
    {
        _bootstrapOptions = bootstrapOptions;
    }

    /// <summary>批量上传本地队列消息，并把业务响应转换为简单成功结果。</summary>
    public async Task<(bool Success, int StatusCode, string? Error)> UploadAsync(
        IReadOnlyCollection<QueueMessage> messages, CancellationToken cancellationToken)
    {
        try
        {
            var parsedMessages = messages.Select(x => JsonSerializer.Deserialize<JsonElement>(x.PayloadJson, JsonOptions)).ToList();
            using var request = CreateRequest(HttpMethod.Post, "api/EdgeGateway/messages");
            request.Content = JsonContent.Create(
                new { DeviceId = _bootstrapOptions.DeviceId, Messages = parsedMessages },
                options: JsonOptions);
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return (false, (int)response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            var code = GetProperty(document.RootElement, "code");
            return code.ValueKind == JsonValueKind.Number && code.GetInt32() == 200
                ? (true, (int)response.StatusCode, null)
                : (false, (int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>上报心跳并读取中心端的连接、采集配置和规则版本。</summary>
    public async Task<CenterVersions> SendHeartbeatAsync(object heartbeat, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/EdgeGateway/heartbeat");
        request.Content = JsonContent.Create(heartbeat, options: JsonOptions);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var data = GetProperty(document.RootElement, "data");
        return new CenterVersions(
            ReadBoolean(data, "enabled", true),
            ReadInt(data, "configVersion"),
            ReadInt(data, "ruleVersion"),
            ReadInt(data, "sourceVersion"));
    }

    /// <summary>获取当前设备的完整生效配置，供 Worker 执行热更新。</summary>
    public async Task<EffectiveEdgeConfiguration> GetEffectiveConfigurationAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get,
            $"api/EdgeGateway/device-config/{Uri.EscapeDataString(_bootstrapOptions.DeviceId)}/effective");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var data = GetProperty(document.RootElement, "data");
        return new EffectiveEdgeConfiguration(
            ReadBoolean(data, "enabled", true),
            ReadInt(data, "configVersion"),
            ReadString(data, "configJson") ?? "{}",
            ReadInt(data, "ruleVersion"),
            ReadString(data, "rulesJson") ?? "[]",
            ReadInt(data, "sourceVersion"),
            ReadSources(data));
    }

    /// <summary>向中心端上报一条结构化运行事件。</summary>
    public async Task ReportEventAsync(string level, string eventType, string message, object? detail,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/EdgeGateway/events");
        request.Content = JsonContent.Create(new
        {
            DeviceCode = _bootstrapOptions.DeviceId,
            Level = level,
            EventType = eventType,
            Message = message,
            DetailJson = detail == null ? null : JsonSerializer.Serialize(detail, JsonOptions)
        }, options: JsonOptions);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>创建中心端请求，并在配置了 ApiKey 时附加机器认证请求头。</summary>
    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(_bootstrapOptions.CenterBaseUrl))
            throw new InvalidOperationException("CenterBaseUrl 未配置");
        var request = new HttpRequestMessage(method,
            new Uri(new Uri(_bootstrapOptions.CenterBaseUrl.TrimEnd('/') + "/"), relativePath));
        if (!string.IsNullOrWhiteSpace(_bootstrapOptions.ApiKey))
            request.Headers.TryAddWithoutValidation("X-Edge-Api-Key", _bootstrapOptions.ApiKey);
        return request;
    }

    /// <summary>忽略属性名大小写读取 JSON 字段。</summary>
    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.EnumerateObject().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>安全读取 JSON 整数字段，缺失或类型不符时返回 0。</summary>
    private static int ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    }

    /// <summary>安全读取 JSON 布尔字段，缺失时使用兼容默认值。</summary>
    private static bool ReadBoolean(JsonElement element, string name, bool defaultValue)
    {
        if (element.ValueKind != JsonValueKind.Object) return defaultValue;
        var value = GetProperty(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
    }

    /// <summary>安全读取 JSON 文本字段。</summary>
    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>反序列化中心端下发的数据源列表。</summary>
    private static IReadOnlyCollection<SourceOptions> ReadSources(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Array.Empty<SourceOptions>();
        var value = GetProperty(element, "sources");
        return value.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<SourceOptions>>(value.GetRawText(), JsonOptions) ?? new List<SourceOptions>()
            : Array.Empty<SourceOptions>();
    }
}
