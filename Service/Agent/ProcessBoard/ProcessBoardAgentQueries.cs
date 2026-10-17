using System.Text.Json;
using System.Text.Json.Nodes;
using Model.EdgeGateway;
using Model.ProcessBoard;
using Model.SystemApplications;
using SqlSugar;

namespace Service;

public sealed partial class ProcessBoardAgentExecutor
{
    private async Task<object> ReadAsync(string action, JsonElement args, ApplicationAccessContext access, CancellationToken token)
    {
        switch (action)
        {
            case "overview":
            {
                var definitions = await board.GetDefinitionsAsync(access);
                var fields = definitions.SelectMany(item => item.Fields).ToArray();
                return new
                {
                    regions = definitions.Count(item => item.Kind == "Region"),
                    lines = definitions.Count(item => item.Kind == "Line"),
                    cards = definitions.Count(item => item.Kind == "Card"),
                    fields = fields.Length, traceFields = fields.Count(field => field.TraceEnabled),
                    trendFields = fields.Count(field => field.TrendEnabled), pushFields = fields.Count(field => field.PushEnabled),
                    enabled = definitions.Count(item => item.Enabled), generatedAt = DateTime.Now
                };
            }
            case "definitions":
            {
                IEnumerable<ProcessBoardDefinition> definitions = await board.GetDefinitionsAsync(access);
                var kind = Text(args, "kind");
                var keyword = Text(args, "keyword");
                if (kind.Length > 0) definitions = definitions.Where(item => item.Kind == kind);
                if (args.TryGetProperty("parentId", out var parent)) definitions = definitions.Where(item => item.ParentId == parent.GetInt64());
                if (args.TryGetProperty("enabled", out var enabled)) definitions = definitions.Where(item => item.Enabled == enabled.GetBoolean());
                if (keyword.Length > 0) definitions = definitions.Where(item => item.Code.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                return Page(definitions.OrderBy(item => item.Sort).ThenBy(item => item.Id).Select(item => new
                {
                    item.Id, item.Kind, item.ParentId, item.Code, item.Name, item.Version, item.Enabled,
                    fields = item.Fields.Count, devices = item.Devices.Count, nodes = item.Flow.Count
                }), args);
            }
            case "definition":
                return await GetDefinitionAsync(access, Id(args));
            case "runtime":
                return await RuntimeAsync(access, args);
            case "sources":
                return await SourcesAsync(access, args);
            case "source-fields":
            {
                var source = new ProcessSourceBinding { TenantMark = Text(args, "tenantMark"), DeviceMark = Text(args, "deviceMark") };
                ProcessBoardService.RequireSource(access, source, false);
                var fields = await dashboard.GetSourceFieldsAsync(source.TenantMark, source.DeviceMark);
                var keyword = Text(args, "keyword");
                return Page(fields.Where(field => keyword.Length == 0 ||
                    field.FieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    field.SourcePath.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    field.DisplayValue?.ToString()?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true), args);
            }
            case "history":
                return await board.GetHistoryAsync(access, Id(args), Text(args, "fieldCode"), Date(args, "start"), Date(args, "end"),
                    Int(args, "page", 1), Math.Clamp(Int(args, "size", 20), 1, 50), Flag(args, "events"));
            case "curve":
                return await board.GetCurveAsync(access, Id(args), Text(args, "fieldCode"), Date(args, "start"), Date(args, "end"),
                    Math.Clamp(Int(args, "points", 100), 50, 600), Flag(args, "raw"));
            case "config-history":
                return await ConfigHistoryAsync(access, args);
            case "push-tasks":
            {
                var line = await GetDefinitionAsync(access, Id(args));
                RequireLine(line);
                return await board.GetPushTasksAsync(access, Id(args), Int(args, "page", 1));
            }
            case "recipients":
                return await push.GetRecipientsAsync();
            case "setting":
                return await board.GetSettingAsync();
            case "formula-preview":
                return await formulas.EvaluateAsync(Deserialize<ProcessFormulaRequest>(args), token);
            case "export-status":
            {
                var result = JsonSerializer.SerializeToNode(await board.GetExportAsync(access, Id(args)), JsonOptions)!.AsObject();
                if (result["status"]?.GetValue<string>() == "Ready")
                    result["downloadUrl"] = $"/api/process-board/exports/{Id(args)}/file";
                return result;
            }
            default:
                throw new ArgumentException("未开放的查询动作");
        }
    }

    private async Task<object> SourcesAsync(ApplicationAccessContext access, JsonElement args)
    {
        var keys = access.SourceKeys.ToArray();
        var keyword = Text(args, "keyword");
        var models = await dictionary.GetModelsAsync();
        var matchedModels = models.Where(model => model.Enabled && model.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(model => model.ModelMark).ToArray();
        var query = Db.Queryable<EdgeRealtimeDeviceSnapshotEntity>()
            .WhereIF(!access.AllResources, item => keys.Contains(item.TenantMark + "\u001f" + item.DeviceCode));
        if (keyword.Length > 0)
            query = query.Where(item => item.DeviceCode.Contains(keyword) || item.ModelMark!.Contains(keyword) || matchedModels.Contains(item.ModelMark!));
        var page = Int(args, "page", 1);
        var size = Math.Clamp(Int(args, "size", 20), 1, 50);
        RefAsync<int> total = 0;
        // SQL分页，只读取当前页源索引；中文映射名称也可搜索，不载入完整报文。
        var rows = await query.OrderByDescending(item => item.ReceiveTime)
            .Select(item => new { item.TenantMark, deviceMark = item.DeviceCode, item.ModelMark, item.ReceiveTime })
            .ToPageListAsync(page, size, total);
        var mapped = rows.Select(item => new
        {
            item.TenantMark, item.deviceMark, item.ModelMark, item.ReceiveTime,
            displayName = models.FirstOrDefault(model => model.Enabled && model.ModelMark == item.ModelMark)?.DisplayName ?? item.deviceMark
        });
        return new { total = (int)total, page, size, records = mapped.ToArray() };
    }

    private async Task<object> RuntimeAsync(ApplicationAccessContext access, JsonElement args)
    {
        var definitions = await board.GetDefinitionsAsync(access);
        var line = definitions.FirstOrDefault(item => item.Id == Id(args) && item.Kind == "Line" && item.Enabled)
            ?? throw new ArgumentException("工艺线体不存在、已停用或未授权");
        if (!definitions.Any(item => item.Id == line.ParentId && item.Kind == "Region" && item.Enabled))
            throw new ArgumentException("所属区域不存在或已停用");
        var targets = definitions.Where(item => item.Enabled && (item.Id == line.Id || item.Kind == "Card" && item.ParentId == line.Id));
        if (args.TryGetProperty("cardId", out var card)) targets = targets.Where(item => item.Id == card.GetInt64());
        var fieldCode = Text(args, "fieldCode");
        var fields = targets.SelectMany(item => item.Fields.Where(field => fieldCode.Length == 0 || field.Code == fieldCode)
            .Select(field => new { definition = item, field })).ToList();
        var page = Int(args, "page", 1);
        var size = Math.Clamp(Int(args, "size", 20), 1, 50);
        var selected = fields.Skip((page - 1) * size).Take(size).ToArray();
        var ids = selected.Select(item => item.definition.Id).Distinct().ToArray();
        var states = ids.Length == 0 ? [] : await Db.Queryable<ProcessBoardValueEntity>().Where(item => ids.Contains(item.CardId)).ToListAsync();
        var records = selected.Select(item =>
        {
            ProcessFieldRuntime runtime;
            try
            {
                RequireSources(access, item.field);
                var state = states.FirstOrDefault(state => state.CardId == item.definition.Id &&
                    state.FieldCode == item.field.Code && state.Version == item.definition.Version);
                runtime = state == null
                    ? new ProcessFieldRuntime { Code = item.field.Code, Error = "等待后台采样" }
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<ProcessFieldRuntime>(state.RuntimeJson)!;
            }
            catch (UnauthorizedAccessException)
            {
                runtime = new ProcessFieldRuntime { Code = item.field.Code, Error = "该字段的数据源未授权" };
            }
            return new { cardId = item.definition.Id, cardName = item.definition.Name, item.definition.Kind,
                item.field.Name, item.field.Unit, runtime };
        }).ToArray();
        return new { lineId = line.Id, lineName = line.Name, total = fields.Count, page, size, records, generatedAt = DateTime.Now };
    }

    private static void RequireSources(ApplicationAccessContext access, ProcessFieldDefinition field)
    {
        if (field.FixedText == null) ProcessBoardService.RequireSource(access, field.Source);
        foreach (var source in field.Variables.Values) ProcessBoardService.RequireSource(access, source);
    }

    private async Task<object> ConfigHistoryAsync(ApplicationAccessContext access, JsonElement args)
    {
        var definition = await GetDefinitionAsync(access, Id(args));
        var query = Db.Queryable<ProcessBoardConfigAuditEntity>().Where(item => item.ConfigId == definition.Id);
        if (args.TryGetProperty("version", out var version))
        {
            var selectedVersion = version.GetInt32();
            var audit = await query.Where(item => item.Version == selectedVersion).FirstAsync()
                ?? throw new ArgumentException("配置版本不存在");
            var historical = Newtonsoft.Json.JsonConvert.DeserializeObject<ProcessBoardDefinition>(audit.DefinitionJson)!;
            foreach (var field in historical.Fields) RequireSources(access, field);
            foreach (var source in historical.Devices) ProcessBoardService.RequireSource(access, source, false);
            return new { audit.Id, audit.ConfigId, audit.Version, audit.UserId, audit.CreateTime, definition = historical };
        }
        RefAsync<int> total = 0;
        var page = Int(args, "page", 1);
        var size = Math.Clamp(Int(args, "size", 20), 1, 50);
        var rows = await query.OrderByDescending(item => item.Id)
            .Select(item => new { item.Id, item.ConfigId, item.Version, item.UserId, item.CreateTime }).ToPageListAsync(page, size, total);
        return new { total = (int)total, page, size, records = rows };
    }
}
