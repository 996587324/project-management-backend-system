using Model.ProjectManage;

namespace Interface;

public interface IProjectManageService
{
    Task<PagedResultDto<ProjectOutputDto>> GetProjectPageAsync(ProjectQueryDto query);
    Task<List<ProjectOutputDto>> GetProjectListAsync();
    Task<List<ProjectStageOptionDto>> GetProjectStagesAsync();
    Task<ProjectOutputDto?> GetProjectAsync(int id);
    Task<int> CreateProjectAsync(ProjectSaveDto dto);
    Task<bool> UpdateProjectAsync(ProjectSaveDto dto);
    Task<bool> ChangeProjectStatusAsync(int id, byte status);
    Task<bool> DeleteProjectAsync(int id);

    Task<PagedResultDto<TaskOutputDto>> GetTaskPageAsync(TaskQueryDto query);
    Task<List<TaskOutputDto>> GetTaskTreeAsync(string? projectId);
    Task<List<TaskParentOptionDto>> GetTaskParentOptionsAsync(string? projectId, int currentId = 0);
    Task<List<TaskTypeOptionDto>> GetTaskTypeOptionsAsync();
    Task<TaskOutputDto?> GetTaskAsync(int id);
    Task<int> CreateTaskAsync(TaskSaveDto dto);
    Task<bool> UpdateTaskAsync(TaskSaveDto dto);
    Task<bool> ChangeTaskStatusAsync(int id, byte status);
    Task<bool> DeleteTaskAsync(int id);

    Task<PagedResultDto<ProjectFileOutputDto>> GetFilePageAsync(FileQueryDto query);
    Task<int> CreateFileAsync(ProjectFileSaveDto dto);
    Task<ProjectFileOutputDto?> GetFileAsync(int id);
    Task<bool> DeleteFileAsync(int id);

    Task<DashboardStatsDto> GetDashboardStatsAsync(string? userId);
}
