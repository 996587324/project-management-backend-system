using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EmailPush;
using Model.SystemApplications;

namespace calmadmin.Controllers;

[Authorize]
[ApiController]
[Route("api/email-push")]
public sealed class EmailPushController(IEmailPushService service, ISystemApplicationService applications) : ControllerBase
{
    private Task<ApplicationAccessContext> AccessAsync() => applications.GetAccessAsync(User, true);

    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities()
    {
        var access = await AccessAsync();
        var administrator = access.HasPermission("EmailPush") && await service.IsAdministratorAsync(access);
        return Ok(new { code = 200, data = new { canConfigure = administrator } });
    }

    [HttpGet("setting")]
    public async Task<IActionResult> GetSetting()
    {
        var setting = await service.GetSettingAsync(await AccessAsync());
        return Ok(new { code = 200, data = setting });
    }

    [HttpPost("setting")]
    public async Task<IActionResult> SaveSetting([FromBody] EmailSettingSaveDto dto)
    {
        var setting = await service.SaveSettingAsync(await AccessAsync(), dto);
        return Ok(new { code = 200, message = "邮件配置已保存", data = setting });
    }

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection()
    {
        await service.TestConnectionAsync(await AccessAsync(), HttpContext.RequestAborted);
        return Ok(new { code = 200, message = "SMTP 连接与认证成功，未发送邮件", data = true });
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> Recipients(string? keyword, int current = 1, int size = 30)
    {
        var users = await service.GetRecipientsAsync(await AccessAsync(), keyword, current, size);
        return Ok(new { code = 200, data = users });
    }

    [HttpPost("test-send")]
    public async Task<IActionResult> SendTest([FromBody] EmailEnqueueDto dto)
    {
        var access = await AccessAsync();
        await service.GetSettingAsync(access); // Administrator role is enforced server-side.
        var result = await service.EnqueueAsync(access, new EmailEnqueueDto
        {
            RecipientUserIds = [access.UserId], Subject = "邮件推送服务测试",
            Body = "这是一封邮件推送中心测试邮件。收到此邮件表示本次SMTP投递及收件链路已工作。",
            IdempotencyKey = dto.IdempotencyKey, Confirm = dto.Confirm
        }, "Test");
        return Ok(new { code = 200, message = result.Message, data = result });
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] EmailEnqueueDto dto)
    {
        var result = await service.EnqueueAsync(await AccessAsync(), dto);
        return Ok(new { code = 200, message = result.Message, data = result });
    }

    [HttpGet("records")]
    public async Task<IActionResult> Records([FromQuery] EmailRecordQuery query)
    {
        var records = await service.GetRecordsAsync(await AccessAsync(), query);
        return Ok(new { code = 200, data = records });
    }

    [HttpPost("records/{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, [FromBody] EmailRetryDto dto)
    {
        await service.RetryAsync(await AccessAsync(), id, dto);
        return Ok(new { code = 200, message = "已重新加入队列", data = true });
    }
}
