using Interface;
using Service;
using SqlSugar;

namespace calmadmin.HostedServices;

public sealed class ProcessBoardInitializationState
{
    public bool Ready { get; set; }
}

/// <summary>让出启动线程，数据库准备、采样、清理不等待模型或网络发送。</summary>
public sealed class ProcessBoardHostedService(
    IServiceScopeFactory scopes,
    ProcessBoardInitializationState state,
    ILogger<ProcessBoardHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var nextCleanup = DateTime.Now.AddMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = 10;
            try
            {
                using var services = scopes.CreateScope();
                if (!state.Ready)
                {
                    var scope = services.ServiceProvider.GetRequiredService<SqlSugarScope>();
                    using var db = scope.GetConnection("calmadmin").CopyNew();
                    await ProcessBoardSchema.InitializeAsync(db);
                    state.Ready = true;
                    logger.LogInformation("Process board schema ready; collection runs in the background.");
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
                var board = services.ServiceProvider.GetRequiredService<IProcessBoardService>();
                delay = (await board.GetSettingAsync()).SampleIntervalSeconds;
                await board.CaptureAsync(stoppingToken);
                if (DateTime.Now >= nextCleanup)
                {
                    await board.CleanupAsync(stoppingToken);
                    nextCleanup = DateTime.Now.AddMinutes(5);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Process board background processing failed; retrying without blocking HTTP.");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
