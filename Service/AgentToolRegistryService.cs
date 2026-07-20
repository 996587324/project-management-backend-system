using Interface;
using Model.Agent;
using SqlSugar;
using System.Collections.Concurrent;

namespace Service;

/// <summary>
/// Agent 工具注册服务，负责初始化系统内置 Agent 和它能调用的工具清单。
/// </summary>
public class AgentToolRegistryService : IAgentToolRegistryService
{
    public const string ProjectManagementAgentCode = "project-management";
    public const string DefaultAdminUserId = "1";

    private readonly SqlSugarScope _dbScope;
    private static readonly ConcurrentDictionary<string, bool> InitializedAgents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim InitializeLock = new(1, 1);
    private static readonly object TableInitializeLock = new();
    private static bool TablesInitialized;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    public AgentToolRegistryService(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
        lock (TableInitializeLock)
        {
            if (TablesInitialized) return;
            Db.CodeFirst.InitTables<
                AgentConversationEntity,
                AgentMessageEntity,
                AgentDefinitionEntity,
                AgentToolRegistryEntity,
                AgentExecutionPlanEntity>();
            Db.CodeFirst.InitTables<AgentInvocationLogEntity>();
            TablesInitialized = true;
        }
    }

    /// <summary>
    /// 确保数据库中存在指定 Agent 的定义和工具清单；不存在则创建，存在则同步最新配置。
    /// </summary>
    public async Task EnsureInitializedAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        if (normalizedCode != ProjectManagementAgentCode) return;
        if (InitializedAgents.ContainsKey(normalizedCode)) return;

        await InitializeLock.WaitAsync();
        try
        {
            if (InitializedAgents.ContainsKey(normalizedCode)) return;

            await BackfillLegacyAgentUserIdsAsync();

            var definition = await Db.Queryable<AgentDefinitionEntity>()
                .FirstAsync(x => x.AgentCode == normalizedCode);
            if (definition == null)
            {
                await Db.Insertable(new AgentDefinitionEntity
                {
                    AgentCode = ProjectManagementAgentCode,
                    Name = "TCL项目管理 Agent 助手",
                    Description = "查询并维护项目、任务、文件和工作台数据。删除类工具不对 Agent 开放。",
                    SystemPrompt =
                        "你是TCL项目管理系统的项目管理 Agent 助手。你必须根据用户意图从可用工具中自主选择最合适的工具。" +
                        "查询真实业务数据时应调用读取工具；新增或修改数据时应调用对应写入工具。" +
                        "不得虚构工具执行结果，不得声称尚未执行的写操作已经完成。" +
                        "系统未提供删除工具，因此不得尝试删除项目、任务或文件。" +
                        "缺少写入必填参数时，直接用自然语言向用户说明缺少的字段，不要编造参数。" +
                        "最终回答使用自然、简洁的中文，不要只返回 JSON。",
                    Enabled = true,
                    MaxToolCalls = 4,
                    Version = "2.0",
                    CreateTime = DateTime.Now,
                    UpdateTime = DateTime.Now
                }).ExecuteCommandAsync();
            }

            foreach (var seed in BuildProjectManagementTools())
            {
                var existing = await Db.Queryable<AgentToolRegistryEntity>()
                    .FirstAsync(x => x.AgentCode == seed.AgentCode && x.ToolName == seed.ToolName);
                if (existing == null)
                {
                    await Db.Insertable(seed).ExecuteCommandAsync();
                    continue;
                }

                // Enabled is deliberately preserved so administrators can disable a seeded tool.
                existing.Title = seed.Title;
                existing.Description = seed.Description;
                existing.Method = seed.Method;
                existing.Route = seed.Route;
                existing.InputSchema = seed.InputSchema;
                existing.OutputSchema = seed.OutputSchema;
                existing.IsWriteAction = seed.IsWriteAction;
                existing.RequiresConfirmation = seed.RequiresConfirmation;
                existing.PermissionKey = seed.PermissionKey;
                existing.RiskLevel = seed.RiskLevel;
                existing.ServiceName = seed.ServiceName;
                existing.ActionName = seed.ActionName;
                existing.Examples = seed.Examples;
                existing.Version = seed.Version;
                existing.Sort = seed.Sort;
                existing.UpdateTime = DateTime.Now;
                await Db.Updateable(existing).ExecuteCommandAsync();
            }

            InitializedAgents[normalizedCode] = true;
        }
        finally
        {
            InitializeLock.Release();
        }
    }

    /// <summary>
    /// 兼容旧数据：早期 Agent 表里没有用户归属时，默认归属到系统管理员账号。
    /// </summary>
    private async Task BackfillLegacyAgentUserIdsAsync()
    {
        await Db.Ado.ExecuteCommandAsync(
            """
            IF OBJECT_ID(N'AgentConversation', N'U') IS NOT NULL
            BEGIN
                UPDATE AgentConversation
                SET UserId = @DefaultAdminUserId
                WHERE UserId IS NULL OR LTRIM(RTRIM(UserId)) = ''
            END

            IF OBJECT_ID(N'AgentExecutionPlan', N'U') IS NOT NULL
               AND OBJECT_ID(N'AgentConversation', N'U') IS NOT NULL
            BEGIN
                UPDATE planTable
                SET planTable.UserId = conversationTable.UserId
                FROM AgentExecutionPlan planTable
                INNER JOIN AgentConversation conversationTable ON planTable.ConversationId = conversationTable.Id
                WHERE planTable.UserId IS NULL OR LTRIM(RTRIM(planTable.UserId)) = ''

                UPDATE AgentExecutionPlan
                SET UserId = @DefaultAdminUserId
                WHERE UserId IS NULL OR LTRIM(RTRIM(UserId)) = ''
            END

            IF OBJECT_ID(N'AgentInvocationLog', N'U') IS NOT NULL
               AND OBJECT_ID(N'AgentExecutionPlan', N'U') IS NOT NULL
            BEGIN
                UPDATE logTable
                SET logTable.UserId = planTable.UserId
                FROM AgentInvocationLog logTable
                INNER JOIN AgentExecutionPlan planTable ON logTable.ExecutionPlanId = planTable.Id
                WHERE logTable.UserId IS NULL OR LTRIM(RTRIM(logTable.UserId)) = ''

                UPDATE AgentInvocationLog
                SET UserId = @DefaultAdminUserId
                WHERE UserId IS NULL OR LTRIM(RTRIM(UserId)) = ''
            END
            """,
            new SugarParameter("@DefaultAdminUserId", DefaultAdminUserId));
    }

    /// <summary>
    /// 查询指定 Agent 的定义信息。
    /// </summary>
    public async Task<AgentDefinitionEntity?> GetAgentAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentDefinitionEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode && x.Enabled);
    }

    /// <summary>
    /// 查询指定 Agent 当前启用的工具，按 Sort 排序后返回。
    /// </summary>
    public async Task<List<AgentToolRegistryEntity>> GetEnabledToolsAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentToolRegistryEntity>()
            .Where(x => x.AgentCode == normalizedCode && x.Enabled)
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    /// <summary>
    /// 查询指定 Agent 的全部工具，包括禁用工具。
    /// </summary>
    public async Task<List<AgentToolRegistryEntity>> GetAllToolsAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentToolRegistryEntity>()
            .Where(x => x.AgentCode == normalizedCode)
            .OrderBy(x => x.Sort)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    /// <summary>
    /// 按 Agent 编码和工具名查询单个工具定义。
    /// </summary>
    public async Task<AgentToolRegistryEntity?> GetToolAsync(string agentCode, string toolName)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentToolRegistryEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode && x.ToolName == toolName);
    }

    /// <summary>
    /// 统一 Agent 编码；为空时使用项目管理 Agent 作为默认值。
    /// </summary>
    public static string NormalizeAgentCode(string? agentCode) =>
        string.IsNullOrWhiteSpace(agentCode) ? ProjectManagementAgentCode : agentCode.Trim().ToLowerInvariant();

    /// <summary>
    /// 构建项目管理 Agent 的内置工具定义，包括查询、写入、状态变更和邮件工具。
    /// </summary>
    private static List<AgentToolRegistryEntity> BuildProjectManagementTools()
    {
        const string objectOutput = """
            {"type":"object","properties":{"success":{"type":"boolean"},"message":{"type":"string"},"data":{}},"required":["success","message"]}
            """;

        return new List<AgentToolRegistryEntity>
        {
            Tool("project.list", "查询项目", "按项目名称、编号、负责人或状态查询项目列表。用户询问有哪些项目、某负责人项目或项目进度时使用。",
                "GET", "GetProjects", ProjectListSchema, objectOutput, false, false, "ProjectLedger", "Low", 10,
                "{\"projectName\":\"示例项目\",\"managerName\":\"王宏\",\"current\":1,\"size\":20}"),
            Tool("project.detail", "项目详情", "按项目 ID、项目编号或项目名称读取单个项目的完整信息。",
                "GET", "GetProject", ProjectIdentitySchema, objectOutput, false, false, "ProjectLedger", "Low", 20,
                "{\"projectCode\":\"KT001\"}"),
            Tool("dashboard.summary", "工作台概况", "读取项目总数、状态分布、平均进度、负责人项目统计和近期项目。",
                "GET", "GetDashboard", EmptySchema, objectOutput, false, false, "ProjectDashboard", "Low", 30, "{}"),
            Tool("task.list", "查询任务", "按项目名称、任务名称、执行人或状态查询任务。",
                "GET", "GetTasks", TaskListSchema, objectOutput, false, false, "TaskBoard", "Low", 40,
                "{\"projectName\":\"示例项目\",\"executorId\":\"10001\"}"),
            Tool("task.detail", "任务详情", "按任务 ID 读取任务详情。",
                "GET", "GetTask", IdSchema, objectOutput, false, false, "TaskBoard", "Low", 50, "{\"id\":12}"),
            Tool("file.list", "查询文件", "按项目 ID、任务 ID、文件夹类型或文件名查询项目文件。",
                "GET", "GetFiles", FileListSchema, objectOutput, false, false, "ProjectFile", "Low", 60,
                "{\"projectId\":2,\"fileName\":\"合同\"}"),

            Tool("project.create", "新增项目", "新增一个或批量新增项目。负责人可使用系统中的用户名、昵称或用户 ID；写入前必须由用户确认。",
                "POST", "CreateProjects", ProjectCreateSchema, objectOutput, true, true, "ProjectLedger_Create", "Medium", 100,
                "{\"projects\":[{\"projectCode\":\"KT001\",\"projectName\":\"示例项目\",\"projectType\":\"验收\",\"managerName\":\"王宏\",\"startTime\":\"2026-07-01\",\"endTime\":\"2026-07-31\"}]}"),
            Tool("project.update", "修改项目", "按 ID、编号或名称修改一个或多个项目。只传需要修改的字段；写入前必须由用户确认。",
                "POST", "UpdateProjects", ProjectUpdateSchema, objectOutput, true, true, "ProjectLedger_Update", "Medium", 110,
                "{\"projects\":[{\"projectCode\":\"KT001\",\"customerName\":\"新客户\"}]}"),
            Tool("project.status", "变更项目状态", "按 ID、编号或名称修改项目状态。状态值：1待启用、2进行中、3延期、4暂停、5已完成、6归档。",
                "POST", "ChangeProjectStatus", ProjectStatusSchema, objectOutput, true, true, "ProjectLedger_Status", "Medium", 120,
                "{\"projects\":[{\"projectCode\":\"KT001\",\"status\":2}]}"),
            Tool("task.create", "新增任务", "在已有项目中新增一个或多个任务。时间必须在所属项目或父任务周期内；写入前必须由用户确认。",
                "POST", "CreateTasks", TaskCreateSchema, objectOutput, true, true, "TaskBoard_Create", "Medium", 130,
                "{\"tasks\":[{\"projectName\":\"示例项目\",\"taskName\":\"开箱\",\"taskType\":\"开箱\",\"startTime\":\"2026-07-01\",\"endTime\":\"2026-07-05\"}]}"),
            Tool("task.update", "修改任务", "按任务 ID 修改一个或多个任务。只传需要修改的字段；写入前必须由用户确认。",
                "POST", "UpdateTasks", TaskUpdateSchema, objectOutput, true, true, "TaskBoard_Update", "Medium", 140,
                "{\"tasks\":[{\"id\":12,\"executorId\":\"10002\"}]}"),
            Tool("task.status", "变更任务状态", "按任务 ID 修改任务状态。状态值：1未开始、2进行中、3已完成、4延期。",
                "POST", "ChangeTaskStatus", TaskStatusSchema, objectOutput, true, true, "TaskBoard_Status", "Medium", 150,
                "{\"tasks\":[{\"id\":12,\"status\":3}]}"),
            Tool("email.send", "发送邮件", "将指定内容发送到收件人并可抄送。发送前必须由用户确认。",
                "POST", "SendEmail", EmailSchema, objectOutput, true, true, "AgentExecutor", "Medium", 200,
                "{\"toEmails\":[\"user@example.com\"],\"ccEmails\":[],\"subject\":\"项目周报\",\"body\":\"正文\"}", "Email")
        };
    }

    private static AgentToolRegistryEntity Tool(
        string name,
        string title,
        string description,
        string method,
        string actionName,
        string inputSchema,
        string outputSchema,
        bool isWrite,
        bool requiresConfirmation,
        string permissionKey,
        string riskLevel,
        int sort,
        string examples,
        string serviceName = "ProjectManagement") => new()
    {
        AgentCode = ProjectManagementAgentCode,
        ToolName = name,
        Title = title,
        Description = description,
        Method = method,
        Route = $"internal://{serviceName}/{actionName}",
        InputSchema = inputSchema,
        OutputSchema = outputSchema,
        IsWriteAction = isWrite,
        RequiresConfirmation = requiresConfirmation,
        Enabled = true,
        PermissionKey = permissionKey,
        RiskLevel = riskLevel,
        ServiceName = serviceName,
        ActionName = actionName,
        Examples = examples,
        Version = "2.0",
        Sort = sort,
        CreateTime = DateTime.Now,
        UpdateTime = DateTime.Now
    };

    private const string EmptySchema = """{"type":"object","properties":{},"additionalProperties":false}""";
    private const string IdSchema = """{"type":"object","properties":{"id":{"type":"integer","minimum":1}},"required":["id"],"additionalProperties":false}""";
    private const string ProjectIdentitySchema = """
        {"type":"object","properties":{"id":{"type":"integer","minimum":1},"projectCode":{"type":"string"},"projectName":{"type":"string"}},"additionalProperties":false}
        """;
    private const string ProjectListSchema = """
        {"type":"object","properties":{"projectName":{"type":"string"},"projectCode":{"type":"string"},"managerName":{"type":"string"},"projectStatus":{"type":"integer","minimum":1,"maximum":6},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string TaskListSchema = """
        {"type":"object","properties":{"projectName":{"type":"string"},"taskName":{"type":"string"},"executorId":{"type":"string"},"taskStatus":{"type":"integer","minimum":1,"maximum":4},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string FileListSchema = """
        {"type":"object","properties":{"projectId":{"type":"integer","minimum":1},"taskId":{"type":"integer","minimum":1},"folderType":{"type":"string"},"fileName":{"type":"string"},"current":{"type":"integer","minimum":1},"size":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """;
    private const string ProjectCreateSchema = """
        {"type":"object","properties":{"updateIfExists":{"type":"boolean"},"projects":{"type":"array","minItems":1,"items":{"type":"object","properties":{"projectCode":{"type":"string","minLength":1},"projectName":{"type":"string","minLength":1},"projectType":{"type":"string"},"projectTag":{"type":"string"},"priority":{"type":"integer","minimum":1,"maximum":3},"projectStage":{"type":"integer","minimum":1,"maximum":3},"projectStatus":{"type":"integer","minimum":1,"maximum":6},"startTime":{"type":"string","format":"date"},"endTime":{"type":"string","format":"date"},"managerId":{"type":"string"},"managerName":{"type":"string"},"customerName":{"type":"string"},"contractCode":{"type":"string"},"remark":{"type":"string"}},"required":["projectCode","projectName","startTime","endTime"]}}},"required":["projects"],"additionalProperties":false}
        """;
    private const string ProjectUpdateSchema = """
        {"type":"object","properties":{"projects":{"type":"array","minItems":1,"items":{"type":"object","properties":{"id":{"type":"integer","minimum":1},"projectCode":{"type":"string"},"projectName":{"type":"string"},"newProjectCode":{"type":"string"},"newProjectName":{"type":"string"},"projectType":{"type":"string"},"projectTag":{"type":"string"},"priority":{"type":"integer","minimum":1,"maximum":3},"projectStage":{"type":"integer","minimum":1,"maximum":3},"projectStatus":{"type":"integer","minimum":1,"maximum":6},"startTime":{"type":"string","format":"date"},"endTime":{"type":"string","format":"date"},"managerId":{"type":"string"},"managerName":{"type":"string"},"customerName":{"type":"string"},"contractCode":{"type":"string"},"remark":{"type":"string"}}}}},"required":["projects"],"additionalProperties":false}
        """;
    private const string ProjectStatusSchema = """
        {"type":"object","properties":{"projects":{"type":"array","minItems":1,"items":{"type":"object","properties":{"id":{"type":"integer","minimum":1},"projectCode":{"type":"string"},"projectName":{"type":"string"},"status":{"type":"integer","minimum":1,"maximum":6}},"required":["status"]}}},"required":["projects"],"additionalProperties":false}
        """;
    private const string TaskCreateSchema = """
        {"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"projectName":{"type":"string","minLength":1},"parentId":{"type":"integer","minimum":0},"taskName":{"type":"string","minLength":1},"taskType":{"type":"string","enum":["开箱","启用","预验收","验收"]},"priority":{"type":"integer","minimum":1,"maximum":3},"taskStatus":{"type":"integer","minimum":1,"maximum":4},"executorId":{"type":"string"},"startTime":{"type":"string","format":"date"},"endTime":{"type":"string","format":"date"},"sort":{"type":"integer"},"remark":{"type":"string"}},"required":["projectName","taskName","startTime","endTime"]}}},"required":["tasks"],"additionalProperties":false}
        """;
    private const string TaskUpdateSchema = """
        {"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"id":{"type":"integer","minimum":1},"projectName":{"type":"string"},"parentId":{"type":"integer","minimum":0},"taskName":{"type":"string"},"taskType":{"type":"string","enum":["开箱","启用","预验收","验收"]},"priority":{"type":"integer","minimum":1,"maximum":3},"taskStatus":{"type":"integer","minimum":1,"maximum":4},"executorId":{"type":"string"},"startTime":{"type":"string","format":"date"},"endTime":{"type":"string","format":"date"},"sort":{"type":"integer"},"remark":{"type":"string"}},"required":["id"]}}},"required":["tasks"],"additionalProperties":false}
        """;
    private const string TaskStatusSchema = """
        {"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"id":{"type":"integer","minimum":1},"status":{"type":"integer","minimum":1,"maximum":4}},"required":["id","status"]}}},"required":["tasks"],"additionalProperties":false}
        """;
    private const string EmailSchema = """
        {"type":"object","properties":{"toEmails":{"type":"array","minItems":1,"items":{"type":"string"}},"ccEmails":{"type":"array","items":{"type":"string"}},"subject":{"type":"string","minLength":1},"body":{"type":"string","minLength":1}},"required":["toEmails","subject","body"],"additionalProperties":false}
        """;
}
