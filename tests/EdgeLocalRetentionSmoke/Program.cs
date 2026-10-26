using System.Reflection;
using Model.EdgeGateway;
using Service;
using SqlSugar;

foreach (var example in new[]
{
    (Now: new DateTime(2026, 10, 19), Oldest: new DateTime(2026, 10, 13)), // Monday
    (Now: new DateTime(2026, 10, 20), Oldest: new DateTime(2026, 10, 14)),
    (Now: new DateTime(2026, 10, 23), Oldest: new DateTime(2026, 10, 19)),
    (Now: new DateTime(2026, 10, 24), Oldest: new DateTime(2026, 10, 19)), // Saturday
    (Now: new DateTime(2026, 10, 25), Oldest: new DateTime(2026, 10, 19)), // Sunday
    (Now: new DateTime(2026, 10, 26), Oldest: new DateTime(2026, 10, 20))
})
{
    Require(EdgeLocalRetentionPolicy.GetOldestRetainedDate(example.Now) == example.Oldest, "weekday/weekend boundary");
}

// Independent day-by-day calculation verifies strict >4 semantics, including files created on weekends.
for (var offset = 0; offset < 40; offset++)
{
    var now = new DateTime(2026, 10, 1).AddDays(offset);
    var cutoff = EdgeLocalRetentionPolicy.GetOldestRetainedDate(now);
    for (var age = 0; age < 35; age++)
    {
        var fileDate = now.AddDays(-age);
        var workdays = 0;
        for (var date = fileDate.AddDays(1); date <= now; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) workdays++;
        }
        Require((fileDate < cutoff) == (workdays > 4), $"strict workday age {fileDate:d} through {now:d}");
    }
}

var testName = "CodexEdgeLocalRetention_" + Guid.NewGuid().ToString("N");
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), testName));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "edge-gateway.db");
var createDay = typeof(EdgeLocalDatabaseService).GetMethod("GetDailyDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!;
var closeDay = typeof(EdgeLocalDatabaseService).GetMethod("CloseDailyDatabase", BindingFlags.Instance | BindingFlags.NonPublic)!;
var cleanup = typeof(EdgeLocalDatabaseService).GetMethod("CleanupExpiredDatabaseFilesIfDueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
var initializedDays = new HashSet<DateTime>();
EdgeLocalDatabaseService? store = null;
var scope = new SqlSugarScope(new ConnectionConfig
{
    ConfigId = "edge-gateway", ConnectionString = $"Data Source={path}",
    DbType = SqlSugar.DbType.Sqlite, InitKeyType = InitKeyType.Attribute, IsAutoCloseConnection = true
});
try
{
    var now = DateTime.UtcNow;
    var cutoff = EdgeLocalRetentionPolicy.GetOldestRetainedDate(now);
    store = new EdgeLocalDatabaseService(scope, new EdgeGatewayBootstrapOptions { DatabasePath = path });
    var expiredDays = new[] { cutoff.AddDays(-1), cutoff.AddDays(-2), cutoff.AddDays(-3) };
    var statuses = new[] { "Pending", "Failed", "Uploaded" };
    for (var index = 0; index < expiredDays.Length; index++)
    {
        AddDay(expiredDays[index], statuses[index]);
    }
    AddDay(cutoff, "Pending");
    AddDay(now.Date.AddDays(-1), "Pending");
    AddDay(now.Date, "Pending");

    // These are deliberately invalid SQLite files; expiry must not open or inspect them before deletion.
    var corruptDay = cutoff.AddDays(-4);
    File.WriteAllText(DailyPath(corruptDay), "corrupt test fixture");
    File.WriteAllText(DailyPath(corruptDay) + "-wal", "stale wal fixture");
    File.WriteAllText(DailyPath(corruptDay) + "-shm", "stale shm fixture");
    var other = Path.Combine(root, "another-module-20000101.db");
    var invalid = Path.Combine(root, "edge-gateway-invalid.db");
    File.WriteAllText(other, "unrelated module");
    File.WriteAllText(invalid, "not a daily database");
    await store.InitializeAsync(CancellationToken.None);

    foreach (var day in expiredDays.Append(corruptDay))
    {
        Require(!File.Exists(DailyPath(day)) && !File.Exists(DailyPath(day) + "-wal") &&
            !File.Exists(DailyPath(day) + "-shm"), "expired DB/WAL/SHM removed regardless of status, even if corrupt");
    }
    Require(File.Exists(DailyPath(cutoff)), "exactly four workdays retained");
    Require(File.Exists(DailyPath(now.Date)), "today retained");
    Require(File.Exists(other) && File.Exists(invalid) && File.Exists(path), "unrelated names and legacy base database untouched");

    // Daily cleanup is idempotent and does not delete fresh files repeatedly.
    await (Task)cleanup.Invoke(store, [now, CancellationToken.None])!;
    Require(File.Exists(DailyPath(cutoff)), "same-date cleanup leaves retained files intact");
    var cancelledDay = cutoff.AddDays(-5);
    File.WriteAllText(DailyPath(cancelledDay), "cancelled cleanup test fixture");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await (Task)cleanup.Invoke(store, [now.AddDays(1), cancelled.Token])!;
        throw new InvalidOperationException("cancelled cleanup should fail before deleting");
    }
    catch (Exception exception) when (exception is OperationCanceledException ||
        exception is TargetInvocationException { InnerException: OperationCanceledException })
    {
        Require(File.Exists(DailyPath(cancelledDay)), "cancelled cleanup does not remove files");
    }
    await (Task)cleanup.Invoke(store, [now.AddDays(1), CancellationToken.None])!;
    Require(!File.Exists(DailyPath(cancelledDay)), "next cleanup retries after cancellation");
    Console.WriteLine("PASS: strict four-workday expiry, weekday/weekend boundaries, real SQLite deletion for Pending/Failed/Uploaded, connection disposal, WAL/SHM cleanup, corrupt files, filename isolation, cancellation and daily idempotency. Only isolated temporary files were used.");

    void AddDay(DateTime day, string status)
    {
        initializedDays.Add(day.Date);
        var db = (SqlSugarClient)createDay.Invoke(store, [day])!;
        db.Insertable(new EdgeLocalMessageQueueEntity
        {
            MessageId = Guid.NewGuid().ToString("N"), DeviceId = "test", SourceId = "test", Status = status,
            CreateTime = day.Date, UploadTime = status == "Uploaded" ? day.Date : null
        }).ExecuteCommand();
    }
}
finally
{
    if (store != null)
    {
        foreach (var day in initializedDays.Append(DateTime.UtcNow.Date)) closeDay.Invoke(store, [day]);
    }
    scope.Dispose();
    if (Path.GetFullPath(root) != Path.Combine(Path.GetFullPath(Path.GetTempPath()), testName) ||
        !testName.StartsWith("CodexEdgeLocalRetention_", StringComparison.Ordinal))
        throw new InvalidOperationException("Unsafe temporary-directory cleanup target");
    Directory.Delete(root, true);
}

string DailyPath(DateTime day) => Path.Combine(root, $"edge-gateway-{day:yyyyMMdd}.db");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
