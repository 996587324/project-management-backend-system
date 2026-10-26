using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/source-catalog")]
public sealed class SourceCatalogController(ISystemApplicationService applications, ISourceCatalogService catalog) : ControllerBase
{
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] SourceCatalogQuery query) =>
        Success(await catalog.ModelsAsync(await applications.GetAccessAsync(User, true), query));

    [HttpGet("devices")]
    public async Task<IActionResult> Devices([FromQuery] SourceCatalogQuery query) =>
        Success(await catalog.DevicesAsync(await applications.GetAccessAsync(User, true), query));

    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] SourceCatalogQuery query) =>
        Success(await catalog.ResolveAsync(await applications.GetAccessAsync(User, true), query));

    private static IActionResult Success(object? data) => new JsonResult(new { code = 200, msg = "success", data },
        new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });
}
