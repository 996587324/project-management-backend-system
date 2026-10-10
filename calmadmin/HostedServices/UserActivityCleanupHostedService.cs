using Interface;

namespace calmadmin.HostedServices;

/// <summary>结算失活用户会话，并清理超过保留周期的审计日志。</summary>
public sealed class UserActivityCleanupHostedService : BackgroundService
{
    private readonly IUserActivityService _service;
    private readonly ILogger<UserActivityCleanupHostedService> _logger;

    public UserActivityCleanupHostedService(IUserActivityService service, ILogger<UserActivityCleanupHostedService> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _service.CleanupAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "User activity cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
