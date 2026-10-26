using Interface;
using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

/// <summary>仅后台初始化，不在请求路径执行 DDL 或建立微信连接。</summary>
public sealed class WeChatPushQueueInitializer(SqlSugarScope scope) : IWeChatPushQueueInitializer
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    public bool IsReady => _ready;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return;
            await Task.Run(() =>
            {
                using var db = scope.GetConnection("calmadmin").CopyNew();
                InitializeSchema(db);
            }, cancellationToken);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void InitializeSchema(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables<WeChatPushQueueSettingEntity, WeChatPushQueueEntity>();
        if (db.Queryable<WeChatPushQueueSettingEntity>().InSingle(1) != null) return;
        try
        {
            db.Insertable(new WeChatPushQueueSettingEntity()).ExecuteCommand();
        }
        catch
        {
            if (db.Queryable<WeChatPushQueueSettingEntity>().InSingle(1) == null) throw;
        }
    }
}
