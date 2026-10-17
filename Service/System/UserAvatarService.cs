using Interface;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Model.User;
using SkiaSharp;
using SqlSugar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Service;

/// <summary>只更新当前账号头像字段；图片解码重编码后保存，不公开服务器文件目录。</summary>
public sealed class UserAvatarService : IUserAvatarService
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    private static readonly SemaphoreSlim[] UserGates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SqlSugarScope _scope;
    private readonly ILogger<UserAvatarService> _logger;
    private readonly string _storageRoot;

    public UserAvatarService(SqlSugarScope scope, IWebHostEnvironment environment,
        IOptions<UserAvatarOptions> options, ILogger<UserAvatarService> logger)
    {
        _scope = scope;
        _logger = logger;
        var configured = options.Value.StorageRoot;
        _storageRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data", "user-avatars")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
    }

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_scope, "calmadmin");

    public async Task<string?> GetFileKeyAsync(string userId, CancellationToken cancellationToken)
    {
        using var operation = DatabaseOperationScope.Begin(_scope);
        cancellationToken.ThrowIfCancellationRequested();
        return (await FindUserAsync(userId)).AvatarFileKey;
    }

    public async Task<UserAvatarContent?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var gate = GetGate(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var operation = DatabaseOperationScope.Begin(_scope);
            var key = (await FindUserAsync(userId)).AvatarFileKey;
            if (string.IsNullOrEmpty(key)) return null;
            var path = GetFilePath(key);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return null;
            return new UserAvatarContent(await File.ReadAllBytesAsync(path, cancellationToken), "image/png");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> SaveAsync(string userId, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumFileBytes)
            throw new ArgumentException("头像不能为空且不能超过 2MB");
        await DecodeGate.WaitAsync(cancellationToken);
        byte[] normalized;
        try
        {
            normalized = await Task.Run(() => NormalizeImage(bytes), cancellationToken);
        }
        finally
        {
            DecodeGate.Release();
        }

        var gate = GetGate(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var operation = DatabaseOperationScope.Begin(_scope);
            var user = await FindUserAsync(userId);
            var key = Guid.NewGuid().ToString("N") + ".png";
            var path = GetFilePath(key);
            Directory.CreateDirectory(_storageRoot);
            try
            {
                await File.WriteAllBytesAsync(path, normalized, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTime.Now;
                var updated = await Db.Updateable<SysUserInfo>()
                    .SetColumns(x => new SysUserInfo { AvatarFileKey = key, UpdateTime = now })
                    .Where(x => x.UserId == userId && x.Enabled).ExecuteCommandAsync();
                if (updated != 1) throw new UnauthorizedAccessException("当前账号不存在或已停用");
            }
            catch
            {
                DeleteManagedFile(key);
                throw;
            }
            DeleteManagedFile(user.AvatarFileKey);
            return key;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAsync(string userId, CancellationToken cancellationToken)
    {
        var gate = GetGate(userId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var operation = DatabaseOperationScope.Begin(_scope);
            var user = await FindUserAsync(userId);
            var now = DateTime.Now;
            var updated = await Db.Updateable<SysUserInfo>()
                .SetColumns(x => new SysUserInfo { AvatarFileKey = null, UpdateTime = now })
                .Where(x => x.UserId == userId && x.Enabled).ExecuteCommandAsync();
            if (updated != 1) throw new UnauthorizedAccessException("当前账号不存在或已停用");
            DeleteManagedFile(user.AvatarFileKey);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SysUserInfo> FindUserAsync(string userId)
    {
        return await Db.Queryable<SysUserInfo>().FirstAsync(x => x.UserId == userId && x.Enabled)
            ?? throw new UnauthorizedAccessException("当前账号不存在或已停用");
    }

    private static byte[] NormalizeImage(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            throw new ArgumentException("请上传真实的 JPG、PNG 或 WebP 图片");
        var info = codec.Info;
        if (info.Width < 16 || info.Height < 16 || info.Width > 4096 || info.Height > 4096 ||
            (long)info.Width * info.Height > 8_000_000 || codec.FrameCount > 1)
            throw new ArgumentException("头像必须为静态图片，边长 16～4096 像素且总像素不超过 800 万");

        using var source = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success)
            throw new ArgumentException("图片内容损坏或不完整，请重新选择图片");
        using var output = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        var size = Math.Min(info.Width, info.Height);
        var left = (info.Width - size) / 2f;
        var top = (info.Height - size) / 2f;
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, new SKRect(left, top, left + size, top + size), new SKRect(0, 0, 512, 512), paint);
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded == null || encoded.Size > MaximumFileBytes) throw new ArgumentException("头像编码失败，请重新选择图片");
        return encoded.ToArray();
    }

    private string GetFilePath(string key)
    {
        if (!Regex.IsMatch(key, "^[0-9a-f]{32}\\.png$")) throw new InvalidOperationException("头像文件标识无效");
        var path = Path.GetFullPath(Path.Combine(_storageRoot, key));
        if (!path.StartsWith(_storageRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("头像路径超出存储目录");
        return path;
    }

    private void DeleteManagedFile(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        try
        {
            File.Delete(GetFilePath(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "清理旧头像失败；不影响已保存的头像信息。");
        }
    }

    private static SemaphoreSlim GetGate(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return UserGates[hash[0] % UserGates.Length];
    }
}
