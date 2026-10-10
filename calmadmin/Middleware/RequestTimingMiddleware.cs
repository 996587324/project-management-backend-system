using System.Diagnostics;

namespace calmadmin.Middleware;

/// <summary>记录慢接口和失败请求，不记录查询参数、请求体、用户凭据或 WebSocket 长连接。</summary>
public sealed class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;
    private readonly int _slowMilliseconds;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _slowMilliseconds = Math.Max(1, configuration.GetValue("BackendDiagnostics:SlowRequestMilliseconds", 1000));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/mcp/v2"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (failed || context.RequestAborted.IsCancellationRequested || context.Response.StatusCode >= 500 ||
                stopwatch.ElapsedMilliseconds >= _slowMilliseconds)
            {
                _logger.LogWarning(
                    "Request completed. Method: {Method}; Path: {Path}; Status: {Status}; Elapsed: {ElapsedMilliseconds} ms; Aborted: {Aborted}; TraceId: {TraceId}.",
                    context.Request.Method, context.Request.Path.Value,
                    failed ? 500 : context.Response.StatusCode, stopwatch.ElapsedMilliseconds,
                    context.RequestAborted.IsCancellationRequested, Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
            }
        }
    }
}
