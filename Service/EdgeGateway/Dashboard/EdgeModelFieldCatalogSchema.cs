using Model.EdgeGateway;
using SqlSugar;

namespace Service;

public static class EdgeModelFieldCatalogSchema
{
    public static void Initialize(ISqlSugarClient db)
    {
        // 必须在 CodeFirst 前兼容有数据的旧表，C# 默认值不能替代 SQL DEFAULT。
        db.Ado.ExecuteCommand("""
            IF OBJECT_ID(N'EdgeDashboardModelFieldCatalog', N'U') IS NOT NULL
               AND COL_LENGTH(N'EdgeDashboardModelFieldCatalog', N'IsSelectionDisabled') IS NULL
            BEGIN
                ALTER TABLE EdgeDashboardModelFieldCatalog
                ADD IsSelectionDisabled bit NOT NULL
                    CONSTRAINT DF_EdgeDashboardModelFieldCatalog_IsSelectionDisabled DEFAULT(0) WITH VALUES;
            END;
            """);
        db.CodeFirst.InitTables<EdgeDashboardModelFieldCatalogEntity>();
    }
}
