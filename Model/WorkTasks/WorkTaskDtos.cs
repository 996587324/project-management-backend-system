using Model.SystemApplications;

namespace Model.WorkTasks;

public sealed record WorkActor(ApplicationAccessContext Access, bool Personal = false, string? KeyId = null, string Scopes = "read,assess,report,submit,upload,download")
{
    public string UserId => Access.UserId;
    public string AppCode => Access.AppCode;
    public string Source => Personal ? "MCP" : "Web";
}
public sealed class WorkTaskCommand
{
    public string Action { get; set; } = "save";
    public string IdempotencyKey { get; set; } = "";
    public int ExpectedVersion { get; set; }
    public string? RecordId { get; set; }
    public int RecordVersion { get; set; }
    public string Title { get; set; } = "";
    public string Requirements { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public string MaterialRule { get; set; } = "TextOrFile";
    public string Priority { get; set; } = "Normal";
    public string GroupId { get; set; } = "";
    public string AssigneeId { get; set; } = "";
    public List<string>? CollaboratorIds { get; set; }
    public string ReviewerId { get; set; } = "";
    public string SummaryLeaderId { get; set; } = "";
    public bool RequiresReview { get; set; } = true;
    public bool PushOnDispatch { get; set; }
    public DateTimeOffset? RequestedDueAt { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ForecastDueAt { get; set; }
    public bool Feasible { get; set; } = true;
    public string Text { get; set; } = "";
    public string NextStep { get; set; } = "";
    public bool? Blocked { get; set; }
    public int? Progress { get; set; }
    public bool ClearForecast { get; set; }
    public List<WorkMilestoneInput> Milestones { get; set; } = [];
    public List<string>? AttachmentIds { get; set; }
}
public sealed class WorkMilestoneInput
{
    public string Title { get; set; } = "";
    public DateTimeOffset DueAt { get; set; }
}
public sealed record WorkCommandResult(string TaskId, int Version, string RecordId, string OperationId);
public sealed class WorkQuery
{
    public string View { get; set; } = "my";
    public string? Status { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 20;
}
public sealed class WorkOverviewQuery
{
    public string? GroupId { get; set; }
    public string? UserId { get; set; }
    public string? Status { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 20;
}
public sealed class WorkCandidateQuery
{
    public string Purpose { get; set; } = "assignee";
    public string? TaskId { get; set; }
    public string GroupId { get; set; } = "";
    public string AssigneeId { get; set; } = "";
    public string? SelectedId { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int Size { get; set; } = 20;
}
public sealed class WorkTaskOptions
{
    public string StorageRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CalmAdmin", "work-task-files");
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxUserBytes { get; set; } = 500 * 1024 * 1024;
    public int UnlinkedFileHours { get; set; } = 24;
}
public sealed class WorkKeyRequest
{
    public string Label { get; set; } = "";
    public string Scopes { get; set; } = "read";
    public int Days { get; set; } = 30;
}
public sealed class WorkUploadRequest
{
    public string TaskId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
}
public sealed class WorkConflictException(string message) : Exception(message);
public sealed class WorkHandoverRequest
{
    public string TaskId { get; set; } = "";
    public string DispatcherId { get; set; } = "";
    public string ReviewerId { get; set; } = "";
    public string SummaryLeaderId { get; set; } = "";
    public int ExpectedVersion { get; set; }
    public string Reason { get; set; } = "";
}
