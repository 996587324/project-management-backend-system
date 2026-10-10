using Model.EdgeGateway;
using Service;
using SqlSugar;

namespace calmadmin.HostedServices;

/// <summary>Web 服务启动后分批补齐设备自己已有的最新快照，不阻塞工作台初始化。</summary>
public sealed class EdgeRealtimeFieldBackfillHostedService : BackgroundService
{
    private readonly SqlSugarScope _dbScope;
    private readonly ILogger<EdgeRealtimeFieldBackfillHostedService> _logger;

    public EdgeRealtimeFieldBackfillHostedService(SqlSugarScope dbScope,
        ILogger<EdgeRealtimeFieldBackfillHostedService> logger)
    {
        _dbScope = dbScope;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long cursor = 0;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var operation = DatabaseOperationScope.Begin(_dbScope);
                    var db = DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");
                    var page = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
                        .Where(x => x.Id > cursor).OrderBy(x => x.Id).Take(100).ToListAsync();
                    if (page.Count == 0)
                    {
                        _logger.LogInformation("Device field latest-value backfill completed.");
                        return;
                    }
                    foreach (var candidate in page)
                    {
                        await EdgeRealtimeFieldLatestStore.WriteGate.WaitAsync(stoppingToken);
                        try
                        {
                            var snapshot = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
                                .InSingleAsync(candidate.Id);
                            if (snapshot != null)
                            {
                                await db.Ado.BeginTranAsync();
                                try
                                {
                                    await EdgeRealtimeFieldLatestStore.SeedAsync(db, snapshot);
                                    await db.Ado.CommitTranAsync();
                                }
                                catch
                                {
                                    await db.Ado.RollbackTranAsync();
                                    throw;
                                }
                            }
                            cursor = candidate.Id;
                        }
                        finally
                        {
                            EdgeRealtimeFieldLatestStore.WriteGate.Release();
                        }
                    }
                    await Task.Delay(200, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Device field latest-value backfill failed after snapshot {Cursor}; retrying.", cursor);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常宿主退出。
        }
    }
}
