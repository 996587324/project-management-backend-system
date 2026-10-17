using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>管理变更和绑定保存共用 SQL Server 会话锁，防止引用检查后并发新增绑定。</summary>
public static class EdgeModelFieldCatalogGuard
{
    public static async Task<IAsyncDisposable> EnterAsync(ISqlSugarClient db)
    {
        var connection = new SqlSugarClient(new ConnectionConfig
        {
            DbType = db.CurrentConnectionConfig.DbType,
            ConnectionString = db.CurrentConnectionConfig.ConnectionString,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute
        });
        try
        {
            var result = await connection.Ado.SqlQuerySingleAsync<int>("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=N'EdgeModelFieldCatalog:configuration',
                    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=10000;
                SELECT @result;
                """);
            if (result < 0)
            {
                throw new InvalidOperationException("字段配置正在被其他操作修改，请稍后重试");
            }
            return new Lease(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "__UNCLASSIFIED__" : model.Trim();

    public static bool PathsOverlap(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<HashSet<string>> GetRemovedPathsAsync(ISqlSugarClient db, string model)
    {
        var normalized = NormalizeModel(model);
        var paths = await db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.ModelMark == normalized && x.IsSelectionDisabled)
            .Select(x => x.SourcePath).ToListAsync();
        return paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task RequireModelPathsAsync(ISqlSugarClient db, string model, IEnumerable<string?> paths)
    {
        var requested = paths.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (requested.Length == 0) return;
        var removed = await GetRemovedPathsAsync(db, model);
        if (requested.Any(path => removed.Any(item => PathsOverlap(item, path))))
        {
            throw new InvalidOperationException("配置中包含已移出字段库的字段，请先恢复或重新选择字段");
        }
    }

    public static async Task RequireDevicePathsAsync(ISqlSugarClient db, string tenant, string device,
        IEnumerable<string?> paths)
    {
        var requested = paths.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (requested.Length == 0) return;
        var snapshot = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Where(x => x.TenantMark == tenant && x.DeviceCode == device)
            .Select(x => new { x.ModelMark }).FirstAsync();
        if (snapshot != null) await RequireModelPathsAsync(db, NormalizeModel(snapshot.ModelMark), requested);
    }

    private sealed class Lease(SqlSugarClient connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.Ado.ExecuteCommandAsync("""
                    EXEC sys.sp_releaseapplock @Resource=N'EdgeModelFieldCatalog:configuration', @LockOwner=N'Session';
                    """);
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
