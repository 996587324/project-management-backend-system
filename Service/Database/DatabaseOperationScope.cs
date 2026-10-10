using SqlSugar;
using System.Runtime.CompilerServices;

namespace Service;

/// <summary>
/// 为一轮后台业务及其调用链提供独立数据库上下文。
/// 并行子任务必须分别 Begin；事务内所有服务通过 GetConnection 复用同一个实例。
/// </summary>
public sealed class DatabaseOperationScope : IDisposable
{
    private static readonly AsyncLocal<DatabaseOperationScope?> Current = new();
    private readonly DatabaseOperationScope? _previous;
    private readonly SqlSugarScope _source;
    private readonly string _operationName;
    private readonly Dictionary<string, ISqlSugarClient> _connections = new(StringComparer.Ordinal);
    private bool _disposed;

    private DatabaseOperationScope(SqlSugarScope source, string operationName)
    {
        _source = source;
        _operationName = operationName;
        _previous = Current.Value;
        Current.Value = this;
    }

    public static string? OperationName => Current.Value?._operationName;

    public static DatabaseOperationScope Begin(SqlSugarScope source, [CallerMemberName] string operationName = "")
    {
        return new DatabaseOperationScope(source, operationName);
    }

    public static ISqlSugarClient GetConnection(SqlSugarScope source, string configId)
    {
        var operation = Current.Value;
        if (operation == null || !ReferenceEquals(operation._source, source))
        {
            return source.GetConnectionScope(configId);
        }

        ObjectDisposedException.ThrowIf(operation._disposed, operation);
        if (!operation._connections.TryGetValue(configId, out var connection))
        {
            var sourceConnection = source.GetConnection(configId);
            connection = sourceConnection.CopyNew();
            operation._connections.Add(configId, connection);
        }

        return connection;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Current.Value = _previous;
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
    }
}
