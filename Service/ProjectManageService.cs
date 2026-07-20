using Interface;
using Model.ProjectManage;
using Model.User;
using NPOI.SS.UserModel;
using SqlSugar;
using System.Globalization;
using System.Text;

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

    public async Task<ProjectBatchImportResultDto> ImportProjectsAsync(string fileName, Stream stream, string? userId = null)
    {
        var rows = ReadProjectImportRows(fileName, stream);
        if (rows.Count < 2) throw new InvalidOperationException("文件内容为空，或未识别到表头和数据行");

        var headers = rows[0].Select(NormalizeImportHeader).ToList();
        var existingCodes = (await GetProjectListAsync())
            .Select(x => x.ProjectCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var importCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = await GetImportUsersAsync();
        var result = new ProjectBatchImportResultDto();

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            result.Total++;
            var project = BuildProjectFromImportRow(headers, row, userId, users);
            var errors = ValidateImportedProject(project, existingCodes, importCodes);
            var item = new ProjectBatchImportRowDto
            {
                RowIndex = i + 1,
                ProjectCode = project.ProjectCode,
                ProjectName = project.ProjectName
            };

            if (errors.Any())
            {
                item.Success = false;
                item.Message = string.Join("；", errors);
                result.FailedCount++;
                result.Rows.Add(item);
                continue;
            }

            try
            {
                item.ProjectId = await CreateProjectAsync(project);
                item.Success = true;
                item.Message = "导入成功";
                result.CreatedCount++;
                existingCodes.Add(project.ProjectCode);
            }
            catch (Exception ex)
            {
                item.Success = false;
                item.Message = ex.Message;
                result.FailedCount++;
            }

            result.Rows.Add(item);
        }

        result.Message = $"批量导入完成：共识别 {result.Total} 行，成功 {result.CreatedCount} 行，失败 {result.FailedCount} 行";
        return result;
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

    public async Task<List<TaskOutputDto>> GetTaskTreeAsync(string? projectId, string? executorId = null)
    {
        var activeProjectKeys = await GetActiveProjectKeysAsync();
        var selectedProjectKeys = await GetProjectKeysAsync(projectId);
        var list = await Db.Queryable<ProjectTaskEntity>()
            .Where(x => x.IsDelete == 0)
            .Where(x => activeProjectKeys.Contains(x.ProjectId))
            .WhereIF(!string.IsNullOrWhiteSpace(projectId), x => selectedProjectKeys.Contains(x.ProjectId))
            .WhereIF(!string.IsNullOrWhiteSpace(executorId), x => x.ExecutorId == executorId)
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

    private static List<List<string>> ReadProjectImportRows(string fileName, Stream stream)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".csv" => ParseCsvRows(ReadAllText(stream)),
            ".xls" or ".xlsx" => ReadExcelRows(stream),
            _ => throw new InvalidOperationException("仅支持 CSV、XLS、XLSX 文件")
        };
    }

    private static string ReadAllText(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static List<List<string>> ReadExcelRows(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        var rows = new List<List<string>>();
        using var workbook = WorkbookFactory.Create(stream);
        var sheet = workbook.NumberOfSheets > 0 ? workbook.GetSheetAt(0) : null;
        if (sheet == null) return rows;

        var maxCellCount = 0;
        for (var i = sheet.FirstRowNum; i <= sheet.LastRowNum; i++)
        {
            var row = sheet.GetRow(i);
            if (row == null) continue;
            maxCellCount = Math.Max(maxCellCount, row.LastCellNum);
        }

        for (var i = sheet.FirstRowNum; i <= sheet.LastRowNum; i++)
        {
            var sheetRow = sheet.GetRow(i);
            if (sheetRow == null) continue;
            var row = new List<string>();
            for (var j = 0; j < maxCellCount; j++)
            {
                row.Add(GetCellText(sheetRow.GetCell(j)).Trim());
            }
            if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
        }

        return rows;
    }

    private static string GetCellText(ICell? cell)
    {
        if (cell == null) return string.Empty;

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue ?? string.Empty,
            CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty
                : FormatNumber(cell.NumericCellValue),
            CellType.Formula => GetFormulaCellText(cell),
            _ => string.Empty
        };
    }

    private static string GetFormulaCellText(ICell cell)
    {
        return cell.CachedFormulaResultType switch
        {
            CellType.String => cell.StringCellValue ?? string.Empty,
            CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty
                : FormatNumber(cell.NumericCellValue),
            _ => string.Empty
        };
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value % 1) < 0.0000001
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static List<List<string>> ParseCsvRows(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var current = content[i];
            if (current == '"')
            {
                if (inQuotes && i + 1 < content.Length && content[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (current == ',' && !inQuotes)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if ((current == '\r' || current == '\n') && !inQuotes)
            {
                if (current == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
                row = new List<string>();
            }
            else
            {
                cell.Append(current);
            }
        }

        row.Add(cell.ToString());
        if (row.Any(x => !string.IsNullOrWhiteSpace(x))) rows.Add(row);
        return rows;
    }

    private async Task<List<SysUserInfo>> GetImportUsersAsync()
    {
        if (!Db.DbMaintenance.IsAnyTable("sys_user_info")) return new List<SysUserInfo>();
        return await Db.Queryable<SysUserInfo>().ToListAsync();
    }

    private static ProjectSaveDto BuildProjectFromImportRow(
        List<string> headers,
        List<string> row,
        string? userId,
        List<SysUserInfo> users)
    {
        string Read(params string[] aliases)
        {
            foreach (var alias in aliases.Select(NormalizeImportHeader))
            {
                var index = headers.FindIndex(x => x == alias);
                if (index >= 0 && index < row.Count) return row[index].Trim();
            }
            return string.Empty;
        }

        var managerValue = Read("managerId", "managerUserId", "负责人ID", "负责人用户ID", "负责人", "manager");
        var managerName = Read("managerName", "负责人名称", "负责人姓名", "managerName");
        var resolvedManager = ResolveImportManager(managerValue, managerName, userId, users);

        return new ProjectSaveDto
        {
            ProjectCode = Read("projectCode", "项目编号", "编号", "code"),
            ProjectName = Read("projectName", "项目名称", "名称", "name"),
            ProjectType = DefaultIfEmpty(Read("projectType", "项目类型", "类型", "type"), "验收"),
            ProjectTag = EmptyToNull(Read("projectTag", "项目标签", "标签", "tag")),
            Priority = ParsePriority(Read("priority", "优先级")),
            ProjectStage = ParseStage(Read("projectStage", "项目阶段", "阶段", "stage")),
            ProjectStatus = ParseProjectStatus(Read("projectStatus", "项目状态", "状态", "status")),
            Progress = 0,
            StartTime = ParseImportDate(Read("startTime", "开始时间", "开始日期", "start")),
            EndTime = ParseImportDate(Read("endTime", "结束时间", "结束日期", "end")),
            ManagerId = resolvedManager.UserId,
            ManagerUserId = resolvedManager.UserId,
            ManagerName = resolvedManager.Name,
            CustomerName = EmptyToNull(Read("customerName", "客户名称", "客户")),
            ContractCode = EmptyToNull(Read("contractCode", "合同号", "合同编号")),
            Remark = EmptyToNull(Read("remark", "备注"))
        };
    }

    private static List<string> ValidateImportedProject(
        ProjectSaveDto project,
        HashSet<string> existingCodes,
        HashSet<string> importCodes)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(project.ProjectCode)) errors.Add("项目编号不能为空");
        if (string.IsNullOrWhiteSpace(project.ProjectName)) errors.Add("项目名称不能为空");
        if (project.StartTime == default) errors.Add("开始时间不能为空或格式不正确");
        if (project.EndTime == default) errors.Add("结束时间不能为空或格式不正确");
        if (project.StartTime != default && project.EndTime != default && project.StartTime.Date > project.EndTime.Date)
            errors.Add("开始时间不能晚于结束时间");
        if (string.IsNullOrWhiteSpace(project.ManagerId)) errors.Add("负责人不能为空");
        if (!string.IsNullOrWhiteSpace(project.ProjectCode) && existingCodes.Contains(project.ProjectCode))
            errors.Add("项目编号已存在");
        if (!string.IsNullOrWhiteSpace(project.ProjectCode) && !importCodes.Add(project.ProjectCode))
            errors.Add("导入文件中项目编号重复");
        return errors;
    }

    private static (string UserId, string Name) ResolveImportManager(
        string managerValue,
        string managerName,
        string? fallbackUserId,
        List<SysUserInfo> users)
    {
        var input = string.IsNullOrWhiteSpace(managerValue) ? managerName : managerValue;
        var user = string.IsNullOrWhiteSpace(input)
            ? null
            : users.FirstOrDefault(x =>
                string.Equals(x.UserId, input, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.UserName, input, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.NickName, input, StringComparison.OrdinalIgnoreCase));

        if (user != null)
            return (user.UserId, string.IsNullOrWhiteSpace(user.NickName) ? user.UserName : user.NickName);

        if (!string.IsNullOrWhiteSpace(input))
            return (input.Trim(), string.IsNullOrWhiteSpace(managerName) ? input.Trim() : managerName.Trim());

        if (!string.IsNullOrWhiteSpace(fallbackUserId))
        {
            var fallback = users.FirstOrDefault(x => x.UserId == fallbackUserId);
            return (fallbackUserId.Trim(), fallback == null ? fallbackUserId.Trim() : string.IsNullOrWhiteSpace(fallback.NickName) ? fallback.UserName : fallback.NickName);
        }

        return (string.Empty, string.Empty);
    }

    private static string NormalizeImportHeader(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private static string DefaultIfEmpty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? EmptyToNull(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTime ParseImportDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)) return date;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            try
            {
                return DateTime.FromOADate(number);
            }
            catch
            {
                return default;
            }
        }
        return default;
    }

    private static byte ParsePriority(string value)
    {
        if (byte.TryParse(value, out var number) && number is >= 1 and <= 3) return number;
        return value.Trim() switch
        {
            "高" or "高优先级" => 1,
            "低" or "低优先级" => 3,
            _ => 2
        };
    }

    private static byte ParseStage(string value)
    {
        if (byte.TryParse(value, out var number) && number is >= 1 and <= 3) return number;
        return value.Trim() switch
        {
            "启用" => 2,
            "验收" => 3,
            _ => 1
        };
    }

    private static byte ParseProjectStatus(string value)
    {
        if (byte.TryParse(value, out var number) && number is >= 1 and <= 6) return number;
        return value.Trim() switch
        {
            "进行中" => 2,
            "延期" => 3,
            "暂停" => 4,
            "完成" or "已完成" => 5,
            "归档" or "已归档" => 6,
            _ => 1
        };
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
