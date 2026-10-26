using Interface;
using Model.HourlyProduction;
using Model.SystemApplications;
using SqlSugar;
using System.Text.Json;

internal static class HourlyDeletionSmoke
{
    public static async Task RunAsync(IHourlyProductionService service, ISqlSugarClient db, ApplicationAccessContext access)
    {
        var board = new HourlyBoardDefinition
        {
            Code = "delete-regression",
            Lines = new[] { "L1", "L2" }.Select(code => new HourlyLineDefinition
            {
                Code = code, Name = code, SummaryProcessCode = "P3",
                Processes = new[] { "P1", "P2", "P3" }.Select(process => new HourlyProcessDefinition
                {
                    Code = process, RowCode = process, Name = "工序" + process,
                    Bindings = [new HourlyBinding { Code = "binding", SourceKey = "tenant", DeviceKey = "device-" + process, SnPath = "/SN" }]
                }).ToList()
            }).ToList()
        };
        board = await service.SaveAsync(access, board);
        // 模拟已存在的历史记录，删除配置不能级联删除过站数据或报表。
        var passId = await db.Insertable(new HourlyProductionPassEntity
        {
            EventKey = "delete-regression", BoardCode = board.Code, ProcessKey = "L1/P1", Sn = "historic-sn",
            ProductionDate = DateTime.Today, OccurredAt = DateTime.Today.AddHours(8), SlotStart = DateTime.Today.AddHours(8),
            Quantity = 1, Output = 1, RuleVersion = 1
        }).ExecuteReturnBigIdentityAsync();
        var reportId = await db.Insertable(new HourlyProductionReportEntity
        {
            ReportKey = "delete-regression", BoardCode = board.Code, SlotStart = DateTime.Today.AddHours(8),
            ConfigVersion = 1, SnapshotJson = "{}", CreatedAt = DateTime.Now
        }).ExecuteReturnBigIdentityAsync();
        var retained = JsonSerializer.Serialize(board.Lines[0].Processes.Skip(1));
        var other = JsonSerializer.Serialize(board.Lines[1]);
        board.Lines[0].Processes.RemoveAt(0);
        board = await service.SaveAsync(access, board);
        Check(board.Version == 2 && JsonSerializer.Serialize(board.Lines[0].Processes) == retained &&
            JsonSerializer.Serialize(board.Lines[1]) == other, "deleting process saves remaining bindings without changing the other line");
        Check(await db.Queryable<HourlyProductionPassEntity>().AnyAsync(x => x.Id == passId) &&
            await db.Queryable<HourlyProductionReportEntity>().AnyAsync(x => x.Id == reportId), "deleting process preserves saved passes and reports");
        var stale = Clone(board);
        stale.Lines[0].Processes.RemoveAll(x => x.Code == "P3");
        await Invalid(() => service.SaveAsync(access, stale), "总产出口径");
        stale.Lines[0].SummaryProcessCode = "P2";
        await service.SaveAsync(access, stale);
        var incomplete = Clone(board);
        incomplete.Lines[1].Processes[1].Bindings[0].DeviceKey = "";
        await Invalid(() => service.SaveAsync(access, incomplete), "线体“L2”工序“工序P2”数据绑定 1");
        var removeIncomplete = Clone(board);
        removeIncomplete.Lines[1].Processes[1].Bindings[0].DeviceKey = "";
        removeIncomplete.Lines[1].Processes.RemoveAt(1);
        // 使用最新版本，只需验证剩余配置，已经删除的工序不参与校验。
        removeIncomplete.Version = (await service.ListAsync(access)).Single(x => x.Code == board.Code).Version;
        await service.SaveAsync(access, removeIncomplete);
        Check(true, "removed incomplete process is ignored while incomplete remaining bindings report exact location");
    }

    private static HourlyBoardDefinition Clone(HourlyBoardDefinition board) =>
        JsonSerializer.Deserialize<HourlyBoardDefinition>(JsonSerializer.Serialize(board))!;

    private static async Task Invalid(Func<Task> action, string message)
    {
        try { await action(); }
        catch (ArgumentException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected configuration rejection: " + message);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
