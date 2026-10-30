using Interface;
using Microsoft.Extensions.Caching.Memory;
using Model.WorkTasks;

namespace calmadmin.WorkTasks;

/// <summary>保护协议初始化、发现、调用及受控附件下载，不使用网页会话，也不续期网页空闲时间。</summary>
public sealed class WorkTaskPersonalAccessMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly object CounterGate=new();
    public async Task InvokeAsync(HttpContext context, IWorkTaskService service)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp/work-tasks")) { await next(context); return; }
        context.Response.Headers.CacheControl="no-store";
        var limit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly:false }) limit.MaxRequestBodySize=1024 * 1024;
        try
        {
            var header=context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("请使用个人工作任务凭证连接");
            var actor=await service.AuthenticateKeyAsync(header[7..]);
            WorkTaskRequestCounter counter;
            lock (CounterGate)
                counter=cache.GetOrCreate("work-mcp:" + actor.KeyId + ":" + DateTime.UtcNow.ToString("yyyyMMddHHmm"), entry =>
                { entry.AbsoluteExpirationRelativeToNow=TimeSpan.FromMinutes(2); return new WorkTaskRequestCounter(); })!;
            if (Interlocked.Increment(ref counter.Count) > 120)
            {
                context.Response.StatusCode=429; context.Response.Headers.RetryAfter="60";
                await context.Response.WriteAsJsonAsync(new { code=429, msg="个人凭证请求过于频繁，请稍后重试" }); return;
            }
            context.Items[typeof(WorkActor)]=actor;
            await next(context);
        }
        catch (UnauthorizedAccessException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode=401; context.Response.Headers.WWWAuthenticate="Bearer";
            await context.Response.WriteAsJsonAsync(new { code=401, msg=exception.Message });
        }
    }
}
