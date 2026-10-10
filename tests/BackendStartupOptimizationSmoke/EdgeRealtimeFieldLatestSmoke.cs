using AutoMapper;
using Interface;
using Microsoft.Extensions.Logging.Abstractions;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Service;
using SqlSugar;

internal static class EdgeRealtimeFieldLatestSmoke
{
    public static async Task RunAsync(SqlSugarScope scope)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var db = DatabaseOperationScope.GetConnection(scope, "calmadmin");
        db.CodeFirst.InitTables(typeof(EdgeRealtimeDeviceSnapshotEntity),
            typeof(EdgeRealtimeDeviceFieldLatestEntity), typeof(EdgeDashboardDeviceCardEntity),
            typeof(EdgeDashboardDeviceCardFieldEntity), typeof(EdgeDashboardDeviceCardBoardSettingEntity),
            typeof(EdgeDashboardDeviceCardTraceStateEntity), typeof(EdgeDashboardDeviceCardChangeRecordEntity),
            typeof(EdgeDashboardDeviceCardAlertStateEntity), typeof(EdgeDashboardDeviceCardAlertRecordEntity),
            typeof(EdgeDashboardModelFieldCatalogEntity), typeof(EdgeDashboardModelFieldDisplayMappingEntity),
            typeof(EdgeDeviceCardPushRuleEntity), typeof(EdgeDeviceCardPushRecipientEntity),
            typeof(EdgeDeviceCardPushStateEntity), typeof(EdgeDeviceCardPushTaskEntity),
            typeof(EdgeDeviceCardPushQueueSettingEntity));
        var mapper = new MapperConfiguration(config => config.AddProfile<上传MES.Config.AutoMapperConfigs>())
            .CreateMapper();
        var dashboard = new EdgeDashboardService(scope, mapper);
        var telemetry = new TelemetryProbe();
        var logger = new CaptureLogger();
        var receiver = new EdgeRealtimeDataService(scope, mapper, new EnabledFeature(), telemetry, dashboard, logger);
        var clock = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var card = new EdgeDashboardDeviceCardEntity
        {
            LineCode = "LATEST_PROBE", CardCode = "C1", CardName = "字段保留测试",
            TenantMark = "LATEST_T1", DeviceMark = "D1", ModelMark = "M1",
            StatusSourcePath = "/data/status", StatusMappingJson = "{\"1\":\"ONLINE\",\"2\":\"FAULT\"}"
        };
        card.Id = await db.Insertable(card).ExecuteReturnBigIdentityAsync();
        await db.Insertable(new EdgeDashboardDeviceCardFieldEntity
        {
            CardId = card.Id, FieldCode = "temp", DisplayName = "温度", SourcePath = "/data/temp",
            ValueType = "Number", TraceEnabled = true, TraceThreshold = 0.5m,
            AlarmUpperLimit = 27m, UseModelDisplayMapping = false
        }).ExecuteCommandAsync();

        await SendAsync("{\"temp\":25,\"status\":2,\"zero\":0,\"flag\":false,\"text\":\"\",\"nested\":{\"a\":1,\"b\":2},\"array\":[1,2],\"a/b~c\":3,\"Case\":4,\"case\":5,\"date\":\"2026-10-09T00:00:00Z\"}");
        var firstFieldTimestamp = clock;
        var firstTraceTime = (await db.Queryable<EdgeDashboardDeviceCardTraceStateEntity>().FirstAsync()).LastDataTime;
        await SendAsync("{\"zero\":1,\"nested\":{\"a\":6}}", model: null);
        var runtime = await dashboard.GetDeviceCardBoardRuntimeAsync(card.LineCode);
        Require(runtime.Cards.Single().Status == "FAULT", "missing status retains fault");
        Require(Convert.ToDecimal(runtime.Cards.Single().Fields.Single().RawValue) == 25,
            "real card reads persisted temperature after unrelated delta");
        Require((long)telemetry.Last!.Data["temp"]! == 25, "statistics receives merged sample");
        Require(telemetry.Last.ModelMark == "M1", "omitted model metadata retained");
        var temperatureRow = await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>()
            .FirstAsync(x => x.TenantMark == "LATEST_T1" && x.DeviceCode == "D1" && x.SourcePath == "/data/temp");
        Require(temperatureRow.SourceTimestamp == firstFieldTimestamp,
            "missing field does not advance its actual last-report timestamp");
        Require((await db.Queryable<EdgeDashboardDeviceCardTraceStateEntity>().FirstAsync()).LastDataTime == firstTraceTime,
            "missing field does not cause extra trace checkpoint writes");
        await db.Insertable(new EdgeDashboardModelFieldCatalogEntity
        {
            ModelMark = "M1", SourcePath = "/data/otherDeviceOnly", FieldName = "其他设备样例",
            ValueType = "Integer", LastValueJson = "999", SampleTenantMark = "OTHER", SampleDeviceMark = "OTHER"
        }).ExecuteCommandAsync();
        var options = await dashboard.GetSourceFieldsAsync("LATEST_T1", "D1");
        Require(options.Single(x => x.SourcePath == "/data/otherDeviceOnly").CurrentValue == null &&
            Convert.ToDecimal(options.Single(x => x.SourcePath == "/data/temp").CurrentValue) == 25,
            "historical field selection retained without other-device value contamination");
        var merged = await LoadAsync("LATEST_T1", "D1");
        Require(merged["flag"]!.Value<bool>() == false && merged["text"]!.Value<string>() == "",
            "false and empty strings are actual values");
        Require(merged["nested"]!["b"]!.Value<int>() == 2 && merged["Case"]!.Value<int>() == 4 &&
            merged["case"]!.Value<int>() == 5 && merged["a/b~c"]!.Value<int>() == 3,
            "nested delta, case-sensitive paths and escaped pointer names");
        Require(merged["date"]!.Type == JTokenType.String, "date-looking strings retain type");
        Require(await db.Queryable<EdgeDashboardDeviceCardChangeRecordEntity>().CountAsync() == 0,
            "omitted field does not create false trace");
        var raw = await receiver.GetLatestAsync("LATEST_T1", "D1");
        Require(raw != null && !raw.DataList.First().Data.ContainsKey("temp"), "raw message remains raw delta");

        await SendAsync("{\"temp\":28}");
        Require(logger.Errors.Count == 0, "trace pipeline errors: " + string.Join(";", logger.Errors));
        Require(await db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>().CountAsync() == 1,
            "upper limit creates actual SQL alert insert");
        await SendAsync("{\"zero\":2}");
        Require(await db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>().CountAsync() == 1,
            "missing alarm field does not recover or duplicate alarm");
        var offlineTime = DateTime.Now.AddHours(-1);
        await db.Updateable<EdgeRealtimeDeviceSnapshotEntity>()
            .SetColumns(x => x.ReceiveTime == offlineTime)
            .Where(x => x.TenantMark == "LATEST_T1" && x.DeviceCode == "D1").ExecuteCommandAsync();
        using (var restartedScope = new SqlSugarScope(new ConnectionConfig
        {
            ConfigId = "calmadmin", ConnectionString = db.CurrentConnectionConfig.ConnectionString,
            DbType = SqlSugar.DbType.SqlServer, IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute
        }))
        using (DatabaseOperationScope.Begin(restartedScope))
        {
            var restartedDashboard = new EdgeDashboardService(restartedScope, mapper);
            var offlineCard = (await restartedDashboard.GetDeviceCardBoardRuntimeAsync(card.LineCode)).Cards.Single();
            Require(!offlineCard.DataOnline && offlineCard.Status == "OFFLINE" &&
                Convert.ToDecimal(offlineCard.Fields.Single().RawValue) == 28,
                "fresh service/connection retains field value while device is offline");
        }
        await SendAsync("{\"temp\":null,\"nested\":null,\"array\":[9]}");
        merged = await LoadAsync("LATEST_T1", "D1");
        Require(merged["temp"]!.Type == JTokenType.Null && merged["nested"]!.Type == JTokenType.Null,
            "explicit null clears persisted value and descendants");
        Require(((JArray)merged["array"]!).Count == 1, "arrays replace rather than accumulate");
        Require(await db.Queryable<EdgeDashboardDeviceCardChangeRecordEntity>().AnyAsync(x =>
            x.FieldCode == "temp" && x.NewValueJson == "null"), "explicit null is traced");
        await SendAsync("{\"nested\":{\"c\":7}}");
        merged = await LoadAsync("LATEST_T1", "D1");
        Require(merged["nested"]!["b"] == null, "cleared child never resurrects");
        var lastTime = clock;
        var ignored = await SendAsync("{\"temp\":99}", at: lastTime - 10000);
        Require(ignored.IgnoredOlderCount == 1, "older frame ignored");
        var count = await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().CountAsync();
        await SendAsync("{\"nested\":{\"c\":7}}", at: lastTime, id: "same");
        await SendAsync("{\"nested\":{\"c\":7}}", at: lastTime, id: "same");
        Require(await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().CountAsync() == count,
            "repeated message does not accumulate field rows");
        await SendAsync("{\"temp\":88}", tenant: "LATEST_T2");
        await SendAsync("{\"temp\":77}", device: "D2");
        merged = await LoadAsync("LATEST_T1", "D1");
        Require(merged["temp"]!.Type == JTokenType.Null, "tenant/device isolation");
        await SendAsync("{\"onlyNewModel\":8}", model: "M2");
        merged = await LoadAsync("LATEST_T1", "D1");
        Require(merged["temp"] == null && merged["status"] == null, "model replacement clears old model fields");

        var legacy = new EdgeRealtimeDeviceSnapshotEntity
        {
            TenantMark = "LEGACY", DeviceCode = "D3", ModelMark = "M1",
            DataListJson = "[{\"data\":{\"oldField\":42,\"temp\":23}}]"
        };
        legacy.Id = await db.Insertable(legacy).ExecuteReturnBigIdentityAsync();
        await SendAsync("{\"newField\":43}", tenant: "LEGACY", device: "D3",
            at: DateTimeOffset.Now.AddSeconds(1).ToUnixTimeMilliseconds());
        Require((await LoadAsync("LEGACY", "D3"))["oldField"]!.Value<int>() == 42,
            "first delta seeds own legacy snapshot");
        await EdgeRealtimeFieldLatestStore.SeedAsync(db, legacy);
        Require((await LoadAsync("LEGACY", "D3"))["newField"]!.Value<int>() == 43,
            "late backfill cannot overwrite live state");

        await db.Ado.BeginTranAsync();
        var rollback = new EdgeRealtimeDeviceSnapshotEntity
        {
            TenantMark = "ROLLBACK", DeviceCode = "D4", DataListJson = "[{\"data\":{\"v\":1}}]"
        };
        await EdgeRealtimeFieldLatestStore.ApplyAsync(db, rollback, null);
        await db.Ado.RollbackTranAsync();
        Require(!await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().AnyAsync(x => x.TenantMark == "ROLLBACK"),
            "latest values participate in caller transaction rollback");
        db.CodeFirst.InitTables<EdgeRealtimeDeviceFieldLatestEntity>();
        // 模拟旧版本非空告警表：保留已有记录和筛选索引，修复后允许真实 NULL 插入。
        await db.Ado.ExecuteCommandAsync("""
            UPDATE dbo.EdgeDashboardDeviceCardAlertRecord SET ClaimExpireTime=GETDATE(), PushedTime=GETDATE();
            DROP INDEX IX_EdgeDashboardDeviceCardAlertRecord_Push ON dbo.EdgeDashboardDeviceCardAlertRecord;
            ALTER TABLE dbo.EdgeDashboardDeviceCardAlertRecord ALTER COLUMN ClaimExpireTime datetime NOT NULL;
            ALTER TABLE dbo.EdgeDashboardDeviceCardAlertRecord ALTER COLUMN PushedTime datetime NOT NULL;
            CREATE INDEX IX_EdgeDashboardDeviceCardAlertRecord_Push
                ON dbo.EdgeDashboardDeviceCardAlertRecord(PushStatus, ClaimExpireTime, DataTime);
            """);
        EdgeDashboardDeviceCardSchema.EnsureNullablePushTimes(db);
        EdgeDashboardDeviceCardSchema.EnsureNullablePushTimes(db);
        db.CodeFirst.InitTables<EdgeDashboardDeviceCardAlertRecordEntity>();
        await db.Insertable(new EdgeDashboardDeviceCardAlertRecordEntity
        {
            CardId = card.Id, LineCode = card.LineCode, DeviceMark = card.DeviceMark,
            FieldCode = "migration", DisplayName = "迁移校验", AlertType = "UpperExceeded",
            DataTime = DateTime.Now, ReceiveTime = DateTime.Now
        }).ExecuteCommandAsync();
        Require(await db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>().AnyAsync(x =>
            x.FieldCode == "migration" && x.PushedTime == null && x.ClaimExpireTime == null),
            "old nonempty alert table migration supports nullable SQL timestamps");
        Require((await LoadAsync("LEGACY", "D3"))["oldField"]!.Value<int>() == 42,
            "repeat initialization preserves nonempty table");
        Require(logger.Errors.Count == 0, "actual trace/alert pipeline has no hidden errors: " +
            string.Join(";", logger.Errors));
        Console.WriteLine("PASS: persisted device fields, real receiver/card/status/trace/alerts, null/type/array semantics, isolation, rollback and repeated initialization.");

        async Task<EdgeRealtimeReceiveResultDto> SendAsync(string json, string tenant = "LATEST_T1",
            string device = "D1", string? model = "M1", long? at = null, string? id = null)
        {
            clock = at ?? clock + 1000;
            return await receiver.ReceiveAsync(new EdgeRealtimeDataReceiveDto
            {
                TenantMark = tenant, DeviceMark = device, ModelMark = model,
                Time = clock, MessageId = id ?? Guid.NewGuid().ToString("N"),
                Data = JObject.Parse(json).ToObject<Dictionary<string, object?>>()!
            }, CancellationToken.None);
        }

        async Task<JObject> LoadAsync(string tenant, string device)
        {
            using var readOperation = DatabaseOperationScope.Begin(scope);
            var readDb = DatabaseOperationScope.GetConnection(scope, "calmadmin");
            var snapshot = await readDb.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
                .FirstAsync(x => x.TenantMark == tenant && x.DeviceCode == device);
            await EdgeRealtimeFieldLatestStore.HydrateAsync(readDb, [snapshot]);
            using var reader = new JsonTextReader(new StringReader(snapshot.DataListJson))
            {
                DateParseHandling = DateParseHandling.None
            };
            var block = (JObject)JArray.Load(reader)[0];
            return (JObject)block.GetValue("data", StringComparison.OrdinalIgnoreCase)!;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("LATEST FIELD FAILED: " + message);
    }

    private sealed class EnabledFeature : IEdgeGatewayFeatureService
    {
        public Task EnsureEnabledAsync() => Task.CompletedTask;
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);
        public Task<EdgeGatewayFeatureSettingOutputDto> GetSettingAsync() => throw new NotSupportedException();
        public Task<EdgeGatewayFeatureSettingOutputDto> SaveSettingAsync(EdgeGatewayFeatureSettingSaveDto dto) =>
            throw new NotSupportedException();
    }

    private sealed class TelemetryProbe : IEdgeDashboardTelemetryProcessor
    {
        public EdgeDashboardTelemetrySampleDto? Last { get; private set; }
        public Task ProcessAsync(EdgeDashboardTelemetrySampleDto sample, CancellationToken cancellationToken)
        {
            Last = sample;
            return Task.CompletedTask;
        }
        public Task SweepOfflineAsync(DateTime now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<EdgeRealtimeDataService>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception != null) Errors.Add(exception.ToString());
        }
    }
}
