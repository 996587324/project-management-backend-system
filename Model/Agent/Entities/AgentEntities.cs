using SqlSugar;

namespace Model.Agent;

[SugarTable("AgentConversation")]
public class AgentConversationEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 64)]
    public string UserId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, DefaultValue = "project-management")]
    public string AgentCode { get; set; } = "project-management";
    [SugarColumn(Length = 128)]
    public string Title { get; set; } = string.Empty;
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ScopeProjectName { get; set; }
    [SugarColumn(Length = 32)]
    public string Mode { get; set; } = "Online";
    [SugarColumn(Length = 32)]
    public string Status { get; set; } = "Active";
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("AgentMessage")]
public class AgentMessageEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int ConversationId { get; set; }
    [SugarColumn(Length = 32)]
    public string Role { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Content { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
