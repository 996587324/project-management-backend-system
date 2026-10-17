using System.Reflection;
using System.Text.Json;
using AutoMapper;
using calmadmin.Config;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using Model.User;
using Newtonsoft.Json;
using Service;
using SqlSugar;
using 上传MES.Config;

if (args.Length != 1) throw new ArgumentException("请提供后端根目录；仅在随机隔离数据库执行验证");
using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(args[0]), "calmadmin", "appsettings.json")));
var configured = configuration.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var database = "CodexFieldCatalog_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString);
await master.OpenAsync();
async Task Master(string command)
{
    await using var sql = master.CreateCommand();
    sql.CommandText = command;
    await sql.ExecuteNonQueryAsync();
}
await Master($"CREATE DATABASE [{database}]");
SqlSugarScope? scope = null;
try
{
    scope = new SqlSugarScope(new ConnectionConfig
    {
        ConfigId = "calmadmin", DbType = SqlSugar.DbType.SqlServer, InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true,
        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = database }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    EdgeModelFieldCatalogSchema.Initialize(db);
    EdgeModelFieldCatalogSchema.Initialize(db);
    Check(!db.DbMaintenance.GetColumnInfosByTableName("EdgeDashboardModelFieldCatalog", false)
        .Single(x => x.DbColumnName == "IsSelectionDisabled").IsNullable,
        "fresh schema initializes repeatedly with a real NOT NULL selection flag");
    // 表尚无业务数据，且连接只指向本次随机创建的隔离库；重建旧版结构测试兼容升级。
    Check(new SqlConnectionStringBuilder(db.CurrentConnectionConfig.ConnectionString).InitialCatalog == database,
        "legacy migration fixture remains inside this run's isolated database");
    db.DbMaintenance.DropTable("EdgeDashboardModelFieldCatalog");
    db.CodeFirst.InitTables<LegacyCatalog>();
    await db.Insertable(new LegacyCatalog { ModelMark = "MODEL", SourcePath = "/data/temperature", FieldName = "temperature", LastValueJson = "25" }).ExecuteCommandAsync();
    EdgeModelFieldCatalogSchema.Initialize(db);
    EdgeModelFieldCatalogSchema.Initialize(db);
    Check((await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().FirstAsync()).IsSelectionDisabled == false,
        "populated legacy table upgrades twice with valid SQL default and original data");
    db.Ado.ExecuteCommand("""
        INSERT INTO EdgeDashboardModelFieldCatalog
        (ModelMark,SourcePath,FieldName,ValueType,SeenCount,IsDelete,FirstSeenTime,LastSeenTime,CreateTime,UpdateTime)
        VALUES ('MODEL','/data/unused','unused','Number',1,0,GETDATE(),GETDATE(),GETDATE(),GETDATE());
        """);
    Check(!(await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().Where(x => x.SourcePath == "/data/unused").FirstAsync()).IsSelectionDisabled,
        "real INSERT omitting new column uses database DEFAULT rather than CLR initializer");
    db.CodeFirst.InitTables(typeof(EdgeRealtimeDeviceSnapshotEntity), typeof(EdgeRealtimeDeviceFieldLatestEntity),
        typeof(EdgeDashboardModelFieldDisplayMappingEntity), typeof(EdgeDashboardModelAlarmRuleEntity),
        typeof(EdgeDashboardRobotBindingEntity), typeof(EdgeDashboardRobotFieldBindingEntity), typeof(EdgeDashboardRobotStateRuleEntity),
        typeof(EdgeDashboardDeviceBindingEntity), typeof(EdgeDashboardDeviceFieldBindingEntity),
        typeof(EdgeDashboardDeviceCardEntity), typeof(EdgeDashboardDeviceCardFieldEntity),
        typeof(EdgeDashboardLineOutputBindingEntity), typeof(ProcessBoardConfigEntity), typeof(SysUserOperationLogEntity));
    var snapshot = new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "T", DeviceCode = "D", ModelMark = "MODEL",
        DataListJson = """[{"data":{"temperature":25,"unused":4}}]"""
    };
    await db.Insertable(snapshot).ExecuteCommandAsync();
    await EdgeRealtimeFieldLatestStore.SeedAsync(db, snapshot);
    await db.Insertable(new EdgeDashboardModelFieldDisplayMappingEntity
    {
        ModelMark = "MODEL", SourcePath = "/data/temperature", DisplayName = "工艺温度"
    }).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardModelFieldDisplayMappingEntity
    {
        ModelMark = "MODEL", SourcePath = "/data/unused", DisplayName = "闲置字段"
    }).ExecuteCommandAsync();
    var mapper = new MapperConfiguration(config => config.AddProfile<AutoMapperConfigs>()).CreateMapper();
    var dashboard = new EdgeDashboardService(scope, mapper);
    var service = new EdgeModelFieldCatalogService(scope, dashboard);
    await db.Insertable(new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "T", DeviceCode = "LEGACY", ModelMark = "LEGACY_MODEL",
        DataListJson = """[{"data":{"oldField":1}}]"""
    }).ExecuteCommandAsync();
    Check((await service.GetModelsAsync()).Contains("LEGACY_MODEL") &&
        (await service.GetPageAsync(new() { ModelMark = "LEGACY_MODEL", Keyword = "oldField" })).Total == 1,
        "pre-upgrade snapshots without catalog rows are discoverable and hydrated for management");
    var admin = new ApplicationAccessContext { PlatformAdministrator = true, UserId = "catalog-test", UserName = "测试操作者", AppCode = "A" };
    var temperature = await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().Where(x => x.SourcePath == "/data/temperature").FirstAsync();
    var unused = await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().Where(x => x.SourcePath == "/data/unused").FirstAsync();
    Check((await service.GetModelsAsync()).Contains("MODEL"), "management includes discovered models without manual mapping");
    var search = await service.GetPageAsync(new() { ModelMark = "MODEL", Keyword = "工艺温度", Size = 1 });
    Check(search.Total == 1 && search.Records.Count == 1 && search.Records[0].Id == temperature.Id,
        "server pagination searches shared Chinese mapping without leaking sample values");
    var beforeSnapshot = (await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync()).DataListJson;
    var beforeValues = await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().CountAsync();
    var removed = await service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [unused.Id], Removed = true, Confirm = true }, admin);
    Check(removed.Applied && removed.ChangedCount == 1, "unused field removed and audit committed");
    Check((await service.GetPageAsync(new() { ModelMark = "MODEL", Removed = true })).Records.Single().Id == unused.Id,
        "removed filter reads persisted state");
    Check((await dashboard.GetSourceFieldsAsync("T", "D")).All(x => x.SourcePath != "/data/unused"),
        "final merged snapshot plus catalog excludes removed field");
    await dashboard.SaveModelFieldDisplayMappingsAsync(new()
    {
        ModelMark = "MODEL", Mappings =
        [
            new() { SourcePath = "/data/temperature", DisplayName = "工艺温度" },
            new() { SourcePath = "/data/unused", DisplayName = "闲置字段映射仍保留" }
        ]
    });
    Check((await dashboard.GetSourceFieldsAsync("T", "D")).All(x => x.SourcePath != "/data/unused"),
        "saving retained metadata for removed fields neither fails nor restores selection");
    var processor = new EdgeDashboardTelemetryProcessor(scope, NullLogger<EdgeDashboardTelemetryProcessor>.Instance);
    await processor.ProcessAsync(new EdgeDashboardTelemetrySampleDto
    {
        TenantMark = "T", DeviceMark = "D", ModelMark = "MODEL",
        Data = new() { ["temperature"] = 25, ["unused"] = 4 }, ReceiveTime = DateTime.Now
    }, CancellationToken.None);
    Check((await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().InSingleAsync(unused.Id)).IsSelectionDisabled,
        "new telemetry never resurrects manual removal");
    // 强制走旧快照回补方法，验证其 Updateable 也不会覆盖人工状态。
    var hydrate = typeof(EdgeDashboardService).GetMethod("HydrateModelFieldCatalogFromSnapshotsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await (Task)hydrate.Invoke(dashboard, ["MODEL"])!;
    Check((await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().InSingleAsync(unused.Id)).IsSelectionDisabled,
        "legacy snapshot hydration never resurrects manual removal");
    Check((await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().FirstAsync()).DataListJson == beforeSnapshot &&
        await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().CountAsync() == beforeValues,
        "raw snapshot and latest-value table unchanged by metadata removal");
    await ExpectRejected(() => service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [unused.Id], Confirm = false }, admin),
        "confirm flag required");
    await ExpectRejected(() => service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [unused.Id], Confirm = true }, new()),
        "write permission enforced inside business service");
    await ExpectRejected(() => service.ChangeAsync(new() { ModelMark = "OTHER", Ids = [unused.Id], Confirm = true }, admin),
        "cannot mutate IDs from another model");
    await ExpectRejected(() => EdgeModelFieldCatalogGuard.RequireDevicePathsAsync(db, "T", "D", ["/data/unused"]),
        "new binding cannot bypass candidate filtering");
    Check(EdgeModelFieldCatalogGuard.PathsOverlap("/data/item", "/data/item/value") &&
        !EdgeModelFieldCatalogGuard.PathsOverlap("/data/item", "/data/items"), "parent reference detection uses path segment boundaries");
    await db.Insertable(new ProcessBoardConfigEntity
    {
        Kind = "Card", Code = "CARD", Name = "工艺卡片",
        DefinitionJson = JsonConvert.SerializeObject(new ProcessBoardDefinition
        {
            Kind = "Card", Name = "工艺卡片", Fields = [new ProcessFieldDefinition
            {
                Code = "formula", Name = "计算值", FixedText = "constant", Formula = "v*2",
                Variables = new() { ["v"] = new ProcessSourceBinding { TenantMark = "T", DeviceMark = "D", SourcePath = "/data/temperature" } }
            }]
        })
    }).ExecuteCommandAsync();
    var robot = new EdgeDashboardRobotBindingEntity { TenantMark = "T", DeviceMark = "D", RobotName = "机械手", LineCode = "L" };
    robot.Id = await db.Insertable(robot).ExecuteReturnBigIdentityAsync();
    await db.Insertable(new EdgeDashboardRobotFieldBindingEntity { RobotBindingId = robot.Id, SourcePath = "/data/temperature" }).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardRobotStateRuleEntity { RobotBindingId = robot.Id, SourcePath = "/data/temperature", FaultReasonPath = "/data/temperature" }).ExecuteCommandAsync();
    var device = new EdgeDashboardDeviceBindingEntity { TenantMark = "T", SourceDeviceMark = "D", StatusSourcePath = "/data/temperature" };
    device.Id = await db.Insertable(device).ExecuteReturnBigIdentityAsync();
    await db.Insertable(new EdgeDashboardDeviceFieldBindingEntity { DeviceBindingId = device.Id, SourcePath = "/data/temperature" }).ExecuteCommandAsync();
    var card = new EdgeDashboardDeviceCardEntity { TenantMark = "T", DeviceMark = "ARCHIVED", ModelMark = "MODEL", StatusSourcePath = "/data/temperature" };
    card.Id = await db.Insertable(card).ExecuteReturnBigIdentityAsync();
    await db.Insertable(new EdgeDashboardDeviceCardFieldEntity { CardId = card.Id, SourcePath = "/data/temperature" }).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardLineOutputBindingEntity { TenantMark = "T", DeviceMark = "D", SourcePath = "/data/temperature" }).ExecuteCommandAsync();
    await db.Insertable(new EdgeDashboardModelAlarmRuleEntity { ModelMark = "MODEL", SourcePath = "/data/temperature", FaultReasonPath = "/data/temperature" }).ExecuteCommandAsync();
    var references = await service.GetReferencesAsync("MODEL", temperature.Id);
    foreach (var module in new[] { "机械手展示字段", "机械手状态规则", "设备状态字段", "设备展示字段", "设备卡片状态", "设备卡片展示/追溯/推送", "线体产量", "模型报警规则" })
        Check(references.Any(x => x.Module == module), "reference scan includes " + module);
    Check(references.Any(x => x.Module == "工艺公式变量"), "formula-variable references include disabled and enabled saved configurations");
    var blocked = await service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [temperature.Id, unused.Id], Removed = true, Confirm = true }, admin);
    Check(!blocked.Applied && blocked.ReferenceCount > 0 &&
        !(await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().InSingleAsync(temperature.Id)).IsSelectionDisabled,
        "one referenced field blocks the whole batch without partial writes");
    var restored = await service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [unused.Id], Removed = false, Confirm = true }, admin);
    Check(restored.Applied && (await dashboard.GetSourceFieldsAsync("T", "D")).Any(x => x.SourcePath == "/data/unused"),
        "restoring makes preserved current value selectable again");
    Check(await db.Queryable<SysUserOperationLogEntity>().CountAsync() == 2,
        "only successful state mutations create transactional operation audit");
    db.Ado.ExecuteCommand("EXEC sys.sp_rename N'SysUserOperationLog', N'SysUserOperationLogHold';");
    try
    {
        var failed = false;
        try
        {
            await service.ChangeAsync(new() { ModelMark = "MODEL", Ids = [unused.Id], Removed = true, Confirm = true }, admin);
        }
        catch (SqlException exception) when (exception.Number == 208)
        {
            failed = true;
        }
        Check(failed && !(await db.Queryable<EdgeDashboardModelFieldCatalogEntity>().InSingleAsync(unused.Id)).IsSelectionDisabled,
            "audit insert failure rolls back manual state change atomically");
    }
    finally
    {
        db.Ado.ExecuteCommand("EXEC sys.sp_rename N'SysUserOperationLogHold', N'SysUserOperationLog';");
    }
    var single = new EdgeDashboardModelFieldCatalogEntity { ModelMark = "ALL_REMOVED", SourcePath = "/data/only", FieldName = "only" };
    single.Id = await db.Insertable(single).ExecuteReturnBigIdentityAsync();
    await service.ChangeAsync(new() { ModelMark = "ALL_REMOVED", Ids = [single.Id], Removed = true, Confirm = true }, admin);
    Check((await dashboard.GetModelSourceFieldsAsync("ALL_REMOVED")).Count == 0,
        "a model with every field removed returns empty rather than hydrating or failing");
    await db.Insertable(new EdgeDashboardModelFieldCatalogEntity
    {
        ModelMark = "DUP", SourcePath = "/data/x", FieldName = "x", IsSelectionDisabled = true
    }).ExecuteCommandAsync();
    var duplicate = new EdgeDashboardModelFieldCatalogEntity { ModelMark = "DUP", SourcePath = "/data/x", FieldName = "x" };
    duplicate.Id = await db.Insertable(duplicate).ExecuteReturnBigIdentityAsync();
    Check((await service.GetPageAsync(new() { ModelMark = "DUP", Removed = false })).Total == 0 &&
        (await service.GetPageAsync(new() { ModelMark = "DUP", Removed = true })).Records.All(x => x.Removed) &&
        (await dashboard.GetModelSourceFieldsAsync("DUP")).Count == 0,
        "old duplicate path rows cannot bypass logical manual removal");
    await service.ChangeAsync(new() { ModelMark = "DUP", Ids = [duplicate.Id], Confirm = true, Removed = false }, admin);
    Check((await dashboard.GetModelSourceFieldsAsync("DUP")).Count == 1,
        "restoring one duplicate path restores every copy consistently");
    await using (var lease = await EdgeModelFieldCatalogGuard.EnterAsync(db))
    {
        using var rival = new SqlConnection(db.CurrentConnectionConfig.ConnectionString);
        await rival.OpenAsync();
        await using var command = rival.CreateCommand();
        command.CommandText = """
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=N'EdgeModelFieldCatalog:configuration',
                @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0;
            SELECT @r;
            """;
        Check(Convert.ToInt32(await command.ExecuteScalarAsync()) < 0, "cross-connection administrative writes cannot race reference checks");
    }
    Console.WriteLine("PASS: isolated catalog migration, actual inserts, search, references, removal, restore and reception checks complete");
}
finally
{
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!database.StartsWith("CodexFieldCatalog_") || !Guid.TryParseExact(database[18..], "N", out _))
        throw new InvalidOperationException("拒绝清理非本次创建的隔离数据库");
    await Master($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static async Task ExpectRejected(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
    {
        Console.WriteLine("PASS: " + message);
        return;
    }
    throw new InvalidOperationException("FAIL: " + message);
}

[SugarTable("EdgeDashboardModelFieldCatalog")]
internal sealed class LegacyCatalog
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64)] public string ModelMark { get; set; } = "MODEL";
    [SugarColumn(Length = 512)] public string SourcePath { get; set; } = string.Empty;
    [SugarColumn(Length = 128)] public string FieldName { get; set; } = string.Empty;
    [SugarColumn(Length = 32)] public string ValueType { get; set; } = "Number";
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)] public string? LastValueJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? SampleTenantMark { get; set; }
    [SugarColumn(Length = 128, IsNullable = true)] public string? SampleDeviceMark { get; set; }
    public long SeenCount { get; set; } = 1;
    public byte IsDelete { get; set; }
    public DateTime FirstSeenTime { get; set; } = DateTime.Now;
    public DateTime LastSeenTime { get; set; } = DateTime.Now;
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
