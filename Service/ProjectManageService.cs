using Interface;
using Model.ProjectManage;
using Model.User;
using SqlSugar;

namespace Service;

public class ProjectManageService : IProjectManageService
{
    private readonly SqlSugarScope _dbScope;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");
    private static readonly List<ProjectStageOptionDto> ProjectStages = new()
    {
        new ProjectStageOptionDto { Value = 1, Label = "开箱", Sort = 1 },
        new ProjectStageOptionDto { Value = 2, Label = "启用", Sort = 2 },
        new ProjectStageOptionDto { Value = 3, Label = "验收", Sort = 3 }
    };
    private static readonly List<TaskTypeOptionDto> DefaultTaskTypes = new()
    {
        new TaskTypeOptionDto { Label = "开箱", Value = "开箱", Sort = 1 },
        new TaskTypeOptionDto { Label = "启用", Value = "启用", Sort = 2 },
        new TaskTypeOptionDto { Label = "预验收", Value = "预验收", Sort = 3 },
        new TaskTypeOptionDto { Label = "验收", Value = "验收", Sort = 4 }
    };

    public ProjectManageService(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
        Db.CodeFirst.InitTables<ProjectEntity, ProjectTaskEntity, ProjectFileEntity, ProjectTaskTypeConfigEntity>();
    }

    public async Task<PagedResultDto<ProjectOutputDto>> GetProjectPageAsync(ProjectQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Max(query.Size, 1);
        RefAsync<int> total = 0;
        var list = await BuildProjectQuery(query).OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new PagedResultDto<ProjectOutputDto>
        {
            Records = await FillProjectStatsAsync(list.Select(MapProject).ToList()),
            Current = page,
            Size = size,
            Total = total
        };
    }

    public async Task<List<ProjectOutputDto>> GetProjectListAsync()
    {
        var list = await Db.Queryable<ProjectEntity>().Where(x => x.IsDelete == 0)
            .OrderByDescending(x => x.CreateTime).ToListAsync();
        return await FillProjectStatsAsync(list.Select(MapProject).ToList());
    }

    public Task<List<ProjectStageOptionDto>> GetProjectStagesAsync()
    {
        return Task.FromResult(ProjectStages
            .OrderBy(x => x.Sort)
            .Select(x => new ProjectStageOptionDto { Value = x.Value, Label = x.Label, Sort = x.Sort })
            .ToList());
    }

    public async Task<ProjectOutputDto?> GetProjectAsync(int id)
    {
        var entity = await Db.Queryable<ProjectEntity>().FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return null;
        return (await FillProjectStatsAsync(new List<ProjectOutputDto> { MapProject(entity) })).First();
    }

    public async Task<int> CreateProjectAsync(ProjectSaveDto dto)
    {
        if (await Db.Queryable<ProjectEntity>().AnyAsync(x => x.ProjectCode == dto.ProjectCode && x.IsDelete == 0))
            throw new InvalidOperationException("项目编号已存在");

        var entity = new ProjectEntity();
        ApplyProject(dto, entity);
        entity.CreateTime = DateTime.Now;
        return await Db.Insertable(entity).ExecuteReturnIdentityAsync();
    }

    public async Task<bool> UpdateProjectAsync(ProjectSaveDto dto)
    {
        var entity = await Db.Queryable<ProjectEntity>().FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
        if (entity == null) return false;
        if (await Db.Queryable<ProjectEntity>().AnyAsync(x => x.Id != dto.Id && x.ProjectCode == dto.ProjectCode && x.IsDelete == 0))
            throw new InvalidOperationException("项目编号已存在");

        var oldProjectName = entity.ProjectName;
        ApplyProject(dto, entity);
        entity.UpdateTime = DateTime.Now;
        var changed = await Db.Updateable(entity).ExecuteCommandHasChangeAsync();
        if (oldProjectName != entity.ProjectName)
        {
            await Db.Updateable<ProjectTaskEntity>()
                .SetColumns(x => new ProjectTaskEntity { ProjectId = entity.ProjectName, UpdateTime = DateTime.Now })
                .Where(x => x.ProjectId == oldProjectName && x.IsDelete == 0)
                .ExecuteCommandAsync();
        }
        return changed;
    }

    public async Task<bool> ChangeProjectStatusAsync(int id, byte status)
    {
        return await Db.Updateable<ProjectEntity>()
            .SetColumns(x => new ProjectEntity { ProjectStatus = status, UpdateTime = DateTime.Now })
            .Where(x => x.Id == id && x.IsDelete == 0)
            .ExecuteCommandHasChangeAsync();
    }

    public async Task<bool> DeleteProjectAsync(int id)
    {
        try
        {
            return await Db.Updateable<ProjectEntity>()
                            .SetColumns(x => new ProjectEntity { IsDelete = 1, UpdateTime = DateTime.Now })
                            .Where(x => x.Id == id)
                            .ExecuteCommandHasChangeAsync();
        }
        catch(Exception ex)
        {
            throw ex;
        }

    }

    public async Task<PagedResultDto<TaskOutputDto>> GetTaskPageAsync(TaskQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Max(query.Size, 1);
        RefAsync<int> total = 0;
        var list = await BuildTaskQuery(query).OrderBy(x => x.Sort).OrderBy(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new PagedResultDto<TaskOutputDto>
        {
            Records = await FillTaskProjectNamesAsync(list.Select(MapTask).ToList()),
            Current = page,
            Size = size,
            Total = total
        };
    }

    public async Task<List<TaskOutputDto>> GetTaskTreeAsync(string? projectId)
    {
        var activeProjectKeys = await GetActiveProjectKeysAsync();
        var selectedProjectKeys = await GetProjectKeysAsync(projectId);
        var list = await Db.Queryable<ProjectTaskEntity>()
            .Where(x => x.IsDelete == 0)
            .Where(x => activeProjectKeys.Contains(x.ProjectId))
            .WhereIF(!string.IsNullOrWhiteSpace(projectId), x => selectedProjectKeys.Contains(x.ProjectId))
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        var dtos = await FillTaskProjectNamesAsync(list.Select(MapTask).ToList());
        var lookup = dtos.ToDictionary(x => x.Id);
        foreach (var task in dtos.Where(x => x.ParentId != 0))
        {
            if (lookup.TryGetValue(task.ParentId, out var parent))
                parent.Children.Add(task);
        }
        return dtos.Where(x => x.ParentId == 0 || !lookup.ContainsKey(x.ParentId)).ToList();
    }

    public async Task<List<TaskParentOptionDto>> GetTaskParentOptionsAsync(string? projectId, int currentId = 0)
    {
        var activeProjectKeys = await GetActiveProjectKeysAsync();
        var selectedProjectKeys = await GetProjectKeysAsync(projectId);
        var list = await Db.Queryable<ProjectTaskEntity>()
            .Where(x => x.IsDelete == 0)
            .Where(x => activeProjectKeys.Contains(x.ProjectId))
            .WhereIF(!string.IsNullOrWhiteSpace(projectId), x => selectedProjectKeys.Contains(x.ProjectId))
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.CreateTime)
            .ToListAsync();
        var dtos = await FillTaskProjectNamesAsync(list.Select(MapTask).ToList());
        var lookup = dtos.ToDictionary(x => x.Id);

        int GetLevel(TaskOutputDto item)
        {
            var level = 1;
            var current = item;
            var visited = new HashSet<int> { item.Id };
            while (current.ParentId != 0 && lookup.TryGetValue(current.ParentId, out var parent) && visited.Add(parent.Id))
            {
                level++;
                current = parent;
            }
            return level;
        }

        return dtos
            .Select(x => new { Task = x, Level = GetLevel(x) })
            .Where(x => x.Task.Id != currentId && x.Level < 3)
            .Select(x => new TaskParentOptionDto
            {
                Id = x.Task.Id,
                Label = $"{new string('　', Math.Max(x.Level - 1, 0))}{x.Task.TaskName}",
                Level = x.Level,
                ProjectName = x.Task.ProjectName ?? x.Task.ProjectId
            })
            .ToList();
    }

    public async Task<List<TaskTypeOptionDto>> GetTaskTypeOptionsAsync()
    {
        await EnsureTaskTypeConfigsAsync();
        var list = await Db.Queryable<ProjectTaskTypeConfigEntity>()
            .Where(x => x.IsDelete == 0 && x.IsEnable == 1)
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return list.Select(x => new TaskTypeOptionDto
        {
            Id = x.Id,
            Label = x.TypeName,
            Value = x.TypeName,
            Sort = x.Sort
        }).ToList();
    }

    public async Task<TaskOutputDto?> GetTaskAsync(int id)
    {
        var entity = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return null;
        return (await FillTaskProjectNamesAsync(new List<TaskOutputDto> { MapTask(entity) })).First();
    }

    public async Task<int> CreateTaskAsync(TaskSaveDto dto)
    {
        await EnsureProjectActiveAsync(dto.ProjectId);
        await EnsureTaskLevelAsync(dto.ParentId, dto.ProjectId, dto.StartTime, dto.EndTime);
        var entity = new ProjectTaskEntity();
        ApplyTask(dto, entity);
        entity.TaskStatus = ResolveTaskStatus(entity.Progress, entity.EndTime, entity.TaskStatus);
        entity.CreateTime = DateTime.Now;
        var id = await Db.Insertable(entity).ExecuteReturnIdentityAsync();
        await RecalculateProjectProgressAsync(entity.ProjectId);
        return id;
    }

    public async Task<bool> UpdateTaskAsync(TaskSaveDto dto)
    {
        var entity = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == dto.Id && x.IsDelete == 0);
        if (entity == null) return false;
        var oldProjectId = entity.ProjectId;
        await EnsureProjectActiveAsync(dto.ProjectId);
        await EnsureTaskLevelAsync(dto.ParentId, dto.ProjectId, dto.StartTime, dto.EndTime, dto.Id);
        ApplyTask(dto, entity);
        entity.TaskStatus = ResolveTaskStatus(entity.Progress, entity.EndTime, entity.TaskStatus);
        entity.UpdateTime = DateTime.Now;
        var changed = await Db.Updateable(entity).ExecuteCommandHasChangeAsync();
        await RecalculateProjectProgressAsync(entity.ProjectId);
        if (oldProjectId != entity.ProjectId) await RecalculateProjectProgressAsync(oldProjectId);
        return changed;
    }

    public async Task<bool> ChangeTaskStatusAsync(int id, byte status)
    {
        var entity = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return false;
        entity.TaskStatus = status;
        entity.Progress = status == 3 ? 100 : status == 1 ? 0 : entity.Progress;
        entity.UpdateTime = DateTime.Now;
        var changed = await Db.Updateable(entity).ExecuteCommandHasChangeAsync();
        await RecalculateProjectProgressAsync(entity.ProjectId);
        return changed;
    }

    public async Task<bool> DeleteTaskAsync(int id)
    {
        var entity = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == id && x.IsDelete == 0);
        if (entity == null) return false;
        var ids = await CollectTaskIdsAsync(id);
        var changed = await Db.Updateable<ProjectTaskEntity>()
            .SetColumns(x => new ProjectTaskEntity { IsDelete = 1, UpdateTime = DateTime.Now })
            .Where(x => ids.Contains(x.Id))
            .ExecuteCommandHasChangeAsync();
        await RecalculateProjectProgressAsync(entity.ProjectId);
        return changed;
    }

    public async Task<PagedResultDto<ProjectFileOutputDto>> GetFilePageAsync(FileQueryDto query)
    {
        var page = Math.Max(query.Current, 1);
        var size = Math.Max(query.Size, 1);
        RefAsync<int> total = 0;
        var list = await Db.Queryable<ProjectFileEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(query.ProjectId.HasValue, x => x.ProjectId == query.ProjectId!.Value)
            .WhereIF(query.TaskId.HasValue, x => x.TaskId == query.TaskId!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.FolderType), x => x.FolderType == query.FolderType)
            .WhereIF(!string.IsNullOrWhiteSpace(query.FileName), x => x.FileName.Contains(query.FileName!))
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(page, size, total);
        return new PagedResultDto<ProjectFileOutputDto>
        {
            Records = list.Select(MapFile).ToList(),
            Current = page,
            Size = size,
            Total = total
        };
    }

    public async Task<int> CreateFileAsync(ProjectFileSaveDto dto)
    {
        var existing = await Db.Queryable<ProjectFileEntity>()
            .FirstAsync(x => x.IsDelete == 0 && x.ProjectId == dto.ProjectId && x.FileName == dto.FileName);

        if (existing != null)
        {
            existing.TaskId = dto.TaskId;
            existing.FileSuffix = dto.FileSuffix;
            existing.FileSize = dto.FileSize;
            existing.FilePath = dto.FilePath;
            existing.FolderType = dto.FolderType;
            existing.UploadUserId = dto.UploadUserId;
            await Db.Updateable(existing).ExecuteCommandAsync();
            return existing.Id;
        }

        var entity = new ProjectFileEntity
        {
            ProjectId = dto.ProjectId,
            TaskId = dto.TaskId,
            FileName = dto.FileName,
            FileSuffix = dto.FileSuffix,
            FileSize = dto.FileSize,
            FilePath = dto.FilePath,
            FolderType = dto.FolderType,
            UploadUserId = dto.UploadUserId,
            CreateTime = DateTime.Now
        };
        return await Db.Insertable(entity).ExecuteReturnIdentityAsync();
    }

    public async Task<ProjectFileOutputDto?> GetFileAsync(int id)
    {
        var entity = await Db.Queryable<ProjectFileEntity>().FirstAsync(x => x.Id == id && x.IsDelete == 0);
        return entity == null ? null : MapFile(entity);
    }

    public async Task<bool> DeleteFileAsync(int id)
    {
        return await Db.Updateable<ProjectFileEntity>()
            .SetColumns(x => new ProjectFileEntity { IsDelete = 1 })
            .Where(x => x.Id == id)
            .ExecuteCommandHasChangeAsync();
    }

    public async Task<DashboardStatsDto> GetDashboardStatsAsync(string? userId)
    {
        var projects = await GetProjectListAsync();
        var activeProjectKeys = projects.SelectMany(x => new[] { x.Id.ToString(), x.ProjectName }).Distinct().ToList();
        var taskQuery = Db.Queryable<ProjectTaskEntity>()
            .Where(x => x.IsDelete == 0)
            .Where(x => activeProjectKeys.Contains(x.ProjectId));
        if (!string.IsNullOrWhiteSpace(userId)) taskQuery = taskQuery.Where(x => x.ExecutorId == userId);
        var tasks = await taskQuery.ToListAsync();
        return new DashboardStatsDto
        {
            TotalProjects = projects.Count,
            PendingProjects = projects.Count(x => x.ProjectStatus == 1),
            RunningProjects = projects.Count(x => x.ProjectStatus == 2),
            DelayedProjects = projects.Count(x => x.ProjectStatus == 3),
            FinishedProjects = projects.Count(x => x.ProjectStatus == 5),
            PausedProjects = projects.Count(x => x.ProjectStatus == 4),
            ArchivedProjects = projects.Count(x => x.ProjectStatus == 6),
            AverageProgress = projects.Count == 0 ? 0 : Math.Round(projects.Average(x => x.Progress), 2),
            PendingTasks = tasks.Count(x => x.TaskStatus == 1 || x.TaskStatus == 2),
            FinishedTasks = tasks.Count(x => x.TaskStatus == 3),
            ProjectStatusDistribution = BuildProjectStatusDistribution(projects),
            ManagerProjectStatusStats = BuildManagerProjectStatusStats(projects),
            RecentProjects = projects.Take(8).ToList(),
            TodoTasks = await FillTaskProjectNamesAsync(tasks.Where(x => x.TaskStatus is 1 or 2 or 4).OrderBy(x => x.EndTime).Take(8).Select(MapTask).ToList())
        };
    }

    private static List<ProjectStatusStatDto> BuildProjectStatusDistribution(List<ProjectOutputDto> projects)
    {
        var statusItems = new[]
        {
            new { Status = (byte)1, Label = "待启用", Color = "#8b95a5" },
            new { Status = (byte)2, Label = "进行中", Color = "#1677ff" },
            new { Status = (byte)3, Label = "延期", Color = "#ffb020" },
            new { Status = (byte)4, Label = "暂停", Color = "#f59e0b" },
            new { Status = (byte)5, Label = "已完成", Color = "#22c55e" },
            new { Status = (byte)6, Label = "已归档", Color = "#94a3b8" }
        };

        return statusItems
            .Select(x => new ProjectStatusStatDto
            {
                Status = x.Status,
                Label = x.Label,
                Color = x.Color,
                Value = projects.Count(project => project.ProjectStatus == x.Status)
            })
            .ToList();
    }

    private static List<ManagerProjectStatusStatDto> BuildManagerProjectStatusStats(List<ProjectOutputDto> projects)
    {
        return projects
            .GroupBy(x =>
            {
                var managerId = string.IsNullOrWhiteSpace(x.ManagerUserId) ? x.ManagerId : x.ManagerUserId;
                var managerName = string.IsNullOrWhiteSpace(x.ManagerName)
                    ? (string.IsNullOrWhiteSpace(managerId) ? "未分配" : managerId)
                    : x.ManagerName;
                return new { ManagerId = managerId ?? string.Empty, ManagerName = managerName };
            })
            .Select(x => new ManagerProjectStatusStatDto
            {
                ManagerId = x.Key.ManagerId,
                ManagerName = x.Key.ManagerName,
                PendingProjects = x.Count(project => project.ProjectStatus == 1),
                RunningProjects = x.Count(project => project.ProjectStatus == 2),
                FinishedProjects = x.Count(project => project.ProjectStatus == 5),
                TotalProjects = x.Count()
            })
            .OrderByDescending(x => x.TotalProjects)
            .ThenBy(x => x.ManagerName)
            .ToList();
    }

    private ISugarQueryable<ProjectEntity> BuildProjectQuery(ProjectQueryDto query)
    {
        var managerFilter = query.ManagerUserId ?? query.ManagerId;
        var hasManagerFilter = !string.IsNullOrWhiteSpace(managerFilter);
        var hasNumericManagerFilter = int.TryParse(managerFilter, out var numericManagerId);

        return Db.Queryable<ProjectEntity>()
            .Where(x => x.IsDelete == 0)
            .WhereIF(!string.IsNullOrWhiteSpace(query.ProjectName), x => x.ProjectName.Contains(query.ProjectName!))
            .WhereIF(!string.IsNullOrWhiteSpace(query.ProjectCode), x => x.ProjectCode.Contains(query.ProjectCode!))
            .WhereIF(!string.IsNullOrWhiteSpace(query.ProjectType), x => x.ProjectType == query.ProjectType)
            .WhereIF(query.ProjectStatus.HasValue, x => x.ProjectStatus == query.ProjectStatus!.Value)
            .WhereIF(query.ProjectStage.HasValue, x => x.ProjectStage == query.ProjectStage!.Value)
            .WhereIF(hasManagerFilter && hasNumericManagerFilter, x => x.ManagerUserId == managerFilter || x.ManagerId == numericManagerId)
            .WhereIF(hasManagerFilter && !hasNumericManagerFilter, x => x.ManagerUserId == managerFilter)
            .WhereIF(DateTime.TryParse(query.StartTime, out var start), x => x.CreateTime >= start)
            .WhereIF(DateTime.TryParse(query.EndTime, out var end), x => x.CreateTime <= end);
    }

    private ISugarQueryable<ProjectTaskEntity> BuildTaskQuery(TaskQueryDto query)
    {
        var activeProjects = Db.Queryable<ProjectEntity>()
            .Where(x => x.IsDelete == 0)
            .Select(x => new ProjectKeyItem { Id = x.Id, ProjectName = x.ProjectName })
            .ToList();
        var activeProjectKeys = BuildProjectKeys(activeProjects);
        var selectedProjectKeys = BuildProjectKeys(activeProjects, query.ProjectId);
        return Db.Queryable<ProjectTaskEntity>()
            .Where(x => x.IsDelete == 0)
            .Where(x => activeProjectKeys.Contains(x.ProjectId))
            .WhereIF(!string.IsNullOrWhiteSpace(query.ProjectId), x => selectedProjectKeys.Contains(x.ProjectId))
            .WhereIF(query.ParentId.HasValue, x => x.ParentId == query.ParentId!.Value)
            .WhereIF(query.TaskStatus.HasValue, x => x.TaskStatus == query.TaskStatus!.Value)
            .WhereIF(!string.IsNullOrWhiteSpace(query.ExecutorId), x => x.ExecutorId == query.ExecutorId)
            .WhereIF(!string.IsNullOrWhiteSpace(query.TaskName), x => x.TaskName.Contains(query.TaskName!));
    }

    private async Task<List<ProjectOutputDto>> FillProjectStatsAsync(List<ProjectOutputDto> projects)
    {
        try
        {
            if (!projects.Any()) return projects;
            var ids = projects.Select(x => x.Id).ToList();
            var projectKeys = projects.SelectMany(x => new[] { x.Id.ToString(), x.ProjectName }).Distinct().ToList();
            var tasks = await Db.Queryable<ProjectTaskEntity>().Where(x => x.IsDelete == 0 && projectKeys.Contains(x.ProjectId)).ToListAsync();
            var files = await Db.Queryable<ProjectFileEntity>().Where(x => x.IsDelete == 0 && ids.Contains(x.ProjectId)).ToListAsync();
            foreach (var project in projects)
            {
                project.TaskCount = tasks.Count(x => x.ProjectId == project.Id.ToString() || x.ProjectId == project.ProjectName);
                project.FileCount = files.Count(x => x.ProjectId == project.Id);
            }

            var managerIds = projects
                .Select(x => string.IsNullOrWhiteSpace(x.ManagerUserId) ? x.ManagerId : x.ManagerUserId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            if (managerIds.Any() && Db.DbMaintenance.IsAnyTable("sys_user_info"))
            {
                var users = await Db.Queryable<SysUserInfo>()
                    .Where(x => managerIds.Contains(x.UserId))
                    .ToListAsync();
                var userMap = users.ToDictionary(x => x.UserId, x => string.IsNullOrWhiteSpace(x.NickName) ? x.UserName : x.NickName);

                foreach (var project in projects)
                {
                    var managerUserId = string.IsNullOrWhiteSpace(project.ManagerUserId) ? project.ManagerId : project.ManagerUserId;
                    if (!string.IsNullOrWhiteSpace(managerUserId) && userMap.TryGetValue(managerUserId, out var managerName))
                    {
                        project.ManagerUserId = managerUserId;
                        project.ManagerName = managerName;
                    }
                }
            }
            return projects;
        }
        catch(Exception ex)
        {
            throw ex;
        }

    }

    private async Task RecalculateProjectProgressAsync(string projectId)
    {
        var project = await GetProjectByKeyAsync(projectId);
        if (project == null) return;
        var projectKeys = new[] { project.Id.ToString(), project.ProjectName };
        var tasks = await Db.Queryable<ProjectTaskEntity>()
            .Where(x => projectKeys.Contains(x.ProjectId) && x.IsDelete == 0)
            .ToListAsync();
        var parentIds = tasks.Where(x => x.ParentId != 0).Select(x => x.ParentId).ToHashSet();
        var leafTasks = tasks.Where(x => !parentIds.Contains(x.Id)).ToList();
        var progressNodes = leafTasks.Any() ? leafTasks : tasks;
        var completedCount = progressNodes.Count(x => x.TaskStatus == 3 || x.Progress >= 100);
        var progress = progressNodes.Count == 0 ? 0 : Math.Round(completedCount * 100m / progressNodes.Count, 2);
        await Db.Updateable<ProjectEntity>()
            .SetColumns(x => new ProjectEntity { Progress = progress, UpdateTime = DateTime.Now })
            .Where(x => x.Id == project.Id)
            .ExecuteCommandAsync();
    }

    private async Task<List<string>> GetActiveProjectKeysAsync()
    {
        var projects = await Db.Queryable<ProjectEntity>()
            .Where(x => x.IsDelete == 0)
            .Select(x => new ProjectKeyItem { Id = x.Id, ProjectName = x.ProjectName })
            .ToListAsync();
        return BuildProjectKeys(projects);
    }

    private async Task<List<string>> GetProjectKeysAsync(string? projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return new List<string>();
        var project = await GetProjectByKeyAsync(projectKey);
        return project == null ? new List<string>() : new List<string> { project.Id.ToString(), project.ProjectName };
    }

    private async Task<ProjectEntity?> GetProjectByKeyAsync(string projectKey)
    {
        if (int.TryParse(projectKey, out var projectId))
        {
            return await Db.Queryable<ProjectEntity>()
                .FirstAsync(x => x.IsDelete == 0 && (x.Id == projectId || x.ProjectName == projectKey));
        }

        return await Db.Queryable<ProjectEntity>()
            .FirstAsync(x => x.IsDelete == 0 && x.ProjectName == projectKey);
    }

    private static List<string> BuildProjectKeys(IEnumerable<ProjectKeyItem> projects, string? selectedProjectKey = null)
    {
        var source = string.IsNullOrWhiteSpace(selectedProjectKey)
            ? projects
            : projects.Where(x => x.Id.ToString() == selectedProjectKey || x.ProjectName == selectedProjectKey);

        return source
            .SelectMany(x => new[] { x.Id.ToString(), x.ProjectName })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
    }

    private async Task<List<TaskOutputDto>> FillTaskProjectNamesAsync(List<TaskOutputDto> tasks)
    {
        if (!tasks.Any()) return tasks;
        var projectKeys = tasks
            .Select(x => x.ProjectId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        var numericProjectIds = projectKeys
            .Where(x => int.TryParse(x, out _))
            .Select(int.Parse)
            .ToList();

        var projects = await Db.Queryable<ProjectEntity>()
            .Where(x => x.IsDelete == 0 && (numericProjectIds.Contains(x.Id) || projectKeys.Contains(x.ProjectName)))
            .Select(x => new { x.Id, x.ProjectName })
            .ToListAsync();
        var projectMap = projects
            .SelectMany(x => new[]
            {
                new { Key = x.Id.ToString(), x.ProjectName },
                new { Key = x.ProjectName, x.ProjectName }
            })
            .GroupBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.First().ProjectName);
        foreach (var task in tasks)
        {
            if (projectMap.TryGetValue(task.ProjectId, out var projectName))
            {
                task.ProjectName = projectName;
                task.ProjectId = projectName;
            }
        }
        return tasks;
    }

    private async Task EnsureTaskTypeConfigsAsync()
    {
        var exists = await Db.Queryable<ProjectTaskTypeConfigEntity>().AnyAsync(x => x.IsDelete == 0);
        if (exists) return;

        var now = DateTime.Now;
        var entities = DefaultTaskTypes.Select(x => new ProjectTaskTypeConfigEntity
        {
            TypeCode = x.Value,
            TypeName = x.Label,
            Sort = x.Sort,
            IsEnable = 1,
            CreateTime = now
        }).ToList();

        await Db.Insertable(entities).ExecuteCommandAsync();
    }

    private async Task EnsureTaskLevelAsync(int parentId, string projectId, DateTime startTime, DateTime endTime, int currentId = 0)
    {
        if (parentId == 0) return;
        var parent = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == parentId && x.IsDelete == 0);
        if (parent == null) throw new InvalidOperationException("父任务不存在");
        var projectKeys = await GetProjectKeysAsync(projectId);
        if (!projectKeys.Contains(parent.ProjectId)) throw new InvalidOperationException("父任务必须属于同一个项目");
        if (parent.Id == currentId) throw new InvalidOperationException("父任务不能指向自身");
        if (startTime.Date < parent.StartTime.Date || endTime.Date > parent.EndTime.Date)
            throw new InvalidOperationException($"子任务时间必须在父任务时间范围内：{parent.StartTime:yyyy-MM-dd} 至 {parent.EndTime:yyyy-MM-dd}");
        var level = 2;
        while (parent.ParentId != 0)
        {
            level++;
            if (level > 3) throw new InvalidOperationException("第一阶段仅支持三级任务");
            parent = await Db.Queryable<ProjectTaskEntity>().FirstAsync(x => x.Id == parent.ParentId && x.IsDelete == 0);
            if (parent == null) break;
            if (parent.Id == currentId) throw new InvalidOperationException("父任务不能指向自身");
        }
    }

    private async Task EnsureProjectActiveAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("所属项目不能为空");
        var exists = await GetProjectByKeyAsync(projectId) != null;
        if (!exists) throw new InvalidOperationException("所属项目不存在或已删除");
    }

    private async Task<List<int>> CollectTaskIdsAsync(int rootId)
    {
        var all = await Db.Queryable<ProjectTaskEntity>().Where(x => x.IsDelete == 0).ToListAsync();
        var result = new List<int>();
        void Walk(int id)
        {
            result.Add(id);
            foreach (var child in all.Where(x => x.ParentId == id))
                Walk(child.Id);
        }
        Walk(rootId);
        return result;
    }

    private static byte ResolveTaskStatus(decimal progress, DateTime endTime, byte fallback)
    {
        if (progress >= 100) return 3;
        if (endTime.Date < DateTime.Now.Date) return 4;
        if (progress <= 0) return 1;
        return fallback == 4 ? (byte)2 : fallback;
    }

    private static void ApplyProject(ProjectSaveDto dto, ProjectEntity entity)
    {
        entity.ProjectCode = dto.ProjectCode;
        entity.ProjectName = dto.ProjectName;
        entity.ProjectType = dto.ProjectType;
        entity.ProjectTag = dto.ProjectTag;
        entity.Priority = dto.Priority;
        entity.ProjectStage = dto.ProjectStage;
        entity.ProjectStatus = dto.ProjectStatus;
        entity.StartTime = dto.StartTime;
        entity.EndTime = dto.EndTime;
        var managerUserId = dto.ManagerUserId ?? dto.ManagerId;
        entity.ManagerUserId = managerUserId;
        entity.ManagerName = dto.ManagerName;
        entity.ManagerId = int.TryParse(managerUserId, out var managerId) ? managerId : 0;
        entity.CustomerName = dto.CustomerName;
        entity.ContractCode = dto.ContractCode;
        entity.Remark = dto.Remark;
    }

    private static void ApplyTask(TaskSaveDto dto, ProjectTaskEntity entity)
    {
        entity.ProjectId = dto.ProjectId;
        entity.ParentId = dto.ParentId;
        entity.TaskName = dto.TaskName;
        entity.TaskType = dto.TaskType;
        entity.Priority = dto.Priority;
        entity.Progress = Math.Clamp(dto.Progress, 0, 100);
        entity.TaskStatus = dto.TaskStatus;
        entity.ExecutorId = dto.ExecutorId;
        entity.StartTime = dto.StartTime;
        entity.EndTime = dto.EndTime;
        entity.Sort = dto.Sort;
        entity.Remark = dto.Remark;
    }

    private static ProjectOutputDto MapProject(ProjectEntity x) => new()
    {
        Id = x.Id,
        ProjectCode = x.ProjectCode,
        ProjectName = x.ProjectName,
        ProjectType = x.ProjectType,
        ProjectTag = x.ProjectTag,
        Priority = x.Priority,
        ProjectStage = x.ProjectStage,
        ProjectStatus = x.ProjectStatus,
        Progress = x.Progress,
        StartTime = x.StartTime,
        EndTime = x.EndTime,
        ManagerId = string.IsNullOrWhiteSpace(x.ManagerUserId) ? x.ManagerId.ToString() : x.ManagerUserId,
        ManagerUserId = string.IsNullOrWhiteSpace(x.ManagerUserId) ? x.ManagerId.ToString() : x.ManagerUserId,
        ManagerName = x.ManagerName,
        CustomerName = x.CustomerName,
        ContractCode = x.ContractCode,
        Remark = x.Remark,
        CreateTime = x.CreateTime,
        UpdateTime = x.UpdateTime
    };

    private static TaskOutputDto MapTask(ProjectTaskEntity x) => new()
    {
        Id = x.Id,
        ProjectId = x.ProjectId,
        ParentId = x.ParentId,
        TaskName = x.TaskName,
        TaskType = x.TaskType,
        Priority = x.Priority,
        Progress = x.Progress,
        TaskStatus = x.TaskStatus,
        ExecutorId = x.ExecutorId,
        StartTime = x.StartTime,
        EndTime = x.EndTime,
        Sort = x.Sort,
        Remark = x.Remark,
        CreateTime = x.CreateTime,
        UpdateTime = x.UpdateTime
    };

    private static ProjectFileOutputDto MapFile(ProjectFileEntity x) => new()
    {
        Id = x.Id,
        ProjectId = x.ProjectId,
        TaskId = x.TaskId,
        FileName = x.FileName,
        FileSuffix = x.FileSuffix,
        FileSize = x.FileSize,
        FilePath = x.FilePath,
        FolderType = x.FolderType,
        UploadUserId = x.UploadUserId,
        CreateTime = x.CreateTime
    };

    private sealed class ProjectKeyItem
    {
        public int Id { get; set; }
        public string ProjectName { get; set; } = string.Empty;
    }
}
