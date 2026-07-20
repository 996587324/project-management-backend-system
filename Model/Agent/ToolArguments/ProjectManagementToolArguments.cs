namespace Model.Agent.ToolArguments;

/// <summary>
/// 查询项目列表时，Agent 传给项目管理工具的筛选条件。
/// </summary>
public sealed class ProjectListArguments
{
    public string? ProjectName { get; set; }
    public string? ProjectCode { get; set; }
    public string? ManagerName { get; set; }
    public byte? ProjectStatus { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>
/// 定位单个项目时使用的参数，支持按 Id、项目编号或项目名称查找。
/// </summary>
public sealed class ProjectIdentityArguments
{
    public int? Id { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectName { get; set; }
}

/// <summary>
/// 只需要一个数据库主键 Id 的通用工具参数。
/// </summary>
public sealed class IdArguments
{
    public int Id { get; set; }
}

/// <summary>
/// 查询任务列表时，Agent 传入的过滤和分页条件。
/// </summary>
public sealed class TaskListArguments
{
    public string? ProjectName { get; set; }
    public string? TaskName { get; set; }
    public string? ExecutorId { get; set; }
    public byte? TaskStatus { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>
/// 查询项目文件列表时使用的过滤和分页条件。
/// </summary>
public sealed class FileListArguments
{
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? FolderType { get; set; }
    public string? FileName { get; set; }
    public int Current { get; set; } = 1;
    public int Size { get; set; } = 20;
}

/// <summary>
/// 批量新增项目的工具参数；UpdateIfExists 为 true 时，相同项目会转为更新。
/// </summary>
public sealed class ProjectCreateArguments
{
    public bool UpdateIfExists { get; set; }
    public List<ProjectCreateItem> Projects { get; set; } = new();
}

/// <summary>
/// Agent 新增项目时传入的单条项目数据。
/// </summary>
public sealed class ProjectCreateItem
{
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string? ProjectType { get; set; }
    public string? ProjectTag { get; set; }
    public byte? Priority { get; set; }
    public byte? ProjectStage { get; set; }
    public byte? ProjectStatus { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? ManagerId { get; set; }
    public string? ManagerName { get; set; }
    public string? CustomerName { get; set; }
    public string? ContractCode { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// 批量修改项目的工具参数。
/// </summary>
public sealed class ProjectUpdateArguments
{
    public List<ProjectUpdateItem> Projects { get; set; } = new();
}

/// <summary>
/// Agent 修改项目时传入的单条项目数据；空字段表示沿用数据库中的原值。
/// </summary>
public sealed class ProjectUpdateItem
{
    public int? Id { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public string? NewProjectCode { get; set; }
    public string? NewProjectName { get; set; }
    public string? ProjectType { get; set; }
    public string? ProjectTag { get; set; }
    public byte? Priority { get; set; }
    public byte? ProjectStage { get; set; }
    public byte? ProjectStatus { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ManagerId { get; set; }
    public string? ManagerName { get; set; }
    public string? CustomerName { get; set; }
    public string? ContractCode { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// 批量变更项目状态的工具参数。
/// </summary>
public sealed class ProjectStatusArguments
{
    public List<ProjectStatusItem> Projects { get; set; } = new();
}

/// <summary>
/// 单条项目状态变更数据，支持按 Id、项目编号或项目名称定位项目。
/// </summary>
public sealed class ProjectStatusItem
{
    public int? Id { get; set; }
    public string? ProjectCode { get; set; }
    public string? ProjectName { get; set; }
    public byte Status { get; set; }
}

/// <summary>
/// 批量新增任务的工具参数。
/// </summary>
public sealed class TaskCreateArguments
{
    public List<TaskCreateItem> Tasks { get; set; } = new();
}

/// <summary>
/// Agent 新增任务时传入的单条任务数据。
/// </summary>
public sealed class TaskCreateItem
{
    public string ProjectName { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string? TaskType { get; set; }
    public byte? Priority { get; set; }
    public byte? TaskStatus { get; set; }
    public string? ExecutorId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int? Sort { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// 批量修改任务的工具参数。
/// </summary>
public sealed class TaskUpdateArguments
{
    public List<TaskUpdateItem> Tasks { get; set; } = new();
}

/// <summary>
/// Agent 修改任务时传入的单条任务数据；空字段表示沿用数据库中的原值。
/// </summary>
public sealed class TaskUpdateItem
{
    public int Id { get; set; }
    public string? ProjectName { get; set; }
    public int? ParentId { get; set; }
    public string? TaskName { get; set; }
    public string? TaskType { get; set; }
    public byte? Priority { get; set; }
    public byte? TaskStatus { get; set; }
    public string? ExecutorId { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? Sort { get; set; }
    public string? Remark { get; set; }
}

/// <summary>
/// 批量变更任务状态的工具参数。
/// </summary>
public sealed class TaskStatusArguments
{
    public List<TaskStatusItem> Tasks { get; set; } = new();
}

/// <summary>
/// 单条任务状态变更数据。
/// </summary>
public sealed class TaskStatusItem
{
    public int Id { get; set; }
    public byte Status { get; set; }
}

/// <summary>
/// 批量执行工具时，每一条输入数据对应的一条执行结果。
/// </summary>
public sealed class ExecutionRow
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? Id { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
