using SqlSugar;

namespace Model.WorkTasks;

public abstract class WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", IsPrimaryKey = true, Length = 64)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [SugarColumn(ColumnDataType = "nvarchar", Length = 8)] public string AppCode { get; set; } = "A";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[SugarTable("work_group")]
public sealed class WorkGroup : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 120)] public string Name { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? ParentId { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? LeaderId { get; set; }
    public bool Enabled { get; set; } = true;
}
[SugarTable("work_group_member")]
public sealed class WorkMember : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string GroupId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string UserId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
[SugarTable("work_role_grant")]
public sealed class WorkGrant : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string UserId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 32)] public string Role { get; set; } = "worker";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Scope { get; set; } = "Self";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? TargetId { get; set; }
    public bool Enabled { get; set; } = true;
    [SugarColumn(IsNullable = true)] public DateTime? ExpiresAtUtc { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string GrantedBy { get; set; } = "";
}
[SugarTable("work_task")]
public sealed class WorkTask : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 160)] public string Title { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Requirements { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string AcceptanceCriteria { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string MaterialRule { get; set; } = "TextOrFile";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Priority { get; set; } = "Normal";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 32)] public string Status { get; set; } = "Draft";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string GroupId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string CreatedBy { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string DispatcherId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string AssigneeId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ReviewerId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string SummaryLeaderId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RevisionId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? PlanId { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? RequestedDueAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? OriginalDueAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CommittedDueAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ForecastDueAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? SubmittedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? CompletedAtUtc { get; set; }
    public bool RequiresReview { get; set; } = true;
    public bool Blocked { get; set; }
    public int Progress { get; set; }
    public int Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
[SugarTable("work_task_revision")]
public sealed class WorkRevision : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    public int Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string SnapshotJson { get; set; } = "{}";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ActorId { get; set; } = "";
}
[SugarTable("work_task_participant")]
public sealed class WorkTaskParticipant : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string UserId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
[SugarTable("work_task_plan")]
public sealed class WorkPlan : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RevisionId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ProposedBy { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Status { get; set; } = "Pending";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Explanation { get; set; } = "";
    [SugarColumn(IsNullable = true)] public DateTime? StartAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? DueAtUtc { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? DecidedBy { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? DecidedAtUtc { get; set; }
}
[SugarTable("work_task_milestone")]
public sealed class WorkMilestone : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string PlanId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 160)] public string Title { get; set; } = "";
    public DateTime DueAtUtc { get; set; }
    public int Sort { get; set; }
}
[SugarTable("work_task_report")]
public sealed class WorkReport : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ActorId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Kind { get; set; } = "Progress";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Text { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string NextStep { get; set; } = "";
    public int Progress { get; set; }
    public bool Blocked { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Source { get; set; } = "Web";
}
[SugarTable("work_task_submission")]
public sealed class WorkSubmission : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RevisionId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64, IsNullable = true)] public string? PlanId { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ActorId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Text { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Status { get; set; } = "Draft";
    public int Version { get; set; } = 1;
    [SugarColumn(IsNullable = true)] public DateTime? SubmittedAtUtc { get; set; }
}
[SugarTable("work_task_review")]
public sealed class WorkReview : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string SubmissionId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ActorId { get; set; } = "";
    public bool Accepted { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Opinion { get; set; } = "";
}
[SugarTable("work_task_attachment")]
public sealed class WorkAttachment : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar(64)", IsNullable = true)] public string? IssuedByKeyId { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string OwnerId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 240)] public string Name { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 120)] public string ContentType { get; set; } = "application/octet-stream";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 80)] public string StorageKey { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Status { get; set; } = "Uploading";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TicketHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
[SugarTable("work_task_attachment_link")]
public sealed class WorkAttachmentLink : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string AttachmentId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RecordId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Kind { get; set; } = "Report";
}
[SugarTable("work_task_audit")]
public sealed class WorkAudit : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string ActorId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 40)] public string Action { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Source { get; set; } = "Web";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string Summary { get; set; } = "";
}
[SugarTable("work_personal_key")]
public sealed class WorkPersonalKey : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string OwnerId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 80)] public string Label { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string SecretHash { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 400)] public string Scopes { get; set; } = "read";
    public DateTime ExpiresAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? RevokedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? LastUsedAtUtc { get; set; }
}
[SugarTable("work_command_receipt")]
public sealed class WorkReceipt : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string OwnerId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string PayloadHash { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string ResultJson { get; set; } = "{}";
}
[SugarTable("work_task_outbox")]
public sealed class WorkEvent : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 40)] public string Action { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string SnapshotJson { get; set; } = "{}";
    public bool Processed { get; set; }
    [SugarColumn(DefaultValue = "0")] public bool PushRequested { get; set; }
}
[SugarTable("work_task_digest")]
public sealed class WorkDigest : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RecipientId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Perspective { get; set; } = "Employee";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string BusinessDate { get; set; } = "";
    public DateTime CutoffUtc { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")] public string SnapshotJson { get; set; } = "[]";
}
[SugarTable("work_notification_delivery")]
public sealed class WorkDelivery : WorkRow
{
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string OriginId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string RecipientId { get; set; } = "";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Perspective { get; set; } = "Employee";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 16)] public string Channel { get; set; } = "Inbox";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 24)] public string Status { get; set; } = "Pending";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 128, IsNullable = true)] public string? ChannelReference { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 500, IsNullable = true)] public string? Error { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? ReadAtUtc { get; set; }
}
[SugarTable("work_notification_rule")]
public sealed class WorkNotificationRule : WorkRow
{
    [SugarColumn(ColumnDataType="nvarchar(4000)",DefaultValue=" ")] public string IncludedDates { get; set; } = "";
    [SugarColumn(ColumnDataType="nvarchar(4000)",DefaultValue=" ")] public string ExcludedDates { get; set; } = "";
    public bool Enabled { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar", Length = 64)] public string TimeZone { get; set; } = "Asia/Shanghai";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 5)] public string DailyTime { get; set; } = "17:00";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 20)] public string WeekDays { get; set; } = "1,2,3,4,5";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 32)] public string EmployeeAudience { get; set; } = "ActiveOrChanged";
    [SugarColumn(ColumnDataType = "nvarchar", Length = 80)] public string Channels { get; set; } = "Inbox";
    public int CatchUpMinutes { get; set; } = 120;
}
