using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;

namespace calmadmin.Controllers;

// 与既有卡片配置保持同一后台登录鉴权，不开放到公开免登录看板。
[Authorize]
[ApiController]
[Route("api/EdgeGateway/dashboard/device-card-push")]
public sealed class EdgeDeviceCardPushController : ControllerBase
{
    private readonly IEdgeDeviceCardPushService _service;

    public EdgeDeviceCardPushController(IEdgeDeviceCardPushService service)
    {
        _service = service;
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> Recipients() => Success(await _service.GetRecipientsAsync());

    [HttpGet("queue-setting")]
    public async Task<IActionResult> QueueSetting() => Success(await _service.GetQueueSettingAsync());

    [HttpPost("queue-setting")]
    public async Task<IActionResult> SaveQueueSetting([FromBody] EdgeDeviceCardPushQueueSettingDto dto)
    {
        try
        {
            await _service.SaveQueueSettingAsync(dto);
            return Success(true, "队列发送间隔已保存");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = 400, message = ex.Message });
        }
    }

    [HttpGet("tasks")]
    public async Task<IActionResult> Tasks([FromQuery] EdgeDeviceCardPushTaskQueryDto query) =>
        Success(await _service.GetTasksAsync(query));

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] EdgeDeviceCardPushTestDto dto)
    {
        try
        {
            return Success(await _service.QueueTestAsync(dto), "测试消息已加入后台发送队列，请查看推送记录");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = 400, message = ex.Message });
        }
    }

    [HttpPost("tasks/{taskId:long}/retry")]
    public async Task<IActionResult> Retry(long taskId)
    {
        try
        {
            await _service.RetryAsync(taskId);
            return Success(true, "已重新加入发送队列");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = 400, message = ex.Message });
        }
    }

    private static IActionResult Success(object data, string message = "查询成功") =>
        new JsonResult(new { code = 200, message, data });
}
