using Interface;
using Model.EdgeGateway;
using Model.SystemApplications;
using Model.ProcessBoard;
using SqlSugar;

namespace Service;

/// <summary>共享模型→设备目录。授权、搜索、计数均在分页前执行；不读取实时 JSON 或接收日志。</summary>
public sealed class SourceCatalogService(SqlSugarScope scope, ISharedModelDictionaryService dictionary) : ISourceCatalogService
{
    public const string Unclassified = "__UNCLASSIFIED__";

    public async Task<SourceCatalogPage<SourceCatalogModel>> ModelsAsync(ApplicationAccessContext access, SourceCatalogQuery input)
    {
        Validate(access, input);
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var names = await ModelNamesAsync();
        var query = await FilterAsync(db, access, input, names);
        var groups = query.GroupBy(x => x.ModelMark)
            .Select(x => new SourceCatalogCount
            {
                ModelMark = x.ModelMark, DeviceCount = SqlFunc.AggregateCount(x.DeviceKey),
                LatestReceiveTime = SqlFunc.AggregateMax(x.ReceiveTime)
            });
        var total = await groups.Clone().CountAsync();
        var rows = await groups.OrderBy(x => x.ModelMark).ToPageListAsync(input.Page, input.Size);
        return new(rows.Select(x => new SourceCatalogModel(x.ModelMark, Name(names, x.ModelMark), x.DeviceCount, x.LatestReceiveTime)).ToList(), total, input.Page, input.Size);
    }

    public async Task<SourceCatalogPage<SourceCatalogDevice>> DevicesAsync(ApplicationAccessContext access, SourceCatalogQuery input)
    {
        Validate(access, input);
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var names = await ModelNamesAsync();
        var query = await FilterAsync(db, access, input, names);
        var total = await query.Clone().CountAsync();
        var rows = await query.OrderBy(x => x.ModelMark).OrderBy(x => x.SourceKey)
            .OrderBy(x => x.DeviceKey).OrderBy(x => x.SourceDeviceCode)
            .ToPageListAsync(input.Page, input.Size);
        if (input.Kind == "Realtime") await ApplyDeviceNamesAsync(db, access, rows);
        foreach (var row in rows) row.ModelName = Name(names, row.ModelMark);
        return new(rows, total, input.Page, input.Size);
    }

    public async Task<SourceCatalogDevice?> ResolveAsync(ApplicationAccessContext access, SourceCatalogQuery input)
    {
        Validate(access, input);
        if (string.IsNullOrWhiteSpace(input.DeviceKey) && !(input.Kind == "Edge" && input.GatewayId > 0))
            throw new ArgumentException("回显需要完整设备标识");
        using var db = scope.GetConnection("calmadmin").CopyNew();
        var source = input.SourceKey ?? string.Empty;
        var device = input.DeviceKey;
        var businessDevice = input.SourceDeviceCode ?? string.Empty;
        var query = await ScopedQueryAsync(db, access, input);
        var row = await query
            .Where(x => SqlFunc.MappingColumn<string>("[SourceKey] COLLATE Latin1_General_100_BIN2") == source &&
                        SqlFunc.MappingColumn<string>("[SourceDeviceCode] COLLATE Latin1_General_100_BIN2") == businessDevice)
            .WhereIF(!string.IsNullOrWhiteSpace(device), x =>
                SqlFunc.MappingColumn<string>("[DeviceKey] COLLATE Latin1_General_100_BIN2") == device)
            .FirstAsync();
        if (row != null)
        {
            row.ModelName = Name(await ModelNamesAsync(), row.ModelMark);
            if (input.Kind == "Realtime") await ApplyDeviceNamesAsync(db, access, [row]);
        }
        return row;
    }

    private async Task<Dictionary<string, string>> ModelNamesAsync() =>
        (await dictionary.GetModelsAsync()).Where(x => x.Enabled)
        .GroupBy(x => x.ModelMark, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.First().DisplayName, StringComparer.OrdinalIgnoreCase);

    private static string Name(Dictionary<string, string> names, string model) =>
        model == Unclassified ? "未分类模型" : names.GetValueOrDefault(model, model);

    private static async Task<ISugarQueryable<SourceCatalogDevice>> FilterAsync(
        ISqlSugarClient db, ApplicationAccessContext access, SourceCatalogQuery input, Dictionary<string, string> names)
    {
        var query = await ScopedQueryAsync(db, access, input);
        if (!string.IsNullOrWhiteSpace(input.ModelMark))
        {
            var model = input.ModelMark.Trim();
            query = query.Where(x => x.ModelMark == model);
        }
        var keyword = input.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var matched = names.Where(x => x.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key).ToList();
            if ("未分类模型".Contains(keyword, StringComparison.OrdinalIgnoreCase)) matched.Add(Unclassified);
            var models = matched.ToArray();
            var aliasKeys = input.Kind == "Realtime"
                ? (await AliasesAsync(db, access, keyword)).Select(x => x.SourceKey + "\u001f" + x.DeviceKey).Distinct(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            query = query.Where(x => x.DeviceName.Contains(keyword) || x.DeviceKey.Contains(keyword) ||
                x.SourceDeviceCode.Contains(keyword) || x.SourceKey.Contains(keyword) ||
                x.ModelMark.Contains(keyword) || models.Contains(x.ModelMark) || aliasKeys.Contains(
                    SqlFunc.MappingColumn<string>("([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")));
        }
        return query;
    }

    private static async Task<ISugarQueryable<SourceCatalogDevice>> ScopedQueryAsync(
        ISqlSugarClient db, ApplicationAccessContext access, SourceCatalogQuery input)
    {
        var query = Query(db, access, input);
        if (!input.ProcessLineId.HasValue) return query;
        if (!ProcessBoardService.CanReadLine(access, input.ProcessLineId.Value))
            throw new UnauthorizedAccessException("没有该工艺线体的数据源选择权限");
        var row = await db.Queryable<ProcessBoardConfigEntity>().InSingleAsync(input.ProcessLineId.Value);
        if (row?.Kind != "Line") throw new ArgumentException("所属工艺线体不存在");
        var keys = ProcessBoardDeviceScope.Devices(ProcessBoardService.Read(row))
            .Select(x => x.TenantMark + "\u001f" + x.DeviceMark).Distinct(StringComparer.Ordinal).ToArray();
        // 先取授权与线体设备交集，再搜索、分组和分页；大小写不同的资源不能混用。
        return query.Where(x => keys.Contains(SqlFunc.MappingColumn<string>(
            "([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")));
    }

    private static async Task ApplyDeviceNamesAsync(ISqlSugarClient db, ApplicationAccessContext access, List<SourceCatalogDevice> rows)
    {
        if (rows.Count == 0) return;
        var keys = rows.Select(x => x.SourceKey + "\u001f" + x.DeviceKey).ToArray();
        var aliases = await AliasesAsync(db, access, keys: keys);
        var names = aliases.GroupBy(x => x.SourceKey + "\u001f" + x.DeviceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().DeviceName, StringComparer.Ordinal);
        foreach (var row in rows)
            row.DeviceName = names.GetValueOrDefault(row.SourceKey + "\u001f" + row.DeviceKey, row.DeviceKey);
    }

    /// <summary>复用已有设备、机械手、卡片名称作为可搜索别名；不跨未授权线体泄露配置名称。</summary>
    private static async Task<List<SourceCatalogAlias>> AliasesAsync(
        ISqlSugarClient db, ApplicationAccessContext access, string? keyword = null, string[]? keys = null)
    {
        var device = db.Queryable<EdgeDashboardDeviceBindingEntity>().Where(x => x.IsDelete == 0)
            .Select(x => new SourceCatalogAlias { SourceKey = x.TenantMark, DeviceKey = x.SourceDeviceMark, DeviceName = x.DeviceName, LineCode = x.LineCode }).MergeTable();
        var robot = db.Queryable<EdgeDashboardRobotBindingEntity>().Where(x => x.IsDelete == 0)
            .Select(x => new SourceCatalogAlias { SourceKey = x.TenantMark, DeviceKey = x.DeviceMark, DeviceName = x.RobotName, LineCode = x.LineCode }).MergeTable();
        var card = db.Queryable<EdgeDashboardDeviceCardEntity>().Where(x => x.IsDelete == 0)
            .Select(x => new SourceCatalogAlias { SourceKey = x.TenantMark, DeviceKey = x.DeviceMark, DeviceName = x.CardName, LineCode = x.LineCode }).MergeTable();
        var sourceKeys = access.SourceKeys.ToArray();
        var lines = access.LineCodes.ToArray();
        var pageKeys = keys ?? [];
        var result = new List<SourceCatalogAlias>();
        foreach (var query in new[] { device, robot, card })
        {
            query.Where(x => x.DeviceName != "")
                .WhereIF(!access.AllResources, x => sourceKeys.Contains(
                    SqlFunc.MappingColumn<string>("([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")) && lines.Contains(x.LineCode))
                .WhereIF(keyword != null, x => x.DeviceName.Contains(keyword!))
                .WhereIF(keys != null, x => pageKeys.Contains(
                    SqlFunc.MappingColumn<string>("([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")))
                .OrderBy(x => x.DeviceName);
            result.AddRange(await query.ToListAsync());
        }
        return result;
    }

    private static ISugarQueryable<SourceCatalogDevice> Query(
        ISqlSugarClient db, ApplicationAccessContext access, SourceCatalogQuery input)
    {
        var keys = access.SourceKeys.ToArray();
        if (input.Kind == "Realtime")
        {
            var realtime = db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
                .Select(x => new SourceCatalogDevice
                {
                    Kind = "Realtime", SourceKey = SqlFunc.IsNull(x.TenantMark, ""), DeviceKey = x.DeviceCode,
                    SourceDeviceCode = "", DeviceName = x.DeviceCode,
                    ModelMark = SqlFunc.IsNullOrEmpty(x.ModelMark) ? Unclassified : x.ModelMark!,
                    ReceiveTime = x.ReceiveTime, GatewayId = 0, ConnectionId = 0, GatewayName = "", ConnectionName = "", ModelName = ""
                }).MergeTable();
            return access.AllResources ? realtime : realtime.Where(x => keys.Contains(
                SqlFunc.MappingColumn<string>("([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")));
        }

        var gateways = access.DeviceCodes.ToArray();
        var gatewayId = input.GatewayId ?? 0;
        var connectionKey = input.ConnectionKey ?? string.Empty;
        var edge = db.Queryable<EdgeWebSocketSubscriptionDeviceEntity>()
            .InnerJoin<EdgeWebSocketSourceEntity>((device, source) => device.WebSocketSourceId == source.Id)
            .InnerJoin<EdgeDeviceEntity>((device, source, gateway) => source.DeviceId == gateway.Id)
            .Where((device, source, gateway) => device.IsDelete == 0 && source.IsDelete == 0 && gateway.IsDelete == 0)
            .WhereIF(gatewayId > 0, (device, source, gateway) => gateway.Id == gatewayId)
            .WhereIF(connectionKey != "", (device, source, gateway) => source.SourceId == connectionKey)
            .Select((device, source, gateway) => new SourceCatalogDevice
            {
                Kind = "Edge", SourceKey = source.SourceId, DeviceKey = gateway.DeviceCode,
                SourceDeviceCode = device.DeviceCode,
                DeviceName = SqlFunc.IsNullOrEmpty(device.DeviceName) ? device.DeviceCode : device.DeviceName!,
                // 订阅配置没有模型外键，禁止用连接名或跨租户同名设备猜测模型归属。
                ModelMark = Unclassified, GatewayId = gateway.Id, ConnectionId = source.Id,
                // 订阅更新时间不是报文接收时间，不能冒充实时统计。
                ReceiveTime = SqlFunc.MappingColumn<DateTime?>("CAST(NULL AS datetime2)"),
                GatewayName = gateway.DeviceName, ConnectionName = source.SourceName, ModelName = ""
            }).MergeTable();
        return access.AllResources ? edge : edge.Where(x => keys.Contains(
                SqlFunc.MappingColumn<string>("([SourceKey] + NCHAR(31) + [DeviceKey]) COLLATE Latin1_General_100_BIN2")) &&
            gateways.Contains(SqlFunc.MappingColumn<string>("[DeviceKey] COLLATE Latin1_General_100_BIN2")));
    }

    public static void Validate(ApplicationAccessContext access, SourceCatalogQuery input)
    {
        string[] permissions = input.Purpose switch
        {
            "dashboard" => ["ProductionDashboardConfig", "ProductionDashboardLayoutEditor", "ProductionDashboardBoard"],
            "device-card" => ["ProductionDashboardDeviceCardConfig"],
            "process" => ["ProcessBoard", "ProcessBoardConfig"],
            "hourly" => ["HourlyProductionConfig"],
            "trace" => ["EdgeDataTrace", "EdgeDataTraceDetail"],
            "b-mapping" => ["EdgeGatewayConsole"],
            "shared" => ["source.read", "SystemBWorkbench"],
            _ => throw new ArgumentException("未知数据源绑定业务")
        };
        if (access.AppCode is not ("A" or "B") || !permissions.Any(access.HasPermission))
            throw new UnauthorizedAccessException("没有当前业务的数据源读取权限");
        if (input.ProcessLineId.HasValue && (input.ProcessLineId <= 0 || input.Purpose != "process" || input.Kind != "Realtime"))
            throw new ArgumentException("工艺线体范围仅用于工艺看板实时设备选择");
        if (input.Kind is not ("Realtime" or "Edge") ||
            input.Kind == "Edge" && input.Purpose is not ("hourly" or "trace" or "b-mapping"))
            throw new ArgumentException("当前业务不支持该数据源类型");
        if (input.Keyword?.Length > 128 || input.ModelMark?.Length > 128 || input.SourceKey?.Length > 128 ||
            input.DeviceKey?.Length > 128 || input.SourceDeviceCode?.Length > 128 || input.ConnectionKey?.Length > 128)
            throw new ArgumentException("查询参数过长");
        if (input.GatewayId.HasValue && input.GatewayId.Value <= 0) throw new ArgumentException("网关编号无效");
        input.Page = Math.Clamp(input.Page, 1, 100000);
        input.Size = Math.Clamp(input.Size, 1, 100);
    }

    private sealed class SourceCatalogCount
    {
        public string ModelMark { get; set; } = string.Empty;
        public int DeviceCount { get; set; }
        public DateTime? LatestReceiveTime { get; set; }
    }

    private sealed class SourceCatalogAlias
    {
        public string SourceKey { get; set; } = string.Empty;
        public string DeviceKey { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string LineCode { get; set; } = string.Empty;
    }
}
