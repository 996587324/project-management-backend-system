using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using calmadmin.Config;
using Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Model.Agent;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using Model.User;
using Model.WeChatClawBot;
using Service;
using SqlSugar;
using 上传MES.Config;

if (args.Length != 1) throw new ArgumentException("请提供后端根目录；本测试仅使用随机隔离数据库及模拟模型/微信");
using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(args[0]), "calmadmin", "appsettings.json")));
var configured = settings.RootElement.GetProperty("ConnectionStrings").GetProperty("calmadmin").GetString()!;
var databaseName = "CodexProcessAgent_" + Guid.NewGuid().ToString("N");
using var master = new SqlConnection(new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString);
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
        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = databaseName }.ConnectionString
    });
    var db = scope.GetConnectionScope("calmadmin");
    await ProcessBoardSchema.InitializeAsync(db);
    db.CodeFirst.InitTables<EdgeRealtimeDeviceSnapshotEntity, EdgeDashboardModelFieldCatalogEntity, SysUserInfo,
        WeChatClawBotAccountEntity, EdgeDeviceCardPushTaskEntity>();
    await db.Insertable(new EdgeRealtimeDeviceSnapshotEntity
    {
        TenantMark = "tenant", DeviceCode = "device", ModelMark = "PROCESS_MODEL"
    }).ExecuteCommandAsync();
    await db.Insertable(new SysUserInfo { UserId = "recipient", UserName = "测试接收人", Password = "offline", Enabled = true, CreateTime = DateTime.Now }).ExecuteCommandAsync();
    await db.Insertable(new WeChatClawBotAccountEntity
    {
        UserId = "recipient", UserName = "测试接收人", Status = "Bound", BotToken = "offline-never-sent"
    }).ExecuteCommandAsync();
    var interval = 5;
    var push = Proxy.Make<IEdgeDeviceCardPushService>((method, values) => method.Name switch
    {
        "GetQueueSettingAsync" => Task.FromResult(new EdgeDeviceCardPushQueueSettingDto { IntervalSeconds = interval }),
        "SaveQueueSettingAsync" => SaveInterval((EdgeDeviceCardPushQueueSettingDto)values[0]!),
        "GetRecipientsAsync" => Task.FromResult(new List<EdgeDeviceCardPushRecipientDto>
        {
            new() { UserId = "recipient", UserName = "测试接收人", Ready = true, Selectable = true }
        }),
        "GetTasksAsync" => Task.FromResult(new EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>()),
        _ => throw new InvalidOperationException("测试禁止实际推送：" + method.Name)
    });
    Task SaveInterval(EdgeDeviceCardPushQueueSettingDto setting)
    {
        interval = setting.IntervalSeconds;
        return Task.CompletedTask;
    }
    var formula = Proxy.Make<IProcessFormulaService>((_, values) =>
    {
        var request = (ProcessFormulaRequest)values[0]!;
        return Task.FromResult(request.Formula == "x/10"
            ? new ProcessFormulaResult(true, request.Variables["x"] / 10, null)
            : new ProcessFormulaResult(false, null, "invalid expression"));
    });
    var board = new ProcessBoardService(scope, formula, push);
    var applications = Proxy.Make<ISystemApplicationService>((_, _) => throw new NotSupportedException("必须使用显式渠道授权"));
    var dashboard = Proxy.Make<IEdgeDashboardService>((method, _) => method.Name == "GetSourceFieldsAsync"
        ? Task.FromResult(new List<EdgeDashboardSourceFieldOptionDto>
        {
            new() { SourcePath = "/temperature", FieldName = "温度", CurrentValue = 250, DisplayValue = 25, HasModelDisplayMapping = true },
            new() { SourcePath = "/historical-pressure", FieldName = "历史压力", CurrentValue = 5 }
        })
        : throw new NotSupportedException(method.Name));
    var dictionary = Proxy.Make<ISharedModelDictionaryService>((method, _) => method.Name == "GetModelsAsync"
        ? Task.FromResult(new List<EdgeDashboardModelDisplayMappingOutputDto>
        {
            new() { ModelMark = "PROCESS_MODEL", DisplayName = "中文工艺设备", Enabled = true }
        })
        : throw new NotSupportedException(method.Name));
    var executor = new ProcessBoardAgentExecutor(scope, applications, new HttpContextAccessor(), board, formula, dashboard, dictionary, push);
    var mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapperConfigs>()).CreateMapper();
    var registry = new AgentToolRegistryService(scope, mapper,
        [new ProcessBoardAgentModuleProvider(), new QueryAssistantAgentModuleProvider()], []);
    var registered = await registry.GetEnabledToolsAsync(AgentCodes.ProcessBoard);
    Check(registered.Count == 22 && registered.Select(tool => tool.ToolName).Distinct().Count() == 22,
        "all 22 tools are discovered and persisted through the real registry");
    Check(registered.Where(tool => tool.IsWriteAction).All(tool => tool.RequiresConfirmation) &&
          registered.All(tool => !tool.ToolName.Contains("delete") && !tool.ToolName.Contains("remove")),
        "writes always require confirmation; no deletion or arbitrary SQL tools");
    var queries = await registry.GetEnabledToolsAsync(AgentCodes.QueryAssistant);
    Check(queries.Count == registered.Count(tool => !tool.IsWriteAction) && queries.All(tool => !tool.IsWriteAction),
        "global query assistant aggregates only read tools from the new module");

    var dispatcher = new AgentToolDispatcher(scope, mapper, registry, [executor], []);
    var admin = new ApplicationAccessContext
    {
        UserId = "agent-admin", AppCode = "A", AllResources = true, PlatformAdministrator = true,
        Permissions = new(StringComparer.OrdinalIgnoreCase) { "*" }
    };
    ApplicationAccessContext currentAccess = admin;
    using var channel = new AgentChannelExecutionScope(_ => Task.FromResult<string?>(null), default,
        resolveApplicationAccessAsync: () => Task.FromResult(currentAccess));
    var context = new AgentBusinessToolContext { UserId = admin.UserId, AgentCode = AgentCodes.ProcessBoard };
    async Task<JsonElement> Read(string action, object payload)
    {
        var result = await executor.ExecuteAsync(action, JsonSerializer.SerializeToElement(payload), context);
        Check(result.Success, "read/compute tool: " + action);
        return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
    async Task<AgentToolCallDto> Plan(string action, object payload)
    {
        return await dispatcher.DispatchAsync(new AgentToolDispatchRequest
        {
            UserId = admin.UserId, ConversationId = 111, AgentCode = AgentCodes.ProcessBoard,
            ToolName = "process." + action, ArgumentsJson = JsonSerializer.Serialize(payload)
        });
    }
    async Task<JsonElement> Write(string action, object payload)
    {
        var pending = await Plan(action, payload);
        Check(pending.Status == "PendingConfirm", "write is a pending plan: " + action);
        Check(!pending.Confirmed && pending.RequiresConfirmation, "pending write is not mistaken for an already confirmed operation");
        var completed = await dispatcher.ConfirmAsync(new AgentConfirmRequest { ToolCallId = pending.Id, UserId = admin.UserId });
        if (completed?.Status != "Success")
            throw new InvalidOperationException("Confirmed write failed: " + action + " " + completed?.ResponseJson);
        Check(true, "confirmed real write succeeds: " + action);
        Check(completed!.Confirmed, "real confirmer is recorded after write confirmation");
        using var document = JsonDocument.Parse(completed!.ResponseJson);
        return document.RootElement.GetProperty("data").Clone();
    }
    var createRegion = new { kind = "Region", parentId = 0, code = "RA", name = "Agent测试区域" };
    var regionPlan = await Plan("create", createRegion);
    Check(await db.Queryable<ProcessBoardConfigEntity>().CountAsync() == 0, "planning a region never writes business configuration");
    var regionResult = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = admin.UserId, ToolCallId = regionPlan.Id });
    Check(regionResult?.Status == "Success", "confirmed region creation succeeds");
    var region = (await board.GetDefinitionsAsync(admin)).Single();
    var lineResult = await Write("create", new { kind = "Line", parentId = region.Id, code = "LA", name = "Agent测试线体" });
    var lineId = lineResult.GetProperty("id").GetInt64();
    var cardResult = await Write("create", new { kind = "Card", parentId = lineId, code = "CA", name = "Agent参数卡片" });
    var cardId = cardResult.GetProperty("id").GetInt64();
    async Task<ProcessBoardDefinition> Definition(long id) => (await board.GetDefinitionsAsync(admin)).Single(item => item.Id == id);
    var outsideLine = await Plan("field-upsert", new { id = cardId, version = 1, patch = new {
        code = "outside-line", name = "尚未关联设备", source = new { tenantMark = "tenant", deviceMark = "device", sourcePath = "/temperature" } } });
    var outsideResult = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = admin.UserId, ToolCallId = outsideLine.Id });
    Check(outsideResult?.Status == "Failed" && (await Definition(cardId)).Fields.Count == 0,
        "confirmed Agent writes cannot bind a card to a device outside its line");
    await Write("devices-bind", new { id = lineId, version = 1, patch = new { devices = new[] { new { tenantMark = "tenant", deviceMark = "device" } } } });
    var fields = await Write("field-upsert", new
    {
        id = cardId, version = 1, patch = new
        {
            code = "temperature", name = "温度", source = new { tenantMark = "tenant", deviceMark = "device", sourcePath = "/temperature" },
            formula = "x/10", lowerLimit = 10, upperLimit = 80, traceEnabled = true, trendEnabled = true,
            pushEnabled = true, pushOnLimit = true, recipientUserIds = new[] { "recipient" }
        }
    });
    var cardVersion = fields.GetProperty("version").GetInt32();
    await Write("field-upsert", new { id = cardId, version = cardVersion, patch = new { code = "temperature", lowerLimit = (double?)null, changeThreshold = 0.5 } });
    var savedCard = await Definition(cardId);
    Check(savedCard.Fields.Count == 1 && savedCard.Fields[0].Formula == "x/10" && savedCard.Fields[0].Source.DeviceMark == "device" &&
          savedCard.Fields[0].LowerLimit == null && savedCard.Fields[0].UpperLimit == 80 && savedCard.Fields[0].PushEnabled,
        "partial field edits preserve source/formula/push and explicitly clear a nullable limit without duplicates");
    await Write("layout-update", new { id = cardId, version = savedCard.Version, patch = new { fieldColumns = 2, width = 600 } });
    await Write("flows-upsert", new
    {
        id = lineId, version = 2, patch = new { nodes = new[] { new { id = "n1", name = "上料", x = 10, y = 20, cardId } } }
    });
    await Write("flows-upsert", new
    {
        id = lineId, version = 3, patch = new
        {
            nodes = new[] { new { id = "n2", name = "加工", x = 200, y = 20 } }, edges = new[] { new { from = "n1", to = "n2" } }
        }
    });
    await Write("layout-update", new
    {
        id = lineId, version = 4, patch = new
        {
            layout = "free", panelLayout = "split", blocks = new[] { new { cardId, x = 20, y = 30, width = 600, height = 300 } }
        }
    });
    var line = await Definition(lineId);
    Check(line.Flow.Count == 2 && line.Flow[0].CardId == cardId && line.Edges.Count == 1 && line.Devices.Count == 1 && line.Blocks.Count == 1,
        "flow and layout changes preserve previous nodes, card links and device binding");
    var changedName = await Write("update", new { id = lineId, version = line.Version, patch = new { name = "新工艺线体名称" } });
    line = await Definition(lineId);
    Check(line.Name == "新工艺线体名称" && line.Flow.Count == 2 && line.Blocks.Count == 1, "basic edit does not overwrite unrelated configurations");
    var stale = await Plan("update", new { id = lineId, version = 1, patch = new { name = "不应覆盖" } });
    var staleResult = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = admin.UserId, ToolCallId = stale.Id });
    Check(staleResult?.Status == "Failed" && (await Definition(lineId)).Name == line.Name, "stale confirmed plan cannot overwrite a newer version");
    var invalid = await Plan("update", new { id = lineId, version = line.Version, patch = new { parentId = region.Id } });
    Check(invalid.Status == "Invalid", "unexposed parent/identity properties are rejected before planning");
    var unknownField = await Plan("field-upsert", new { id = cardId, version = (await Definition(cardId)).Version,
        patch = new { code = "fake", name = "伪造字段", source = new { tenantMark = "tenant", deviceMark = "device", sourcePath = "/invented" } } });
    var rejectedField = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = admin.UserId, ToolCallId = unknownField.Id });
    Check(rejectedField?.Status == "Failed" && (await Definition(cardId)).Fields.Count == 1, "invented source paths cannot be bound");
    var versionNow = (await Definition(cardId)).Version;
    await db.Insertable(new ProcessBoardValueEntity
    {
        CardId = cardId, FieldCode = "temperature", Version = versionNow,
        RuntimeJson = Newtonsoft.Json.JsonConvert.SerializeObject(new ProcessFieldRuntime
        {
            Code = "temperature", RawNumber = 250, ValueNumber = 25, ValueJson = "25", RawJson = "250", State = "Normal"
        }), UpdateTime = DateTime.Now
    }).ExecuteCommandAsync();
    var runtimeData = await Read("runtime", new { id = lineId });
    Check(runtimeData.GetProperty("records")[0].GetProperty("runtime").GetProperty("valueNumber").GetDouble() == 25,
        "runtime reads persisted actual/calculated values for current configuration version");
    await Read("overview", new { });
    await Read("definitions", new { kind = "Card", keyword = "Agent" });
    await Read("definition", new { id = lineId });
    Check((await Read("sources", new { keyword = "中文工艺" })).GetProperty("total").GetInt32() == 1, "source query searches shared Chinese model mapping");
    Check((await Read("source-fields", new { tenantMark = "tenant", deviceMark = "device", keyword = "历史" })).GetProperty("total").GetInt32() == 1,
        "historical source fields and shared field mapping are reused");
    var historyArgs = new { id = cardId, fieldCode = "temperature", start = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss"), end = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss") };
    await Read("history", historyArgs);
    await Read("curve", historyArgs);
    await Read("config-history", new { id = cardId });
    await Read("config-history", new { id = cardId, version = versionNow });
    await Read("push-tasks", new { id = lineId });
    await Read("recipients", new { });
    await Read("formula-preview", new { formula = "x/10", variables = new { x = 250 } });
    var setting = await Read("setting", new { });
    await Write("setting-update", new
    {
        expected = new { sampleIntervalSeconds = setting.GetProperty("sampleIntervalSeconds").GetInt32(), retentionHours = setting.GetProperty("retentionHours").GetInt32(), pushIntervalSeconds = 5 },
        patch = new { retentionHours = 48 }
    });
    var exported = await Write("export-create", historyArgs);
    await Read("export-status", new { id = exported.GetProperty("id").GetInt64() });
    // 真实Agent应用层的“提出修改→回复确认”链路，只有模型编排结果使用替身。
    var frameworkCalls = 0;
    async Task<AgentFrameworkRunResult> RunFramework(AgentFrameworkRunRequest request)
    {
        frameworkCalls++;
        var currentLine = await Definition(lineId);
        var planned = await dispatcher.DispatchAsync(new AgentToolDispatchRequest
        {
            UserId = request.UserId, ConversationId = request.ConversationId, AgentCode = AgentCodes.ProcessBoard,
            ToolName = "process.update", ArgumentsJson = JsonSerializer.Serialize(new
            {
                id = lineId, version = currentLine.Version, patch = new { name = "自然语言确认后的线体名称" }
            })
        });
        return new AgentFrameworkRunResult { Success = true, Reply = "计划修改线体名称，请回复确认。", ToolCalls = [planned] };
    }
    var framework = Proxy.Make<IAgentFrameworkRuntime>((method, values) => method.Name == "RunAsync"
        ? RunFramework((AgentFrameworkRunRequest)values[0]!) : throw new NotSupportedException(method.Name));
    var mail = Proxy.Make<IEmailService>((method, _) => throw new NotSupportedException(method.Name));
    var agentService = new AgentService(scope, mapper, registry, dispatcher, framework, mail);
    var plannedReply = await agentService.SendMessageAsync(new AgentSendRequest
    {
        UserId = admin.UserId, AgentCode = AgentCodes.ProcessBoard, Mode = "Work", Message = "修改工艺线体名称"
    });
    Check(plannedReply.RequiresConfirmation && (await Definition(lineId)).Name != "自然语言确认后的线体名称",
        "real Agent response reports confirmation required without writing configuration");
    await agentService.SendMessageAsync(new AgentSendRequest
    {
        UserId = admin.UserId, AgentCode = AgentCodes.ProcessBoard, Mode = "Work", Message = "确认", ConversationId = plannedReply.ConversationId
    });
    Check((await Definition(lineId)).Name == "自然语言确认后的线体名称" && frameworkCalls == 1,
        "replying confirmation executes the saved ProcessBoard plan without another model/tool-selection call");
    line = await Definition(lineId);
    var revoked = await Plan("update", new { id = lineId, version = line.Version, patch = new { enabled = false } });
    currentAccess = new ApplicationAccessContext
    {
        UserId = admin.UserId, AppCode = "B", Permissions = new() { "ProcessBoard" },
        LineCodes = new() { "process:" + lineId }, SourceKeys = new() { "tenant\u001fdevice" }
    };
    await Read("definition", new { id = lineId });
    var revokeResult = await dispatcher.ConfirmAsync(new AgentConfirmRequest { UserId = admin.UserId, ToolCallId = revoked.Id });
    Check(revokeResult?.Status == "Failed" && (await Definition(lineId)).Enabled, "confirmation rechecks live system/config permission after revocation");
    await Reject(() => executor.ExecuteAsync("definition", JsonSerializer.SerializeToElement(new { id = region.Id + 100000 }), context),
        "unknown or unauthorized configuration ID is not disclosed");
    await Reject(() => executor.ExecuteAsync("source-fields", JsonSerializer.SerializeToElement(new { tenantMark = "tenant", deviceMark = "other" }), context),
        "restricted B user cannot read another data source");
    await Reject(() => executor.ExecuteAsync("export-status", JsonSerializer.SerializeToElement(new { id = exported.GetProperty("id").GetInt64() }), context),
        "A export cannot be read from B even by the same user");
    currentAccess = new ApplicationAccessContext { UserId = admin.UserId, AppCode = "B", Permissions = new() { "ProcessBoard" } };
    await Reject(() => executor.ExecuteAsync("runtime", JsonSerializer.SerializeToElement(new { id = lineId }), context),
        "line-level data authorization is enforced");
    Check(await db.Queryable<ProcessBoardConfigAuditEntity>().CountAsync() > 5 && await db.Queryable<AgentInvocationLogEntity>().CountAsync() > 10,
        "business version audit and Agent execution audit are both persisted");
    Console.WriteLine("PASS: ProcessBoard Agent complete; no real LLM call, no WeChat message, no production database mutation.");
}
finally
{
    scope?.Dispose();
    SqlConnection.ClearAllPools();
    if (!databaseName.StartsWith("CodexProcessAgent_") || !Guid.TryParseExact(databaseName[18..], "N", out _))
        throw new InvalidOperationException("拒绝清理非本次随机创建的测试数据库");
    await Master($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.WriteLine("PASS: " + message);
        return;
    }
    throw new InvalidOperationException("FAIL: " + message);
}

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []);
    public static T Make<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var instance = Create<T, Proxy>();
        ((Proxy)(object)instance).Handler = handler;
        return instance;
    }
}
