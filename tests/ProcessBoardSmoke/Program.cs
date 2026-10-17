using Interface;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Model.EdgeGateway;
using Model.Menu;
using Model.ProcessBoard;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using Newtonsoft.Json;
using Service;
using SqlSugar;

if (args.Contains("--field-layout-only"))
{
    await FieldColumnsSmoke.RunAsync();
    return;
}

// 此分支仅存在于隔离测试可执行文件，不声明任何真实商业/非商业授权。
if (args.Contains("--process-formula-worker"))
{
    var input = JsonConvert.DeserializeObject<ProcessFormulaWorkerRequest>((await Console.In.ReadLineAsync())!)!;
    var output = ProcessFormulaWorker.EvaluateExpression(input.Expression);
    Console.WriteLine("PROCESS_FORMULA_RESULT:" + JsonConvert.SerializeObject(output));
    return;
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

ProcessFormulaResult Evaluate(string expression, Dictionary<string, double>? variables = null,
    List<string>? functions = null, Dictionary<string, double>? constants = null) =>
    ProcessFormulaWorker.EvaluateExpression(new ProcessFormulaRequest
    {
        Formula = expression, Variables = variables ?? [], Functions = functions ?? [], Constants = constants ?? []
    });

Check(Evaluate("x/10+2", new() { ["x"] = 25 }).Value == 4.5, "arithmetic and x binding");
Check(Math.Abs(Evaluate("sin(pi/2)+sqrt(16)+2^3").Value!.Value - 13) < 1e-9, "full engine functions, constants and exponentiation");
Check(Evaluate("if(x>10,20,5)", new() { ["x"] = 11 }).Value == 20, "conditional expression");
Check(Evaluate("sum(i,1,10,i)").Value == 55, "summation is not blocked by four-operation whitelist");
Check(Evaluate("f(x)", new() { ["x"] = 3 }, ["f(a)=a^2+offset"], new() { ["offset"] = 2 }).Value == 11,
    "custom mathematical function sees configured constant");
Check(!Evaluate("1/0").Success && !Evaluate("sqrt(-1)").Success, "nonfinite results are rejected");
Check(!Evaluate("missing+1").Success && !Evaluate("x+").Success, "missing variables and syntax errors are explicit");
Check(ProcessBoardService.Changed("0.1", "0.3", 0.2), "threshold comparison handles binary floating point rounding");
Check(ProcessBoardService.LimitState("3.005", new ProcessFieldDefinition { UpperLimit = 3.005, ComparisonPrecision = 2 }) == "Normal",
    "value and configured limits use the same comparison precision");
Check(!ProcessFormulaWorker.Evaluate(new ProcessFormulaWorkerRequest()).Success, "no fabricated personal license declaration");
var subprocess = new ProcessFormulaService(Options.Create(new ProcessFormulaOptions
{
    TermsAccepted = true, Signature = "isolated-test-fixture-not-a-license", TimeoutSeconds = 5
}));
var isolated = await subprocess.EvaluateAsync(new ProcessFormulaRequest { Formula = "sqrt(81)" });
Check(isolated.Success && isolated.Value == 9, "isolated worker IPC and output framing");
var concurrent = await Task.WhenAll(Enumerable.Range(1, 6).Select(value => subprocess.EvaluateAsync(new ProcessFormulaRequest
{
    Formula = "x^2", Variables = new() { ["x"] = value }
})));
Check(concurrent.Select(x => x.Value).SequenceEqual(new double?[] { 1, 4, 9, 16, 25, 36 }), "concurrent arguments are isolated");
var shortBudget = new ProcessFormulaService(Options.Create(new ProcessFormulaOptions
{
    TermsAccepted = true, Signature = "isolated-test-fixture-not-a-license", TimeoutSeconds = 1
}));
var timeout = await shortBudget.EvaluateAsync(new ProcessFormulaRequest { Formula = "sum(i,1,1000000000000,i)" });
Check(!timeout.Success && timeout.Error!.Contains("超时"), "expensive expression terminated by process timeout");

if (args.Length == 0)
{
    Console.WriteLine("Formula smoke complete; pass backend root to run isolated SQL checks.");
    return;
}

// 从本地配置私下读取连接串，只在随机隔离数据库建表/写入，不打印凭据，不启动真实后台服务。
var root = Path.GetFullPath(args[0]);
var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "calmadmin", "appsettings.json")));
var connection = config.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var databaseName = "CodexProcessBoard_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(new SqlConnectionStringBuilder(connection) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
async Task Master(string sql)
{
    await using var command = master.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
await Master($"CREATE DATABASE [{databaseName}]");
SqlSugarScope? scope = null;
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true,
        ConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = databaseName }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    await ProcessBoardSchema.InitializeAsync(db);
    await ProcessBoardSchema.InitializeAsync(db);
    Check(await db.Queryable<ProcessBoardSettingEntity>().CountAsync() == 1, "fresh and repeat schema initialization");
    db.DbMaintenance.DropColumn("ProcessBoardSetting", "FormulaUseType");
    db.DbMaintenance.DropColumn("ProcessBoardSetting", "FormulaSignature");
    db.Ado.ExecuteCommand("""
        DECLARE @constraint sysname;
        SELECT @constraint = dc.name FROM sys.default_constraints dc
        JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'ProcessBoardSetting') AND c.name = N'FormulaTermsAccepted';
        IF @constraint IS NOT NULL BEGIN
            DECLARE @sql nvarchar(max) = N'ALTER TABLE ProcessBoardSetting DROP CONSTRAINT ' + QUOTENAME(@constraint);
            EXEC(@sql);
        END;
        """);
    db.DbMaintenance.DropColumn("ProcessBoardSetting", "FormulaTermsAccepted");
    await ProcessBoardSchema.InitializeAsync(db);
    await ProcessBoardSchema.InitializeAsync(db);
    Check(!(await db.Queryable<ProcessBoardSettingEntity>().InSingleAsync(1)).FormulaTermsAccepted,
        "populated old setting table upgrades with real SQL nullable/default constraints");
    Check(db.DbMaintenance.GetColumnInfosByTableName("ProcessBoardExport", false).Single(x => x.DbColumnName == "CompletedTime").IsNullable,
        "not-completed export time explicitly permits SQL NULL");
    db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity, EdgeRealtimeDeviceFieldLatestEntity>();
    db.CodeFirst.InitTables<sys_menu, SysApplicationMenuEntity, SysApplicationMenuEntryEntity>();
    var sourceRoot = new sys_menu
    {
        MenuId = "process-test-root", MenuName = "ProductionDashboard", ParentId = "0", Path = "/production-dashboard",
        Component = "/index/index", MetaTitle = "产线看板", MetaRoles = "", MetaAuthList = "[]", Status = true
    };
    await db.Insertable(sourceRoot).ExecuteCommandAsync();
    await db.Insertable(new SysApplicationMenuEntryEntity
    {
        AppCode = "B", SourceMenuId = sourceRoot.MenuId, PermissionKey = sourceRoot.MenuName,
        DefinitionJson = JsonConvert.SerializeObject(sourceRoot)
    }).ExecuteCommandAsync();
    await ProcessBoardMenus.SeedAsync(db);
    await ProcessBoardMenus.SeedAsync(db);
    Check(await db.Queryable<sys_menu>().CountAsync() == 3 && await db.Queryable<SysApplicationMenuEntryEntity>().CountAsync() == 3,
        "missing dynamic A/B routes are inserted once without overwriting existing menu instances");
    Check((await db.Queryable<sys_menu>().Where(x => x.MenuName == "ProcessBoard" || x.MenuName == "ProcessBoardConfig")
        .Select(x => x.MenuId).ToListAsync()).All(x => int.TryParse(x, out _)), "new A menu IDs remain editable by existing integer DTO");
    var admin = new ApplicationAccessContext
    {
        UserId = "smoke-user", AppCode = "A", AllResources = true, PlatformAdministrator = true,
        Permissions = new(StringComparer.OrdinalIgnoreCase) { "*" }
    };
    var service = new ProcessBoardService(scope, new TestFormula(), new TestPush());
    var region = await service.SaveAsync(admin, new ProcessBoardDefinition { Code = "R1", Name = "测试区域" });
    var line = await service.SaveAsync(admin, new ProcessBoardDefinition { Kind = "Line", ParentId = region.Id, Code = "L1", Name = "测试线体" });
    var card = await service.SaveAsync(admin, new ProcessBoardDefinition
    {
        Kind = "Card", ParentId = line.Id, Code = "C1", Name = "工艺参数",
        Fields = [new ProcessFieldDefinition
        {
            Code = "temperature", Name = "温度", Source = new() { TenantMark = "tenant", DeviceMark = "device", SourcePath = "/data/temp" },
            Formula = "x/10", UpperLimit = 3, TraceEnabled = true, TrendEnabled = true, ChangeThreshold = 0.5
        }]
    });
    await ProcessBoardSchema.InitializeAsync(db);
    Check(await db.Queryable<ProcessBoardConfigEntity>().CountAsync() == 3, "config inserts and repeated initialization preserve rows");
    var restricted = new ApplicationAccessContext
    {
        UserId = "limited", AppCode = "B", Permissions = new() { "ProcessBoard" }, LineCodes = new() { $"process:{line.Id}" },
        SourceKeys = new(StringComparer.Ordinal) { "tenant\u001fdevice" }
    };
    Check((await service.GetDefinitionsAsync(restricted)).Count == 3 &&
        !ProcessBoardService.CanReadLine(new ApplicationAccessContext { LineCodes = new() { "L1" } }, line.Id),
        "A/B scoped process identity does not reuse legacy line codes");
    async Task Input(string data)
    {
        var previous = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync();
        var sample = new EdgeRealtimeDeviceSnapshotEntity
        {
            Id = previous?.Id ?? 0, TenantMark = "tenant", DeviceCode = "device", ModelMark = "MODEL",
            DataTime = DateTime.Now, ReceiveTime = DateTime.Now, CreateTime = previous?.CreateTime ?? DateTime.Now,
            UpdateTime = DateTime.Now, DataListJson = "[{\"Data\":" + data + "}]"
        };
        await EdgeRealtimeFieldLatestStore.WriteGate.WaitAsync();
        try
        {
            await EdgeRealtimeFieldLatestStore.ApplyAsync(db, sample, previous);
            if (sample.Id == 0) await db.Insertable(sample).ExecuteCommandAsync();
            else await db.Updateable(sample).ExecuteCommandAsync();
        }
        finally { EdgeRealtimeFieldLatestStore.WriteGate.Release(); }
        await service.CaptureAsync(CancellationToken.None);
    }
    await Input("{\"temp\":25}");
    var value = await db.Queryable<ProcessBoardValueEntity>().FirstAsync();
    Check(JsonConvert.DeserializeObject<ProcessFieldRuntime>(value.RuntimeJson)!.ValueJson == "2.5", "raw input converted and persisted once");
    await Input("{\"other\":9}");
    value = await db.Queryable<ProcessBoardValueEntity>().FirstAsync();
    Check(JsonConvert.DeserializeObject<ProcessFieldRuntime>(value.RuntimeJson)!.ValueJson == "2.5", "delta frame does not erase previously reported temperature");
    await Input("{\"temp\":28}");
    Check(await db.Queryable<ProcessBoardEventEntity>().CountAsync() == 0, "below-threshold change does not create trace event");
    await Input("{\"temp\":31}");
    Check(await db.Queryable<ProcessBoardEventEntity>().CountAsync() == 2, "cumulative baseline threshold plus upper-limit transition creates events");
    await Input("{\"other\":10}");
    Check(await db.Queryable<ProcessBoardEventEntity>().CountAsync() == 2, "continuous upper-limit state does not repeatedly create alarm events");
    await Input("{\"temp\":null}");
    value = await db.Queryable<ProcessBoardValueEntity>().FirstAsync();
    Check(JsonConvert.DeserializeObject<ProcessFieldRuntime>(value.RuntimeJson)!.Error != null, "explicit null is not replaced with a fabricated zero");
    var history = JsonConvert.SerializeObject(await service.GetHistoryAsync(admin, card.Id, "temperature", DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1), 1, 50, false));
    Check(history.Contains("RuntimeJson"), "paginated original history is readable");
    var curve = JsonConvert.SerializeObject(await service.GetCurveAsync(admin, card.Id, "temperature", DateTime.Now.AddHours(-1), DateTime.Now.AddHours(1), 100, false));
    Check(curve.Contains("Average"), "SQL time-bucket curve aggregation works");
    var export = Newtonsoft.Json.Linq.JObject.FromObject(await service.CreateExportAsync(admin, new ProcessExportRequest
    {
        CardId = card.Id, FieldCode = "temperature", Start = DateTime.Now.AddHours(-1), End = DateTime.Now.AddHours(1)
    }));
    await service.RunExportAsync(CancellationToken.None);
    var file = await service.GetExportFileAsync(admin, (long)export["Id"]!);
    Check(new FileInfo(file).Length > 0, "background keyset export writes bounded batches");
    File.Delete(file); // only the exact random test file returned by this isolated service
    try
    {
        await service.GetExportAsync(restricted, (long)export["Id"]!);
        Check(false, "export ownership isolation");
    }
    catch (UnauthorizedAccessException) { Check(true, "export ownership isolation"); }
    var stale = JsonConvert.DeserializeObject<ProcessBoardDefinition>(JsonConvert.SerializeObject(card))!;
    card.Name = "更新名称";
    card = await service.SaveAsync(admin, card);
    try
    {
        await service.SaveAsync(admin, stale);
        Check(false, "optimistic config version conflict");
    }
    catch (InvalidOperationException) { Check(true, "optimistic config version conflict"); }
    Check(ProcessBoardService.LimitState("3", card.Fields[0]) == "Normal", "equal upper limit is normal");
    db.CodeFirst.InitTables<SysUserInfo, WeChatClawBotAccountEntity, EdgeDeviceCardPushTaskEntity>();
    await db.Insertable(new SysUserInfo
    {
        UserId = "recipient", UserName = "test-recipient", Password = "test-only", NickName = "测试",
        Email = "test@example.invalid", Roles = "R_USER", PermissionKeys = "", Buttons = "", Enabled = true,
        CreateTime = DateTime.Now, UpdateTime = DateTime.Now
    }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotAccountEntity
    {
        UserId = "recipient", UserName = "test-recipient", Status = "Bound", BotId = "test-bot",
        ILinkUserId = "test-chat", BotToken = "test-only-not-a-real-token"
    }).ExecuteCommandAsync();
    card.Fields[0].PushEnabled = true;
    card.Fields[0].RecipientUserIds = ["recipient"];
    card = await service.SaveAsync(admin, card);
    await Input("{\"temp\":40}");
    var queued = await db.Queryable<EdgeDeviceCardPushTaskEntity>().FirstAsync();
    Check(queued != null && queued.CardId == -card.Id && queued.RuleId == -card.Version && queued.LineCode == $"process:{line.Id}",
        "process event is persisted into shared global push queue with separate business identity");
    Check(await ProcessBoardPushBridge.IsCurrentAsync(db, queued!), "queued recipient and configuration version are valid before send");
    card.Fields[0].PushEnabled = false;
    card = await service.SaveAsync(admin, card);
    Check(!await ProcessBoardPushBridge.IsCurrentAsync(db, queued!), "queued message is cancelled after configuration changes");
    Console.WriteLine("Process board SQL smoke complete.");
}
finally
{
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^CodexProcessBoard_[a-f0-9]{32}$"))
        throw new Exception("Unsafe test database name");
    await Master($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
}

sealed class TestFormula : IProcessFormulaService
{
    public Task<ProcessFormulaResult> EvaluateAsync(ProcessFormulaRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(ProcessFormulaWorker.EvaluateExpression(request));
}

sealed class TestPush : IEdgeDeviceCardPushService
{
    public Task<EdgeDeviceCardPushQueueSettingDto> GetQueueSettingAsync() => Task.FromResult(new EdgeDeviceCardPushQueueSettingDto { IntervalSeconds = 5 });
    public Task SaveQueueSettingAsync(EdgeDeviceCardPushQueueSettingDto dto) => Task.CompletedTask;
    public Task<List<EdgeDeviceCardPushRecipientDto>> GetRecipientsAsync() => Task.FromResult(new List<EdgeDeviceCardPushRecipientDto>());
    public Task<EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>> GetTasksAsync(EdgeDeviceCardPushTaskQueryDto query) =>
        Task.FromResult(new EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>());
    public Task<int> QueueTestAsync(EdgeDeviceCardPushTestDto dto) => Task.FromResult(0);
    public Task RetryAsync(long taskId) => Task.CompletedTask;
    public Task DispatchAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
