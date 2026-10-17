using SqlSugar;

namespace Service;

/// <summary>跨进程的 SQL 会话锁。等待模型时不占用数据库事务，也不锁业务表。</summary>
public static class WeChatClawBotAiLock
{
    public static async Task<IAsyncDisposable?> TryEnterAsync(ISqlSugarClient db, string resource, int timeout = 0)
    {
        var connection = new SqlSugarClient(new ConnectionConfig
        {
            DbType = db.CurrentConnectionConfig.DbType,
            ConnectionString = db.CurrentConnectionConfig.ConnectionString,
            InitKeyType = InitKeyType.Attribute, IsAutoCloseConnection = false
        });
        try
        {
            var result = await connection.Ado.SqlQuerySingleAsync<int>("""
                DECLARE @r int;
                EXEC @r=sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive',
                    @LockOwner='Session', @LockTimeout=@timeout;
                SELECT @r;
                """, new { resource, timeout });
            if (result >= 0) return new Lease(connection, resource);
            connection.Dispose();
            return null;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class Lease(SqlSugarClient connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.Ado.ExecuteCommandAsync(
                    "EXEC sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session';", new { resource });
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
