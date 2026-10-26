using Interface;
using Model.HourlyProduction;
using SqlSugar;
using System.Text.Json;

namespace Service;

public sealed class HourlyProductionCatalog
{
    public volatile bool Ready;
    public HourlyBoardDefinition[] Boards { get; set; } = [];
}

/// <summary>只由后台初始化，不在控制器构造及读接口中执行 DDL。</summary>
public sealed class HourlyProductionInitializer(SqlSugarScope scope, HourlyProductionCatalog catalog)
    : IHourlyProductionInitializer
{
    private readonly SemaphoreSlim _gate = new(1);
    public bool IsReady => catalog.Ready;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsReady) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady) return;
            using var db = scope.GetConnection("calmadmin").CopyNew();
            db.CodeFirst.InitTables(typeof(HourlyProductionBoardEntity), typeof(HourlyProductionInboxEntity),
                typeof(HourlyProductionPassEntity), typeof(HourlyProductionBucketEntity),
                typeof(HourlyProductionReportEntity), typeof(HourlyProductionDispatchEntity),
                typeof(HourlyProductionWorkerStateEntity), typeof(HourlyProductionAdviceEntity));
            // 首次部署从当前中心队列水位开始，不擅自重放此前全部报文。
            if (!await db.Queryable<HourlyProductionWorkerStateEntity>().AnyAsync())
            {
                var cursor = db.DbMaintenance.IsAnyTable("EdgeGatewayMessageQueue", false)
                    ? await db.Queryable<Model.EdgeGateway.EdgeMessageQueueEntity>().MaxAsync(x => x.Id) : 0;
                await db.Insertable(new HourlyProductionWorkerStateEntity { EdgeCursor = cursor }).ExecuteCommandAsync();
            }
            await HourlyProductionMenus.SeedAsync(db);
            catalog.Boards = (await db.Queryable<HourlyProductionBoardEntity>().ToListAsync())
                .Select(x => JsonSerializer.Deserialize<HourlyBoardDefinition>(x.DefinitionJson)!)
                .Where(x => x != null).ToArray();
            catalog.Ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
