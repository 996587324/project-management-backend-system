using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.ProjectManage;
using System.Text;

namespace calmadmin.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectManageController : ControllerBase
{
    private readonly IProjectManageService _service;
    private readonly IWebHostEnvironment _environment;

    public ProjectManageController(IProjectManageService service, IWebHostEnvironment environment)
    {
        _service = service;
        _environment = environment;
    }

    [HttpGet("projects")]
    public async Task<IActionResult> GetProjects([FromQuery] ProjectQueryDto query) => Success(await _service.GetProjectPageAsync(query));

    [HttpGet("projects/all")]
    public async Task<IActionResult> GetAllProjects()
    {
        var res = await _service.GetProjectListAsync();
        return Success(res); 
    } 

    [HttpGet("projects/stages")]
    public async Task<IActionResult> GetProjectStages() => Success(await _service.GetProjectStagesAsync());

    [HttpGet("projects/{id:int}")]
    public async Task<IActionResult> GetProject(int id)
    {
        var data = await _service.GetProjectAsync(id);
        return data == null ? Error("项目不存在", 404) : Success(data);
    }

    [HttpPost("projects")]
    public async Task<IActionResult> CreateProject([FromBody] ProjectSaveDto dto)
    {
       var res = new { id = await _service.CreateProjectAsync(dto) };
       return  Success(res, "创建成功");
    }

    [HttpPost("projects/import")]
    public async Task<IActionResult> ImportProjects([FromForm] IFormFile file, [FromForm] string? userId)
    {
        try
        {
            if (file == null || file.Length == 0) return Error("请选择要导入的文件", 400);
            await using var stream = file.OpenReadStream();
            var result = await _service.ImportProjectsAsync(file.FileName, stream, userId);
            return Success(result, result.Message);
        }
        catch (Exception ex)
        {
            return Error($"导入失败: {ex.Message}", 500);
        }
    }

    [HttpGet("projects/import-template")]
    public IActionResult DownloadProjectImportTemplate()
    {
        var csv = new StringBuilder();
        csv.AppendLine("项目编号,项目名称,项目类型,开始时间,结束时间,负责人ID,负责人名称,客户名称,合同编号,项目阶段,项目状态,优先级,项目标签,备注");
        csv.AppendLine("PM-2026-001,示例项目,验收,2026-07-01,2026-07-31,1,系统管理员,示例客户,HT-001,开箱,待启动,中,示例标签,示例备注");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "项目批量导入模板.csv");
    }

    [HttpPost("projects/{id:int}")]
    public async Task<IActionResult> UpdateProject(int id, [FromBody] ProjectSaveDto dto)
    {
        dto.Id = id;
        return Success(await _service.UpdateProjectAsync(dto), "保存成功");
    }

    [HttpPost("projects/{id:int}/status")]
    public async Task<IActionResult> ChangeProjectStatus(int id, [FromBody] StatusDto dto) => Success(await _service.ChangeProjectStatusAsync(id, dto.Status), "状态已更新");

    [HttpPost("projects/delete")]
    public async Task<IActionResult> DeleteProject([FromBody] IdDto dto)
    {
        var id = dto.Id;
       return Success(await _service.DeleteProjectAsync(dto.Id), "删除成功");
    }

    [HttpGet("tasks")]
    public async Task<IActionResult> GetTasks([FromQuery] TaskQueryDto query) => Success(await _service.GetTaskPageAsync(query));

    [HttpGet("tasks/tree")]
    public async Task<IActionResult> GetTaskTree([FromQuery] string? projectId, [FromQuery] string? executorId) =>
        Success(await _service.GetTaskTreeAsync(projectId, executorId));

    [HttpGet("tasks/parent-options")]
    public async Task<IActionResult> GetTaskParentOptions([FromQuery] string? projectId, [FromQuery] int currentId = 0) =>
        Success(await _service.GetTaskParentOptionsAsync(projectId, currentId));

    [HttpGet("tasks/types")]
    public async Task<IActionResult> GetTaskTypes() => Success(await _service.GetTaskTypeOptionsAsync());

    [HttpPost("tasks")]
    public async Task<IActionResult> CreateTask([FromBody] TaskSaveDto dto) => Success(new { id = await _service.CreateTaskAsync(dto) }, "创建成功");

    [HttpPost("tasks/{id:int}")]
    public async Task<IActionResult> UpdateTask(int id, [FromBody] TaskSaveDto dto)
    {
        dto.Id = id;
        return Success(await _service.UpdateTaskAsync(dto), "保存成功");
    }

    [HttpPost("tasks/{id:int}/status")]
    public async Task<IActionResult> ChangeTaskStatus(int id, [FromBody] StatusDto dto) => Success(await _service.ChangeTaskStatusAsync(id, dto.Status), "状态已更新");

    [HttpPost("tasks/delete")]
    public async Task<IActionResult> DeleteTask([FromBody] IdDto dto) => Success(await _service.DeleteTaskAsync(dto.Id), "删除成功");

    [HttpGet("files")]
    public async Task<IActionResult> GetFiles([FromQuery] FileQueryDto query) => Success(await _service.GetFilePageAsync(query));

    [HttpPost("files")]
    public async Task<IActionResult> UploadFile([FromForm] IFormFile file, [FromForm] int projectId, [FromForm] string projectName, [FromForm] int? taskId, [FromForm] string folderType, [FromForm] int uploadUserId = 1)
    {
        try
        {
            if (file.Length == 0) return Error("Invalid upload request", 400);
            if (projectId <= 0) return Error("Invalid upload request", 400);

            var originalFileName = NormalizeUploadFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(originalFileName)) return Error("Invalid upload request", 400);

            var projectDirectoryName = NormalizeProjectDirectoryName(projectName, projectId);
            var uploadRoot = Path.Combine(_environment.ContentRootPath, "uploads", projectDirectoryName);
            Directory.CreateDirectory(uploadRoot);
            var extension = Path.GetExtension(originalFileName);
            var fullPath = Path.Combine(uploadRoot, originalFileName);
            await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream);
            }

            var relativePath = Path.Combine("uploads", projectDirectoryName, originalFileName).Replace("\\", "/");
            var id = await _service.CreateFileAsync(new ProjectFileSaveDto
            {
                ProjectId = projectId,
                TaskId = taskId,
                FileName = originalFileName,
                FileSuffix = extension.TrimStart('.'),
                FileSize = Math.Round(file.Length / 1024m, 2),
                FilePath = relativePath,
                FolderType = folderType,
                UploadUserId = uploadUserId
            });
            return Success(new { id, filePath = relativePath }, "上传成功");
        }
        catch(Exception ex)
        {
            return Error($"上传失败: {ex.Message}", 500);
        }
       
    }

    [HttpGet("files/{id:int}/download")]
    public async Task<IActionResult> DownloadFile(int id)
    {
        var file = await _service.GetFileAsync(id);
        if (file == null) return NotFound();
        var fullPath = ResolveProjectFilePath(file.FilePath);
        if (!System.IO.File.Exists(fullPath)) return NotFound();
        return PhysicalFile(fullPath, "application/octet-stream", file.FileName);
    }

    [HttpGet("files/{id:int}/preview")]
    public async Task<IActionResult> PreviewFile(int id)
    {
        var file = await _service.GetFileAsync(id);
        if (file == null) return NotFound();

        var fullPath = ResolveProjectFilePath(file.FilePath);
        if (!System.IO.File.Exists(fullPath)) return NotFound();

        Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";
        return PhysicalFile(fullPath, GetContentType(file.FileSuffix), enableRangeProcessing: true);
    }

    [HttpPost("files/delete")]
    public async Task<IActionResult> DeleteFile([FromBody] IdDto dto)
    {
        var file = await _service.GetFileAsync(dto.Id);
        if (file != null)
        {
            var fullPath = ResolveProjectFilePath(file.FilePath);
            if (System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }

        return Success(await _service.DeleteFileAsync(dto.Id), "删除成功");
    }

    [HttpPost("files/project-counts")]
    public IActionResult GetProjectFileCounts([FromBody] ProjectFileCountRequest dto)
    {
        var result = dto.ProjectNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToDictionary(x => x, CountProjectFiles);

        return Success(result);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard([FromQuery] string? userId)
    {
       return Success(await _service.GetDashboardStatsAsync(userId));
    }

    private static IActionResult Success(object? data, string msg = "success") => new JsonResult(new { code = 200, msg, data, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private static IActionResult Error(string msg, int code) => new JsonResult(new { code, msg, data = new { }, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

    private string ResolveProjectFilePath(string relativePath)
    {
        var normalizedPath = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
        var fullPath = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, normalizedPath));
        var contentRoot = Path.GetFullPath(_environment.ContentRootPath);

        if (!fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid file path.");
        }

        return fullPath;
    }

    private static string NormalizeUploadFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;

        var safeName = Path.GetFileName(fileName);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidChar, '_');
        }
        return safeName;
    }

    private static string NormalizeProjectDirectoryName(string projectName, int projectId)
    {
        var safeName = NormalizeUploadFileName(projectName);
        return string.IsNullOrWhiteSpace(safeName) ? $"project-{projectId}" : safeName;
    }

    private int CountProjectFiles(string projectName)
    {
        var projectDirectoryName = NormalizeProjectDirectoryName(projectName, 0);
        var projectPath = Path.Combine(_environment.ContentRootPath, "uploads", projectDirectoryName);
        return Directory.Exists(projectPath)
            ? Directory.EnumerateFiles(projectPath, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private static string GetContentType(string suffix)
    {
        return suffix.TrimStart('.').ToLowerInvariant() switch
        {
            "pdf" => "application/pdf",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "csv" => "text/csv; charset=utf-8",
            "txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}

public class StatusDto
{
    public byte Status { get; set; }
}

public class ProjectFileCountRequest
{
    public List<string> ProjectNames { get; set; } = new();
}
