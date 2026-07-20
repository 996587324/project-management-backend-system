using System.Text.Json;
using Interface;
using Model.Agent;
using Model.Agent.ToolArguments;
using Model.ProjectManage;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>
/// 项目管理 Agent 的业务工具执行器，负责把 Agent 工具调用转换为项目、任务、文件等业务服务调用。
/// </summary>
public class ProjectManagementAgentToolExecutor : IAgentBusinessToolExecutor
{
    private readonly IProjectManageService _projectService;
    private readonly SqlSugarScope _dbScope;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProjectManagementAgentToolExecutor(IProjectManageService projectService, SqlSugarScope dbScope)
    {
        _projectService = projectService;
        _dbScope = dbScope;
    }

    /// <summary>
    /// 判断当前执行器是否支持指定的服务名称，调度器用它选择真正执行工具的服务。
    /// </summary>
    public bool CanExecute(string serviceName, string actionName) =>
        serviceName.Equals("ProjectManagement", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Agent 工具执行入口，根据 actionName 分发到具体的查询、新增或修改方法。
    /// </summary>
    public async Task<AgentBusinessToolResult> ExecuteAsync(
        string actionName,
        JsonElement arguments,
        AgentBusinessToolContext context,
        CancellationToken cancellationToken = default)
    {
        return actionName switch
        {
            "GetProjects" => await GetProjectsAsync(arguments),
            "GetProject" => await GetProjectAsync(arguments),
            "GetDashboard" => AgentBusinessToolResult.Ok(
                await _projectService.GetDashboardStatsAsync(context.UserId), "工作台数据查询成功"),
            "GetTasks" => await GetTasksAsync(arguments),
            "GetTask" => await GetTaskAsync(arguments),
            "GetFiles" => await GetFilesAsync(arguments),
            "CreateProjects" => await CreateProjectsAsync(arguments, context.UserId),
            "UpdateProjects" => await UpdateProjectsAsync(arguments, context.UserId),
            "ChangeProjectStatus" => await ChangeProjectStatusAsync(arguments),
            "CreateTasks" => await CreateTasksAsync(arguments, context.UserId),
            "UpdateTasks" => await UpdateTasksAsync(arguments, context.UserId),
            "ChangeTaskStatus" => await ChangeTaskStatusAsync(arguments),
            _ => AgentBusinessToolResult.Fail($"项目管理执行器未实现动作：{actionName}")
        };
    }

    /// <summary>
    /// 查询项目列表，并按名称、编号、负责人和状态过滤。
    /// </summary>
    private async Task<AgentBusinessToolResult> GetProjectsAsync(JsonElement arguments)
    {
        var filter = Deserialize<ProjectListArguments>(arguments);
        var projects = await _projectService.GetProjectListAsync();
        var query = projects.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.ProjectName))
            query = query.Where(x => x.ProjectName.Contains(filter.ProjectName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.ProjectCode))
            query = query.Where(x => x.ProjectCode.Contains(filter.ProjectCode, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.ManagerName))
            query = query.Where(x =>
                (x.ManagerName ?? string.Empty).Contains(filter.ManagerName, StringComparison.OrdinalIgnoreCase) ||
                (x.ManagerUserId ?? x.ManagerId).Contains(filter.ManagerName, StringComparison.OrdinalIgnoreCase));
        if (filter.ProjectStatus.HasValue)
            query = query.Where(x => x.ProjectStatus == filter.ProjectStatus.Value);

        var page = Math.Max(filter.Current, 1);
        var size = Math.Clamp(filter.Size, 1, 100);
        var matched = query.ToList();
        var result = new PagedResultDto<ProjectOutputDto>
        {
            Records = matched.Skip((page - 1) * size).Take(size).ToList(),
            Current = page,
            Size = size,
            Total = matched.Count
        };
        return AgentBusinessToolResult.Ok(result, $"查询到 {matched.Count} 个项目");
    }

    /// <summary>
    /// 按 Id、项目编号或项目名称查询单个项目详情。
    /// </summary>
    private async Task<AgentBusinessToolResult> GetProjectAsync(JsonElement arguments)
    {
        var identity = Deserialize<ProjectIdentityArguments>(arguments);
        var project = await ResolveProjectAsync(identity.Id, identity.ProjectCode, identity.ProjectName);
        return project == null
            ? AgentBusinessToolResult.Fail("未找到匹配的项目")
            : AgentBusinessToolResult.Ok(project, "项目详情查询成功");
    }

    /// <summary>
    /// 查询任务树，并按任务名称、执行人和状态过滤。
    /// </summary>
    private async Task<AgentBusinessToolResult> GetTasksAsync(JsonElement arguments)
    {
        var filter = Deserialize<TaskListArguments>(arguments);
        var result = await _projectService.GetTaskPageAsync(new TaskQueryDto
        {
            Current = Math.Max(filter.Current, 1),
            Size = Math.Clamp(filter.Size, 1, 100),
            ProjectId = filter.ProjectName,
            TaskName = filter.TaskName,
            ExecutorId = filter.ExecutorId,
            TaskStatus = filter.TaskStatus
        });
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 个任务");
    }

    /// <summary>
    /// 按任务 Id 查询单个任务详情。
    /// </summary>
    private async Task<AgentBusinessToolResult> GetTaskAsync(JsonElement arguments)
    {
        var identity = Deserialize<IdArguments>(arguments);
        var task = await _projectService.GetTaskAsync(identity.Id);
        return task == null
            ? AgentBusinessToolResult.Fail("任务不存在")
            : AgentBusinessToolResult.Ok(task, "任务详情查询成功");
    }

    /// <summary>
    /// 查询项目文件列表，支持项目、任务、文件夹类型和文件名筛选。
    /// </summary>
    private async Task<AgentBusinessToolResult> GetFilesAsync(JsonElement arguments)
    {
        var filter = Deserialize<FileListArguments>(arguments);
        var result = await _projectService.GetFilePageAsync(new FileQueryDto
        {
            Current = Math.Max(filter.Current, 1),
            Size = Math.Clamp(filter.Size, 1, 100),
            ProjectId = filter.ProjectId,
            TaskId = filter.TaskId,
            FolderType = filter.FolderType,
            FileName = filter.FileName
        });
        return AgentBusinessToolResult.Ok(result, $"查询到 {result.Total} 个文件");
    }

    /// <summary>
    /// 批量新增项目；如果检测到相同项目，会根据 updateIfExists 决定提示确认还是转为更新。
    /// </summary>
    private async Task<AgentBusinessToolResult> CreateProjectsAsync(JsonElement arguments, string? userId)
    {
        var payload = Deserialize<ProjectCreateArguments>(arguments);
        var rows = new List<ExecutionRow>();
        var users = await GetUsersAsync();

        foreach (var item in payload.Projects)
        {
            var row = new ExecutionRow { Key = item.ProjectCode, Name = item.ProjectName };
            try
            {
                ValidateDateRange(item.StartTime, item.EndTime);
                var manager = ResolveUser(item.ManagerId, item.ManagerName, userId, users);
                var dto = new ProjectSaveDto
                {
                    ProjectCode = Required(item.ProjectCode, "项目编号"),
                    ProjectName = Required(item.ProjectName, "项目名称"),
                    ProjectType = string.IsNullOrWhiteSpace(item.ProjectType) ? "验收" : item.ProjectType.Trim(),
                    ProjectTag = EmptyToNull(item.ProjectTag),
                    Priority = item.Priority is >= 1 and <= 3 ? item.Priority.Value : (byte)2,
                    ProjectStage = item.ProjectStage is >= 1 and <= 3 ? item.ProjectStage.Value : (byte)1,
                    ProjectStatus = item.ProjectStatus is >= 1 and <= 6 ? item.ProjectStatus.Value : (byte)1,
                    Progress = 0,
                    StartTime = item.StartTime.Date,
                    EndTime = item.EndTime.Date,
                    ManagerId = manager.UserId,
                    ManagerUserId = manager.UserId,
                    ManagerName = manager.Name,
                    CustomerName = EmptyToNull(item.CustomerName),
                    ContractCode = EmptyToNull(item.ContractCode),
                    Remark = EmptyToNull(item.Remark)
                };
                var existing = await ResolveDuplicateProjectAsync(dto.ProjectCode, dto.ProjectName);
                if (existing != null)
                {
                    row.Id = existing.Id;
                    if (!payload.UpdateIfExists)
                    {
                        row.Message = $"检测到相同项目（{existing.ProjectCode}/{existing.ProjectName}），是否需要进行更新？如需更新，请确认后设置 updateIfExists=true 再执行。";
                        rows.Add(row);
                        continue;
                    }

                    dto.Id = existing.Id;
                    dto.Progress = existing.Progress;
                    row.Success = await _projectService.UpdateProjectAsync(dto);
                    row.Message = row.Success ? "更新成功" : "未产生变更";
                    rows.Add(row);
                    continue;
                }

                row.Id = await _projectService.CreateProjectAsync(dto);
                if (!row.Id.HasValue || row.Id.Value <= 0)
                {
                    row.Message = "新增失败，数据表未返回有效主键";
                    rows.Add(row);
                    continue;
                }

                var created = await _projectService.GetProjectAsync(row.Id.Value);
                row.Success = created != null;
                row.Message = row.Success ? "新增成功" : "新增失败，未在数据表中查询到新增记录";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }

        return BuildBatchResult(rows, "项目新增");
    }

    /// <summary>
    /// 批量修改项目，未传入的字段沿用数据库中的原值。
    /// </summary>
    private async Task<AgentBusinessToolResult> UpdateProjectsAsync(JsonElement arguments, string? userId)
    {
        var payload = Deserialize<ProjectUpdateArguments>(arguments);
        var rows = new List<ExecutionRow>();
        var users = await GetUsersAsync();

        foreach (var item in payload.Projects)
        {
            var row = new ExecutionRow
            {
                Key = item.Id?.ToString() ?? item.ProjectCode ?? item.ProjectName ?? string.Empty,
                Name = item.ProjectName ?? string.Empty
            };
            try
            {
                var current = await ResolveProjectAsync(item.Id, item.ProjectCode, item.ProjectName);
                if (current == null) throw new InvalidOperationException("项目不存在");

                var manager = string.IsNullOrWhiteSpace(item.ManagerId) && string.IsNullOrWhiteSpace(item.ManagerName)
                    ? (current.ManagerUserId ?? current.ManagerId, current.ManagerName ?? current.ManagerUserId ?? current.ManagerId)
                    : ResolveUser(item.ManagerId, item.ManagerName, userId, users);
                var dto = new ProjectSaveDto
                {
                    Id = current.Id,
                    ProjectCode = string.IsNullOrWhiteSpace(item.NewProjectCode) ? current.ProjectCode : item.NewProjectCode.Trim(),
                    ProjectName = string.IsNullOrWhiteSpace(item.NewProjectName) ? current.ProjectName : item.NewProjectName.Trim(),
                    ProjectType = item.ProjectType ?? current.ProjectType,
                    ProjectTag = item.ProjectTag ?? current.ProjectTag,
                    Priority = item.Priority is >= 1 and <= 3 ? item.Priority.Value : current.Priority,
                    ProjectStage = item.ProjectStage is >= 1 and <= 3 ? item.ProjectStage.Value : current.ProjectStage,
                    ProjectStatus = item.ProjectStatus is >= 1 and <= 6 ? item.ProjectStatus.Value : current.ProjectStatus,
                    Progress = current.Progress,
                    StartTime = item.StartTime?.Date ?? current.StartTime,
                    EndTime = item.EndTime?.Date ?? current.EndTime,
                    ManagerId = manager.Item1,
                    ManagerUserId = manager.Item1,
                    ManagerName = manager.Item2,
                    CustomerName = item.CustomerName ?? current.CustomerName,
                    ContractCode = item.ContractCode ?? current.ContractCode,
                    Remark = item.Remark ?? current.Remark
                };
                ValidateDateRange(dto.StartTime, dto.EndTime);
                row.Success = await _projectService.UpdateProjectAsync(dto);
                row.Id = current.Id;
                row.Message = row.Success ? "修改成功" : "未产生变更";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }

        return BuildBatchResult(rows, "项目修改");
    }

    /// <summary>
    /// 批量变更项目状态，只修改状态字段。
    /// </summary>
    private async Task<AgentBusinessToolResult> ChangeProjectStatusAsync(JsonElement arguments)
    {
        var payload = Deserialize<ProjectStatusArguments>(arguments);
        var rows = new List<ExecutionRow>();
        foreach (var item in payload.Projects)
        {
            var row = new ExecutionRow
            {
                Key = item.Id?.ToString() ?? item.ProjectCode ?? item.ProjectName ?? string.Empty,
                Name = item.ProjectName ?? string.Empty
            };
            try
            {
                var current = await ResolveProjectAsync(item.Id, item.ProjectCode, item.ProjectName);
                if (current == null) throw new InvalidOperationException("项目不存在");
                row.Id = current.Id;
                row.Success = await _projectService.ChangeProjectStatusAsync(current.Id, item.Status);
                row.Message = row.Success ? "状态修改成功" : "未产生变更";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }
        return BuildBatchResult(rows, "项目状态修改");
    }

    /// <summary>
    /// 批量新增任务，并校验任务周期不能超出所属项目或父任务周期。
    /// </summary>
    private async Task<AgentBusinessToolResult> CreateTasksAsync(JsonElement arguments, string? userId)
    {
        var payload = Deserialize<TaskCreateArguments>(arguments);
        var rows = new List<ExecutionRow>();
        var users = await GetUsersAsync();

        foreach (var item in payload.Tasks)
        {
            var row = new ExecutionRow { Key = item.ProjectName, Name = item.TaskName };
            try
            {
                var project = await ResolveProjectAsync(null, null, item.ProjectName);
                if (project == null) throw new InvalidOperationException("所属项目不存在");
                ValidateDateRange(item.StartTime, item.EndTime);
                if (item.StartTime.Date < project.StartTime.Date || item.EndTime.Date > project.EndTime.Date)
                    throw new InvalidOperationException($"任务时间必须在项目周期内：{project.StartTime:yyyy-MM-dd} 至 {project.EndTime:yyyy-MM-dd}");

                var executor = ResolveUser(item.ExecutorId, null, userId, users);
                var dto = new TaskSaveDto
                {
                    ProjectId = project.ProjectName,
                    ParentId = item.ParentId ?? 0,
                    TaskName = Required(item.TaskName, "任务名称"),
                    TaskType = EmptyToNull(item.TaskType),
                    Priority = item.Priority is >= 1 and <= 3 ? item.Priority.Value : (byte)2,
                    Progress = 0,
                    TaskStatus = item.TaskStatus is >= 1 and <= 4 ? item.TaskStatus.Value : (byte)1,
                    ExecutorId = executor.UserId,
                    StartTime = item.StartTime.Date,
                    EndTime = item.EndTime.Date,
                    Sort = item.Sort ?? 0,
                    Remark = EmptyToNull(item.Remark)
                };
                row.Id = await _projectService.CreateTaskAsync(dto);
                row.Success = true;
                row.Message = "新增成功";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }
        return BuildBatchResult(rows, "任务新增");
    }

    /// <summary>
    /// 批量修改任务，自动补齐未传入字段，并校验修改后的时间范围。
    /// </summary>
    private async Task<AgentBusinessToolResult> UpdateTasksAsync(JsonElement arguments, string? userId)
    {
        var payload = Deserialize<TaskUpdateArguments>(arguments);
        var rows = new List<ExecutionRow>();
        var users = await GetUsersAsync();

        foreach (var item in payload.Tasks)
        {
            var row = new ExecutionRow { Key = item.Id.ToString(), Name = item.TaskName ?? string.Empty };
            try
            {
                var current = await _projectService.GetTaskAsync(item.Id);
                if (current == null) throw new InvalidOperationException("任务不存在");
                var projectName = string.IsNullOrWhiteSpace(item.ProjectName) ? current.ProjectName ?? current.ProjectId : item.ProjectName;
                var project = await ResolveProjectAsync(null, null, projectName);
                if (project == null) throw new InvalidOperationException("所属项目不存在");
                var startTime = item.StartTime?.Date ?? current.StartTime;
                var endTime = item.EndTime?.Date ?? current.EndTime;
                ValidateDateRange(startTime, endTime);
                if (startTime.Date < project.StartTime.Date || endTime.Date > project.EndTime.Date)
                    throw new InvalidOperationException($"任务时间必须在项目周期内：{project.StartTime:yyyy-MM-dd} 至 {project.EndTime:yyyy-MM-dd}");

                var executorId = current.ExecutorId;
                if (!string.IsNullOrWhiteSpace(item.ExecutorId))
                    executorId = ResolveUser(item.ExecutorId, null, userId, users).UserId;

                var dto = new TaskSaveDto
                {
                    Id = current.Id,
                    ProjectId = project.ProjectName,
                    ParentId = item.ParentId ?? current.ParentId,
                    TaskName = string.IsNullOrWhiteSpace(item.TaskName) ? current.TaskName : item.TaskName.Trim(),
                    TaskType = item.TaskType ?? current.TaskType,
                    Priority = item.Priority is >= 1 and <= 3 ? item.Priority.Value : current.Priority,
                    Progress = current.Progress,
                    TaskStatus = item.TaskStatus is >= 1 and <= 4 ? item.TaskStatus.Value : current.TaskStatus,
                    ExecutorId = executorId,
                    StartTime = startTime,
                    EndTime = endTime,
                    Sort = item.Sort ?? current.Sort,
                    Remark = item.Remark ?? current.Remark
                };
                row.Id = current.Id;
                row.Success = await _projectService.UpdateTaskAsync(dto);
                row.Message = row.Success ? "修改成功" : "未产生变更";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }
        return BuildBatchResult(rows, "任务修改");
    }

    /// <summary>
    /// 批量变更任务状态。
    /// </summary>
    private async Task<AgentBusinessToolResult> ChangeTaskStatusAsync(JsonElement arguments)
    {
        var payload = Deserialize<TaskStatusArguments>(arguments);
        var rows = new List<ExecutionRow>();
        foreach (var item in payload.Tasks)
        {
            var row = new ExecutionRow { Key = item.Id.ToString() };
            try
            {
                row.Id = item.Id;
                row.Success = await _projectService.ChangeTaskStatusAsync(item.Id, item.Status);
                row.Message = row.Success ? "状态修改成功" : "任务不存在或未产生变更";
            }
            catch (Exception ex)
            {
                row.Message = ex.Message;
            }
            rows.Add(row);
        }
        return BuildBatchResult(rows, "任务状态修改");
    }

    /// <summary>
    /// 统一解析项目身份，优先按 Id 查找，其次按项目编号或项目名称查找。
    /// </summary>
    private async Task<ProjectOutputDto?> ResolveProjectAsync(int? id, string? projectCode, string? projectName)
    {
        if (id.HasValue && id.Value > 0)
            return await _projectService.GetProjectAsync(id.Value);

        var projects = await _projectService.GetProjectListAsync();
        if (!string.IsNullOrWhiteSpace(projectCode))
            return projects.FirstOrDefault(x => x.ProjectCode.Equals(projectCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(projectName))
            return projects.FirstOrDefault(x => x.ProjectName.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>
    /// 新增项目前检查项目编号或项目名称是否已存在，用于避免重复写入。
    /// </summary>
    private async Task<ProjectOutputDto?> ResolveDuplicateProjectAsync(string projectCode, string projectName)
    {
        var projects = await _projectService.GetProjectListAsync();
        var code = projectCode.Trim();
        var name = projectName.Trim();
        return projects.FirstOrDefault(x => x.ProjectCode.Equals(code, StringComparison.OrdinalIgnoreCase))
               ?? projects.FirstOrDefault(x => x.ProjectName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从系统用户表读取可用用户，供负责人和执行人解析使用。
    /// </summary>
    private async Task<List<SysUserInfo>> GetUsersAsync()
    {
        if (!Db.DbMaintenance.IsAnyTable("sys_user_info")) return new List<SysUserInfo>();
        return await Db.Queryable<SysUserInfo>().Where(x => x.Enabled).ToListAsync();
    }

    /// <summary>
    /// 把用户输入的用户 ID、用户名或昵称解析成系统内的用户标识和显示名。
    /// </summary>
    private static (string UserId, string Name) ResolveUser(
        string? userValue,
        string? displayName,
        string? fallbackUserId,
        List<SysUserInfo> users)
    {
        var input = string.IsNullOrWhiteSpace(userValue) ? displayName : userValue;
        if (string.IsNullOrWhiteSpace(input)) input = fallbackUserId;
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("负责人或执行人不能为空");

        var user = users.FirstOrDefault(x =>
            x.UserId.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            x.UserName.Equals(input, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(x.NickName) && x.NickName.Equals(input, StringComparison.OrdinalIgnoreCase)));

        if (user != null)
            return (user.UserId, string.IsNullOrWhiteSpace(user.NickName) ? user.UserName : user.NickName);

        return (input.Trim(), string.IsNullOrWhiteSpace(displayName) ? input.Trim() : displayName.Trim());
    }

    /// <summary>
    /// 汇总批量执行结果，生成统一的成功数、失败数和每行明细。
    /// </summary>
    private static AgentBusinessToolResult BuildBatchResult(List<ExecutionRow> rows, string operation)
    {
        var successCount = rows.Count(x => x.Success);
        var failedCount = rows.Count - successCount;
        var message = $"{operation}完成：共 {rows.Count} 条，成功 {successCount} 条，失败 {failedCount} 条";
        return new AgentBusinessToolResult
        {
            Success = failedCount == 0,
            PartialSuccess = successCount > 0 && failedCount > 0,
            Message = message,
            ErrorMessage = failedCount > 0 && successCount == 0 ? message : null,
            Data = new { total = rows.Count, successCount, failedCount, rows }
        };
    }

    /// <summary>
    /// 将工具参数 JSON 反序列化为强类型参数对象。
    /// </summary>
    private static T Deserialize<T>(JsonElement arguments) where T : new() =>
        JsonSerializer.Deserialize<T>(arguments.GetRawText(), JsonOptions) ?? new T();

    /// <summary>
    /// 校验必填字符串；为空时抛出带字段名的业务异常。
    /// </summary>
    private static string Required(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{fieldName}不能为空") : value.Trim();

    /// <summary>
    /// 把空字符串统一转成 null，避免数据库里保存无意义的空白文本。
    /// </summary>
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 校验开始和结束日期是否合法，并保证开始时间不能晚于结束时间。
    /// </summary>
    private static void ValidateDateRange(DateTime startTime, DateTime endTime)
    {
        if (startTime == default) throw new InvalidOperationException("开始时间不能为空或格式不正确");
        if (endTime == default) throw new InvalidOperationException("结束时间不能为空或格式不正确");
        if (startTime.Date > endTime.Date) throw new InvalidOperationException("开始时间不能晚于结束时间");
    }

}
