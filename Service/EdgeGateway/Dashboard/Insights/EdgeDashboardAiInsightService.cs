using System.Text.Json;
using System.Text.Json.Nodes;
using Interface;
using Microsoft.Extensions.Logging;
using Model.Agent;
using Model.EdgeGateway;
using SqlSugar;

namespace Service;

/// <summary>把当前看板真实数据整理为受控摘要，并定时生成只读 AI 运行建议。</summary>
public sealed class EdgeDashboardAiInsightService : IEdgeDashboardAiInsightService
{
    private const string ProductionDashboardCode = "production-dashboard";
    private const string DeviceCardDashboardCode = "device-card-dashboard";
    private static readonly TimeSpan InsightLifetime = TimeSpan.FromMinutes(6);
    // 单条建议请求不得长期占用后台轮询；超时后隐藏当轮建议，下一周期会自动重试。
    private static readonly TimeSpan ModelRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly SemaphoreSlim StorageGate = new(1, 1);
    private static bool _storageReady;

    private readonly SqlSugarScope _dbScope;
    private readonly IEdgeDashboardService _dashboardService;
    private readonly IAgentModelClient _modelClient;
    private readonly ILogger<EdgeDashboardAiInsightService> _logger;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public EdgeDashboardAiInsightService(
        SqlSugarScope dbScope,
        IEdgeDashboardService dashboardService,
        IAgentModelClient modelClient,
        ILogger<EdgeDashboardAiInsightService> logger)
    {
        _dbScope = dbScope;
        _dashboardService = dashboardService;
        _modelClient = modelClient;
        _logger = logger;
    }

    public async Task<EdgeDashboardAiInsightOutputDto?> GetLatestAsync(string dashboardCode, string lineCode)
    {
        dashboardCode = Require(dashboardCode, "看板编码");
        lineCode = Require(lineCode, "线体编码");
        await EnsureStorageAsync();
        var now = DateTime.Now;
        var entity = await Db.Queryable<EdgeDashboardAiInsightEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.LineCode == lineCode &&
                        x.IsActive && x.ExpiresAt > now)
            .OrderByDescending(x => x.GeneratedAt)
            .FirstAsync();
        return entity == null ? null : ToOutput(entity);
    }

    public async Task<int> RefreshInsightsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        if (!await RefreshGate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            await EnsureStorageAsync();
            var bootstrap = await _dashboardService.GetBootstrapAsync(ProductionDashboardCode, migrateLegacyScenes: false);
            var generated = 0;
            foreach (var line in bootstrap.Lines.Where(x => x.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await RefreshProductionLineAsync(line.LineCode, now, cancellationToken)) generated++;
                if (await RefreshDeviceCardLineAsync(line.LineCode, now, cancellationToken)) generated++;
            }
            return generated;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private async Task<bool> RefreshProductionLineAsync(string lineCode, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await _dashboardService.GetLineRuntimeAsync(lineCode);
            var today = now.Date;
            var statistics = await _dashboardService.GetDailyStatisticsAsync(new EdgeDashboardDailyStatisticQueryDto
            {
                Current = 1,
                Size = 500,
                LineCode = lineCode,
                DateFrom = today,
                DateTo = today
            });
            var faults = await _dashboardService.GetFaultRecordsAsync(new EdgeDashboardFaultQueryDto
            {
                Current = 1,
                Size = 100,
                LineCode = lineCode,
                Status = "Active"
            });
            var lineOutput = await _dashboardService.GetLineOutputAsync(lineCode);

            var summary = new
            {
                generatedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                lineCode,
                dataCoverage = new
                {
                    totalRobots = runtime.Robots.Count,
                    onlineRobots = runtime.Robots.Count(x => x.DataOnline),
                    offlineRobots = runtime.Robots.Where(x => !x.DataOnline).Select(x => x.RobotName).Take(20).ToList()
                },
                currentStates = runtime.Robots
                    .GroupBy(x => x.Status)
                    .ToDictionary(x => x.Key, x => x.Count()),
                lineOutput = new
                {
                    lineOutput.Configured,
                    lineOutput.FieldName,
                    lineOutput.TodayQuantity,
                    lineOutput.Unit,
                    lineOutput.CurrentValue,
                    lineOutput.LatestIncrease,
                    lineOutput.DataOnline,
                    lineOutput.DataTime
                },
                dailyStatistics = statistics.Records.Select(x => new
                {
                    x.RobotName,
                    runningMinutes = Math.Round(x.RunningSeconds / 60m, 1),
                    idleMinutes = Math.Round(x.IdleSeconds / 60m, 1),
                    faultMinutes = Math.Round(x.FaultSeconds / 60m, 1),
                    x.AvailabilityRate,
                    x.FaultCount,
                    x.LastState
                }).Take(100).ToList(),
                activeFaults = faults.Records.Select(x => new
                {
                    x.RobotName,
                    x.FaultCode,
                    x.FaultReason,
                    x.Severity,
                    durationMinutes = Math.Round(x.DurationSeconds / 60m, 1)
                }).Take(30).ToList()
            };
            var reply = await GenerateInsightReplyAsync(new AgentModelRequest
            {
                AgentCode = ProductionDashboardCode,
                UserId = AgentModelConfigurationScope.SystemOwnerUserId,
                SystemPrompt = """
                    你是生产线机械手运行分析助手。只根据提供的看板数据做判断，不得补充未提供的事实，不得执行或建议自动控制设备。
                    设备在线状态、实时状态、当日统计和故障记录都是分析输入；即使设备离线或没有实时状态，也要结合其余已提供数据给出可执行的巡检、排查或运行建议，不能因为设备离线而直接拒绝分析。
                    只要能根据任一已提供数据得出可靠结论，就返回严格 JSON：
                    {"show":true,"level":"Info|Warning|Critical","content":"不超过90字的中文建议","evidence":"不超过120字的数据依据","affectedRobots":["机械手名称"]}
                    仅当模型调用本身无法完成、返回内容无法满足 JSON 格式，或所有数据都为空且确实无法得出任何结论时，返回：{"show":false}
                    不要输出 Markdown、解释文字或代码围栏。
                    """,
                UserMessage = JsonSerializer.Serialize(summary)
            }, cancellationToken);
            if (!reply.Success || !TryParseInsight(reply.Content, out var insight))
            {
                await DeactivateLineAsync(ProductionDashboardCode, lineCode, now);
                return false;
            }

            await SaveInsightAsync(ProductionDashboardCode, lineCode, insight, reply.Provider, reply.Model, now);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DeactivateLineAsync(ProductionDashboardCode, lineCode, now);
            _logger.LogWarning(ex, "Production dashboard AI insight was hidden because generation failed for line {LineCode}.", lineCode);
            return false;
        }
    }

    /// <summary>
    /// 设备卡片只发送配置为 AI 分析的展示字段、状态、数据时效和当天变化次数；不传完整原始报文。
    /// </summary>
    private async Task<bool> RefreshDeviceCardLineAsync(string lineCode, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var cards = await _dashboardService.GetDeviceCardsAsync(lineCode);
            var runtime = await _dashboardService.GetDeviceCardBoardRuntimeAsync(lineCode);
            if (cards.Count == 0 || runtime.Cards.Count == 0)
            {
                await DeactivateLineAsync(DeviceCardDashboardCode, lineCode, now);
                return false;
            }

            var fieldsByCard = cards.ToDictionary(
                card => card.Id,
                card => card.Fields.Where(field => field.Enabled && field.AiAnalysisEnabled)
                    .ToDictionary(field => field.FieldCode, StringComparer.OrdinalIgnoreCase));
            var deviceSnapshot = runtime.Cards.Select(card => new
            {
                card.CardName,
                card.DeviceMark,
                card.Status,
                card.DataOnline,
                card.DataTime,
                card.ReceiveTime,
                card.TodayChangeCount,
                analysisFields = card.Fields
                    .Where(field => fieldsByCard.TryGetValue(card.CardId, out var configured) &&
                                    configured.ContainsKey(field.FieldCode))
                    .Select(field => new { field.DisplayName, field.DisplayValue, field.Unit })
                    .Take(12)
                    .ToList()
            }).Take(100).ToList();
            var summary = new
            {
                generatedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                lineCode,
                overview = runtime.Overview,
                devices = deviceSnapshot
            };
            var reply = await GenerateInsightReplyAsync(new AgentModelRequest
            {
                AgentCode = ProductionDashboardCode,
                UserId = AgentModelConfigurationScope.SystemOwnerUserId,
                SystemPrompt = """
                    你是设备卡片看板运行分析助手。只根据给出的设备状态、数据接收时间、字段变更和已授权展示字段分析，不得补充未提供的事实，不得执行或建议自动控制设备。
                    可以针对离线、故障、告警、字段频繁变化、数据异常或整体运行风险提出巡检与排查建议。即使部分设备离线，也应结合其余可靠数据分析。
                    只要能得出可靠结论，返回严格 JSON：
                    {"show":true,"level":"Info|Warning|Critical","content":"不超过90字的中文建议","evidence":"不超过120字的数据依据","affectedRobots":["设备名称或设备标识"]}
                    仅当模型调用无法完成、返回内容不符合 JSON，或所有输入均为空且无法得出结论时，返回：{"show":false}
                    不要输出 Markdown、解释文字或代码围栏。
                    """,
                UserMessage = JsonSerializer.Serialize(summary)
            }, cancellationToken);
            if (!reply.Success || !TryParseInsight(reply.Content, out var insight))
            {
                await DeactivateLineAsync(DeviceCardDashboardCode, lineCode, now);
                return false;
            }

            await SaveInsightAsync(DeviceCardDashboardCode, lineCode, insight, reply.Provider, reply.Model, now);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DeactivateLineAsync(DeviceCardDashboardCode, lineCode, now);
            _logger.LogWarning(ex, "Device-card dashboard AI insight was hidden because generation failed for line {LineCode}.", lineCode);
            return false;
        }
    }

    /// <summary>为一条看板建议创建独立超时，避免模型缓慢或不可用拖住整个后台周期。</summary>
    private async Task<AgentModelReply> GenerateInsightReplyAsync(
        AgentModelRequest request,
        CancellationToken stoppingToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        requestCancellation.CancelAfter(ModelRequestTimeout);
        return await _modelClient.GenerateReplyAsync(request, requestCancellation.Token);
    }

    private async Task SaveInsightAsync(
        string dashboardCode,
        string lineCode,
        ParsedInsight insight,
        string? provider,
        string? model,
        DateTime now)
    {
        var current = await Db.Queryable<EdgeDashboardAiInsightEntity>()
            .Where(x => x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsActive)
            .OrderByDescending(x => x.GeneratedAt)
            .FirstAsync();
        var robotsJson = JsonSerializer.Serialize(insight.AffectedRobots);
        if (current != null && string.Equals(current.Content, insight.Content, StringComparison.Ordinal) &&
            string.Equals(current.Level, insight.Level, StringComparison.OrdinalIgnoreCase))
        {
            current.Evidence = insight.Evidence;
            current.AffectedRobotsJson = robotsJson;
            current.GeneratedAt = now;
            current.ExpiresAt = now.Add(InsightLifetime);
            current.Provider = Limit(provider, 64);
            current.Model = Limit(model, 128);
            current.UpdateTime = now;
            await Db.Updateable(current).ExecuteCommandAsync();
            return;
        }

        await DeactivateLineAsync(dashboardCode, lineCode, now);
        await Db.Insertable(new EdgeDashboardAiInsightEntity
        {
            DashboardCode = dashboardCode,
            LineCode = lineCode,
            Level = insight.Level,
            Content = insight.Content,
            Evidence = insight.Evidence,
            AffectedRobotsJson = robotsJson,
            GeneratedAt = now,
            ExpiresAt = now.Add(InsightLifetime),
            Provider = Limit(provider, 64),
            Model = Limit(model, 128),
            IsActive = true,
            CreateTime = now,
            UpdateTime = now
        }).ExecuteCommandAsync();
    }

    private async Task DeactivateLineAsync(string dashboardCode, string lineCode, DateTime now)
    {
        await EnsureStorageAsync();
        await Db.Updateable<EdgeDashboardAiInsightEntity>()
            .SetColumns(x => new EdgeDashboardAiInsightEntity { IsActive = false, UpdateTime = now })
            .Where(x => x.DashboardCode == dashboardCode && x.LineCode == lineCode && x.IsActive)
            .ExecuteCommandAsync();
    }

    private static bool TryParseInsight(string? content, out ParsedInsight insight)
    {
        insight = default!;
        try
        {
            var json = JsonNode.Parse(content?.Trim() ?? string.Empty)?.AsObject();
            if (json?["show"]?.GetValue<bool>() != true) return false;
            var message = Limit(json["content"]?.GetValue<string>(), 512);
            if (string.IsNullOrWhiteSpace(message)) return false;
            var level = json["level"]?.GetValue<string>()?.Trim();
            level = level is "Critical" or "Warning" or "Info" ? level : "Info";
            var robots = json["affectedRobots"]?.AsArray()
                .Select(x => Limit(x?.GetValue<string>(), 128))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? [];
            insight = new ParsedInsight(level, message, Limit(json["evidence"]?.GetValue<string>(), 1024), robots);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static EdgeDashboardAiInsightOutputDto ToOutput(EdgeDashboardAiInsightEntity entity)
    {
        List<string> robots;
        try { robots = JsonSerializer.Deserialize<List<string>>(entity.AffectedRobotsJson ?? "[]") ?? []; }
        catch { robots = []; }
        return new EdgeDashboardAiInsightOutputDto
        {
            Id = entity.Id,
            DashboardCode = entity.DashboardCode,
            LineCode = entity.LineCode,
            Level = entity.Level,
            Content = entity.Content,
            Evidence = entity.Evidence,
            AffectedRobots = robots,
            GeneratedAt = entity.GeneratedAt,
            ExpiresAt = entity.ExpiresAt
        };
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name}不能为空") : value.Trim();

    private static string? Limit(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized[..Math.Min(normalized.Length, maxLength)];
    }

    private async Task EnsureStorageAsync()
    {
        if (_storageReady) return;
        await StorageGate.WaitAsync();
        try
        {
            if (_storageReady) return;
            using var initializationDb = Db.CopyNew();
            initializationDb.CodeFirst.InitTables<EdgeDashboardAiInsightEntity>();
            _storageReady = true;
        }
        finally
        {
            StorageGate.Release();
        }
    }

    private sealed record ParsedInsight(string Level, string Content, string? Evidence, List<string> AffectedRobots);
}
