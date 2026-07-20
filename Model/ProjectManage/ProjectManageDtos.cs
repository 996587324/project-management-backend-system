namespace Model.ProjectManage;

public class PageQueryDto
{
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

public class IdDto
{
    public int Id { get; set; }
}

public class PagedResultDto<T>
{
    public List<T> Records { get; set; } = new();
    public int Current { get; set; }
    public int Size { get; set; }
    public int Total { get; set; }
}

public class ProjectQueryDto : PageQueryDto
{
    public string? ProjectName { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectType { get; set; }
    public byte? ProjectStatus { get; set; }
    public byte? ProjectStage { get; set; }
    public string? ManagerId { get; set; }
    public string? ManagerUserId { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
}

public class ProjectSaveDto
{
    public int Id { get; set; }
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectType { get; set; } = string.Empty;
    public string? ProjectTag { get; set; }
    public byte Priority { get; set; } = 2;
    public byte ProjectStage { get; set; } = 1;
    public byte ProjectStatus { get; set; } = 1;
    public decimal Progress { get; set; }
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime EndTime { get; set; } = DateTime.Now;
    public string ManagerId { get; set; } = string.Empty;
    public string? ManagerUserId { get; set; }
    public string? ManagerName { get; set; }
    public string? CustomerName { get; set; }
    public string? ContractCode { get; set; }
    public string? Remark { get; set; }
}

public class ProjectOutputDto : ProjectSaveDto
{
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
    public int TaskCount { get; set; }
    public int FileCount { get; set; }
}

public class ProjectBatchImportResultDto
{
    public int Total { get; set; }
    public int CreatedCount { get; set; }
    public int FailedCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<ProjectBatchImportRowDto> Rows { get; set; } = new();
}

public class ProjectBatchImportRowDto
{
    public int RowIndex { get; set; }
    public bool Success { get; set; }
    public int? ProjectId { get; set; }
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ProjectStageOptionDto
{
    public byte Value { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Sort { get; set; }
}

public class TaskQueryDto : PageQueryDto
{
    public string? ProjectId { get; set; }
    public int? ParentId { get; set; }
    public byte? TaskStatus { get; set; }
    public string? ExecutorId { get; set; }
    public string? TaskName { get; set; }
}

public class TaskSaveDto
{
    public int Id { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public int ParentId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string? TaskType { get; set; }
    public byte Priority { get; set; } = 2;
    public decimal Progress { get; set; }
    public byte TaskStatus { get; set; } = 1;
    public string ExecutorId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Sort { get; set; }
    public string? Remark { get; set; }
}

public class TaskOutputDto : TaskSaveDto
{
    public string? ProjectName { get; set; }
    public DateTime CreateTime { get; set; }
    public DateTime? UpdateTime { get; set; }
    public List<TaskOutputDto> Children { get; set; } = new();
}

public class TaskParentOptionDto
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Level { get; set; }
    public string ProjectName { get; set; } = string.Empty;
}

public class TaskTypeOptionDto
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Sort { get; set; }
}

public class FileQueryDto : PageQueryDto
{
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? FolderType { get; set; }
    public string? FileName { get; set; }
}

public class ProjectFileSaveDto
{
    public int ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileSuffix { get; set; } = string.Empty;
    public decimal FileSize { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FolderType { get; set; } = string.Empty;
    public int UploadUserId { get; set; }
}

public class ProjectFileOutputDto : ProjectFileSaveDto
{
    public int Id { get; set; }
    public DateTime CreateTime { get; set; }
}

public class DashboardStatsDto
{
    public int TotalProjects { get; set; }
    public int PendingProjects { get; set; }
    public int RunningProjects { get; set; }
    public int DelayedProjects { get; set; }
    public int FinishedProjects { get; set; }
    public int PausedProjects { get; set; }
    public int ArchivedProjects { get; set; }
    public decimal AverageProgress { get; set; }
    public int PendingTasks { get; set; }
    public int FinishedTasks { get; set; }
    public List<ProjectStatusStatDto> ProjectStatusDistribution { get; set; } = new();
    public List<ManagerProjectStatusStatDto> ManagerProjectStatusStats { get; set; } = new();
    public List<ProjectOutputDto> RecentProjects { get; set; } = new();
    public List<TaskOutputDto> TodoTasks { get; set; } = new();
}

public class ProjectStatusStatDto
{
    public byte Status { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Value { get; set; }
    public string Color { get; set; } = string.Empty;
}

public class ManagerProjectStatusStatDto
{
    public string ManagerId { get; set; } = string.Empty;
    public string ManagerName { get; set; } = string.Empty;
    public int PendingProjects { get; set; }
    public int RunningProjects { get; set; }
    public int FinishedProjects { get; set; }
    public int TotalProjects { get; set; }
}
