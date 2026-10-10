using System.Security.Cryptography;
using System.Text;
using Model.EdgeGateway;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SqlSugar;

namespace Service;

/// <summary>
/// 字段当前值存储。写入者必须持有 WriteGate，并与原始快照在同一数据库事务中提交。
/// 对象递归合并，数组整组替换，明确的 null/类型切换清除旧子字段。
/// </summary>
public static class EdgeRealtimeFieldLatestStore
{
    public static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static async Task<Dictionary<string, object?>> ApplyAsync(
        ISqlSugarClient db,
        EdgeRealtimeDeviceSnapshotEntity incoming,
        EdgeRealtimeDeviceSnapshotEntity? previous)
    {
        var rows = await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>()
            .Where(x => x.TenantMark == incoming.TenantMark && x.DeviceCode == incoming.DeviceCode)
            .ToListAsync();
        var modelChanged = previous != null &&
            !string.Equals(incoming.ModelMark, previous.ModelMark, StringComparison.OrdinalIgnoreCase);
        var original = rows.ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
        var state = modelChanged
            ? new Dictionary<string, EdgeRealtimeDeviceFieldLatestEntity>(StringComparer.Ordinal)
            : new Dictionary<string, EdgeRealtimeDeviceFieldLatestEntity>(original, StringComparer.Ordinal);

        // 上线后的第一条增量报文先补齐该设备自己的旧快照，绝不使用同模型其他设备样例。
        if (state.Count == 0 && previous != null && !modelChanged)
            Merge(ReadData(previous), "/data", previous, state);
        Merge(ReadData(incoming), "/data", incoming, state);

        var deletedIds = original.Values.Where(x => !state.TryGetValue(x.SourcePath, out var value) ||
            value.Id != x.Id).Select(x => x.Id).ToArray();
        foreach (var batch in deletedIds.Chunk(500))
            await db.Deleteable<EdgeRealtimeDeviceFieldLatestEntity>().In(batch).ExecuteCommandAsync();

        var inserted = state.Values.Where(x => x.Id == 0).ToArray();
        foreach (var batch in inserted.Chunk(200))
            await db.Insertable(batch).ExecuteCommandAsync();

        var updated = state.Values.Where(x => x.Id != 0 &&
            !ReferenceEquals(x, original.GetValueOrDefault(x.SourcePath))).ToArray();
        foreach (var batch in updated.Chunk(200))
            await db.Updateable(batch).ExecuteCommandAsync();
        return Rebuild(state.Values).ToObject<Dictionary<string, object?>>() ?? [];
    }

    /// <summary>批量重建展示用快照。仅改动内存对象，数据库原始报文不受影响。</summary>
    public static async Task HydrateAsync(ISqlSugarClient db,
        IReadOnlyCollection<EdgeRealtimeDeviceSnapshotEntity> snapshots)
    {
        foreach (var batch in snapshots.Chunk(200))
        {
            var tenants = batch.Select(x => x.TenantMark).Distinct().ToArray();
            var devices = batch.Select(x => x.DeviceCode).Distinct().ToArray();
            var rows = await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>()
                .Where(x => tenants.Contains(x.TenantMark) && devices.Contains(x.DeviceCode))
                .ToListAsync();
            var map = rows.GroupBy(x => DeviceKey(x.TenantMark, x.DeviceCode))
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in batch)
            {
                if (!map.TryGetValue(DeviceKey(snapshot.TenantMark, snapshot.DeviceCode), out var fields))
                    continue; // 未补齐的旧设备暂时使用自己的现有原始快照。
                var data = Rebuild(fields).ToObject<Dictionary<string, object?>>() ?? [];
                snapshot.DataListJson = JsonConvert.SerializeObject(new[]
                {
                    new EdgeRealtimeDataItemDto { Data = data }
                });
            }
        }
    }

    /// <summary>补齐任务与接收共用锁；存在任意最新值时不再回灌旧报文。</summary>
    public static async Task SeedAsync(ISqlSugarClient db, EdgeRealtimeDeviceSnapshotEntity snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TenantMark) || string.IsNullOrWhiteSpace(snapshot.DeviceCode))
            return; // 历史非法标识不能写成空租户，避免补齐任务反复触发唯一键冲突。
        if (await db.Queryable<EdgeRealtimeDeviceFieldLatestEntity>().AnyAsync(x =>
                x.TenantMark == snapshot.TenantMark && x.DeviceCode == snapshot.DeviceCode))
            return;
        await ApplyAsync(db, snapshot, null);
    }

    private static string DeviceKey(string? tenant, string device) =>
        JsonConvert.SerializeObject(new[] { tenant ?? string.Empty, device });

    private static JObject ReadData(EdgeRealtimeDeviceSnapshotEntity snapshot) =>
        (JObject)EdgeDashboardJsonFieldResolver.BuildEnvelope(snapshot)["data"]!;

    private static void Merge(JToken token, string path, EdgeRealtimeDeviceSnapshotEntity source,
        Dictionary<string, EdgeRealtimeDeviceFieldLatestEntity> state)
    {
        if (path.Length > 512)
            throw new InvalidOperationException("设备字段 JSON Pointer 路径不能超过 512 个字符");
        state.TryGetValue(path, out var old);
        if (old != null && (source.SourceTimestamp.HasValue && old.SourceTimestamp.HasValue
                ? source.SourceTimestamp.Value < old.SourceTimestamp.Value
                : source.DataTime < old.DataTime)) return;

        var isObject = token is JObject;
        var sameObject = isObject && old?.ValueType == nameof(JTokenType.Object);
        if (!sameObject)
        {
            var prefix = path + "/";
            var children = state.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (var child in children) state.Remove(child);
        }
        var valueJson = isObject ? "{}" : token.ToString(Formatting.None);
        // 相同字段报文可更新时间；相同消息 ID、时间、值则不重复写入。
        if (old == null || old.ValueJson != valueJson || old.DataTime != source.DataTime ||
            old.MessageId != source.MessageId)
        {
            state[path] = new EdgeRealtimeDeviceFieldLatestEntity
            {
                Id = old?.Id ?? 0,
                TenantMark = source.TenantMark ?? string.Empty,
                DeviceCode = source.DeviceCode,
                ModelMark = source.ModelMark,
                SourcePath = path,
                SourcePathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))),
                ValueJson = valueJson,
                ValueType = token.Type.ToString(),
                MessageId = source.MessageId,
                SourceTimestamp = source.SourceTimestamp,
                DataTime = source.DataTime,
                ReceiveTime = source.ReceiveTime,
                CreateTime = old?.CreateTime ?? source.ReceiveTime,
                UpdateTime = source.ReceiveTime
            };
        }
        if (token is not JObject obj) return;
        foreach (var property in obj.Properties())
        {
            var segment = property.Name.Replace("~", "~0").Replace("/", "~1");
            Merge(property.Value, $"{path}/{segment}", source, state);
        }
    }

    private static JObject Rebuild(IEnumerable<EdgeRealtimeDeviceFieldLatestEntity> rows)
    {
        var root = new JObject();
        foreach (var row in rows.OrderBy(x => x.SourcePath.Count(c => c == '/')))
        {
            if (row.SourcePath == "/data") continue;
            var segments = row.SourcePath.Split('/').Skip(2)
                .Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
            var parent = root;
            foreach (var segment in segments.SkipLast(1))
            {
                if (parent[segment] is not JObject child)
                {
                    child = new JObject();
                    parent[segment] = child;
                }
                parent = child;
            }
            using var reader = new JsonTextReader(new StringReader(row.ValueJson))
            {
                DateParseHandling = DateParseHandling.None
            };
            parent[segments[^1]] = JToken.ReadFrom(reader);
        }
        return root;
    }
}
