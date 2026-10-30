using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.WorkTasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace calmadmin.Controllers;

[ApiController, Authorize, ResponseCache(NoStore=true, Location=ResponseCacheLocation.None), Route("api/work-tasks")]
public sealed class WorkTasksController(ISystemApplicationService applications, IWorkTaskService service) : ControllerBase
{
    private async Task<WorkActor> Actor() => new(await applications.GetAccessAsync(User, true));
    internal static IActionResult Success(object data) => new JsonResult(new { code=200, msg="success", data }, new JsonSerializerSettings
        { ContractResolver=new CamelCasePropertyNamesContractResolver(), DateFormatString="yyyy-MM-dd'T'HH:mm:ss.fff'Z'", NullValueHandling=NullValueHandling.Include });
    [HttpGet("bootstrap")] public async Task<IActionResult> Bootstrap() => Success(await service.BootstrapAsync(await Actor()));
    [HttpGet("overview")] public async Task<IActionResult> Overview([FromQuery] WorkOverviewQuery query) => Success(await service.OverviewAsync(await Actor(), query));
    [HttpGet("candidates")] public async Task<IActionResult> Candidates([FromQuery] WorkCandidateQuery query) => Success(await service.CandidatesAsync(await Actor(), query));
    [HttpGet] public async Task<IActionResult> List([FromQuery] WorkQuery query) => Success(await service.ListAsync(await Actor(), query));
    [HttpGet("{id}")] public async Task<IActionResult> Detail(string id) => Success(await service.DetailAsync(await Actor(), id));
    [HttpPost("drafts"), RequestSizeLimit(256 * 1024)] public async Task<IActionResult> Create(WorkTaskCommand command) => Success(await service.CommandAsync(await Actor(), null, command));
    [HttpPost("{id}/commands"), RequestSizeLimit(256 * 1024)] public async Task<IActionResult> Command(string id, WorkTaskCommand command) => Success(await service.CommandAsync(await Actor(), id, command));
    [HttpGet("operations/{id}")] public async Task<IActionResult> Operation(string id) => Success(await service.OperationAsync(await Actor(), id));
    [HttpGet("inbox")] public async Task<IActionResult> Inbox() => Success(await service.InboxAsync(await Actor()));
    [HttpPost("inbox/{id}/read")] public async Task<IActionResult> Read(string id) { await service.MarkReadAsync(await Actor(), id); return Success(true); }
}

[ApiController, Authorize, ResponseCache(NoStore=true, Location=ResponseCacheLocation.None), Route("api/work-task-admin")]
public sealed class WorkTaskAdminController(ISystemApplicationService applications, IWorkTaskService service) : ControllerBase
{
    private async Task<WorkActor> Actor() => new(await applications.GetAccessAsync(User, true));
    [HttpGet] public async Task<IActionResult> Get() => WorkTasksController.Success(await service.AdministrationAsync(await Actor()));
    [HttpPost("groups")] public async Task<IActionResult> Group(WorkGroup value) => WorkTasksController.Success(await service.SaveGroupAsync(await Actor(), value));
    [HttpPost("members")] public async Task<IActionResult> Member(WorkMember value) => WorkTasksController.Success(await service.SaveMemberAsync(await Actor(), value));
    [HttpPost("grants")] public async Task<IActionResult> Grant(WorkGrant value) => WorkTasksController.Success(await service.SaveGrantAsync(await Actor(), value));
    [HttpPost("notification-rule")] public async Task<IActionResult> Rule(WorkNotificationRule value) => WorkTasksController.Success(await service.SaveRuleAsync(await Actor(), value));
    [HttpPost("deliveries/{id}/retry")] public async Task<IActionResult> Retry(string id) { await service.RetryDeliveryAsync(await Actor(),id);return WorkTasksController.Success(true); }
    [HttpPost("handover")] public async Task<IActionResult> Handover(WorkHandoverRequest value) {await service.HandoverAsync(await Actor(),value);return WorkTasksController.Success(true);}
}

[ApiController, Authorize, ResponseCache(NoStore=true, Location=ResponseCacheLocation.None), Route("api/work-task-keys")]
public sealed class WorkTaskKeysController(ISystemApplicationService applications, IWorkTaskService service) : ControllerBase
{
    private async Task<WorkActor> Actor() => new(await applications.GetAccessAsync(User, true));
    [HttpGet] public async Task<IActionResult> List() => WorkTasksController.Success(await service.KeysAsync(await Actor()));
    [HttpGet("activity")] public async Task<IActionResult> Activity() => WorkTasksController.Success(await service.KeyActivityAsync(await Actor()));
    [HttpPost] public async Task<IActionResult> Create(WorkKeyRequest value) { Response.Headers.CacheControl="no-store"; return WorkTasksController.Success(await service.CreateKeyAsync(await Actor(), value)); }
    [HttpPost("{id}/revoke")] public async Task<IActionResult> Revoke(string id) { await service.RevokeKeyAsync(await Actor(), id); return WorkTasksController.Success(true); }
}

[ApiController, Authorize, ResponseCache(NoStore=true, Location=ResponseCacheLocation.None), Route("api/work-task-uploads")]
public sealed class WorkTaskUploadsController(ISystemApplicationService applications, IWorkTaskService service) : ControllerBase
{
    private async Task<WorkActor> Actor() => new(await applications.GetAccessAsync(User, true));
    [HttpPost] public async Task<IActionResult> Begin(WorkUploadRequest value) => WorkTasksController.Success(await service.BeginUploadAsync(await Actor(), value));
    // 此动作由专用的一次性上传票据及当前人员权限授权，不接受任意本地文件路径。
    [AllowAnonymous, HttpPut("{id}/content"), RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Content(string id, CancellationToken cancellationToken)
    {
        await service.UploadAsync(id, Request.Headers["X-Work-Upload-Ticket"].ToString(), Request.Body, cancellationToken);
        return WorkTasksController.Success(true);
    }
    [HttpPost("{id}/complete")] public async Task<IActionResult> Complete(string id) => WorkTasksController.Success(await service.CompleteUploadAsync(await Actor(), id));
}

[ApiController, Authorize, ResponseCache(NoStore=true, Location=ResponseCacheLocation.None), Route("api/work-task-attachments")]
public sealed class WorkTaskAttachmentsController(ISystemApplicationService applications, IWorkTaskService service) : ControllerBase
{
    [HttpGet("{id}/content")]
    public async Task<IActionResult> Download(string id)
    {
        var file=await service.DownloadAsync(new WorkActor(await applications.GetAccessAsync(User, true)), id);
        Response.Headers.CacheControl="no-store"; Response.Headers.XContentTypeOptions="nosniff";
        return PhysicalFile(file.Path, file.ContentType, file.Name, enableRangeProcessing:false);
    }
}
