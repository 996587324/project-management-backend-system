using System.Collections.Concurrent;
using AutoMapper;
using Interface;
using Model.Agent;
using SqlSugar;

namespace Service;

/// <summary>
/// Agent 工具注册服务，通过 IOC 自动发现模块提供器和独立工具动作，并同步到数据库注册表。
/// </summary>
public class AgentToolRegistryService : IAgentToolRegistryService
{
    public const string ProjectManagementAgentCode = AgentCodes.ProjectManagement;
    public const string DefaultAdminUserId = "1";

    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IReadOnlyDictionary<string, IAgentModuleProvider> _modules;
    private readonly IReadOnlyCollection<IAgentToolAction> _toolActions;
    private static readonly ConcurrentDictionary<string, bool> InitializedAgents = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim InitializeLock = new(1, 1);
    private static readonly object TableInitializeLock = new();
    private static bool TablesInitialized;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    public AgentToolRegistryService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IEnumerable<IAgentModuleProvider> modules,
        IEnumerable<IAgentToolAction> toolActions)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _modules = modules
            .GroupBy(x => NormalizeAgentCode(x.Module.AgentCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
        _toolActions = toolActions.ToList();

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
    /// 同步指定 Agent 的定义和工具配置。数据库中的启停状态会保留，代码只更新元数据和 Schema。
    /// </summary>
    public async Task EnsureInitializedAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        if (!_modules.TryGetValue(normalizedCode, out var module)) return;
        if (InitializedAgents.ContainsKey(normalizedCode)) return;

        await InitializeLock.WaitAsync();
        try
        {
            if (InitializedAgents.ContainsKey(normalizedCode)) return;

            await BackfillLegacyAgentUserIdsAsync();
            await SyncAgentDefinitionAsync(module.Module, normalizedCode);

            var actionTools = _toolActions
                .Select(x => x.Definition)
                .Where(x => NormalizeAgentCode(x.AgentCode) == normalizedCode);
            var toolSeeds = module.Tools.Concat(actionTools).ToList();
            var duplicateTool = toolSeeds
                .GroupBy(x => x.ToolName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicateTool != null)
                throw new InvalidOperationException($"Agent {normalizedCode} 存在重复工具：{duplicateTool.Key}");

            foreach (var seed in toolSeeds)
                await SyncToolAsync(seed, normalizedCode);

            InitializedAgents[normalizedCode] = true;
        }
        finally
        {
            InitializeLock.Release();
        }
    }

    /// <summary>新增或更新 Agent 定义，同时保留管理员在数据库设置的启停状态。</summary>
    private async Task SyncAgentDefinitionAsync(AgentModuleSeed seed, string normalizedCode)
    {
        var existing = await Db.Queryable<AgentDefinitionEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode);
        if (existing == null)
        {
            var entity = _mapper.Map<AgentDefinitionEntity>(seed);
            entity.AgentCode = normalizedCode;
            entity.CreateTime = DateTime.Now;
            entity.UpdateTime = DateTime.Now;
            await Db.Insertable(entity).ExecuteCommandAsync();
            return;
        }

        var enabled = existing.Enabled;
        var createTime = existing.CreateTime;
        _mapper.Map(seed, existing);
        existing.AgentCode = normalizedCode;
        existing.Enabled = enabled;
        existing.CreateTime = createTime;
        existing.UpdateTime = DateTime.Now;
        await Db.Updateable(existing).ExecuteCommandAsync();
    }

    /// <summary>新增或更新工具注册信息，同时保留管理员在数据库设置的启停状态。</summary>
    private async Task SyncToolAsync(AgentToolSeed seed, string normalizedCode)
    {
        var existing = await Db.Queryable<AgentToolRegistryEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode && x.ToolName == seed.ToolName);
        if (existing == null)
        {
            var entity = _mapper.Map<AgentToolRegistryEntity>(seed);
            entity.AgentCode = normalizedCode;
            entity.CreateTime = DateTime.Now;
            entity.UpdateTime = DateTime.Now;
            await Db.Insertable(entity).ExecuteCommandAsync();
            return;
        }

        var enabled = existing.Enabled;
        var createTime = existing.CreateTime;
        _mapper.Map(seed, existing);
        existing.AgentCode = normalizedCode;
        existing.Enabled = enabled;
        existing.CreateTime = createTime;
        existing.UpdateTime = DateTime.Now;
        await Db.Updateable(existing).ExecuteCommandAsync();
    }

    /// <summary>兼容旧数据：早期 Agent 表里没有用户归属时，默认归属到系统管理员账号。</summary>
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

    /// <summary>查询指定 Agent 的已启用定义。</summary>
    public async Task<AgentDefinitionEntity?> GetAgentAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentDefinitionEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode && x.Enabled);
    }

    /// <summary>查询指定 Agent 当前启用的工具。</summary>
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

    /// <summary>查询指定 Agent 的全部工具，包括已禁用工具。</summary>
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

    /// <summary>按 Agent 编码和工具名查询单个工具定义。</summary>
    public async Task<AgentToolRegistryEntity?> GetToolAsync(string agentCode, string toolName)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        await EnsureInitializedAsync(normalizedCode);
        return await Db.Queryable<AgentToolRegistryEntity>()
            .FirstAsync(x => x.AgentCode == normalizedCode && x.ToolName == toolName);
    }

    /// <summary>从模块配置中读取建议问题；未知 Agent 返回空集合。</summary>
    public Task<List<string>> GetSuggestionsAsync(string agentCode)
    {
        var normalizedCode = NormalizeAgentCode(agentCode);
        var suggestions = _modules.TryGetValue(normalizedCode, out var module)
            ? module.Module.Suggestions.ToList()
            : new List<string>();
        return Task.FromResult(suggestions);
    }

    /// <summary>统一 Agent 编码；为空时保持项目管理 Agent 的兼容默认值。</summary>
    public static string NormalizeAgentCode(string? agentCode) =>
        string.IsNullOrWhiteSpace(agentCode)
            ? AgentCodes.ProjectManagement
            : agentCode.Trim().ToLowerInvariant();
}
