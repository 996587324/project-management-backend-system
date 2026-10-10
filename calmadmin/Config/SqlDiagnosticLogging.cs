using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using SqlSugar;
using Service;

namespace calmadmin.Config;

/// <summary>只输出耗时与 SQL 指纹，禁止将参数、原始 SQL 中的凭据或业务值写入诊断日志。</summary>
public static class SqlDiagnosticLogging
{
    public static void Configure(ISqlSugarClient db, ILogger logger, IConfiguration configuration)
    {
        var slowMilliseconds = Math.Max(1, configuration.GetValue("BackendDiagnostics:SlowSqlMilliseconds", 500));
        var logAll = configuration.GetValue("BackendDiagnostics:LogAllSql", false);
        object configId = db.CurrentConnectionConfig.ConfigId;
        var executionTimer = new AsyncLocal<Stopwatch?>();
        // CopyNew 会复制 AOP 委托，不能在回调里读取原客户端的 SqlExecutionTime。
        db.Aop.OnLogExecuting = (sql, parameters) => executionTimer.Value = Stopwatch.StartNew();

        db.Aop.OnLogExecuted = (sql, parameters) =>
        {
            var elapsed = executionTimer.Value?.Elapsed.TotalMilliseconds ?? 0;
            if (elapsed >= slowMilliseconds)
            {
                logger.LogWarning(
                    "Slow SQL. Database: {Database}; Operation: {Operation}; Elapsed: {ElapsedMilliseconds} ms; Fingerprint: {Fingerprint}; Length: {SqlLength}; TraceId: {TraceId}.",
                    configId, DatabaseOperationScope.OperationName ?? "HTTP/Startup", elapsed,
                    Fingerprint(sql), sql.Length, Activity.Current?.TraceId.ToString());
            }
            else if (logAll)
            {
                logger.LogInformation(
                    "SQL completed. Database: {Database}; Operation: {Operation}; Elapsed: {ElapsedMilliseconds} ms; Fingerprint: {Fingerprint}; Length: {SqlLength}; TraceId: {TraceId}.",
                    configId, DatabaseOperationScope.OperationName ?? "HTTP/Startup", elapsed,
                    Fingerprint(sql), sql.Length, Activity.Current?.TraceId.ToString());
            }
        };
        db.Aop.OnError = error => logger.LogError(
            "SQL failed. Database: {Database}; Operation: {Operation}; ErrorType: {ErrorType}; Fingerprint: {Fingerprint}; TraceId: {TraceId}.",
            configId, DatabaseOperationScope.OperationName ?? "HTTP/Startup", error.GetType().Name,
            Fingerprint(error.Sql), Activity.Current?.TraceId.ToString());
    }

    private static string Fingerprint(string? sql)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql ?? string.Empty));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
