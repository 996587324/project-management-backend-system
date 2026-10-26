using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.HourlyProduction;
using Model.SystemApplications;
using Model.EdgeGateway;
using SqlSugar;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/hourly-production")]
public sealed class HourlyProductionController(
    ISystemApplicationService applications, IHourlyProductionService service,
    SqlSugarScope scope, IEdgeDashboardService dashboard, ISharedModelDictionaryService dictionary,
    IWeChatPushQueueService wechat) : ControllerBase
{
    private Task<ApplicationAccessContext> Access() => applications.GetAccessAsync(User, true);

    [HttpGet("boards")]
    public async Task<IActionResult> Boards()
    {
        var access = await Access();
        var rows = await service.ListAsync(access);
        return Success(rows.Select(x => new { x.Code, x.Name, x.Published }));
    }

    [HttpGet("definitions")]
    public async Task<IActionResult> Definitions()
    {
        var access = await Access();
        if (!access.HasPermission("HourlyProductionConfig")) throw new UnauthorizedAccessException("没有配置权限");
        return Success(await service.ListAsync(access));
    }

    [HttpPost("definitions")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Save(HourlyBoardDefinition definition) => Success(await service.SaveAsync(await Access(), definition));

    [HttpGet("runtime")]
    public async Task<IActionResult> Runtime(string code) => Success(await service.RuntimeAsync(await Access(), code));

    [HttpGet("reports")]
    public async Task<IActionResult> Reports(string code, int page = 1) => Success(await service.ReportsAsync(await Access(), code, page));

    [HttpGet("dispatches")]
    public async Task<IActionResult> Dispatches(string code, int page = 1) => Success(await service.DispatchesAsync(await Access(), code, page));

    [HttpPost("dispatches/{id:long}/retry")]
    public async Task<IActionResult> Retry(long id, [FromBody] Dictionary<string, bool> request)
    {
        await service.RetryDispatchAsync(await Access(), id, request.GetValueOrDefault("confirm"));
        return Success(true);
    }

    private async Task<ApplicationAccessContext> ConfigAccess()
    {
        var access = await Access();
        if (!access.HasPermission("HourlyProductionConfig")) throw new UnauthorizedAccessException("没有小时配置权限");
        return access;
    }

    [HttpGet("sources")]
    public async Task<IActionResult> Sources(string kind = "Realtime", string? keyword = null)
    {
        var access = await ConfigAccess();
        if (keyword?.Length > 128) throw new ArgumentException("搜索内容过长");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var keys = access.SourceKeys.ToArray();
        if (kind == "Edge")
        {
            var devices = access.DeviceCodes.ToArray();
            var rows = await db.Queryable<EdgeMessageQueueEntity>()
                .WhereIF(!access.AllResources, x => devices.Contains(x.DeviceCode) && keys.Contains(x.SourceId + "\u001f" + x.DeviceCode))
                .WhereIF(!string.IsNullOrWhiteSpace(keyword), x => x.DeviceCode.Contains(keyword!) || x.SourceId.Contains(keyword!))
                .GroupBy(x => new { x.SourceId, x.DeviceCode, x.SourceDeviceCode })
                .Select(x => new
                {
                    sourceKey = x.SourceId,
                    deviceKey = x.DeviceCode,
                    sourceDeviceCode = x.SourceDeviceCode,
                    name = x.SourceId + "/" + x.DeviceCode + "/" + x.SourceDeviceCode
                })
                .Take(500).ToListAsync();
            return Success(rows);
        }
        var sources = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(!access.AllResources, x => keys.Contains(x.TenantMark + "\u001f" + x.DeviceCode))
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), x => x.DeviceCode.Contains(keyword!) || x.ModelMark!.Contains(keyword!))
            .OrderByDescending(x => x.ReceiveTime).Take(500).ToListAsync();
        var mappings = await dictionary.GetModelsAsync();
        return Success(sources.Select(x => new
        {
            sourceKey = x.TenantMark ?? "",
            deviceKey = x.DeviceCode,
            name = (mappings.FirstOrDefault(m => m.Enabled && m.ModelMark == x.ModelMark)?.DisplayName ?? x.ModelMark) + " / " + x.DeviceCode
        }));
    }

    [HttpGet("fields")]
    public async Task<IActionResult> Fields(string sourceKey, string deviceKey)
    {
        var access = await ConfigAccess();
        if (!access.AllResources && !access.SourceKeys.Contains(sourceKey + "\u001f" + deviceKey))
            throw new UnauthorizedAccessException("没有数据源授权");
        return Success(await dashboard.GetSourceFieldsAsync(sourceKey, deviceKey));
    }

    [HttpGet("wechat-recipients")]
    public async Task<IActionResult> WeChatRecipients(string? keyword = null) =>
        Success(await wechat.GetRecipientsAsync(await ConfigAccess(), keyword, 1, 100));

    [HttpGet("diagnostics")]
    public async Task<IActionResult> Diagnostics()
    {
        var access = await ConfigAccess();
        using var db = scope.GetConnection("calmadmin").CopyNew();
        // 诊断只显示本人授权数据源，不返回SN或原始报文。
        var keys = access.SourceKeys.ToArray();
        var rows = await db.Queryable<HourlyProductionInboxEntity>()
            .WhereIF(!access.AllResources, x => keys.Contains(x.SourceKey + "\u001f" + x.DeviceKey))
            .Where(x => x.Error != null).OrderByDescending(x => x.Id).Take(50)
            .Select(x => new { x.Id, x.SourceKey, x.DeviceKey, x.Error, x.ReceivedAt }).ToListAsync();
        return Success(rows);
    }

    private IActionResult Success(object data) => new JsonResult(new { code = 200, msg = "success", data },
        new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        });
}

[ApiController]
[AllowAnonymous]
[Route("api/hourly-production/public")]
public sealed class HourlyProductionPublicController(IHourlyProductionService service) : ControllerBase
{
    [HttpGet("runtime")]
    public async Task<IActionResult> Runtime(string code) =>
        new JsonResult(new { code = 200, msg = "success", data = await service.RuntimeAsync(null, code, publicRead: true) },
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });
}
