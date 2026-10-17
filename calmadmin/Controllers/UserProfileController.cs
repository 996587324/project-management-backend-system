using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Service;

namespace calmadmin.Controllers;

[ApiController]
[Authorize]
[Route("api/user-profile")]
public sealed class UserProfileController(ISystemApplicationService applications, IUserAvatarService avatars) : ControllerBase
{
    [HttpGet("avatar/info")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAvatarInfo(CancellationToken cancellationToken)
    {
        var access = await applications.GetAccessAsync(User, true);
        var key = await avatars.GetFileKeyAsync(access.UserId, cancellationToken);
        return Metadata(key);
    }

    [HttpGet("avatar")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAvatar(CancellationToken cancellationToken)
    {
        var access = await applications.GetAccessAsync(User, true);
        var image = await avatars.GetAsync(access.UserId, cancellationToken);
        return image == null ? NoContent() : File(image.Bytes, image.ContentType);
    }

    [HttpPost("avatar")]
    [RequestSizeLimit(UserAvatarService.MaximumFileBytes + 128 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = UserAvatarService.MaximumFileBytes + 128 * 1024)]
    public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        var access = await applications.GetAccessAsync(User, true);
        if (file.Length <= 0 || file.Length > UserAvatarService.MaximumFileBytes)
            return BadRequest(new { code = 400, message = "头像不能为空且不能超过 2MB" });
        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        try
        {
            var key = await avatars.SaveAsync(access.UserId, stream.ToArray(), cancellationToken);
            return Metadata(key);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = 400, message = exception.Message });
        }
    }

    [HttpPost("avatar/reset")]
    public async Task<IActionResult> ResetAvatar(CancellationToken cancellationToken)
    {
        var access = await applications.GetAccessAsync(User, true);
        await avatars.ResetAsync(access.UserId, cancellationToken);
        return Metadata(null);
    }

    private IActionResult Metadata(string? key)
    {
        return Ok(new
        {
            code = 200, msg = "操作成功",
            data = new { avatar = string.IsNullOrEmpty(key) ? null : $"/api/user-profile/avatar?v={key}", avatarVersion = key }
        });
    }
}
