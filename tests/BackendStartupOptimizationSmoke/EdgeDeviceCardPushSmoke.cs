using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AutoMapper;
using Interface;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Model.EdgeGateway;
using Model.User;
using Model.WeChatClawBot;
using Service;
using SqlSugar;

internal static class EdgeDeviceCardPushSmoke
{
    public static async Task RunAsync(SqlSugarScope scope)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var db = DatabaseOperationScope.GetConnection(scope, "calmadmin");
        WeChatClawBotDatabaseInitializer.Initialize(db);
        foreach (var id in new[] { "PUSH_A", "PUSH_B" })
        {
            await db.Insertable(new SysUserInfo
            {
                UserId = id, UserName = "测试微信用户" + id, Password = "isolated-probe",
                CreateTime = DateTime.Now, Enabled = true
            }).ExecuteCommandAsync();
            await db.Insertable(new WeChatClawBotAccountEntity
            {
                UserId = id, UserName = "测试微信用户" + id, Status = "Bound",
                BotToken = "synthetic-probe-token", ILinkUserId = id,
                BaseUrl = "https://ilinkai.weixin.qq.com", McpPushEnabled = false
            }).ExecuteCommandAsync();
            await db.Insertable(new WeChatClawBotConversationEntity
            {
                UserId = id, ContextToken = "synthetic-probe-context"
            }).ExecuteCommandAsync();
        }
        using var handler = new FakeWeChatHandler();
        var weChat = new WeChatClawBotService(scope, new FakeFactory(handler),
            Options.Create(new WeChatClawBotOptions()), new EphemeralDataProtectionProvider());
        var service = new EdgeDeviceCardPushService(scope, weChat);
        var mapper = new MapperConfiguration(config => config.AddProfile<上传MES.Config.AutoMapperConfigs>()).CreateMapper();
        var dashboard = new EdgeDashboardService(scope, mapper);
        var receiver = new EdgeRealtimeDataService(scope, mapper, new EnabledFeature(), new NoTelemetry(), dashboard,
            NullLogger<EdgeRealtimeDataService>.Instance);
        long clock = DateTimeOffset.Now.AddMinutes(-3).ToUnixTimeMilliseconds();
        Require((await service.GetQueueSettingAsync()).IntervalSeconds == 5, "new singleton defaults to 5 seconds");
        await MustRejectAsync(() => service.SaveQueueSettingAsync(new() { IntervalSeconds = 0 }), "zero interval rejected");
        await service.SaveQueueSettingAsync(new() { IntervalSeconds = 5 });
        Require((await service.GetQueueSettingAsync()).IntervalSeconds == 5, "queue settings save/read");
        await ReceiveAsync(25);
        var saved = await dashboard.SaveDeviceCardAsync(new EdgeDashboardDeviceCardSaveDto
        {
            LineCode = "PUSH_LINE", CardCode = "PUSH_CARD", CardName = "推送验证设备",
            TenantMark = "PUSH_TENANT", DeviceMark = "PUSH_DEVICE", ModelMark = "PUSH_MODEL",
            Fields = [new EdgeDashboardDeviceCardFieldSaveDto
            {
                FieldCode = "temperature", SourcePath = "/data/temp", DisplayName = "温度",
                ValueType = "Number", Unit = "℃", AlarmUpperLimit = 27, TraceEnabled = false,
                UseModelDisplayMapping = false,
                Push = new EdgeDeviceCardPushConfigDto
                {
                    Enabled = true, RecipientUserIds = ["PUSH_A", "PUSH_B"], OnChange = true,
                    OnLimit = true, OnRecovery = true, ChangeThreshold = 0.5m, ChangeIntervalSeconds = 3600
                }
            }]
        });
        Require(saved.Fields.Single().Push?.RecipientUserIds.Count == 2, "card save/read includes recipients");
        var ruleId = (await db.Queryable<EdgeDeviceCardPushRuleEntity>().FirstAsync(x => x.CardId == saved.Id)).Id;
        saved.Fields.Single().Push = null;
        saved = await dashboard.SaveDeviceCardAsync(saved);
        Require(saved.Fields.Single().Push?.Enabled == true, "older client omitting Push does not disable existing rule");
        Require((await db.Queryable<EdgeDeviceCardPushRuleEntity>().FirstAsync(x => x.CardId == saved.Id)).Id == ruleId,
            "field-table rebuild retains stable rule identity");
        var options = await service.GetRecipientsAsync();
        Require(options.Where(x => x.UserId.StartsWith("PUSH_")).All(x => x.Ready && x.Selectable),
            "bound enabled users ready; no dependency on external MCP permission");
        Require(!JsonSerializer.Serialize(options).Contains("synthetic-probe"), "recipient API excludes credentials");

        await ReceiveAsync(25);
        Require(await CountTasksAsync() == 0, "first report only establishes baseline");
        await ReceiveAsync(null, missing: true);
        Require(await CountTasksAsync() == 0, "omitted field does not push");
        await ReceiveAsync(25.3m);
        Require(await CountTasksAsync() == 0, "sub-threshold change does not push");
        await ReceiveAsync(25.5m);
        Require(await CountTasksAsync() == 2 && handler.CallCount == 0,
            "cumulative threshold queues one task per recipient without any network call on receive");
        await service.DispatchAsync(CancellationToken.None);
        Require(handler.CallCount == 0, "5-second collection window does not immediately send");
        await MakeWindowReadyAsync();
        await service.DispatchAsync(CancellationToken.None);
        Require(handler.CallCount == 1, "one recipient batch per interval, not a burst to all users");
        await service.DispatchAsync(CancellationToken.None);
        Require(handler.CallCount == 1, "persisted global pacing survives a new dispatch");
        await DispatchReadyAsync();
        Require((await TasksAsync()).All(x => x.Status == "Sent") && handler.CallCount == 2,
            "actual sender and per-recipient SQL delivery logs succeed");
        await DispatchReadyAsync();
        Require(handler.CallCount == 2, "sent tasks are not sent twice");
        await ReceiveAsync(28);
        Require(await CountTasksAsync() == 4, "upper limit bypasses ordinary change cooldown");
        var alert = await db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>()
            .FirstAsync(x => x.CardId == saved.Id && x.AlertType == "UpperExceeded");
        Require(alert != null && alert.PushStatus == "Internal", "system-owned alert does not enter external pending queue");
        var external = await dashboard.ClaimDeviceCardAlertsAsync("probe-mcp", 10, saved.LineCode, null, null);
        Require(external.Count == 0, "existing MCP cannot claim system-owned alert");
        await ReceiveAsync(29);
        await ReceiveAsync(null, missing: true);
        await ReceiveAsync(null);
        Require(await CountTasksAsync() == 4, "persistent limit, missing field and explicit null do not spam or recover");
        await ReceiveAsync(26);
        Require(await CountTasksAsync() == 6, "normal numeric value creates recovery notification for both recipients");
        await DispatchReadyAsync();
        Require((await TasksAsync()).All(x => x.Status == "Sent"), "all upper/recovery deliveries marked sent");
        var paired = (await TasksAsync()).Where(x => x.UserId == "PUSH_A" && x.EventType is "UpperExceeded" or "Recovered").ToList();
        Require(paired.Count == 2 && paired.Select(x => x.DeliveryId).Distinct().Count() == 1,
            "alarm and recovery retained in one actual delivery, not collapsed away");
        Require(handler.Contents.Any(x => x.Contains("变更汇总") && x.Contains("恢复正常") && x.Contains("超过上限")),
            "actual combined message includes all events");

        await ReceiveAsync(null, missing: true);
        await service.QueueTestAsync(new EdgeDeviceCardPushTestDto
        {
            CardId = saved.Id, FieldCode = "temperature", RecipientUserIds = ["PUSH_A"],
            MessageTemplate = "测试 {device} {field}={value}{unit} {time}"
        });
        var queued = (await TasksAsync()).Last();
        Require(queued.Content.Contains("=26℃") && queued.DataTime < DateTimeOffset.FromUnixTimeMilliseconds(clock).LocalDateTime,
            "manual test uses persisted actual value and actual field time, not last unrelated frame");
        var calls = handler.CallCount;
        var secondService = new EdgeDeviceCardPushService(scope, weChat);
        await MakeWindowReadyAsync();
        await Task.WhenAll(service.DispatchAsync(CancellationToken.None), secondService.DispatchAsync(CancellationToken.None));
        Require(handler.CallCount == calls + 1, "concurrent workers claim once");

        handler.Mode["PUSH_A"] = "Unknown";
        await QueueTestAsync("PUSH_A");
        await DispatchReadyAsync();
        var unknown = (await TasksAsync()).Last();
        Require(unknown.Status == "Unknown" && unknown.DeliveryId.HasValue, "transport ambiguity retained in real delivery and task audit");
        calls = handler.CallCount;
        await DispatchReadyAsync();
        Require(handler.CallCount == calls, "unknown result is not automatically resent");
        await MustRejectAsync(() => service.RetryAsync(unknown.Id), "unknown task manual retry guarded");

        handler.Mode["PUSH_B"] = "Failed";
        await QueueTestAsync("PUSH_B");
        await DispatchReadyAsync();
        var failing = (await TasksAsync()).Last();
        Require(failing.Status == "Retrying", "explicit HTTP rejection schedules bounded retry");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await db.Updateable<EdgeDeviceCardPushTaskEntity>()
                .SetColumns(x => x.NextAttemptTime == DateTime.Now).Where(x => x.Id == failing.Id).ExecuteCommandAsync();
            await DispatchReadyAsync();
        }
        failing = await db.Queryable<EdgeDeviceCardPushTaskEntity>().InSingleAsync(failing.Id);
        Require(failing.Status == "Failed" && failing.AttemptCount == 3, "max three automatic attempts then stop");
        handler.Mode["PUSH_B"] = "Sent";
        await service.RetryAsync(failing.Id);
        await DispatchReadyAsync();
        Require((await db.Queryable<EdgeDeviceCardPushTaskEntity>().InSingleAsync(failing.Id)).Status == "Sent",
            "explicit user retry works after failure fixed");

        await db.Updateable<WeChatClawBotConversationEntity>()
            .SetColumns(x => x.ContextToken == null).Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        Require(!(await service.GetRecipientsAsync()).Single(x => x.UserId == "PUSH_B").Ready,
            "binding alone does not report send readiness");
        await QueueTestAsync("PUSH_B");
        calls = handler.CallCount;
        await DispatchReadyAsync();
        var blocked = (await TasksAsync()).Last();
        Require(blocked.Status == "Blocked" && handler.CallCount == calls, "missing conversation blocks without outbound request");
        await db.Updateable<WeChatClawBotConversationEntity>()
            .SetColumns(x => x.ContextToken == "synthetic-probe-context").Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        await service.RetryAsync(blocked.Id);
        await DispatchReadyAsync();
        Require((await db.Queryable<EdgeDeviceCardPushTaskEntity>().InSingleAsync(blocked.Id)).Status == "Sent",
            "blocked task can be explicitly retried after conversation ready");

        await QueueTestAsync("PUSH_B");
        var interrupted = (await TasksAsync()).Last();
        await db.Updateable<EdgeDeviceCardPushTaskEntity>()
            .SetColumns(x => new EdgeDeviceCardPushTaskEntity
            {
                Status = "Processing", LeaseExpireTime = DateTime.Now.AddMinutes(-1)
            }).Where(x => x.Id == interrupted.Id).ExecuteCommandAsync();
        calls = handler.CallCount;
        await DispatchReadyAsync();
        Require((await db.Queryable<EdgeDeviceCardPushTaskEntity>().InSingleAsync(interrupted.Id)).Status == "Unknown" &&
                handler.CallCount == calls, "restart/expired lease does not duplicate uncertain message");

        var config = saved.Fields.Single().Push!;
        config.Channel = "ExternalMcp";
        saved = await dashboard.SaveDeviceCardAsync(saved);
        var prior = await CountTasksAsync();
        await ReceiveAsync(30);
        Require(await CountTasksAsync() == prior, "external channel does not enqueue system sends");
        Require(await db.Queryable<EdgeDashboardDeviceCardAlertRecordEntity>().AnyAsync(x =>
            x.CardId == saved.Id && x.PushStatus == "Pending"), "external channel preserves existing MCP alert workflow");

        // 数值 0、布尔 false 也是有效初值；这两个字段均不开启追溯。
        handler.Mode["PUSH_A"] = "Sent";
        await RawAsync(new Dictionary<string, object?> { ["flag"] = false, ["count"] = 0 });
        saved.Fields.Add(new EdgeDashboardDeviceCardFieldSaveDto
        {
            FieldCode = "flag", SourcePath = "/data/flag", DisplayName = "开关",
            ValueType = "Boolean", UseModelDisplayMapping = false,
            Push = new EdgeDeviceCardPushConfigDto
            {
                Enabled = true, OnChange = true, OnLimit = false, ChangeThreshold = 0.5m,
                ChangeIntervalSeconds = 0, RecipientUserIds = ["PUSH_A"]
            }
        });
        saved.Fields.Add(new EdgeDashboardDeviceCardFieldSaveDto
        {
            FieldCode = "count", SourcePath = "/data/count", DisplayName = "计数",
            ValueType = "Integer", UseModelDisplayMapping = false,
            Push = new EdgeDeviceCardPushConfigDto
            {
                Enabled = true, OnChange = true, OnLimit = false, ChangeIntervalSeconds = 0,
                RecipientUserIds = ["PUSH_B"]
            }
        });
        saved = await dashboard.SaveDeviceCardAsync(saved);
        await RawAsync(new Dictionary<string, object?> { ["flag"] = false, ["count"] = 0 });
        var beforeChange = await CountTasksAsync();
        await RawAsync(new Dictionary<string, object?> { ["flag"] = true, ["count"] = 1 });
        Require(await CountTasksAsync() == beforeChange + 2, "false/zero baselines support change notifications without trace enabled");
        await RawAsync(new Dictionary<string, object?> { ["flag"] = true, ["count"] = 1 });
        Require(await CountTasksAsync() == beforeChange + 2, "same actual values do not queue duplicates");
        saved.Fields.Single(x => x.FieldCode == "flag").Push!.Enabled = false;
        saved = await dashboard.SaveDeviceCardAsync(saved);
        Require(await db.Queryable<EdgeDeviceCardPushTaskEntity>().AnyAsync(x =>
            x.CardId == saved.Id && x.FieldCode == "flag" && x.Status == "Cancelled"),
            "disabling rule cancels pending tasks rather than sending stale configuration");

        var beforeRollback = await CountTasksAsync();
        var previous = await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .FirstAsync(x => x.TenantMark == "PUSH_TENANT" && x.DeviceCode == "PUSH_DEVICE");
        var rawBeforeRollback = previous.DataListJson;
        await EdgeRealtimeFieldLatestStore.WriteGate.WaitAsync();
        try
        {
            await db.Ado.BeginTranAsync();
            var next = new EdgeRealtimeDeviceSnapshotEntity
            {
                Id = previous.Id, TenantMark = previous.TenantMark, DeviceCode = previous.DeviceCode,
                ModelMark = previous.ModelMark, SourceTimestamp = clock + 1000,
                DataTime = DateTimeOffset.FromUnixTimeMilliseconds(clock + 1000).LocalDateTime,
                DataListJson = "[{\"Data\":{\"count\":2}}]", MessageId = "rollback-probe"
            };
            var merged = await EdgeRealtimeFieldLatestStore.ApplyAsync(db, next, previous);
            await db.Updateable(next).ExecuteCommandAsync();
            await EdgeDeviceCardPushService.CaptureAsync(db, new EdgeDashboardTelemetrySampleDto
            {
                TenantMark = "PUSH_TENANT", DeviceMark = "PUSH_DEVICE", ModelMark = next.ModelMark,
                MessageId = next.MessageId, SourceTimestamp = next.SourceTimestamp, DataTime = next.DataTime,
                ReceiveTime = DateTime.Now, Data = merged, ReportedData = new() { ["count"] = 2 }
            }, CancellationToken.None);
            Require(await CountTasksAsync() == beforeRollback + 1, "task created within source transaction");
            await db.Ado.RollbackTranAsync();
        }
        finally
        {
            EdgeRealtimeFieldLatestStore.WriteGate.Release();
        }
        Require(await CountTasksAsync() == beforeRollback &&
            (await db.Queryable<EdgeRealtimeDeviceSnapshotEntity>().InSingleAsync(previous.Id)).DataListJson == rawBeforeRollback,
            "raw snapshot/latest value and notification task roll back together");
        await db.Updateable<SysUserInfo>().SetColumns(x => x.Enabled == false)
            .Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        calls = handler.CallCount;
        await DispatchReadyAsync();
        Require(handler.CallCount == calls && await db.Queryable<EdgeDeviceCardPushTaskEntity>().AnyAsync(x =>
            x.CardId == saved.Id && x.FieldCode == "count" && x.Status == "Blocked"),
            "disabled local user is rechecked at send time without outbound request");
        await db.Updateable<SysUserInfo>().SetColumns(x => x.Enabled == true)
            .Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        await service.QueueTestAsync(new EdgeDeviceCardPushTestDto
        {
            CardId = saved.Id, FieldCode = "count", RecipientUserIds = ["PUSH_B"]
        });
        var priorBindingTask = (await TasksAsync()).Last();
        await db.Updateable<WeChatClawBotAccountEntity>().SetColumns(x => x.ILinkUserId == "new-wechat-identity")
            .Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        calls = handler.CallCount;
        await DispatchReadyAsync();
        Require((await db.Queryable<EdgeDeviceCardPushTaskEntity>().InSingleAsync(priorBindingTask.Id)).Status == "Cancelled" &&
            handler.CallCount == calls, "rebinding to another WeChat identity cancels old queued message");
        await db.Updateable<WeChatClawBotAccountEntity>().SetColumns(x => x.ILinkUserId == "PUSH_B")
            .Where(x => x.UserId == "PUSH_B").ExecuteCommandAsync();
        var countField = saved.Fields.Single(x => x.FieldCode == "count");
        countField.AlarmUpperLimit = 2;
        countField.Push!.ChangeThreshold = 0.5m;
        countField.Push.OnLimit = true;
        saved = await dashboard.SaveDeviceCardAsync(saved);
        var beforeStrings = await CountTasksAsync();
        await RawAsync(new() { ["count"] = "1" });
        await RawAsync(new() { ["count"] = "1.3" });
        Require(await CountTasksAsync() == beforeStrings, "declared numeric field supports numeric string threshold");
        await RawAsync(new() { ["count"] = "1.5" });
        Require(await CountTasksAsync() == beforeStrings + 1, "numeric string reaches cumulative threshold");
        await RawAsync(new() { ["count"] = "3" });
        Require(await CountTasksAsync() == beforeStrings + 2 && (await TasksAsync()).Last().EventType == "UpperExceeded",
            "numeric strings follow existing numeric-field limit semantics");
        await DispatchReadyAsync();
        var largeTemplate = new string('L', 1900);
        for (var i = 0; i < 6; i++)
            await service.QueueTestAsync(new() { CardId = saved.Id, FieldCode = "count",
                RecipientUserIds = ["PUSH_B"], MessageTemplate = largeTemplate });
        var largeTasks = (await TasksAsync()).TakeLast(6).Select(x => x.Id).ToArray();
        await MakeWindowReadyAsync();
        calls = handler.CallCount;
        await service.DispatchAsync(CancellationToken.None);
        Require(handler.CallCount == calls + 1 && await db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => largeTasks.Contains(x.Id) && x.Status == "Pending").CountAsync() > 0,
            "message length cap leaves overflow safely queued rather than discarding");
        Require(handler.Contents.All(x => x.Length <= 10000), "all actual message bodies within sender size limit");
        await DispatchReadyAsync();
        Require(await db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => largeTasks.Contains(x.Id) && x.Status == "Sent").CountAsync() == 6,
            "overflow sent in later paced batch");
        handler.Mode["PUSH_B"] = "Unknown";
        for (var i = 0; i < 2; i++)
            await service.QueueTestAsync(new() { CardId = saved.Id, FieldCode = "count", RecipientUserIds = ["PUSH_B"] });
        var uncertainIds = (await TasksAsync()).TakeLast(2).Select(x => x.Id).ToArray();
        calls = handler.CallCount;
        await DispatchReadyAsync();
        var uncertainBatch = await db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => uncertainIds.Contains(x.Id)).ToListAsync();
        Require(handler.CallCount == calls + 1 && uncertainBatch.All(x => x.Status == "Unknown") &&
            uncertainBatch.Select(x => x.DeliveryId).Distinct().Count() == 1,
            "ambiguous combined send marks all members unknown without repeating the batch");

        // 隔离旧表：模拟已存在记录、缺少新 NOT NULL 间隔列；验证数据库默认约束补列。
        var legacyDb = db.CopyNew();
        await legacyDb.Ado.ExecuteCommandAsync("""
            CREATE TABLE EdgePushQueueSettingLegacyProbe (
                Id int NOT NULL PRIMARY KEY,
                NextSendTime datetime2 NULL,
                LeaseOwner nvarchar(64) NULL,
                LeaseExpireTime datetime2 NULL,
                UpdateTime datetime NOT NULL
            );
            INSERT INTO EdgePushQueueSettingLegacyProbe (Id, UpdateTime) VALUES (1, GETDATE());
            """);
        EdgeDeviceCardPushQueueSchema.EnsureIntervalColumn(legacyDb, "EdgePushQueueSettingLegacyProbe");
        EdgeDeviceCardPushQueueSchema.EnsureIntervalColumn(legacyDb, "EdgePushQueueSettingLegacyProbe");
        var legacySetting = await legacyDb.Ado.SqlQuerySingleAsync<EdgeDeviceCardPushQueueSettingEntity>(
            "SELECT * FROM EdgePushQueueSettingLegacyProbe WHERE Id = 1");
        Require(legacySetting.IntervalSeconds == 5 && legacySetting.NextSendTime == null &&
            legacySetting.LeaseExpireTime == null && legacySetting.LeaseOwner == null,
            "legacy nonempty table adds default interval and preserves nullable not-yet-sent/lease values");
        prior = await CountTasksAsync();
        await service.SaveQueueSettingAsync(new() { IntervalSeconds = 7 });
        db.CodeFirst.InitTables(typeof(EdgeDeviceCardPushRuleEntity), typeof(EdgeDeviceCardPushRecipientEntity),
            typeof(EdgeDeviceCardPushStateEntity), typeof(EdgeDeviceCardPushTaskEntity), typeof(EdgeDeviceCardPushQueueSettingEntity));
        Require(await CountTasksAsync() == prior, "repeat initialization preserves nonempty push tables");
        Require((await service.GetQueueSettingAsync()).IntervalSeconds == 7,
            "repeat initialization retains configured interval instead of resetting to default");
        Console.WriteLine("PASS: device card push real SQL save/read, event queue, actual sender with fake HTTP, per-recipient results, threshold/cooldown/null, MCP ownership, concurrency, bounded retry and unknown-result protection.");

        async Task MakeWindowReadyAsync()
        {
            // 测试只推进隔离数据库中的时钟标记，生产不会绕过间隔。
            await db.Updateable<EdgeDeviceCardPushTaskEntity>()
                .SetColumns(x => x.CreateTime == DateTime.Now.AddMinutes(-2))
                .Where(x => x.Status == "Pending" || x.Status == "Retrying").ExecuteCommandAsync();
            await db.Updateable<EdgeDeviceCardPushQueueSettingEntity>()
                .SetColumns(x => x.NextSendTime == DateTime.Now.AddMinutes(-2)).Where(x => x.Id == 1).ExecuteCommandAsync();
        }

        async Task DispatchReadyAsync()
        {
            for (var round = 0; round < 5; round++)
            {
                await MakeWindowReadyAsync();
                await service.DispatchAsync(CancellationToken.None);
            }
        }

        async Task ReceiveAsync(decimal? temperature, bool missing = false)
        {
            clock += 1000;
            var data = missing ? new Dictionary<string, object?> { ["other"] = clock } :
                new Dictionary<string, object?> { ["temp"] = temperature };
            await receiver.ReceiveAsync(new EdgeRealtimeDataReceiveDto
            {
                TenantMark = "PUSH_TENANT", DeviceMark = "PUSH_DEVICE", ModelMark = "PUSH_MODEL",
                Time = clock, MessageId = "probe-" + clock, Data = data
            }, CancellationToken.None);
        }

        async Task RawAsync(Dictionary<string, object?> data)
        {
            clock += 1000;
            await receiver.ReceiveAsync(new EdgeRealtimeDataReceiveDto
            {
                TenantMark = "PUSH_TENANT", DeviceMark = "PUSH_DEVICE", ModelMark = "PUSH_MODEL",
                Time = clock, MessageId = "probe-" + clock, Data = data
            }, CancellationToken.None);
        }

        Task<int> CountTasksAsync() => db.Queryable<EdgeDeviceCardPushTaskEntity>().Where(x => x.CardId == saved.Id).CountAsync();
        Task<List<EdgeDeviceCardPushTaskEntity>> TasksAsync() => db.Queryable<EdgeDeviceCardPushTaskEntity>()
            .Where(x => x.CardId == saved.Id).OrderBy(x => x.Id).ToListAsync();
        Task<int> QueueTestAsync(string user) => service.QueueTestAsync(new EdgeDeviceCardPushTestDto
        {
            CardId = saved.Id, FieldCode = "temperature", RecipientUserIds = [user]
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("PUSH FAILED: " + message);
    }

    private static async Task MustRejectAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("PUSH FAILED: " + message);
    }

    private sealed class FakeFactory(FakeWeChatHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeWeChatHandler : HttpMessageHandler
    {
        public int CallCount;
        public List<string> Contents { get; } = [];
        public Dictionary<string, string> Mode { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri!.AbsolutePath == "/ilink/bot/sendmessage", "only message endpoint used; no real network");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var user = json.RootElement.GetProperty("msg").GetProperty("to_user_id").GetString()!;
            Contents.Add(json.RootElement.GetProperty("msg").GetProperty("item_list")[0]
                .GetProperty("text_item").GetProperty("text").GetString()!);
            Interlocked.Increment(ref CallCount);
            await Task.Delay(2, cancellationToken);
            var mode = Mode.GetValueOrDefault(user, "Sent");
            if (mode == "Unknown") throw new HttpRequestException("Synthetic connection lost after request");
            return new HttpResponseMessage(mode == "Failed" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(mode == "Failed" ? "synthetic explicit rejection" :
                    "{\"ret\":0,\"message_id\":\"synthetic-success\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NoTelemetry : IEdgeDashboardTelemetryProcessor
    {
        public Task ProcessAsync(EdgeDashboardTelemetrySampleDto sample, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SweepOfflineAsync(DateTime now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EnabledFeature : IEdgeGatewayFeatureService
    {
        public Task EnsureEnabledAsync() => Task.CompletedTask;
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);
        public Task<EdgeGatewayFeatureSettingOutputDto> GetSettingAsync() => throw new NotSupportedException();
        public Task<EdgeGatewayFeatureSettingOutputDto> SaveSettingAsync(EdgeGatewayFeatureSettingSaveDto dto) => throw new NotSupportedException();
    }
}
