using SqlSugar;

namespace Model.Agent;

[SugarTable("AgentDefinition")]
public class AgentDefinitionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? SystemPrompt { get; set; }

    public bool Enabled { get; set; } = true;
    public int MaxToolCalls { get; set; } = 4;

    [SugarColumn(Length = 32)]
    public string Version { get; set; } = "1.0";

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("AgentToolRegistry")]
public class AgentToolRegistryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    [SugarColumn(Length = 96)]
    public string ToolName { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Title { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(Length = 16)]
    public string Method { get; set; } = "POST";

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Route { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string InputSchema { get; set; } = "{\"type\":\"object\"}";

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string OutputSchema { get; set; } = "{\"type\":\"object\"}";

    public bool IsWriteAction { get; set; }
    public bool RequiresConfirmation { get; set; }
    public bool Enabled { get; set; } = true;

    [SugarColumn(Length = 96, IsNullable = true)]
    public string? PermissionKey { get; set; }

    [SugarColumn(Length = 32)]
    public string RiskLevel { get; set; } = "Low";

    [SugarColumn(Length = 128)]
    public string ServiceName { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string ActionName { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Examples { get; set; }

    [SugarColumn(Length = 32)]
    public string Version { get; set; } = "1.0";

    public int Sort { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("AgentExecutionPlan")]
public class AgentExecutionPlanEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public int ToolId { get; set; }

    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    [SugarColumn(Length = 96)]
    public string ToolName { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string ToolTitle { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserId { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ArgumentsJson { get; set; } = "{}";

    [SugarColumn(Length = 64)]
    public string ArgumentsHash { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ResultJson { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(Length = 32)]
    public string Status { get; set; } = "Created";

    public bool IsWriteAction { get; set; }
    public bool RequiresConfirmation { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ConfirmedBy { get; set; }

    public DateTime? ConfirmTime { get; set; } = DateTime.Now;

    [SugarColumn(Length = 64)]
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; } = DateTime.Now;
}

[SugarTable("AgentInvocationLog")]
public class AgentInvocationLogEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    public int? ExecutionPlanId { get; set; }
    public int? ConversationId { get; set; }
    public int? ToolId { get; set; }

    [SugarColumn(Length = 64)]
    public string AgentCode { get; set; } = string.Empty;

    [SugarColumn(Length = 96)]
    public string ToolName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? UserId { get; set; }

    [SugarColumn(Length = 32)]
    public string Stage { get; set; } = "Dispatch";

    [SugarColumn(Length = 32)]
    public string Status { get; set; } = "Success";

    [SugarColumn(Length = 16)]
    public string Method { get; set; } = "POST";

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Route { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ServiceName { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ActionName { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? RequestJson { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ResponseJson { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ErrorMessage { get; set; }

    [SugarColumn(Length = 64)]
    public string CorrelationId { get; set; } = string.Empty;

    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public long DurationMs { get; set; }
}
