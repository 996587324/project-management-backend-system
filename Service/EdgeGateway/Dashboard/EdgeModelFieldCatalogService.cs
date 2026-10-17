using Interface;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using Model.User;
using Newtonsoft.Json;
using SqlSugar;

namespace Service;

public sealed class EdgeModelFieldCatalogService(SqlSugarScope scope, IEdgeDashboardService dashboard) : IEdgeModelFieldCatalogService
{
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(scope, "calmadmin");

    public async Task<List<string>> GetModelsAsync()
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var discovered = await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.IsDelete == 0).OrderBy(x => x.ModelMark).Select(x => x.ModelMark).Distinct().ToListAsync();
        var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Select(x => x.ModelMark).Distinct().ToListAsync();
        return discovered.Concat(snapshots.Select(EdgeModelFieldCatalogGuard.NormalizeModel))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<EdgeModelFieldCatalogPage> GetPageAsync(EdgeModelFieldCatalogQuery input)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var model = RequireModel(input.ModelMark);
        if (!await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>().AnyAsync(x => x.ModelMark == model))
        {
            // 升级前只有快照、还未再次上报的模型也应可以管理；复用已有补齐逻辑。
            var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
                .Select(x => x.ModelMark).Distinct().ToListAsync();
            if (snapshots.Any(x => EdgeModelFieldCatalogGuard.NormalizeModel(x).Equals(model, StringComparison.OrdinalIgnoreCase)))
                await dashboard.GetModelSourceFieldsAsync(model);
        }
        var keyword = input.Keyword?.Trim() ?? string.Empty;
        if (keyword.Length > 512) throw new ArgumentException("搜索内容最多 512 字");
        if (input.LastSeenStart > input.LastSeenEnd) throw new ArgumentException("时间范围无效");
        var query = Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.ModelMark == model && x.IsDelete == 0);
        if (input.Removed.HasValue)
        {
            var removed = input.Removed.Value;
            if (removed)
            {
                query = query.Where(x => SqlFunc.Subqueryable<EdgeDashboardModelFieldCatalogEntity>()
                    .Where(other => other.ModelMark == model && other.SourcePath == x.SourcePath && other.IsSelectionDisabled).Any());
            }
            else
            {
                query = query.Where(x => !SqlFunc.Subqueryable<EdgeDashboardModelFieldCatalogEntity>()
                    .Where(other => other.ModelMark == model && other.SourcePath == x.SourcePath && other.IsSelectionDisabled).Any());
            }
        }
        if (input.LastSeenStart.HasValue)
        {
            var start = input.LastSeenStart.Value;
            query = query.Where(x => x.LastSeenTime >= start);
        }
        if (input.LastSeenEnd.HasValue)
        {
            var end = input.LastSeenEnd.Value;
            query = query.Where(x => x.LastSeenTime <= end);
        }
        if (keyword.Length > 0)
        {
            query = query.Where(x => x.SourcePath.Contains(keyword) || x.FieldName.Contains(keyword) ||
                SqlFunc.Subqueryable<EdgeDashboardModelFieldDisplayMappingEntity>().Where(m =>
                    m.ModelMark == model && m.SourcePath == x.SourcePath && m.IsDelete == 0 &&
                    m.DashboardCode == "production-dashboard" && m.DisplayName.Contains(keyword)).Any());
        }
        var current = Math.Max(1, input.Current);
        var size = Math.Clamp(input.Size, 1, 100);
        RefAsync<int> total = 0;
        var rows = await query.OrderByDescending(x => x.LastSeenTime).OrderBy(x => x.Id)
            .Select(x => new EdgeDashboardModelFieldCatalogEntity
            {
                Id = x.Id, ModelMark = x.ModelMark, SourcePath = x.SourcePath, FieldName = x.FieldName,
                ValueType = x.ValueType, FirstSeenTime = x.FirstSeenTime, LastSeenTime = x.LastSeenTime,
                SeenCount = x.SeenCount, IsSelectionDisabled = x.IsSelectionDisabled
            })
            .ToPageListAsync(current, size, total);
        var paths = rows.Select(x => x.SourcePath).ToArray();
        var removedPaths = await EdgeModelFieldCatalogGuard.GetRemovedPathsAsync(Db, model);
        var references = await FindReferencesAsync(model, paths);
        var mappings = await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
            .Where(x => x.ModelMark == model && x.IsDelete == 0 && x.DashboardCode == "production-dashboard")
            .Select(x => new { x.SourcePath, x.DisplayName, x.UpdateTime }).ToListAsync();
        var names = mappings.GroupBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(v => v.UpdateTime).First().DisplayName,
                StringComparer.OrdinalIgnoreCase);
        return new EdgeModelFieldCatalogPage
        {
            Current = current, Size = size, Total = total.Value,
            Records = rows.Select(x => new EdgeModelFieldCatalogRow
            {
                Id = x.Id, ModelMark = x.ModelMark, SourcePath = x.SourcePath, FieldName = x.FieldName,
                DisplayName = names.GetValueOrDefault(x.SourcePath) ?? x.FieldName, ValueType = x.ValueType,
                Removed = removedPaths.Contains(x.SourcePath), FirstSeenTime = x.FirstSeenTime,
                LastSeenTime = x.LastSeenTime, SeenCount = x.SeenCount,
                ReferenceCount = references.Count(r => r.SourcePath.Equals(x.SourcePath, StringComparison.OrdinalIgnoreCase))
            }).ToList()
        };
    }

    public async Task<List<EdgeModelFieldReference>> GetReferencesAsync(string modelMark, long id)
    {
        using var operation = DatabaseOperationScope.Begin(scope);
        var model = RequireModel(modelMark);
        var row = await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.Id == id && x.ModelMark == model && x.IsDelete == 0)
            .Select(x => new { x.SourcePath }).FirstAsync()
            ?? throw new ArgumentException("历史字段不存在，请刷新");
        return await FindReferencesAsync(model, [row.SourcePath]);
    }

    public async Task<EdgeModelFieldCatalogChangeResult> ChangeAsync(EdgeModelFieldCatalogChange input,
        ApplicationAccessContext actor)
    {
        if (!actor.PlatformAdministrator && !actor.Permissions.Contains("shared-dictionary.write"))
            throw new UnauthorizedAccessException("修改共享字段库需要平台级授权");
        if (!input.Confirm) throw new ArgumentException("请明确确认本次移除或恢复操作");
        var model = RequireModel(input.ModelMark);
        if (input.Ids == null || input.Ids.Count is < 1 or > 200 || input.Ids.Any(x => x <= 0))
            throw new ArgumentException("每次请选择 1～200 个有效字段");
        var ids = input.Ids.Distinct().ToArray();
        using var operation = DatabaseOperationScope.Begin(scope);
        await using var catalogLease = await EdgeModelFieldCatalogGuard.EnterAsync(Db);
        var rows = await Db.Queryable<EdgeDashboardModelFieldCatalogEntity>()
            .Where(x => x.ModelMark == model && x.IsDelete == 0 && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SourcePath }).ToListAsync();
        if (rows.Count != ids.Length) throw new ArgumentException("部分字段不存在或不属于该模型，请刷新后重试");
        // 同一模型/路径可能有升级前重复记录；所有副本必须统一移除或恢复。
        var paths = rows.Select(x => x.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var references = input.Removed ? await FindReferencesAsync(model, paths) : [];
        if (references.Count > 0)
        {
            return new EdgeModelFieldCatalogChangeResult
            {
                Applied = false, ReferenceCount = references.Count, References = references.Take(1000).ToList()
            };
        }
        var affected = 0;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            affected = await Db.Updateable<EdgeDashboardModelFieldCatalogEntity>()
                .SetColumns(x => new EdgeDashboardModelFieldCatalogEntity
                {
                    IsSelectionDisabled = input.Removed, UpdateTime = DateTime.Now
                })
                .Where(x => x.ModelMark == model && paths.Contains(x.SourcePath) && x.IsSelectionDisabled != input.Removed)
                .ExecuteCommandAsync();
            await Db.Insertable(new SysUserOperationLogEntity
            {
                UserId = Clip(actor.UserId, 64), UserName = Clip(actor.UserName, 128),
                SessionId = Clip(actor.SessionId, 64), AppCode = Clip(actor.AppCode, 32),
                Module = "共享历史字段管理", Action = input.Removed ? "批量移出字段库" : "批量恢复字段",
                HttpMethod = "POST", RequestPath = "/api/shared-data/catalog/selection", Target = Clip(model, 256),
                Success = true, StatusCode = 200, DurationMs = 0, CreateTime = DateTime.Now,
                DetailJson = JsonConvert.SerializeObject(new { model, paths, removed = input.Removed, affected })
            }).ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) throw transaction.ErrorException;
        return new EdgeModelFieldCatalogChangeResult { Applied = true, ChangedCount = affected };
    }

    private async Task<List<EdgeModelFieldReference>> FindReferencesAsync(string model, string[] requested)
    {
        if (requested.Length == 0) return [];
        var result = new List<EdgeModelFieldReference>();
        var deduplication = new HashSet<(string Path, string Module, string Location)>();
        void Add(string? path, string module, string location)
        {
            foreach (var target in requested.Where(x => EdgeModelFieldCatalogGuard.PathsOverlap(x, path)))
            {
                if (deduplication.Add((target, module, location)))
                    result.Add(new EdgeModelFieldReference { SourcePath = target, Module = module, Location = location });
            }
        }
        var snapshots = await Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .Select(x => new { x.TenantMark, x.DeviceCode, x.ModelMark }).ToListAsync();
        var sources = snapshots.Where(x => EdgeModelFieldCatalogGuard.NormalizeModel(x.ModelMark)
                .Equals(model, StringComparison.OrdinalIgnoreCase))
            .Select(x => JsonConvert.SerializeObject(new[] { x.TenantMark ?? string.Empty, x.DeviceCode }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Matches(string tenant, string device) => sources.Contains(JsonConvert.SerializeObject(new[] { tenant, device }));
        var knownSources = snapshots.Select(x => JsonConvert.SerializeObject(new[] { x.TenantMark ?? string.Empty, x.DeviceCode }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Db.DbMaintenance.IsAnyTable("EdgeDashboardRobotBinding", false))
        {
            var bindings = (await Db.Queryable<EdgeDashboardRobotBindingEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                .Where(x => Matches(x.TenantMark, x.DeviceMark)).ToDictionary(x => x.Id);
            foreach (var field in await Db.Queryable<EdgeDashboardRobotFieldBindingEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                if (bindings.TryGetValue(field.RobotBindingId, out var binding))
                    Add(field.SourcePath, "机械手展示字段", $"{binding.LineCode}/{binding.RobotName}#{binding.Id}/{field.DisplayName}");
            foreach (var rule in await Db.Queryable<EdgeDashboardRobotStateRuleEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                if (bindings.TryGetValue(rule.RobotBindingId, out var binding))
                {
                    var location = $"{binding.LineCode}/{binding.RobotName}#{binding.Id}/{rule.RuleName}";
                    Add(rule.SourcePath, "机械手状态规则", location);
                    Add(rule.FaultReasonPath, "机械手报警原因", location);
                }
        }
        if (Db.DbMaintenance.IsAnyTable("EdgeDashboardDeviceBinding", false))
        {
            var bindings = (await Db.Queryable<EdgeDashboardDeviceBindingEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                .Where(x => Matches(x.TenantMark, x.SourceDeviceMark)).ToDictionary(x => x.Id);
            foreach (var binding in bindings.Values)
                Add(binding.StatusSourcePath, "设备状态字段", $"{binding.LineCode}/{binding.DeviceName}#{binding.Id}");
            foreach (var field in await Db.Queryable<EdgeDashboardDeviceFieldBindingEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                if (bindings.TryGetValue(field.DeviceBindingId, out var binding))
                    Add(field.SourcePath, "设备展示字段", $"{binding.LineCode}/{binding.DeviceName}#{binding.Id}/{field.DisplayName}");
        }
        if (Db.DbMaintenance.IsAnyTable("EdgeDashboardDeviceCard", false))
        {
            var cards = (await Db.Queryable<EdgeDashboardDeviceCardEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                .Where(x => Matches(x.TenantMark, x.DeviceMark) ||
                    !knownSources.Contains(JsonConvert.SerializeObject(new[] { x.TenantMark, x.DeviceMark })) &&
                    EdgeModelFieldCatalogGuard.NormalizeModel(x.ModelMark).Equals(model, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Id);
            foreach (var card in cards.Values)
                Add(card.StatusSourcePath, "设备卡片状态", $"{card.LineCode}/{card.CardName}#{card.Id}");
            foreach (var field in await Db.Queryable<EdgeDashboardDeviceCardFieldEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                if (cards.TryGetValue(field.CardId, out var card))
                    Add(field.SourcePath, "设备卡片展示/追溯/推送", $"{card.LineCode}/{card.CardName}#{card.Id}/{field.DisplayName}");
        }
        if (Db.DbMaintenance.IsAnyTable("EdgeDashboardLineOutputBinding", false))
        {
            foreach (var binding in await Db.Queryable<EdgeDashboardLineOutputBindingEntity>().Where(x => x.IsDelete == 0).ToListAsync())
                if (Matches(binding.TenantMark, binding.DeviceMark))
                    Add(binding.SourcePath, "线体产量", $"{binding.LineCode}/{binding.FieldName}#{binding.Id}");
        }
        if (Db.DbMaintenance.IsAnyTable("EdgeDashboardModelAlarmRule", false))
        {
            foreach (var rule in await Db.Queryable<EdgeDashboardModelAlarmRuleEntity>()
                .Where(x => x.ModelMark == model && x.IsDelete == 0).ToListAsync())
            {
                Add(rule.SourcePath, "模型报警规则", $"{model}/{rule.RuleName}#{rule.Id}");
                Add(rule.FaultReasonPath, "模型报警原因", $"{model}/{rule.RuleName}#{rule.Id}");
            }
        }
        if (Db.DbMaintenance.IsAnyTable("ProcessBoardConfig", false))
        {
            foreach (var row in await Db.Queryable<ProcessBoardConfigEntity>().Where(x => x.Kind == "Card" || x.Kind == "Line").ToListAsync())
            {
                var definition = ProcessBoardService.Read(row);
                foreach (var field in definition.Fields)
                {
                    var location = $"{definition.Name}#{row.Id}/{field.Name}";
                    if (field.FixedText == null && !field.Source.FixedValue.HasValue && Matches(field.Source.TenantMark, field.Source.DeviceMark))
                        Add(field.Source.SourcePath, "工艺展示/曲线/追溯/推送", location);
                    foreach (var variable in field.Variables.Where(x => !x.Value.FixedValue.HasValue))
                        if (Matches(variable.Value.TenantMark, variable.Value.DeviceMark))
                            Add(variable.Value.SourcePath, "工艺公式变量", location + "/" + variable.Key);
                }
            }
        }
        // 中文格式映射本身只是元数据，不作为阻止清理的业务绑定；移除后保留以便恢复。
        // WebSocket 追溯按订阅参数编码工作，不直接引用本 HTTP JSON Pointer 字段库。
        return result;
    }

    private static string RequireModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64)
            throw new ArgumentException("请选择有效模型标识");
        return value.Trim();
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
}
