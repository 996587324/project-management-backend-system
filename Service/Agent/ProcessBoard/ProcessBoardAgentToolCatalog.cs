using System.Text.Json;
using Model.Agent;

namespace Service;

/// <summary>集中声明工具和受控Schema，拒绝未开放的属性、删除、SQL及任意脚本。</summary>
public static class ProcessBoardAgentToolCatalog
{
    private static object Text(int max = 128)
    {
        return new { type = "string", maxLength = max };
    }

    private static object Number(double min = 0, double max = 8000)
    {
        return new { type = "number", minimum = min, maximum = max };
    }

    private static object Integer(long min = 1, long max = long.MaxValue)
    {
        return new { type = "integer", minimum = min, maximum = max };
    }
    private static object Boolean => new { type = "boolean" };
    private static object NullableNumber => new { type = new[] { "number", "null" } };
    private static object NullableText => new { type = new[] { "string", "null" }, maxLength = 8192 };
    private static object Enum(params string[] values)
    {
        return new { type = "string", @enum = values };
    }

    private static object Array(object items, int max)
    {
        return new { type = "array", items, maxItems = max };
    }

    private static object Object(Dictionary<string, object> properties, params string[] required)
    {
        return new { type = "object", properties, required, additionalProperties = false };
    }

    private static object Source => Object(new()
    {
        ["tenantMark"] = Text(64), ["deviceMark"] = Text(128), ["sourcePath"] = Text(512), ["fixedValue"] = NullableNumber
    });

    private static object Field => Object(new()
    {
        ["code"] = Text(64), ["name"] = Text(), ["unit"] = Text(32), ["source"] = Source,
        ["fixedText"] = NullableText, ["formula"] = Text(8192), ["recalculateEverySample"] = Boolean,
        ["variables"] = new { type = "object", additionalProperties = Source, maxProperties = 64 },
        ["functions"] = Array(Text(8192), 64),
        ["constants"] = new { type = "object", additionalProperties = new { type = "number" }, maxProperties = 64 },
        ["precision"] = Integer(0, 12), ["comparisonPrecision"] = Integer(0, 12),
        ["lowerLimit"] = NullableNumber, ["upperLimit"] = NullableNumber,
        ["traceEnabled"] = Boolean, ["trendEnabled"] = Boolean, ["traceRawValue"] = Boolean,
        ["changeThreshold"] = Number(0, double.MaxValue), ["pushEnabled"] = Boolean,
        ["pushOnChange"] = Boolean, ["pushOnLimit"] = Boolean, ["pushOnRecovery"] = Boolean,
        ["recipientUserIds"] = Array(Text(64), 100)
    }, "code");

    private static Dictionary<string, object> LayoutProperties => new()
    {
        ["layout"] = Enum("grid", "horizontal", "vertical", "free"), ["columns"] = Integer(1, 12),
        ["fieldColumns"] = Integer(1, 6), ["width"] = Integer(160, 3840), ["height"] = Integer(100, 2160),
        ["panelLayout"] = Enum("tabs", "split"), ["flowHeight"] = Integer(160, 2160),
        ["trendHeight"] = Integer(200, 2160), ["flowWidth"] = Integer(0, 3840), ["trendWidth"] = Integer(0, 3840)
    };

    private static Dictionary<string, object> PageProperties => new()
    {
        ["page"] = Integer(1, 100000), ["size"] = Integer(1, 50), ["keyword"] = Text(128)
    };

    private static Dictionary<string, object> HistoryProperties => new()
    {
        ["id"] = Integer(), ["fieldCode"] = Text(64),
        ["start"] = new { type = "string", format = "date" }, ["end"] = new { type = "string", format = "date" },
        ["page"] = Integer(1, 100000), ["size"] = Integer(1, 50), ["events"] = Boolean,
        ["points"] = Integer(50, 600), ["raw"] = Boolean
    };

    private static object Mutation(object patch)
    {
        return Object(new()
        {
            ["id"] = Integer(), ["version"] = Integer(), ["patch"] = patch
        }, "id", "version", "patch");
    }

    public static IReadOnlyCollection<AgentToolSeed> Definitions { get; } = Build();

    private static IReadOnlyCollection<AgentToolSeed> Build()
    {
        var tools = new List<AgentToolSeed>();
        void Add(string action, string title, string description, object schema, string example, bool write = false)
        {
            tools.Add(new AgentToolSeed
            {
                AgentCode = AgentCodes.ProcessBoard, ToolName = "process." + action, Title = title,
                Description = description, Method = write ? "POST" : "GET",
                Route = "internal://ProcessBoard/" + action, InputSchema = JsonSerializer.Serialize(schema),
                OutputSchema = "{\"type\":\"object\"}", IsWriteAction = write, RequiresConfirmation = write,
                // 每次执行/确认时由执行器根据真实网页登录或已验证微信渠道，重新校验当前系统和资源授权。
                PermissionKey = null, ServiceName = "ProcessBoard", ActionName = action,
                RiskLevel = write ? "Medium" : "Low", Sort = tools.Count + 1, Examples = example
            });
        }

        Add("overview", "查询工艺看板概览", "统计授权范围内区域、线体、卡片、参数和追溯/推送配置数量，不载入全部实时或历史数据。", Object(new()), "{}");
        var list = PageProperties;
        list["kind"] = Enum("Region", "Line", "Card"); list["parentId"] = Integer(0); list["enabled"] = Boolean;
        Add("definitions", "查找区域线体与卡片", "分页返回配置摘要及ID、version。kind区分Region/Line/Card；按parentId查看子配置，再用definition读取详情。", Object(list), "{\"kind\":\"Line\",\"size\":20}");
        Add("definition", "读取完整工艺配置", "读取指定ID的完整区域、线体或卡片配置，包括version、参数公式、流程图和布局；修改前必读。", Object(new() { ["id"] = Integer() }, "id"), "{\"id\":1}");
        Add("runtime", "查询线体实时参数", "分页查看线体的卡片实时参数、计算结果、数据时间、超限状态；id为线体ID，可按cardId和fieldCode缩小范围。", Object(new()
        {
            ["id"] = Integer(), ["cardId"] = Integer(), ["fieldCode"] = Text(64),
            ["page"] = Integer(1, 100000), ["size"] = Integer(1, 50)
        }, "id"), "{\"id\":1,\"size\":10}");
        Add("sources", "查找可绑定设备数据源", "分页查找授权设备，带共享模型中文名称；不返回完整报文。", Object(PageProperties), "{\"keyword\":\"设备\"}");
        var fields = PageProperties;
        fields["tenantMark"] = Text(64); fields["deviceMark"] = Text(128);
        Add("source-fields", "查询实时与历史源字段", "读取授权设备的可用历史字段、最后实际值及共享中文映射；绑定时必须使用返回的完整sourcePath。", Object(fields, "tenantMark", "deviceMark"), "{\"tenantMark\":\"T1\",\"deviceMark\":\"D1\"}");
        Add("history", "查询工艺参数追溯和事件", "分页查询某卡片或线体参数的采样、变化和上下限事件；events=true查事件，否则查采样。时间使用服务器本地时间。", Object(HistoryProperties, "id", "fieldCode", "start", "end"), "{\"id\":1,\"fieldCode\":\"temperature\",\"start\":\"2026-10-17 08:00:00\",\"end\":\"2026-10-17 09:00:00\"}");
        Add("curve", "查询工艺曲线聚合点", "按字段和时间查询平均值、最小值、最大值及样本数，最多600点；raw区分原始与公式计算值。", Object(HistoryProperties, "id", "fieldCode", "start", "end"), "{\"id\":1,\"fieldCode\":\"temperature\",\"start\":\"2026-10-17 08:00:00\",\"end\":\"2026-10-17 09:00:00\",\"points\":100}");
        Add("config-history", "查询工艺配置版本审计", "分页返回配置修改人、时间、版本；指定version时返回该历史定义，仍校验历史数据源授权。", Object(new()
        {
            ["id"] = Integer(), ["version"] = Integer(), ["page"] = Integer(1, 100000), ["size"] = Integer(1, 50)
        }, "id"), "{\"id\":1}");
        Add("push-tasks", "查询工艺推送投递记录", "按线体查看参数变化/越限的推送记录和发送结果，需要工艺配置权限，不触发发送。", Object(new() { ["id"] = Integer(), ["page"] = Integer(1, 100000) }, "id"), "{\"id\":1}");
        Add("recipients", "查找已绑定微信接收人", "查询可配置微信接收人，需要工艺配置权限；不返回密钥、token，不发送消息。", Object(new()), "{}");
        Add("setting", "读取工艺采样及保留设置", "查询采样间隔、保留小时、共享推送间隔和公式授权配置；本工具不修改设置。", Object(new()), "{}");
        Add("formula-preview", "试算工艺参数公式", "用mXparser试算表达式和显式变量/常量/数学函数，需要配置权限；仅返回试算结果，不更改真实值或授权声明。", Object(new()
        {
            ["formula"] = Text(8192), ["variables"] = new { type = "object", additionalProperties = new { type = "number" } },
            ["constants"] = new { type = "object", additionalProperties = new { type = "number" } },
            ["functions"] = Array(Text(8192), 64)
        }, "formula"), "{\"formula\":\"x/10\",\"variables\":{\"x\":250}}");
        Add("export-status", "查询工艺导出进度", "查询属于当前用户及系统的异步导出状态，返回需登录的下载地址，不泄漏服务器文件路径。", Object(new() { ["id"] = Integer() }, "id"), "{\"id\":1}");
        Add("create", "新增区域线体或参数卡片", "创建Region/Line/Card；区域parentId=0、线体父级是区域、卡片父级是工艺线体。确认后返回真实ID；不猜ID。", Object(new()
        {
            ["kind"] = Enum("Region", "Line", "Card"), ["parentId"] = Integer(0), ["code"] = Text(64),
            ["name"] = Text(), ["sort"] = Integer(0), ["enabled"] = Boolean
        }, "kind", "parentId", "code", "name"), "{\"kind\":\"Region\",\"parentId\":0,\"code\":\"R1\",\"name\":\"装配区\"}", true);
        Add("update", "编辑工艺配置基本信息", "按id和最新version修改名称、编码、排序或启停；只改patch内显式属性，保留参数、流程和布局；不能移动父级。", Mutation(Object(new()
        {
            ["code"] = Text(64), ["name"] = Text(), ["sort"] = Integer(0), ["enabled"] = Boolean
        })), "{\"id\":1,\"version\":1,\"patch\":{\"name\":\"新区名\"}}", true);
        Add("field-upsert", "新增或编辑工艺参数", "id为卡片或线体ID；patch.code匹配字段，新增还需name与source或fixedText。可改公式变量、上下限、追溯、曲线、阈值、接收人及推送触发条件。省略保留，null可清除上下限/固定文本。不删除字段。", Mutation(Field), "{\"id\":3,\"version\":1,\"patch\":{\"code\":\"temperature\",\"name\":\"温度\",\"source\":{\"tenantMark\":\"T1\",\"deviceMark\":\"D1\",\"sourcePath\":\"/temperature\"},\"upperLimit\":80,\"traceEnabled\":true}}", true);
        Add("flows-upsert", "新增或编辑工艺流程图", "id为线体ID；按节点id合并，节点包含名称、坐标及可选关联卡片。连线按from/to添加；不移除已有节点或连线。", Mutation(Object(new()
        {
            ["nodes"] = Array(Object(new()
            {
                ["id"] = Text(64), ["name"] = Text(), ["x"] = Number(-8000), ["y"] = Number(-8000),
                ["cardId"] = new { type = new[] { "integer", "null" } }
            }, "id", "name", "x", "y"), 200),
            ["edges"] = Array(Object(new() { ["from"] = Text(64), ["to"] = Text(64) }, "from", "to"), 400)
        })), "{\"id\":2,\"version\":1,\"patch\":{\"nodes\":[{\"id\":\"n1\",\"name\":\"上料\",\"x\":20,\"y\":30}]}}", true);
        Add("devices-bind", "绑定线体设备", "按租户/设备标识合并线体设备列表，不移除已有设备；绑定前查询可用数据源。", Mutation(Object(new() { ["devices"] = Array(Source, 200) }, "devices")), "{\"id\":2,\"version\":1,\"patch\":{\"devices\":[{\"tenantMark\":\"T1\",\"deviceMark\":\"D1\"}]}}", true);
        var layout = LayoutProperties;
        layout["blocks"] = Array(Object(new()
        {
            ["cardId"] = Integer(), ["x"] = Integer(0, 8000), ["y"] = Integer(0, 8000),
            ["width"] = Integer(160, 3840), ["height"] = Integer(100, 2160)
        }, "cardId", "x", "y", "width", "height"), 200);
        Add("layout-update", "调整工艺看板自由布局", "修改区域/线体/卡片宽高、排布、每行卡片数、fieldColumns每行参数组数、流程/趋势面板宽高和tabs/split；blocks按cardId合并，不移除已有布局。", Mutation(Object(layout)), "{\"id\":3,\"version\":1,\"patch\":{\"fieldColumns\":2,\"width\":600}}", true);
        Add("setting-update", "调整工艺采样与保留设置", "仅平台管理员可调整采样秒数、保留小时及共享推送间隔，后者也影响设备卡片推送。先查setting，携带expected原值防止覆盖并确认。不能修改公式授权声明。", Object(new()
        {
            ["expected"] = Object(new()
            {
                ["sampleIntervalSeconds"] = Integer(1, 3600), ["retentionHours"] = Integer(1, 8760),
                ["pushIntervalSeconds"] = Integer(1, 3600)
            }, "sampleIntervalSeconds", "retentionHours", "pushIntervalSeconds"),
            ["patch"] = Object(new()
            {
                ["sampleIntervalSeconds"] = Integer(1, 3600), ["retentionHours"] = Integer(1, 8760),
                ["pushIntervalSeconds"] = Integer(1, 3600)
            })
        }, "expected", "patch"), "{\"expected\":{\"sampleIntervalSeconds\":5,\"retentionHours\":120,\"pushIntervalSeconds\":5},\"patch\":{\"retentionHours\":48}}", true);
        Add("export-create", "创建工艺参数异步导出", "按卡片/线体ID、字段和时间创建当前用户的异步采样/事件CSV导出，不在模型回复内展开大量数据；确认后再查export-status。", Object(HistoryProperties, "id", "fieldCode", "start", "end"), "{\"id\":3,\"fieldCode\":\"temperature\",\"start\":\"2026-10-17 08:00:00\",\"end\":\"2026-10-17 09:00:00\"}", true);
        return tools.AsReadOnly();
    }
}
