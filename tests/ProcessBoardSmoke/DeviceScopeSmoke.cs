using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;
using Service;
using SqlSugar;

internal static class DeviceScopeSmoke
{
    private static ProcessSourceBinding Source(string device, string tenant = "tenant", string path = "/value") =>
        new() { TenantMark = tenant, DeviceMark = device, SourcePath = path };
    private static ProcessFieldDefinition Field(string code, string device) =>
        new() { Code = code, Name = code, Source = Source(device) };
    private static ProcessBoardDefinition Copy(ProcessBoardDefinition definition) =>
        JsonConvert.DeserializeObject<ProcessBoardDefinition>(JsonConvert.SerializeObject(definition))!;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static async Task Rejected(Func<Task> action, string text)
    {
        try { await action(); } catch (ArgumentException error) when (error.Message.Contains(text)) { return; }
        throw new Exception("Expected rejection: " + text);
    }

    public static async Task RunAsync(SqlSugarScope scope, ProcessBoardService service, ApplicationAccessContext admin, long regionId)
    {
        var db = scope.GetConnectionScope("calmadmin");
        var line = await service.SaveAsync(admin, new()
        { Kind = "Line", ParentId = regionId, Code = "SCOPE", Name = "设备范围测试线", Devices = [Source("scope-a"), Source("scope-b")] });
        ProcessBoardDefinition Card(string code, params ProcessFieldDefinition[] fields) =>
            new() { Kind = "Card", ParentId = line.Id, Code = code, Name = code, Fields = fields.ToList() };
        var valid = await service.SaveAsync(admin, Card("scope-valid", Field("value", "scope-a")));
        await Rejected(() => service.SaveAsync(admin, Card("scope-outside", Field("value", "scope-x"))), "未关联");
        var variable = Field("formula", "scope-a");
        variable.Variables["outside"] = Source("scope-x");
        await Rejected(() => service.SaveAsync(admin, Card("scope-variable", variable)), "变量");
        var tenant = Field("tenant", "scope-a"); tenant.Source.TenantMark = "other";
        await Rejected(() => service.SaveAsync(admin, Card("scope-tenant", tenant)), "未关联");
        await Rejected(() => service.SaveAsync(admin, Card("scope-case", Field("case", "SCOPE-A"))), "未关联");
        var metadata = Card("scope-metadata"); metadata.Devices.Add(Source("scope-x"));
        await Rejected(() => service.SaveAsync(admin, metadata), "关联设备");

        var emptyLine = await service.SaveAsync(admin, new() { Kind = "Line", ParentId = regionId, Code = "SCOPE_EMPTY", Name = "未关联设备线" });
        var emptyCard = Card("scope-empty", Field("value", "scope-a")); emptyCard.ParentId = emptyLine.Id;
        await Rejected(() => service.SaveAsync(admin, emptyCard), "未关联");
        emptyCard.Fields = [new() { Code = "text", Name = "文字", FixedText = "预设值" },
            new() { Code = "number", Name = "固定数值", Source = new() { FixedValue = 5 }, Variables = new() { ["constant"] = new() { FixedValue = 2 } } }];
        await service.SaveAsync(admin, emptyCard);
        Check(true, "new card, variable and metadata bindings obey exact line device bounds; fixed values work on an empty line");

        // 仅在隔离库中模拟升级前存在的越界卡片，不通过新保存逻辑制造非法配置。
        var historical = Card("scope-legacy", Field("legacy", "scope-x"));
        historical.Fields[0].Variables["v"] = Source("scope-y");
        historical.Version = 1;
        var entity = new ProcessBoardConfigEntity { Kind = "Card", ParentId = line.Id, Code = historical.Code, Name = historical.Name,
            Enabled = true, Version = 1, DefinitionJson = JsonConvert.SerializeObject(historical), CreateTime = DateTime.Now, UpdateTime = DateTime.Now };
        historical.Id = await db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        historical.Name = "历史卡片仅改名称";
        historical = await service.SaveAsync(admin, historical);
        var changedPath = Copy(historical); changedPath.Fields[0].Source.SourcePath = "/new-path";
        await Rejected(() => service.SaveAsync(admin, changedPath), "未关联");
        var newField = Copy(historical); newField.Fields.Add(Field("new", "scope-x"));
        await Rejected(() => service.SaveAsync(admin, newField), "未关联");
        var renamed = Copy(historical); renamed.Fields[0].Variables = new() { ["newSlot"] = Source("scope-y") };
        await Rejected(() => service.SaveAsync(admin, renamed), "未关联");
        var cloned = Copy(historical); cloned.Id = 0; cloned.Version = 0; cloned.Code = "scope-clone";
        await Rejected(() => service.SaveAsync(admin, cloned), "未关联");
        Check((await db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(historical.Id)).Version == historical.Version,
            "legacy binding is preserved without allowing path changes, new fields, renamed variables or copied-card bypass");

        valid.Enabled = false; await service.SaveAsync(admin, valid);
        var removed = Copy(line); removed.Devices.RemoveAll(x => x.DeviceMark == "scope-a");
        await Rejected(() => service.SaveAsync(admin, removed), "scope-valid");
        var unused = Copy(line); unused.Devices.RemoveAll(x => x.DeviceMark == "scope-b");
        line = await service.SaveAsync(admin, unused);
        line.Devices.Add(Source("scope-b")); line = await service.SaveAsync(admin, line);
        var raceLine = Copy(line); raceLine.Devices.RemoveAll(x => x.DeviceMark == "scope-b");
        async Task<bool> TrySave(ProcessBoardDefinition definition)
        { try { await service.SaveAsync(admin, definition); return true; } catch (ArgumentException) { return false; } }
        var race = await Task.WhenAll(TrySave(raceLine), TrySave(Card("scope-race", Field("value", "scope-b"))));
        Check(race.Count(x => x) == 1, "device removal and new card binding are serialized; disabled cards still protect references");
    }
}
