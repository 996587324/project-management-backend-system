using System.Text.Json;
using Interface;
using Model.HourlyProduction;
using Model.SystemApplications;
using Service;
using SqlSugar;

internal static class HourlyReaddSmoke
{
    public static async Task RunAsync(IHourlyProductionService service, IHourlyProductionIngress ingress, SqlSugarScope scope, ApplicationAccessContext access)
    {
        var db = scope.GetConnectionScope("calmadmin");
        var board = await service.SaveAsync(access, Board("readd-regression"));
        var original = Copy(board.Lines[0].Processes[0]);
        var initialTime = HourlyProductionRules.Now.AddSeconds(-10);
        async Task Send(string message, string sn, DateTime time)
        {
            await ingress.CaptureRealtimeAsync("readd-tenant", "readd-device", message, time, [JsonSerializer.Serialize(new { SN = sn })], default);
            await service.TickAsync(default);
        }
        await Send("readd-original", "same-sn", initialTime);
        var pass = await db.Queryable<HourlyProductionPassEntity>().FirstAsync(x => x.BoardCode == board.Code && x.ProcessKey == "L1/P1");
        Check(pass != null && original.InstanceId == "L1/P1", "first identity preserves legacy pass keys");
        var report = new HourlyProductionReportEntity { ReportKey = "readd-frozen", BoardCode = board.Code, SlotStart = initialTime.Date,
            ConfigVersion = board.Version, SnapshotJson = "{\"frozen\":true}", CreatedAt = HourlyProductionRules.Now };
        report.Id = await db.Insertable(report).ExecuteReturnBigIdentityAsync();
        board.Lines[0].Processes.RemoveAt(0);
        board = await service.SaveAsync(access, board);
        var archived = (await service.RemovedProcessesAsync(access, board.Code, "L1")).Single();
        Check(archived.CanRestore && archived.Definition!.Bindings[0].DeviceKey == "readd-device", "removed process retains full binding and statistical configuration");
        var invalid = Copy(board);
        var invalidProcess = Copy(archived.Definition!); invalidProcess.CreationMode = "Restore"; invalidProcess.Mode = "Count";
        invalid.Lines[0].Processes.Add(invalidProcess);
        await Reject(() => service.SaveAsync(access, invalid), "restore cannot silently change statistical rules");
        board.Lines[0].Processes.Add(Copy(archived.Definition!)); board.Lines[0].Processes[^1].CreationMode = "Restore";
        async Task<bool> RestoreAttempt(HourlyBoardDefinition request)
        {
            try { await service.SaveAsync(access, request); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var attempts = await Task.WhenAll(RestoreAttempt(Copy(board)), RestoreAttempt(Copy(board)));
        Check(attempts.Count(x => x) == 1, "concurrent restoration commits once and rejects the stale version");
        board = (await service.ListAsync(access)).Single(x => x.Code == board.Code);
        var restored = board.Lines[0].Processes.Single(x => x.Code == "P1");
        Check(restored.InstanceId == original.InstanceId && restored.ActiveSince.HasValue, "same-code restoration resumes original identity with activation boundary");
        await Send("readd-gap", "gap-sn", restored.ActiveSince!.Value.AddSeconds(-1));
        Check(!await db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.BoardCode == board.Code && x.ProcessKey == restored.InstanceId && x.Sn == "gap-sn"), "restoration does not backfill events from the removed interval");
        await Send("readd-resumed", "same-sn", restored.ActiveSince.Value.AddSeconds(1));
        Check(await db.Queryable<HourlyProductionPassEntity>().Where(x => x.BoardCode == board.Code && x.ProcessKey == original.InstanceId).SumAsync(x => x.Output) == 1,
            "restored process continues original SN duplicate suppression");
        board.Lines[0].Processes.RemoveAll(x => x.Code == "P1"); board = await service.SaveAsync(access, board);
        var replacement = Copy(original); replacement.InstanceId = ""; replacement.CreationMode = "New";
        board.Lines[0].Processes.Add(replacement); board = await service.SaveAsync(access, board);
        var fresh = board.Lines[0].Processes.Single(x => x.Code == "P1");
        Check(fresh.InstanceId != original.InstanceId && fresh.InstanceId.StartsWith("p_"), "same-code new process gets independent server identity");
        await Send("readd-new", "same-sn", fresh.ActiveSince!.Value.AddSeconds(1));
        var count = await db.Queryable<HourlyProductionPassEntity>().CountAsync(x => x.BoardCode == board.Code && x.ProcessKey == fresh.InstanceId);
        await Send("readd-new", "same-sn", fresh.ActiveSince.Value.AddSeconds(1));
        Check(count == 1 && await db.Queryable<HourlyProductionPassEntity>().CountAsync(x => x.BoardCode == board.Code && x.ProcessKey == fresh.InstanceId) == 1,
            "new identity does not repeat transport retries");
        var freshPass = await db.Queryable<HourlyProductionPassEntity>().FirstAsync(x => x.BoardCode == board.Code && x.ProcessKey == fresh.InstanceId);
        Check(freshPass.Output == 1 && freshPass.Rework == 0 && await db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.Id == pass!.Id),
            "same SN counts independently in new instance while original passes survive");
        var runtime = await service.RuntimeAsync(access, board.Code);
        Check(runtime.Lines[0].Processes.Single(x => x.Code == "P1").Points.Sum(x => x.Output ?? 0) == 1,
            "runtime uses new identity without merging original hourly buckets");
        Check((await db.Queryable<HourlyProductionReportEntity>().InSingleAsync(report.Id)).SnapshotJson == "{\"frozen\":true}", "delete restore and recreate preserve frozen report snapshots");
        var tampered = Copy(board); tampered.Lines[0].Processes.Single(x => x.Code == "P1").InstanceId = original.InstanceId;
        await Reject(() => service.SaveAsync(access, tampered), "client cannot forge an existing process identity");
        var stale = Copy(board); stale.Version--;
        var identityCount = await db.Queryable<HourlyProductionProcessEntity>().CountAsync();
        await Reject(() => service.SaveAsync(access, stale), "stale configuration cannot change identity state");
        Check(await db.Queryable<HourlyProductionProcessEntity>().CountAsync() == identityCount, "rejected save leaves no orphan identities");
        var switchBack = Copy(board);
        switchBack.Lines[0].Processes.RemoveAll(x => x.Code == "P1");
        var oldInstance = Copy(original); oldInstance.CreationMode = "Restore";
        switchBack.Lines[0].Processes.Add(oldInstance);
        board = await service.SaveAsync(access, switchBack);
        Check(board.Lines[0].Processes.Single(x => x.Code == "P1").InstanceId == original.InstanceId &&
            await db.Queryable<HourlyProductionProcessEntity>().CountAsync(x => x.BoardCode == board.Code && x.LineCode == "L1" && x.Code == "P1" && x.Active) == 1,
            "remove current instance and restore older same-code instance commit atomically");

        // Simulate a populated pre-upgrade database, including a process already removed before this feature existed.
        var legacy = Board("readd-legacy"); legacy.Version = 7; legacy.Lines[0].Processes.RemoveAt(0);
        await db.Insertable(new HourlyProductionBoardEntity { Code = legacy.Code, AppCode = "A", DefinitionJson = JsonSerializer.Serialize(legacy), Version = 7, UpdatedBy = access.UserId, UpdateTime = HourlyProductionRules.Now }).ExecuteCommandAsync();
        var oldPass = new HourlyProductionPassEntity { EventKey = "readd-legacy-pass", BoardCode = legacy.Code, ProcessKey = "L1/P1", Sn = "legacy-sn",
            ProductionDate = initialTime.Date, OccurredAt = initialTime, SlotStart = initialTime.Date, Quantity = 9, Output = 9, RuleVersion = 1 };
        oldPass.Id = await db.Insertable(oldPass).ExecuteReturnBigIdentityAsync();
        await db.Insertable(new HourlyProductionBucketEntity { BoardCode = legacy.Code, ProcessKey = "L1/P1", SlotStart = initialTime.Date, Output = 9, UpdatedAt = HourlyProductionRules.Now }).ExecuteCommandAsync();
        await new HourlyProductionInitializer(scope, new HourlyProductionCatalog()).InitializeAsync(default);
        var unknown = (await service.RemovedProcessesAsync(access, legacy.Code, "L1")).Single();
        Check(!unknown.CanRestore && unknown.Definition == null && unknown.RemovedAt == null, "legacy removed process preserves unknown configuration and timestamps as NULL");
        legacy = (await service.ListAsync(access)).Single(x => x.Code == legacy.Code);
        Check(legacy.Version == 7 && legacy.Lines.All(x => x.Processes.All(p => p.InstanceId.Length > 0)), "migration backfills active identities without changing business configuration version");
        var newLegacy = Copy(original); newLegacy.InstanceId = ""; newLegacy.CreationMode = "New";
        legacy.Lines[0].Processes.Add(newLegacy); legacy = await service.SaveAsync(access, legacy);
        Check(legacy.Lines[0].Processes.Single(x => x.Code == "P1").InstanceId != "L1/P1" && await db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.Id == oldPass.Id),
            "already-removed legacy code can be reused without deleting old statistics");
        Check((await service.RuntimeAsync(access, legacy.Code)).DataUpdatedAt == null, "retired buckets do not masquerade as current-instance data updates");
        await db.Ado.ExecuteCommandAsync("ALTER TABLE HourlyProductionProcess DROP COLUMN RemovedAt");
        var beforeMigration = await db.Queryable<HourlyProductionProcessEntity>().CountAsync();
        await new HourlyProductionInitializer(scope, new HourlyProductionCatalog()).InitializeAsync(default);
        await new HourlyProductionInitializer(scope, new HourlyProductionCatalog()).InitializeAsync(default);
        Check(await db.Queryable<HourlyProductionProcessEntity>().CountAsync() == beforeMigration &&
            await db.Ado.GetIntAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='HourlyProductionProcess' AND COLUMN_NAME IN ('RemovedAt','ActivatedAt','DefinitionJson') AND IS_NULLABLE='YES'") == 3,
            "populated identity table upgrades optional columns repeatably without data loss");
    }
    private static HourlyBoardDefinition Board(string code) => new() { Code = code, StartTime = "00:00", EndTime = "00:00", Published = true,
        Lines = new[] { "L1", "L2" }.Select(line => new HourlyLineDefinition { Code = line, Name = line, SummaryProcessCode = "P2", Processes = new[] { "P1", "P2" }.Select(process => new HourlyProcessDefinition {
            Code = process, RowCode = process, Name = process, Bindings = process == "P1" ? [new HourlyBinding { Code = "binding", SourceKey = "readd-tenant", DeviceKey = "readd-device", SnPath = "/SN" }] : [] }).ToList() }).ToList() };
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL lifecycle: " + message); Console.WriteLine("PASS lifecycle: " + message); }
    private static async Task Reject(Func<Task> action, string message) { try { await action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { Check(true, message); return; } throw new Exception("FAIL lifecycle: " + message); }
}
