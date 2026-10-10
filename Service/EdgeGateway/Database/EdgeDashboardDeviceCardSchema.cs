using SqlSugar;

namespace Service;

/// <summary>修复尚未认领、尚未推送的卡片告警时间列，保持 NULL 的业务含义。</summary>
public static class EdgeDashboardDeviceCardSchema
{
    public static void EnsureNullablePushTimes(ISqlSugarClient db)
    {
        // 名称均为代码内固定标识。保留既有 datetime 类型和数据，不删除表或索引。
        db.Ado.ExecuteCommand("""
            IF EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.EdgeDashboardDeviceCardAlertRecord')
                  AND name = N'ClaimExpireTime' AND is_nullable = 0)
                ALTER TABLE dbo.EdgeDashboardDeviceCardAlertRecord ALTER COLUMN ClaimExpireTime datetime NULL;

            IF EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.EdgeDashboardDeviceCardAlertRecord')
                  AND name = N'PushedTime' AND is_nullable = 0)
                ALTER TABLE dbo.EdgeDashboardDeviceCardAlertRecord ALTER COLUMN PushedTime datetime NULL;
            """);
    }
}
