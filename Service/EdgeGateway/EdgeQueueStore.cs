using AutoMapper;
using System.Text.Json;
using Interface;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 边缘节点 SQLite 存储实现，提供可靠消息队列、事件、上传记录和配置快照持久化。
/// </summary>
public class EdgeQueueStore : IEdgeQueueStore
{
    private readonly IEdgeLocalDatabaseService _localDatabase;
    private readonly EdgeGatewayBootstrapOptions _bootstrapOptions;
    private readonly IMapper _mapper;

    /// <summary>注入单例本地数据库服务、启动参数和 AutoMapper。</summary>
    public EdgeQueueStore(
        IEdgeLocalDatabaseService localDatabase,
        EdgeGatewayBootstrapOptions bootstrapOptions,
        IMapper mapper)
    {
        _localDatabase = localDatabase;
        _bootstrapOptions = bootstrapOptions;
        _mapper = mapper;
    }

    /// <summary>委托单例数据库服务创建 SQLite 目录、固定连接、表和索引。</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        _localDatabase.InitializeAsync(cancellationToken);

    /// <summary>以 MessageId 去重后写入本地 Pending 队列。</summary>
    public Task<bool> EnqueueAsync(StandardMessage message, CancellationToken cancellationToken) =>
        _localDatabase.InsertMessageIfAbsentAsync(
            _mapper.Map<EdgeLocalMessageQueueEntity>(message),
            cancellationToken);

    /// <summary>按创建时间读取到期的 Pending 或 Failed 消息。</summary>
    public async Task<List<QueueMessage>> GetDueMessagesAsync(int limit, CancellationToken cancellationToken)
    {
        var records = await _localDatabase.GetDueMessagesAsync(
            DateTime.UtcNow,
            limit,
            cancellationToken);
        return _mapper.Map<List<QueueMessage>>(records);
    }

    /// <summary>标记消息上传成功并写入上传记录。</summary>
    public Task MarkUploadedAsync(QueueMessage message, int statusCode, CancellationToken cancellationToken) =>
        CompleteAttemptAsync(message, true, statusCode, null, cancellationToken);

    /// <summary>标记消息上传失败并安排指数退避重试。</summary>
    public Task MarkFailedAsync(QueueMessage message, string error, int? statusCode,
        CancellationToken cancellationToken) =>
        CompleteAttemptAsync(message, false, statusCode, error, cancellationToken);

    /// <summary>统计本地消息队列状态。</summary>
    public Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
        _localDatabase.GetQueueStatisticsAsync(cancellationToken);

    /// <summary>构造本地事件实体后调用单例数据库服务写入。</summary>
    public Task RecordEventAsync(
        string level,
        string eventType,
        string message,
        object? detail,
        CancellationToken cancellationToken) =>
        _localDatabase.InsertSystemEventAsync(
            new EdgeLocalSystemEventEntity
            {
                Level = level,
                EventType = eventType,
                Message = message,
                DetailJson = detail == null ? null : JsonSerializer.Serialize(detail),
                CreateTime = DateTime.UtcNow
            },
            cancellationToken);

    /// <summary>
    /// 在一个事务中保存中心端已生效的连接、映射和规则，避免部分配置更新。
    /// </summary>
    public async Task SaveEffectiveConfigurationAsync(EffectiveEdgeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var mappings = ParseMappings(configuration.ConfigJson);
        var rules = ParseRules(configuration.RulesJson);
        var deviceConfig = configuration.ConfigVersion > 0
            ? _mapper.Map<EdgeLocalDeviceConfigEntity>(configuration)
            : null;
        if (deviceConfig != null) deviceConfig.DeviceId = _bootstrapOptions.DeviceId;
        var ruleVersion = configuration.RuleVersion > 0
            ? _mapper.Map<EdgeLocalRuleVersionEntity>(configuration)
            : null;

        await _localDatabase.SaveEffectiveConfigurationAsync(
            new EdgeLocalConfigurationWriteModel
            {
                DeviceId = _bootstrapOptions.DeviceId,
                SourceVersion = configuration.SourceVersion,
                ConfigVersion = configuration.ConfigVersion,
                RuleVersion = configuration.RuleVersion,
                Sources = _mapper.Map<List<EdgeLocalSourceConfigEntity>>(configuration.Sources),
                DeviceConfig = deviceConfig,
                Mappings = mappings,
                RuleVersionSnapshot = ruleVersion,
                Rules = rules
            },
            cancellationToken);
    }

    /// <summary>完成一次上传尝试，并调用单例数据库服务原子更新队列与上传记录。</summary>
    private Task CompleteAttemptAsync(
        QueueMessage message,
        bool success,
        int? statusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var uploadRecord = _mapper.Map<EdgeLocalUploadRecordEntity>(message);
        uploadRecord.Success = success;
        uploadRecord.StatusCode = statusCode;
        uploadRecord.ErrorMessage = error;
        uploadRecord.CreateTime = now;
        var retryCount = success ? message.RetryCount : message.RetryCount + 1;

        return _localDatabase.CompleteUploadAttemptAsync(
            new EdgeLocalUploadAttemptWriteModel
            {
                QueueId = message.Id,
                Success = success,
                RetryCount = retryCount,
                AttemptTime = now,
                NextRetryTime = success
                    ? null
                    : now.AddSeconds(GetRetryDelay(retryCount)),
                ErrorMessage = error,
                UploadRecord = uploadRecord
            },
            cancellationToken);
    }

    /// <summary>计算指数退避秒数，并限制最大等待时间为 300 秒。</summary>
    private int GetRetryDelay(int retryCount) =>
        Math.Min(
            300,
            Math.Max(_bootstrapOptions.RetryBaseSeconds, 1) *
            (int)Math.Pow(2, Math.Min(retryCount, 6)));

    /// <summary>从采集配置 JSON 中拆出字段映射，供本地排查和恢复使用。</summary>
    private static List<EdgeLocalMappingRuleEntity> ParseMappings(string configJson)
    {
        var result = new List<EdgeLocalMappingRuleEntity>();
        using var document = JsonDocument.Parse(configJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
        var property = document.RootElement.EnumerateObject()
            .FirstOrDefault(x => x.Name.Equals("mappings", StringComparison.OrdinalIgnoreCase));
        if (property.Value.ValueKind != JsonValueKind.Array) return result;
        foreach (var mapping in property.Value.EnumerateArray())
        {
            var sourceField = ReadString(mapping, "sourceField") ?? string.Empty;
            var targetField = ReadString(mapping, "targetField") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceField) || string.IsNullOrWhiteSpace(targetField)) continue;
            result.Add(new EdgeLocalMappingRuleEntity
            {
                SourceId = ReadString(mapping, "sourceId") ?? string.Empty,
                SourceField = sourceField,
                TargetField = targetField,
                RuleJson = mapping.GetRawText(),
                UpdateTime = DateTime.UtcNow
            });
        }
        return result;
    }

    /// <summary>从规则快照 JSON 中拆出当前生效规则。</summary>
    private static List<EdgeLocalRuleEntity> ParseRules(string rulesJson)
    {
        var result = new List<EdgeLocalRuleEntity>();
        using var document = JsonDocument.Parse(rulesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var rule in document.RootElement.EnumerateArray())
        {
            var ruleCode = ReadString(rule, "ruleCode") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ruleCode)) continue;
            result.Add(new EdgeLocalRuleEntity
            {
                RuleCode = ruleCode,
                RuleType = ReadString(rule, "ruleType") ?? "Filter",
                RuleJson = ReadString(rule, "ruleJson") ?? "{}",
                Enabled = ReadBoolean(rule, "enabled", true),
                UpdateTime = DateTime.UtcNow
            });
        }
        return result;
    }

    /// <summary>忽略属性名大小写读取 JSON 文本字段。</summary>
    private static string? ReadString(JsonElement element, string name)
    {
        var property = element.EnumerateObject()
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
    }

    /// <summary>忽略属性名大小写读取 JSON 布尔字段。</summary>
    private static bool ReadBoolean(JsonElement element, string name, bool fallback)
    {
        var property = element.EnumerateObject()
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.Value.GetBoolean()
            : fallback;
    }
}
