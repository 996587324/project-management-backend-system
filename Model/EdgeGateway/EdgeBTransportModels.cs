namespace Model.EdgeGateway;

/// <summary>
/// B 系统协议发送器的统一结果，供队列服务记录目标、状态码、返回内容和失败原因。
/// </summary>
public record EdgeBTransportResult(
    bool Success,
    string Target,
    int? StatusCode = null,
    string? ErrorMessage = null,
    string? ResponseContent = null);
