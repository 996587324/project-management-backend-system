using Interface;
using Model.Agent;
using SqlSugar;

namespace Service;

/// <summary>
/// 集中创建模型平台、Agent 绑定和模型调用日志表，并维护系统统一配置所需索引。
/// </summary>
public sealed class AgentModelDatabaseInitializer : IAgentModelDatabaseInitializer
{
    private readonly SqlSugarScope _dbScope;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    public AgentModelDatabaseInitializer(SqlSugarScope dbScope)
    {
        _dbScope = dbScope;
    }

    /// <summary>只初始化一次表结构、用户归属字段和查询索引。</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            cancellationToken.ThrowIfCancellationRequested();

            // 旧版本的三张表已存在但没有 OwnerUserId。必须先补列，再让 SqlSugar 按新实体校验表结构；
            // 否则 InitTables 会先因为实体字段与旧表不一致而失败，无法走到后续迁移逻辑。
            await EnsureUserScopeColumnsAsync();
            Db.CodeFirst.InitTables<
                AgentModelProviderEntity,
                AgentModelBindingEntity,
                AgentModelInvocationLogEntity>();
            await EnsureIndexesAsync();
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>确保系统级模型配置中始终存在可编辑的 Turing、DeepSeek 预置项，不写入任何密钥也不会自动启用。</summary>
    public async Task EnsureSystemProviderPresetsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await EnsureSystemProviderPresetAsync(
            "turing",
            "Turing 中转平台",
            "https://live-turing.cn.llm.tcljd.com/api/v1",
            string.Empty,
            "Turing OpenAI Chat Completions 兼容中转平台；请填写已开通的模型编码和 API Key 后启用。",
            cancellationToken);
        await EnsureSystemProviderPresetAsync(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com",
            "deepseek-chat",
            "DeepSeek OpenAI Chat Completions 兼容接口；请填写 API Key 后启用。",
            cancellationToken);
    }

    private async Task EnsureSystemProviderPresetAsync(
        string providerCode,
        string providerName,
        string baseUrl,
        string model,
        string remark,
        CancellationToken cancellationToken)
    {
        var exists = await Db.Queryable<AgentModelProviderEntity>()
            .AnyAsync(x => x.OwnerUserId == AgentModelConfigurationScope.SystemOwnerUserId &&
                           x.ProviderCode == providerCode && x.IsDelete == 0);
        if (exists) return;

        try
        {
            await Db.Insertable(new AgentModelProviderEntity
            {
                OwnerUserId = AgentModelConfigurationScope.SystemOwnerUserId,
                ProviderCode = providerCode,
                ProviderName = providerName,
                ProtocolType = "OpenAIChatCompletions",
                BaseUrl = baseUrl,
                EndpointPath = "/chat/completions",
                ApiKeyHeader = "Authorization",
                Model = model,
                Temperature = 0.2,
                MaxTokens = 1200,
                ClientTimeoutSeconds = 90,
                PlatformTimeoutSeconds = 75,
                MaxRetries = 1,
                Enabled = false,
                Remark = remark,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
        catch
        {
            var created = await Db.Queryable<AgentModelProviderEntity>()
                .AnyAsync(x => x.OwnerUserId == AgentModelConfigurationScope.SystemOwnerUserId &&
                               x.ProviderCode == providerCode && x.IsDelete == 0);
            if (!created) throw;
        }
    }

    /// <summary>为升级前的表补充归属列；运行配置固定使用系统归属标识。</summary>
    private async Task EnsureUserScopeColumnsAsync()
    {
        await Db.Ado.ExecuteCommandAsync(
            """
            IF OBJECT_ID(N'AgentModelProvider', N'U') IS NOT NULL AND COL_LENGTH(N'AgentModelProvider', N'OwnerUserId') IS NULL
                ALTER TABLE AgentModelProvider ADD OwnerUserId nvarchar(64) NOT NULL CONSTRAINT DF_AgentModelProvider_OwnerUserId DEFAULT N'';

            IF OBJECT_ID(N'AgentModelBinding', N'U') IS NOT NULL AND COL_LENGTH(N'AgentModelBinding', N'OwnerUserId') IS NULL
                ALTER TABLE AgentModelBinding ADD OwnerUserId nvarchar(64) NOT NULL CONSTRAINT DF_AgentModelBinding_OwnerUserId DEFAULT N'';

            IF OBJECT_ID(N'AgentModelInvocationLog', N'U') IS NOT NULL AND COL_LENGTH(N'AgentModelInvocationLog', N'OwnerUserId') IS NULL
                ALTER TABLE AgentModelInvocationLog ADD OwnerUserId nvarchar(64) NOT NULL CONSTRAINT DF_AgentModelInvocationLog_OwnerUserId DEFAULT N'';
            """);
    }

    /// <summary>创建系统统一平台、绑定和日志索引，并替换旧的全局唯一索引。</summary>
    private async Task EnsureIndexesAsync()
    {
        await Db.Ado.ExecuteCommandAsync(
            """
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AgentModelProvider_Code' AND object_id = OBJECT_ID(N'AgentModelProvider'))
                DROP INDEX UX_AgentModelProvider_Code ON AgentModelProvider;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AgentModelProvider_OwnerCode' AND object_id = OBJECT_ID(N'AgentModelProvider'))
                CREATE UNIQUE INDEX UX_AgentModelProvider_OwnerCode ON AgentModelProvider(OwnerUserId, ProviderCode) WHERE IsDelete = 0;

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AgentModelBinding_AgentProvider' AND object_id = OBJECT_ID(N'AgentModelBinding'))
                DROP INDEX UX_AgentModelBinding_AgentProvider ON AgentModelBinding;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_AgentModelBinding_OwnerAgentProvider' AND object_id = OBJECT_ID(N'AgentModelBinding'))
                CREATE UNIQUE INDEX UX_AgentModelBinding_OwnerAgentProvider ON AgentModelBinding(OwnerUserId, AgentCode, ProviderId) WHERE IsDelete = 0;

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AgentModelBinding_Resolve' AND object_id = OBJECT_ID(N'AgentModelBinding'))
                DROP INDEX IX_AgentModelBinding_Resolve ON AgentModelBinding;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AgentModelBinding_OwnerResolve' AND object_id = OBJECT_ID(N'AgentModelBinding'))
                CREATE INDEX IX_AgentModelBinding_OwnerResolve ON AgentModelBinding(OwnerUserId, AgentCode, Enabled, IsPrimary, Priority) INCLUDE (ProviderId, IsDelete);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AgentModelInvocationLog_AgentTime' AND object_id = OBJECT_ID(N'AgentModelInvocationLog'))
                CREATE INDEX IX_AgentModelInvocationLog_AgentTime ON AgentModelInvocationLog(AgentCode, CreateTime DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AgentModelInvocationLog_TraceId' AND object_id = OBJECT_ID(N'AgentModelInvocationLog'))
                CREATE INDEX IX_AgentModelInvocationLog_TraceId ON AgentModelInvocationLog(TraceId) WHERE TraceId IS NOT NULL;
            """);
    }

}
