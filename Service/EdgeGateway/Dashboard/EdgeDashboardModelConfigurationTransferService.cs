using System.Globalization;
using System.Text;
using Interface;
using Model.EdgeGateway;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using SqlSugar;

namespace Service;

/// <summary>使用项目既有 NPOI 组件读写模型映射、字段展示映射与状态优先级工作簿。</summary>
public sealed class EdgeDashboardModelConfigurationTransferService : IEdgeDashboardModelConfigurationTransferService
{
    private const string ModelDisplaySheet = "设备模型映射";
    private const string ModelFieldSheet = "模型字段映射";
    private const string ModelStateSheet = "模型状态优先级";
    private const string SpreadsheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly IEdgeDashboardService _dashboardService;
    private readonly SqlSugarScope _dbScope;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    public EdgeDashboardModelConfigurationTransferService(
        IEdgeDashboardService dashboardService,
        SqlSugarScope dbScope)
    {
        _dashboardService = dashboardService;
        _dbScope = dbScope;
    }

    public async Task<EdgeDashboardModelConfigurationExportDto> ExportAsync(
        string dashboardCode,
        bool includeData,
        CancellationToken cancellationToken = default)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        using var workbook = new XSSFWorkbook();
        var displayRows = includeData
            ? await BuildModelDisplayRowsAsync(dashboardCode, cancellationToken)
            : BuildModelDisplayExamples();
        var fieldRows = includeData
            ? await BuildModelFieldRowsAsync(dashboardCode, cancellationToken)
            : BuildModelFieldExamples();
        var stateRows = includeData
            ? await BuildModelStateRowsAsync(dashboardCode, cancellationToken)
            : BuildModelStateExamples();

        WriteSheet(workbook, ModelDisplaySheet, "模型标识、显示名称和建议设备类别。导入时模型标识相同的记录会更新。",
            ["模型标识", "显示名称", "建议类别", "排序", "启用"], displayRows);
        WriteSheet(workbook, ModelFieldSheet, "同一模型的字段清单会整体覆盖，请在导出当前配置后编辑，避免遗漏已有字段。",
            ["模型标识", "原始字段", "展示名称", "值类型", "格式", "单位", "小数位", "值映射JSON", "排序", "启用"], fieldRows);
        WriteSheet(workbook, ModelStateSheet, "同一模型的状态优先级会整体覆盖。优先级数值越小，状态裁决越优先。",
            ["模型标识", "状态编码", "优先级"], stateRows);

        await using var stream = new MemoryStream();
        workbook.Write(stream, true);
        return new EdgeDashboardModelConfigurationExportDto
        {
            FileName = includeData
                ? $"production-dashboard-model-config-{DateTime.Now:yyyyMMddHHmmss}.xlsx"
                : "production-dashboard-model-config-template.xlsx",
            ContentType = SpreadsheetContentType,
            Content = stream.ToArray()
        };
    }

    public async Task<EdgeDashboardModelConfigurationImportResultDto> ImportAsync(
        string fileName,
        Stream stream,
        string dashboardCode,
        string? csvConfigurationType,
        CancellationToken cancellationToken = default)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var rowsBySheet = extension switch
        {
            ".xlsx" or ".xls" => ReadWorkbookRows(stream),
            ".csv" => new Dictionary<string, List<ImportRow>>(StringComparer.OrdinalIgnoreCase)
            {
                [ResolveCsvSheetName(csvConfigurationType)] = ReadCsvRows(stream, ResolveCsvSheetName(csvConfigurationType))
            },
            _ => throw new InvalidOperationException("仅支持 CSV、XLS 或 XLSX 文件")
        };

        var result = new EdgeDashboardModelConfigurationImportResultDto();
        await ImportModelDisplayRowsAsync(
            rowsBySheet.GetValueOrDefault(ModelDisplaySheet) ?? [], dashboardCode, result, cancellationToken);
        await ImportModelFieldRowsAsync(
            rowsBySheet.GetValueOrDefault(ModelFieldSheet) ?? [], dashboardCode, result, cancellationToken);
        await ImportModelStateRowsAsync(
            rowsBySheet.GetValueOrDefault(ModelStateSheet) ?? [], dashboardCode, result, cancellationToken);

        if (result.Total == 0)
            throw new InvalidOperationException("未识别到可导入配置。Excel 请使用导出模板中的工作表名称；CSV 请先选择配置类别。");
        result.Message = $"导入完成：共识别 {result.Total} 条，成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条";
        return result;
    }

    private async Task ImportModelDisplayRowsAsync(
        List<ImportRow> rows,
        string dashboardCode,
        EdgeDashboardModelConfigurationImportResultDto result,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var existing = (await _dashboardService.GetModelDisplayMappingsAsync(dashboardCode))
            .ToDictionary(x => x.ModelMark, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            result.Total++;
            var modelMark = Value(row, "模型标识");
            try
            {
                var dto = new EdgeDashboardModelDisplayMappingSaveDto
                {
                    Id = existing.GetValueOrDefault(modelMark)?.Id ?? 0,
                    DashboardCode = dashboardCode,
                    ModelMark = modelMark,
                    DisplayName = Value(row, "显示名称"),
                    EquipmentType = NormalizeEquipmentType(ValueOrDefault(row, "建议类别", "Device")),
                    Sort = ParseInt(Value(row, "排序"), 0, "排序"),
                    Enabled = ParseBoolean(ValueOrDefault(row, "启用", "是"), "启用")
                };
                var saved = await _dashboardService.SaveModelDisplayMappingAsync(dto);
                existing[modelMark] = saved;
                result.SuccessCount++;
            }
            catch (Exception exception)
            {
                AddFailure(result, row, modelMark, exception.Message);
            }
        }
    }

    private async Task ImportModelFieldRowsAsync(
        List<ImportRow> rows,
        string dashboardCode,
        EdgeDashboardModelConfigurationImportResultDto result,
        CancellationToken cancellationToken)
    {
        foreach (var group in rows.GroupBy(x => Value(x, "模型标识"), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                foreach (var row in group)
                {
                    result.Total++;
                    AddFailure(result, row, string.Empty, "模型标识不能为空");
                }
                continue;
            }

            var groupRows = group.ToList();
            result.Total += groupRows.Count;
            try
            {
                var mappings = groupRows.Select(row => new EdgeDashboardModelFieldDisplayMappingSaveDto
                {
                    SourcePath = Value(row, "原始字段"),
                    DisplayName = Value(row, "展示名称"),
                    ValueType = ValueOrDefault(row, "值类型", "String"),
                    FormatType = ValueOrDefault(row, "格式", "Direct"),
                    Unit = NullIfEmpty(Value(row, "单位")),
                    Decimals = ParseNullableInt(Value(row, "小数位"), "小数位"),
                    MappingJson = NullIfEmpty(Value(row, "值映射JSON")),
                    Sort = ParseInt(Value(row, "排序"), 0, "排序"),
                    Enabled = ParseBoolean(ValueOrDefault(row, "启用", "是"), "启用")
                }).ToList();
                await _dashboardService.SaveModelFieldDisplayMappingsAsync(new EdgeDashboardModelFieldDisplayMappingListSaveDto
                {
                    DashboardCode = dashboardCode,
                    ModelMark = group.Key,
                    Mappings = mappings
                });
                result.SuccessCount += groupRows.Count;
            }
            catch (Exception exception)
            {
                foreach (var row in groupRows) AddFailure(result, row, group.Key, exception.Message);
            }
        }
    }

    private async Task ImportModelStateRowsAsync(
        List<ImportRow> rows,
        string dashboardCode,
        EdgeDashboardModelConfigurationImportResultDto result,
        CancellationToken cancellationToken)
    {
        foreach (var group in rows.GroupBy(x => Value(x, "模型标识"), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                foreach (var row in group)
                {
                    result.Total++;
                    AddFailure(result, row, string.Empty, "模型标识不能为空");
                }
                continue;
            }

            var groupRows = group.ToList();
            result.Total += groupRows.Count;
            try
            {
                await _dashboardService.SaveModelStatePrioritiesAsync(new EdgeDashboardModelStatePriorityListSaveDto
                {
                    DashboardCode = dashboardCode,
                    ModelMark = group.Key,
                    Priorities = groupRows.Select(row => new EdgeDashboardModelStatePrioritySaveDto
                    {
                        State = Value(row, "状态编码"),
                        Priority = ParseInt(Value(row, "优先级"), 0, "优先级")
                    }).ToList()
                });
                result.SuccessCount += groupRows.Count;
            }
            catch (Exception exception)
            {
                foreach (var row in groupRows) AddFailure(result, row, group.Key, exception.Message);
            }
        }
    }

    private async Task<List<IReadOnlyList<string>>> BuildModelDisplayRowsAsync(string dashboardCode, CancellationToken cancellationToken) =>
        (await _dashboardService.GetModelDisplayMappingsAsync(dashboardCode))
        .Select(x => (IReadOnlyList<string>)[x.ModelMark, x.DisplayName, x.EquipmentType, x.Sort.ToString(), ToBooleanText(x.Enabled)])
        .ToList();

    private async Task<List<IReadOnlyList<string>>> BuildModelFieldRowsAsync(string dashboardCode, CancellationToken cancellationToken) =>
        (await Db.Queryable<EdgeDashboardModelFieldDisplayMappingEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
            .OrderBy(x => x.ModelMark).OrderBy(x => x.Sort).ToListAsync())
        .Select(x => (IReadOnlyList<string>)[x.ModelMark, x.SourcePath, x.DisplayName, x.ValueType, x.FormatType,
            x.Unit ?? string.Empty, x.Decimals?.ToString() ?? string.Empty, x.MappingJson ?? string.Empty, x.Sort.ToString(), ToBooleanText(x.Enabled)])
        .ToList();

    private async Task<List<IReadOnlyList<string>>> BuildModelStateRowsAsync(string dashboardCode, CancellationToken cancellationToken) =>
        (await Db.Queryable<EdgeDashboardModelStatePriorityEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.IsDelete == 0)
            .OrderBy(x => x.ModelMark).OrderBy(x => x.Priority).ToListAsync())
        .Select(x => (IReadOnlyList<string>)[x.ModelMark, x.State, x.Priority.ToString()]).ToList();

    private static List<IReadOnlyList<string>> BuildModelDisplayExamples() =>
        [["MODEL_CODE", "设备业务名称", "Device", "10", "是"]];
    private static List<IReadOnlyList<string>> BuildModelFieldExamples() =>
        [["MODEL_CODE", "data.status", "运行状态", "String", "ValueMap", "", "", "{\"1\":\"运行\",\"0\":\"待机\"}", "10", "是"]];
    private static List<IReadOnlyList<string>> BuildModelStateExamples() =>
        [["MODEL_CODE", "FAULT", "10"], ["MODEL_CODE", "IDLE", "20"]];

    private static void WriteSheet(
        IWorkbook workbook,
        string name,
        string note,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sheet = workbook.CreateSheet(name);
        var noteRow = sheet.CreateRow(0);
        noteRow.CreateCell(0).SetCellValue(note);
        var headerRow = sheet.CreateRow(1);
        for (var column = 0; column < headers.Count; column++)
        {
            headerRow.CreateCell(column).SetCellValue(headers[column]);
            sheet.SetColumnWidth(column, Math.Min(28 * 256, Math.Max(12 * 256, Encoding.UTF8.GetByteCount(headers[column]) * 2 * 256)));
        }
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = sheet.CreateRow(rowIndex + 2);
            for (var column = 0; column < headers.Count; column++)
                row.CreateCell(column).SetCellValue(rows[rowIndex].ElementAtOrDefault(column) ?? string.Empty);
        }
        sheet.CreateFreezePane(0, 2);
        sheet.SetAutoFilter(new NPOI.SS.Util.CellRangeAddress(1, Math.Max(1, rows.Count + 1), 0, headers.Count - 1));
    }

    private static Dictionary<string, List<ImportRow>> ReadWorkbookRows(Stream stream)
    {
        using var workbook = WorkbookFactory.Create(stream);
        var result = new Dictionary<string, List<ImportRow>>(StringComparer.OrdinalIgnoreCase);
        var formatter = new DataFormatter(CultureInfo.InvariantCulture);
        for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            if (sheet.LastRowNum < 1) continue;
            var headers = ReadHeaders(sheet.GetRow(1), formatter);
            if (headers.Count == 0) continue;
            var rows = new List<ImportRow>();
            for (var rowIndex = 2; rowIndex <= sheet.LastRowNum; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row == null) continue;
                var values = ReadRow(row, headers, formatter);
                if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
                rows.Add(new ImportRow(sheet.SheetName, rowIndex + 1, values));
            }
            result[sheet.SheetName] = rows;
        }
        return result;
    }

    private static List<ImportRow> ReadCsvRows(Stream stream, string sheetName)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var reader = new StringReader(DecodeCsv(buffer.ToArray()));
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return [];
        var headers = ParseCsvLine(headerLine).Select(x => x.Trim()).ToList();
        var rows = new List<ImportRow>();
        var rowIndex = 1;
        while (reader.ReadLine() is { } line)
        {
            rowIndex++;
            var cells = ParseCsvLine(line);
            var values = headers.Select((header, index) => new KeyValuePair<string, string>(header, cells.ElementAtOrDefault(index) ?? string.Empty))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(new ImportRow(sheetName, rowIndex, values));
        }
        return rows;
    }

    private static List<string> ReadHeaders(IRow? row, DataFormatter formatter)
    {
        if (row == null) return [];
        var headers = new List<string>();
        for (var index = 0; index < row.LastCellNum; index++) headers.Add(formatter.FormatCellValue(row.GetCell(index)).Trim());
        return headers;
    }

    private static Dictionary<string, string> ReadRow(IRow row, IReadOnlyList<string> headers, DataFormatter formatter) =>
        headers.Select((header, index) => new KeyValuePair<string, string>(header, formatter.FormatCellValue(row.GetCell(index)).Trim()))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { value.Append('"'); index++; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (character == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); continue; }
            value.Append(character);
        }
        values.Add(value.ToString());
        return values;
    }

    /// <summary>兼容 UTF-8 和 Excel 在中文 Windows 中默认导出的 GB18030 CSV。</summary>
    private static string DecodeCsv(byte[] content)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(content);
        }
    }

    private static string ResolveCsvSheetName(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "model-display" => ModelDisplaySheet,
        "model-fields" => ModelFieldSheet,
        "model-state-priorities" => ModelStateSheet,
        _ => throw new InvalidOperationException("导入 CSV 时请指定配置类别")
    };

    private static string NormalizeEquipmentType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "robot" or "机械手" => "Robot",
        "device" or "通用设备" => "Device",
        "machine" or "加工设备" => "Machine",
        "inspection" or "检测设备" => "Inspection",
        _ => value.Trim()
    };

    private static string Value(ImportRow row, string header) => row.Values.GetValueOrDefault(header)?.Trim() ?? string.Empty;
    private static string ValueOrDefault(ImportRow row, string header, string fallback) => string.IsNullOrWhiteSpace(Value(row, header)) ? fallback : Value(row, header);
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name}不能为空") : value.Trim();
    private static int ParseInt(string value, int fallback, string name) => string.IsNullOrWhiteSpace(value) ? fallback : int.TryParse(value, out var result) ? result : throw new InvalidOperationException($"{name}必须为整数");
    private static int? ParseNullableInt(string value, string name) => string.IsNullOrWhiteSpace(value) ? null : ParseInt(value, 0, name);
    private static bool ParseBoolean(string value, string name) => value.Trim().ToLowerInvariant() switch { "是" or "true" or "1" or "启用" => true, "否" or "false" or "0" or "停用" => false, _ => throw new InvalidOperationException($"{name}仅支持 是/否") };
    private static string ToBooleanText(bool value) => value ? "是" : "否";
    private static void AddFailure(EdgeDashboardModelConfigurationImportResultDto result, ImportRow row, string key, string reason)
    {
        result.FailedCount++;
        result.Failures.Add(new EdgeDashboardModelConfigurationImportFailureDto { SheetName = row.SheetName, RowIndex = row.RowIndex, Key = key, Reason = reason });
    }

    private sealed record ImportRow(string SheetName, int RowIndex, IReadOnlyDictionary<string, string> Values);
}
