using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;
using Model.SystemApplications;
using SqlSugar;

namespace calmadmin.Controllers;

/// <summary>共享元数据和经过资源授权的数据查询；B 不调用未适配授权的 A 接口。</summary>
[ApiController]
[Authorize]
[Route("api/shared-data")]
public sealed class SharedDataController(
    ISystemApplicationService applications,
    ISharedModelDictionaryService dictionary,
    IEdgeDashboardService dashboard,
    SqlSugarScope scope,
    IEdgeModelFieldCatalogService fieldCatalog) : ControllerBase
{
    [HttpGet("models")]
    public async Task<IActionResult> Models()
    {
        await RequireDictionaryAsync();
        return Success(await dictionary.GetModelsAsync());
    }

    [HttpPost("models")]
    public async Task<IActionResult> SaveModel([FromBody] EdgeDashboardModelDisplayMappingSaveDto dto)
    {
        await RequireDictionaryAsync(write: true);
        return Success(await dictionary.SaveModelAsync(dto));
    }

    [HttpPost("models/{id:long}/delete")]
    public async Task<IActionResult> DeleteModel(long id)
    {
        await RequireDictionaryAsync(write: true);
        await dictionary.DeleteModelAsync(id);
        return Success(true);
    }

    [HttpGet("fields")]
    public async Task<IActionResult> Fields([FromQuery] string modelMark)
    {
        await RequireDictionaryAsync();
        return Success(await dictionary.GetFieldsAsync(modelMark));
    }

    [HttpPost("fields")]
    public async Task<IActionResult> SaveFields([FromBody] EdgeDashboardModelFieldDisplayMappingListSaveDto dto)
    {
        await RequireDictionaryAsync(write: true);
        return Success(await dictionary.SaveFieldsAsync(dto));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] string modelMark)
    {
        await RequireDictionaryAsync();
        // 历史目录是共享结构信息，不把其他设备的历史样例值暴露为当前实际值。
        var fields = await dashboard.GetModelSourceFieldsAsync(modelMark);
        foreach (var field in fields)
        {
            field.CurrentValue = null;
            field.DisplayValue = null;
        }
        return Success(fields);
    }

    [HttpGet("sources")]
    public async Task<IActionResult> Sources([FromQuery] string? keyword)
    {
        var access = await RequireSourcesAsync();
        var db = scope.GetConnectionScope("calmadmin");
        var keys = access.SourceKeys.ToArray();
        // 必须先限制资源再分页，不能取出一页后过滤导致漏数据及泄漏总数。
        var rows = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(!access.AllResources, x => keys.Contains(x.TenantMark + "\u001f" + x.DeviceCode))
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), x => x.DeviceCode.Contains(keyword!) || x.ModelMark!.Contains(keyword!))
            .OrderByDescending(x => x.ReceiveTime).Take(500)
            .Select(x => new { x.TenantMark, x.DeviceCode, x.ModelMark, x.ReceiveTime, x.DataTime }).ToListAsync();
        var mappings = await dictionary.GetModelsAsync();
        return Success(rows.Select(x => new
        {
            x.TenantMark, deviceMark = x.DeviceCode, x.ModelMark, x.ReceiveTime, x.DataTime,
            modelDisplayName = mappings.FirstOrDefault(m => m.Enabled && m.ModelMark == x.ModelMark)?.DisplayName ?? x.ModelMark,
            sourceKey = x.TenantMark + "\u001f" + x.DeviceCode
        }));
    }

    [HttpGet("source-fields")]
    public async Task<IActionResult> SourceFields([FromQuery] string tenantMark, [FromQuery] string deviceMark)
    {
        var access = await RequireSourcesAsync();
        if (!access.AllResources && !access.SourceKeys.Contains(tenantMark + "\u001f" + deviceMark))
            throw new UnauthorizedAccessException("没有该设备数据源授权");
        return Success(await dashboard.GetSourceFieldsAsync(tenantMark, deviceMark));
    }

    [HttpGet("workbench")]
    public async Task<IActionResult> Workbench()
    {
        var access = await applications.GetAccessAsync(User, true);
        if (!access.HasPermission("SystemBWorkbench")) throw new UnauthorizedAccessException("没有工作台权限");
        return Success(new
        {
            access.AppCode, access.UserName,
            authorizedLines = access.LineCodes.Order().ToList(),
            authorizedProjects = access.ProjectCodes.Order().ToList(),
            authorizedDevices = access.DeviceCodes.Order().ToList(),
            access.AllResources,
            mcpEnabled = access.McpScopes.Count > 0
        });
    }

    [HttpGet("catalog/models")]
    public async Task<IActionResult> CatalogModels()
    {
        await RequireDictionaryAsync();
        return Success(await fieldCatalog.GetModelsAsync());
    }

    [HttpGet("catalog/page")]
    public async Task<IActionResult> CatalogPage([FromQuery] EdgeModelFieldCatalogQuery query)
    {
        await RequireDictionaryAsync();
        return Success(await fieldCatalog.GetPageAsync(query));
    }

    [HttpGet("catalog/references")]
    public async Task<IActionResult> CatalogReferences([FromQuery] string modelMark, [FromQuery] long id)
    {
        await RequireDictionaryAsync();
        return Success(await fieldCatalog.GetReferencesAsync(modelMark, id));
    }

    [HttpPost("catalog/selection")]
    public async Task<IActionResult> CatalogSelection([FromBody] EdgeModelFieldCatalogChange input)
    {
        await RequireDictionaryAsync(write: true);
        var actor = await applications.GetAccessAsync(User, true);
        return Success(await fieldCatalog.ChangeAsync(input, actor));
    }

    private async Task RequireDictionaryAsync(bool write = false)
    {
        var access = await applications.GetAccessAsync(User, true);
        if (write)
        {
            if (!access.PlatformAdministrator && !access.Permissions.Contains("shared-dictionary.write"))
                throw new UnauthorizedAccessException("修改共享字典需要平台级授权");
        }
        else if (!access.PlatformAdministrator && !access.HasPermission("shared-dictionary.read"))
            throw new UnauthorizedAccessException("没有共享字典读取权限");
    }

    private async Task<ApplicationAccessContext> RequireSourcesAsync()
    {
        var access = await applications.GetAccessAsync(User, true);
        if (!access.HasPermission("source.read") && !access.HasPermission("SystemBWorkbench"))
            throw new UnauthorizedAccessException("没有数据源读取权限");
        return access;
    }

    private static IActionResult Success(object data) => new JsonResult(new { code = 200, msg = "查询成功", data });
}
