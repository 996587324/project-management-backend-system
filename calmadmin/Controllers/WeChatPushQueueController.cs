using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace calmadmin.Controllers;

[Authorize]
[ApiController]
[Route("api/wechat-clawbot/push-queue")]
public sealed class WeChatPushQueueController(IWeChatPushQueueService service, ISystemApplicationService applications) : ControllerBase
{
    [HttpGet("setting")]
    public async Task<IActionResult> GetSetting()
    {
        var access = await applications.GetAccessAsync(User, true);
        var seconds = await service.GetIntervalAsync(access);
        var canConfigure = await service.CanConfigureAsync(access);
        return Ok(new { code = 200, data = new { intervalSeconds = seconds, canConfigure } });
    }

    [HttpPost("setting")]
    public async Task<IActionResult> SetSetting(WeChatPushQueueSettingRequest request)
    {
        await service.SetIntervalAsync(await applications.GetAccessAsync(User, true), request.IntervalSeconds);
        return Ok(new { code = 200, message = "发送间隔已保存", data = true });
    }

    [HttpGet("records")]
    public async Task<IActionResult> GetRecords(long? taskId = null, int current = 1, int size = 20)
    {
        var records = await service.GetRecordsAsync(await applications.GetAccessAsync(User, true), taskId, current, size);
        return Ok(new { code = 200, data = records });
    }
}

public sealed class WeChatPushQueueSettingRequest
{
    public int IntervalSeconds { get; set; } = 5;
}
