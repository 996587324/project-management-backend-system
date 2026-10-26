using Interface;
using Model.EmailPush;
using SqlSugar;

namespace Service;

public sealed class EmailPushDatabaseInitializer(SqlSugarScope scope) : IEmailPushDatabaseInitializer
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
            // CodeFirst contains synchronous DDL; run only in the background initializer, never on a business request.
            await Task.Run(() =>
            {
                using var db = scope.GetConnection("calmadmin").CopyNew();
                EmailPushSchema.Initialize(db);
            }, cancellationToken);
            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class EmailPushSchema
{
    public static void Initialize(ISqlSugarClient db)
    {
        RepairNullableColumns<EmailPushSettingEntity>(db);
        RepairNullableColumns<EmailPushDeliveryEntity>(db);
        db.CodeFirst.InitTables<EmailPushSettingEntity, EmailPushTaskEntity,
            EmailPushDeliveryEntity, EmailPushConfigurationAuditEntity>();
        if (db.Queryable<EmailPushSettingEntity>().InSingle(1) == null)
        {
            try
            {
                db.Insertable(new EmailPushSettingEntity()).ExecuteCommand();
            }
            catch
            {
                if (db.Queryable<EmailPushSettingEntity>().InSingle(1) == null) throw;
            }
        }
    }

    private static void RepairNullableColumns<T>(ISqlSugarClient db)
    {
        var entity = db.EntityMaintenance.GetEntityInfo<T>();
        if (!db.DbMaintenance.IsAnyTable(entity.DbTableName, false)) return;
        var columns = db.DbMaintenance.GetColumnInfosByTableName(entity.DbTableName, false);
        foreach (var field in entity.Columns.Where(x => x.IsNullable && !x.IsIgnore))
        {
            var column = columns.FirstOrDefault(x => x.DbColumnName.Equals(field.DbColumnName, StringComparison.OrdinalIgnoreCase));
            if (column == null || column.IsNullable) continue;
            column.IsNullable = true;
            if (column.DataType.Equals("datetime", StringComparison.OrdinalIgnoreCase))
            {
                column.Length = 0;
                column.DecimalDigits = 0;
                column.Scale = 0;
            }
            db.DbMaintenance.UpdateColumn(entity.DbTableName, column);
        }
    }
}
