using SqlSugar;

namespace Model.EdgeGateway;

/// <summary>边缘 SQLite 中保存的当前设备采集配置快照。</summary>
[SugarTable("device_config")]
public class EdgeLocalDeviceConfigEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "device_id", Length = 64)]
    public string DeviceId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "config_json", ColumnDataType = "TEXT")]
    public string ConfigJson { get; set; } = "{}";
    [SugarColumn(ColumnName = "version")]
    public int Version { get; set; }
    [SugarColumn(ColumnName = "update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 中保存的数据源配置快照。</summary>
[SugarTable("source_config")]
public class EdgeLocalSourceConfigEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "source_id", Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "protocol", Length = 32)]
    public string Protocol { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "config_json", ColumnDataType = "TEXT")]
    public string ConfigJson { get; set; } = "{}";
    [SugarColumn(ColumnName = "enabled")]
    public bool Enabled { get; set; } = true;
    [SugarColumn(ColumnName = "update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 中拆分保存的字段映射规则。</summary>
[SugarTable("mapping_rule")]
public class EdgeLocalMappingRuleEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "source_id", Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "source_field", Length = 128)]
    public string SourceField { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "target_field", Length = 128)]
    public string TargetField { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "rule_json", ColumnDataType = "TEXT")]
    public string RuleJson { get; set; } = "{}";
    [SugarColumn(ColumnName = "update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 中拆分保存的当前生效规则。</summary>
[SugarTable("edge_rule")]
public class EdgeLocalRuleEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "rule_code", Length = 64)]
    public string RuleCode { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "rule_type", Length = 32)]
    public string RuleType { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "rule_json", ColumnDataType = "TEXT")]
    public string RuleJson { get; set; } = "{}";
    [SugarColumn(ColumnName = "enabled")]
    public bool Enabled { get; set; } = true;
    [SugarColumn(ColumnName = "update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 中保存的规则版本快照。</summary>
[SugarTable("rule_version")]
public class EdgeLocalRuleVersionEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "version")]
    public int Version { get; set; }
    [SugarColumn(ColumnName = "snapshot_json", ColumnDataType = "TEXT")]
    public string SnapshotJson { get; set; } = "[]";
    [SugarColumn(ColumnName = "create_time")]
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 可靠上传队列。数据库文件按天拆分，表名保持不变。</summary>
[SugarTable("message_queue")]
public class EdgeLocalMessageQueueEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "message_id", Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "device_id", Length = 64)]
    public string DeviceId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "source_id", Length = 64)]
    public string SourceId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "payload_json", ColumnDataType = "TEXT")]
    public string PayloadJson { get; set; } = "{}";
    [SugarColumn(ColumnName = "status", Length = 24)]
    public string Status { get; set; } = "Pending";
    [SugarColumn(ColumnName = "retry_count")]
    public int RetryCount { get; set; }
    [SugarColumn(ColumnName = "next_retry_time", IsNullable = true)]
    public DateTime? NextRetryTime { get; set; }
    [SugarColumn(ColumnName = "last_error", ColumnDataType = "TEXT", IsNullable = true)]
    public string? LastError { get; set; }
    [SugarColumn(ColumnName = "create_time")]
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
    [SugarColumn(ColumnName = "upload_time", IsNullable = true)]
    public DateTime? UploadTime { get; set; }
}

/// <summary>记录某个本地上传队列日表是否已全部上传，已完成的日表不再进入读取范围。</summary>
[SugarTable("queue_day_status")]
public class EdgeLocalQueueDayStatusEntity
{
    [SugarColumn(ColumnName = "day_key", IsPrimaryKey = true, Length = 8)]
    public string DayKey { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "upload_completed")]
    public bool UploadCompleted { get; set; }
    [SugarColumn(ColumnName = "completed_time", IsNullable = true)]
    public DateTime? CompletedTime { get; set; }
    [SugarColumn(ColumnName = "update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>本地结构迁移标记，避免旧单表数据在服务重启后重复迁移。</summary>
[SugarTable("local_storage_migration")]
public class EdgeLocalStorageMigrationEntity
{
    [SugarColumn(ColumnName = "migration_key", IsPrimaryKey = true, Length = 64)]
    public string MigrationKey { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "completed_time")]
    public DateTime CompletedTime { get; set; } = DateTime.UtcNow;
}


/// <summary>边缘 SQLite 中每次中心上传尝试的结果记录。</summary>
[SugarTable("upload_record")]
public class EdgeLocalUploadRecordEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "message_id", Length = 64)]
    public string MessageId { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "success")]
    public bool Success { get; set; }
    [SugarColumn(ColumnName = "status_code", IsNullable = true)]
    public int? StatusCode { get; set; }
    [SugarColumn(ColumnName = "error_message", ColumnDataType = "TEXT", IsNullable = true)]
    public string? ErrorMessage { get; set; }
    [SugarColumn(ColumnName = "create_time")]
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>边缘 SQLite 中的本地运行事件。</summary>
[SugarTable("system_event")]
public class EdgeLocalSystemEventEntity
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(ColumnName = "level", Length = 24)]
    public string Level { get; set; } = "Info";
    [SugarColumn(ColumnName = "event_type", Length = 64)]
    public string EventType { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "message", ColumnDataType = "TEXT")]
    public string Message { get; set; } = string.Empty;
    [SugarColumn(ColumnName = "detail_json", ColumnDataType = "TEXT", IsNullable = true)]
    public string? DetailJson { get; set; }
    [SugarColumn(ColumnName = "create_time")]
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
}
