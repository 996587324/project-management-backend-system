namespace Model.EdgeGateway;

/// <summary>
/// 中心生效配置写入边缘 SQLite 时使用的完整实体集合。
/// </summary>
public class EdgeLocalConfigurationWriteModel
{
    public string DeviceId { get; set; } = string.Empty;
    public int SourceVersion { get; set; }
    public int ConfigVersion { get; set; }
    public int RuleVersion { get; set; }
    public List<EdgeLocalSourceConfigEntity> Sources { get; set; } = new();
    public EdgeLocalDeviceConfigEntity? DeviceConfig { get; set; }
    public List<EdgeLocalMappingRuleEntity> Mappings { get; set; } = new();
    public EdgeLocalRuleVersionEntity? RuleVersionSnapshot { get; set; }
    public List<EdgeLocalRuleEntity> Rules { get; set; } = new();
}

/// <summary>
/// 一次中心上传完成后，需要在同一 SQLite 事务中更新的队列状态和上传记录。
/// </summary>
public class EdgeLocalUploadAttemptWriteModel
{
    public int QueueId { get; set; }
    public bool Success { get; set; }
    public int RetryCount { get; set; }
    public DateTime AttemptTime { get; set; }
    public DateTime? NextRetryTime { get; set; }
    public string? ErrorMessage { get; set; }
    public EdgeLocalUploadRecordEntity UploadRecord { get; set; } = new();
}
