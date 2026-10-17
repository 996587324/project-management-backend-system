using System.Reflection;
using Model.ProcessBoard;
using Model.SystemApplications;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Service;

internal static class FieldColumnsSmoke
{
    public static async Task RunAsync()
    {
        Check(new ProcessBoardDefinition().FieldColumns == 1, "new configurations default to one field per row");

        var historical = ProcessBoardService.Read(new ProcessBoardConfigEntity
        {
            Id = 17,
            Version = 2,
            DefinitionJson = """{"Kind":"Card","Code":"legacy","Name":"旧卡片","Columns":3}"""
        });
        Check(historical.FieldColumns == 1 && historical.Columns == 3,
            "old JSON defaults to one field column without changing outer columns");
        Check(historical.Id == 17 && historical.Version == 2, "reading retains entity ID and version");

        for (var columns = 1; columns <= 6; columns++)
        {
            var definition = new ProcessBoardDefinition
            {
                Kind = "Card",
                Code = "card",
                Name = "参数卡片",
                Columns = 3,
                FieldColumns = columns,
                Fields = [new ProcessFieldDefinition { Code = "temperature", Name = "温度", FixedText = "25" }]
            };
            var fieldIdentity = JsonConvert.SerializeObject(definition.Fields[0]);
            var json = JsonConvert.SerializeObject(definition);
            var restored = ProcessBoardService.Read(new ProcessBoardConfigEntity { DefinitionJson = json });
            Check(restored.FieldColumns == columns && restored.Columns == 3,
                $"JSON round-trip preserves {columns} field columns independently");
            Check(JsonConvert.SerializeObject(restored.Fields[0]) == fieldIdentity,
                "presentation changes do not modify field identity or tracing baseline inputs");

            // 与 Controller 相同的 camelCase JSON 参数反序列化方式。
            var clientJson = JObject.Parse(json);
            clientJson.Remove("FieldColumns");
            clientJson["fieldColumns"] = columns;
            Check(JsonConvert.DeserializeObject<ProcessBoardDefinition>(clientJson.ToString())!.FieldColumns == columns,
                "camelCase frontend input retains configured field columns");
        }

        // 只调用保存前的内存校验，不初始化表、不访问业务数据库、不启动后台服务。
        var service = new ProcessBoardService(null!, null!, null!);
        var validate = typeof(ProcessBoardService).GetMethod("ValidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var access = new ApplicationAccessContext { AllResources = true };
        foreach (var columns in new[] { -1, 0, 7, int.MaxValue })
        {
            var rejected = false;
            try
            {
                await (Task)validate.Invoke(service, [access, new ProcessBoardDefinition
                {
                    Kind = "Card", Code = "card", Name = "参数卡片", FieldColumns = columns
                }])!;
            }
            catch (ArgumentException exception) when (exception.Message.Contains("每行参数数量"))
            {
                rejected = true;
            }
            Check(rejected, $"save validation rejects invalid field column count {columns} before querying parent");
        }
        foreach (var columns in new[] { 1, 6 })
        {
            await (Task)validate.Invoke(service, [access, new ProcessBoardDefinition
            {
                Kind = "Region", Code = "region", Name = "区域", FieldColumns = columns
            }])!;
        }
        Console.WriteLine("PASS: field column validation accepts inclusive bounds 1 and 6");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }
        Console.WriteLine("PASS: " + message);
    }
}
