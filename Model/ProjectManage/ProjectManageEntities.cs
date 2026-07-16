using SqlSugar;

namespace Model.ProjectManage;

[SugarTable("Project")]
public class ProjectEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string ProjectType { get; set; } = string.Empty;
    public string? ProjectTag { get; set; }
    public byte Priority { get; set; } = 2;
    public byte ProjectStage { get; set; } = 1;
    public byte ProjectStatus { get; set; } = 1;
    public decimal Progress { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int ManagerId { get; set; }
    public string? ManagerUserId { get; set; }
    public string? ManagerName { get; set; }
    public string? CustomerName { get; set; }
    public string? ContractCode { get; set; }
    public string? Remark { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("Task")]
public class ProjectTaskEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 64)]
    public string ProjectId { get; set; } = string.Empty;
    public int ParentId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string? TaskType { get; set; }
    public byte Priority { get; set; } = 2;
    public decimal Progress { get; set; }
    public byte TaskStatus { get; set; } = 1;
    [SugarColumn(Length = 64)]
    public string ExecutorId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Sort { get; set; }
    public string? Remark { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("TaskTypeConfig")]
public class ProjectTaskTypeConfigEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    [SugarColumn(Length = 64)]
    public string TypeCode { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string TypeName { get; set; } = string.Empty;
    public int Sort { get; set; }
    public byte IsEnable { get; set; } = 1;
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime? UpdateTime { get; set; } = DateTime.Now;
}

[SugarTable("ProjectFile")]
public class ProjectFileEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileSuffix { get; set; } = string.Empty;
    public decimal FileSize { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FolderType { get; set; } = string.Empty;
    public int UploadUserId { get; set; }
    public byte IsDelete { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
