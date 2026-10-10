using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

/// <summary>微信绑定、会话和投递表的结构初始化及旧版本可空列迁移。</summary>
public static class WeChatClawBotDatabaseInitializer
{
    public static void Initialize(ISqlSugarClient db)
    {
        // 旧版本可能已将可空属性创建为 NOT NULL，必须先放宽现有列约束。
        // 只调整实体明确声明的可空列，保留数据库里的类型、长度和已有数据。
        EnsureNullableColumns<WeChatClawBotAccountEntity>(db);
        EnsureNullableColumns<WeChatClawBotBindingSessionEntity>(db);
        EnsureNullableColumns<WeChatClawBotConversationEntity>(db);
        EnsureNullableColumns<WeChatClawBotDeliveryEntity>(db);
        EnsureNullableColumns<WeChatClawBotInboundMessageEntity>(db);

        db.CodeFirst.InitTables<
            WeChatClawBotAccountEntity,
            WeChatClawBotBindingSessionEntity,
            WeChatClawBotConversationEntity,
            WeChatClawBotDeliveryEntity>();
        db.CodeFirst.InitTables<WeChatClawBotInboundMessageEntity>();
    }

    private static void EnsureNullableColumns<TEntity>(ISqlSugarClient db)
    {
        var entity = db.EntityMaintenance.GetEntityInfo<TEntity>();
        if (!db.DbMaintenance.IsAnyTable(entity.DbTableName, false))
        {
            return;
        }

        var databaseColumns = db.DbMaintenance.GetColumnInfosByTableName(entity.DbTableName, false);
        foreach (var entityColumn in entity.Columns.Where(column =>
                     column.IsNullable && !column.IsIgnore && !column.IsPrimarykey))
        {
            var databaseColumn = databaseColumns.FirstOrDefault(column =>
                string.Equals(column.DbColumnName, entityColumn.DbColumnName, StringComparison.OrdinalIgnoreCase));
            if (databaseColumn == null || databaseColumn.IsNullable)
            {
                continue;
            }

            databaseColumn.IsNullable = true;
            if (databaseColumn.DataType.Equals("datetime", StringComparison.OrdinalIgnoreCase) ||
                databaseColumn.DataType.Equals("smalldatetime", StringComparison.OrdinalIgnoreCase) ||
                databaseColumn.DataType.Equals("date", StringComparison.OrdinalIgnoreCase))
            {
                // SQL Server 的 datetime 元数据含显示精度（23, 3），但 ALTER COLUMN
                // 不接受 datetime(23,3)。这些类型无需列宽或精度参数。
                databaseColumn.Length = 0;
                databaseColumn.DecimalDigits = 0;
                databaseColumn.Scale = 0;
            }

            db.DbMaintenance.UpdateColumn(entity.DbTableName, databaseColumn);
        }
    }
}
