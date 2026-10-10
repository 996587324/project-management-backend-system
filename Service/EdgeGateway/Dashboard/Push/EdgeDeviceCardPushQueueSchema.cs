using SqlSugar;

namespace Service;

public static class EdgeDeviceCardPushQueueSchema
{
    /// <summary>兼容有数据的旧配置表；使用 SQL 默认值补列，不依赖 CLR 初始值。</summary>
    public static void EnsureIntervalColumn(ISqlSugarClient db, string tableName = "EdgeDeviceCardPushQueueSetting")
    {
        if (!db.DbMaintenance.IsAnyTable(tableName, false)) return;
        var columns = db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        if (columns.Any(x => x.DbColumnName.Equals("IntervalSeconds", StringComparison.OrdinalIgnoreCase))) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("无效的内部队列配置表名", nameof(tableName));
        // 当前 SqlSugar 版本的 AddColumn(DefaultValue) 会通过 Object 表查询回填，
        // SQL Server 上不能使用；仅这条兼容 DDL 使用显式数据库默认约束。
        db.Ado.ExecuteCommand($"ALTER TABLE [{tableName}] ADD [IntervalSeconds] int NOT NULL DEFAULT (5) WITH VALUES");
    }
}
