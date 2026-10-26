using System.Text.Json;
using AutoMapper;
using Interface;
using Model.Agent;
using Model.ProjectManage;
using SqlSugar;

namespace Service;

/// <summary>
/// Agent 应用服务，负责编排会话、框架运行、工具调用、确认执行和最终回复。
/// </summary>
public class AgentService : IAgentService
{
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IAgentToolRegistryService _registry;
    private readonly IAgentToolDispatcher _dispatcher;
    private readonly IAgentFrameworkRuntime _frameworkRuntime;
    private readonly IEmailService _emailService;
    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public AgentService(
        SqlSugarScope dbScope,
        IMapper mapper,
        IAgentToolRegistryService registry,
        IAgentToolDispatcher dispatcher,
        IAgentFrameworkRuntime frameworkRuntime,
        IEmailService emailService)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _registry = registry;
        _dispatcher = dispatcher;
        _frameworkRuntime = frameworkRuntime;
        _emailService = emailService;
    }

    /// <summary>
    /// 分页查询会话列表，用户为空时返回全部会话。
    /// </summary>
    public async Task<PagedResultDto<AgentConversationDto>> GetConversationPageAsync(
        string? userId,
        int current = 1,
        int size = 20,
        string? agentCode = null)
    {
        var ownerUserId = RequireUserId(userId);
        var normalizedAgentCode = AgentToolRegistryService.NormalizeAgentCode(agentCode);
        await _registry.EnsureInitializedAsync(normalizedAgentCode);
        var page = Math.Max(current, 1);
        var pageSize = Math.Clamp(size, 1, 100);
        RefAsync<int> total = 0;
        var list = await Db.Queryable<AgentConversationEntity>()
            .Where(x => x.UserId == ownerUserId && x.AgentCode == normalizedAgentCode)
            .OrderByDescending(x => x.UpdateTime)
            .OrderByDescending(x => x.Id)
            .ToPageListAsync(page, pageSize, total);

        return new PagedResultDto<AgentConversationDto>
        {
            Records = _mapper.Map<List<AgentConversationDto>>(list),
            Current = page,
            Size = pageSize,
            Total = total
        };
    }

    /// <summary>
    /// 查询会话详情，同时带出历史消息和工具调用记录。
    /// </summary>
    public async Task<AgentConversationDetailDto?> GetConversationAsync(int id, string? userId)
    {
        var ownerUserId = RequireUserId(userId);
        var conversation = await Db.Queryable<AgentConversationEntity>()
            .FirstAsync(x => x.Id == id && x.UserId == ownerUserId);
        if (conversation == null) return null;

        var messages = await Db.Queryable<AgentMessageEntity>()
            .Where(x => x.ConversationId == id)
            .OrderBy(x => x.CreateTime)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return new AgentConversationDetailDto
        {
            Conversation = _mapper.Map<AgentConversationDto>(conversation),
            Messages = _mapper.Map<List<AgentMessageDto>>(messages),
            ToolCalls = await _dispatcher.GetConversationExecutionsAsync(id)
        };
    }

    /// <summary>
    /// 创建新的 Agent 会话，并初始化对应 Agent 的工具注册表。
    /// </summary>
    public async Task<AgentConversationDto> CreateConversationAsync(AgentStartRequest request)
    {
        var ownerUserId = RequireUserId(request.UserId);
        var interactionMode = NormalizeInteractionMode(request.Mode);
        var agentCode = ResolveAgentCode(interactionMode, request.AgentCode);
        var agent = await _registry.GetAgentAsync(agentCode)
                    ?? throw new InvalidOperationException($"Agent {agentCode} 不存在或已禁用");
        var entity = new AgentConversationEntity
        {
            AgentCode = agent.AgentCode,
            UserId = ownerUserId,
            Title = LimitText(string.IsNullOrWhiteSpace(request.Title) ? "Agent 会话" : request.Title.Trim(), 128),
            ScopeProjectName = EmptyToNull(request.ScopeProjectName),
            Mode = interactionMode,
            Status = "Active",
            CreateTime = DateTime.Now,
            UpdateTime = DateTime.Now
        };
        entity.Id = await Db.Insertable(entity).ExecuteReturnIdentityAsync();
        return _mapper.Map<AgentConversationDto>(entity);
    }

    /// <summary>
    /// 处理用户消息：保存消息、让模型选择工具、执行工具，再生成最终回复。
    /// </summary>
    public async Task<AgentResponseDto> SendMessageAsync(
        AgentSendRequest request,
        Func<AgentProgressDto, Task>? progress = null)
    {
        request.UserId = RequireUserId(request.UserId);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new InvalidOperationException("消息不能为空");

        var interactionMode = NormalizeInteractionMode(request.Mode);
        request.Mode = interactionMode;
        var agentCode = ResolveAgentCode(interactionMode, request.AgentCode);
        request.AgentCode = agentCode;
        var modelUserMessage = BuildModelUserMessage(request);
        await ReportProgressAsync(progress, "preparing", "正在准备 Agent 会话");
        var isProjectCsv = interactionMode == "Work" && agentCode == AgentCodes.ProjectManagement && IsProjectCsvRequest(request);
        var agent = await _registry.GetAgentAsync(agentCode)
                    ?? throw new InvalidOperationException($"Agent {agentCode} 不存在或已禁用");
        var conversation = await GetOrCreateConversationAsync(request, agent.AgentCode);
        await ReportProgressAsync(progress, "conversation_ready", "会话已准备，正在读取上下文", conversation.Id);
        var history = await GetRecentMessagesAsync(conversation.Id);
        await AddMessageAsync(conversation.Id, "user", request.Message.Trim());

        await ReportProgressAsync(progress, "checking_confirmation", "正在检查待确认的执行计划", conversation.Id);
        var confirmationResponse = await TryConfirmFromConversationAsync(conversation, request);
        if (confirmationResponse != null)
        {
            await ReportProgressAsync(progress, "saving_result", "执行完成，正在保存结果", conversation.Id);
            return confirmationResponse;
        }

        // 聊天模式从后端强制路由到无工具的 chat Agent，绝不读取工具目录、选择工具或执行操作。
        if (interactionMode == "Chat")
        {
            await ReportProgressAsync(progress, "generating_reply", "正在生成聊天回复", conversation.Id);
            var frameworkResult = await _frameworkRuntime.RunAsync(new AgentFrameworkRunRequest
            {
                AgentCode = agent.AgentCode,
                UserId = request.UserId,
                AgentName = agent.Name,
                UserMessage = request.Message.Trim(),
                SystemPrompt = agent.SystemPrompt ?? string.Empty,
                ScopeProjectName = request.ScopeProjectName ?? conversation.ScopeProjectName,
                ConversationId = conversation.Id,
                History = history,
                Tools = []
            }, AgentChannelExecutionScope.Current?.CancellationToken ?? default);
            var chatFailureFeedback = AgentReplyFailureFeedback.Build(frameworkResult.FailureKind, [], false);
            if (AgentChannelExecutionScope.Current != null && !frameworkResult.Success)
                throw new AgentChannelExecutionException(false, chatFailureFeedback + "\n本次未扣除对话额度。");
            var chatReplyText = frameworkResult.Success && !string.IsNullOrWhiteSpace(frameworkResult.Reply)
                ? frameworkResult.Reply.Trim()
                : chatFailureFeedback;
            await AddMessageAsync(conversation.Id, "assistant", chatReplyText);
            await TouchConversationAsync(conversation.Id);
            await ReportProgressAsync(progress, "saving_result", "回答已生成，正在保存会话", conversation.Id);
            return new AgentResponseDto
            {
                ConversationId = conversation.Id,
                Reply = chatReplyText,
                Suggestions = await _registry.GetSuggestionsAsync(agent.AgentCode)
            };
        }

        var tools = await _registry.GetEnabledToolsAsync(agent.AgentCode);
        if (isProjectCsv)
        {
            tools = tools
                .Where(x => x.ToolName.Equals("project.create", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!tools.Any())
                throw new InvalidOperationException("项目新增工具未启用，无法分析 CSV 导入计划");
        }
        var toolDefinitions = _mapper.Map<List<AgentToolDefinitionDto>>(tools);
        await ReportProgressAsync(
            progress,
            "selecting_tools",
            isProjectCsv ? "LLM 正在分析 CSV 并整理待写入项目" : "正在理解问题并选择合适的工具",
            conversation.Id);
        var frameworkRun = await _frameworkRuntime.RunAsync(new AgentFrameworkRunRequest
        {
            AgentCode = agent.AgentCode,
            UserId = request.UserId,
            AgentName = agent.Name,
            SystemPrompt = agent.SystemPrompt ?? string.Empty,
            UserMessage = modelUserMessage,
            ScopeProjectName = request.ScopeProjectName ?? conversation.ScopeProjectName,
            ConversationId = conversation.Id,
            History = history,
            Tools = toolDefinitions,
            MaxToolCalls = agent.MaxToolCalls,
            MaxOutputTokens = isProjectCsv ? 6000 : null,
            OnToolInvokingAsync = tool => ReportProgressAsync(
                progress,
                "executing_tool",
                $"正在处理：{tool.Title}",
                conversation.Id)
        }, AgentChannelExecutionScope.Current?.CancellationToken ?? default);

        var calls = frameworkRun.ToolCalls;
        var mayHaveWriteEffects = AgentReplyFailureFeedback.MayHaveWriteEffects(calls, toolDefinitions);
        var failureFeedback = AgentReplyFailureFeedback.Build(frameworkRun.FailureKind, calls, mayHaveWriteEffects);
        if (AgentChannelExecutionScope.Current != null && !frameworkRun.Success)
            throw new AgentChannelExecutionException(mayHaveWriteEffects,
                failureFeedback + (calls.Count == 0 ? "\n本次未扣除对话额度。" : string.Empty), calls.Count > 0);
        var toolMap = tools.ToDictionary(x => x.ToolName, StringComparer.OrdinalIgnoreCase);
        var plan = calls
            .Select((call, index) =>
            {
                toolMap.TryGetValue(call.ToolName, out var tool);
                return new AgentPlanStepDto
                {
                    Sort = index + 1,
                    Title = tool?.Title ?? call.ToolName,
                    Description = tool?.Description ?? string.Empty,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.RequestJson,
                    RequiresConfirmation = call.RequiresConfirmation
                };
            })
            .ToList();

        await ReportProgressAsync(progress, "generating_reply", "工具处理完成，正在组织回答", conversation.Id);
        var reply = frameworkRun.Success && !string.IsNullOrWhiteSpace(frameworkRun.Reply)
            ? frameworkRun.Reply.Trim()
            : BuildFallbackReply(calls, failureFeedback);
        if (isProjectCsv && !calls.Any())
            reply = $"CSV 已完成分析，但 LLM 没有生成可确认的项目新增计划，因此没有写入任何数据。请检查表头和必填字段后重试。\n\n{reply}";
        await ReportProgressAsync(progress, "saving_result", "回答已生成，正在保存会话", conversation.Id);
        await AddMessageAsync(conversation.Id, "assistant", reply);
        await TouchConversationAsync(conversation.Id);

        return new AgentResponseDto
        {
            ConversationId = conversation.Id,
            Reply = reply,
            Plan = plan,
            ToolCalls = calls,
            RequiresConfirmation = calls.Any(x => x.RequiresConfirmation && !x.Confirmed && x.Status == "PendingConfirm"),
            Suggestions = await _registry.GetSuggestionsAsync(agent.AgentCode)
        };
    }

    private static async Task ReportProgressAsync(
        Func<AgentProgressDto, Task>? progress,
        string stage,
        string message,
        int? conversationId = null)
    {
        if (progress == null) return;

        try
        {
            await progress(new AgentProgressDto
            {
                Stage = stage,
                Message = message,
                ConversationId = conversationId
            });
        }
        catch
        {
            // A disconnected client must not interrupt an Agent business operation already in progress.
        }
    }

    /// <summary>
    /// 获取 Agent 工具目录，供前端展示和模型选择工具时使用。
    /// </summary>
    public async Task<List<AgentToolDefinitionDto>> GetToolCatalogAsync(string? agentCode = null)
    {
        var tools = await _registry.GetAllToolsAsync(AgentToolRegistryService.NormalizeAgentCode(agentCode));
        return _mapper.Map<List<AgentToolDefinitionDto>>(tools);
    }

    /// <summary>
    /// 确认一个待执行的写操作工具调用。
    /// </summary>
    public async Task<AgentToolCallDto?> ConfirmToolCallAsync(AgentConfirmRequest request)
    {
        request.UserId = RequireUserId(request.UserId);
        var call = await _dispatcher.ConfirmAsync(request);
        if (call == null) return null;
        var conversation = await Db.Queryable<AgentConversationEntity>()
            .FirstAsync(x => x.Id == call.ConversationId && x.UserId == request.UserId);
        if (conversation == null) return null;

        var reply = BuildExecutionReply(call);
        await AddMessageAsync(call.ConversationId, "assistant", reply);
        await TouchConversationAsync(call.ConversationId);
        return call;
    }

    /// <summary>
    /// 删除会话及其消息，带用户校验，避免误删其他人的会话。
    /// </summary>
    public async Task<bool> DeleteConversationAsync(AgentDeleteConversationRequest request)
    {
        var ownerUserId = RequireUserId(request.UserId);
        var conversation = await Db.Queryable<AgentConversationEntity>()
            .FirstAsync(x => x.Id == request.Id && x.UserId == ownerUserId);
        if (conversation == null) return false;

        var executionIds = await Db.Queryable<AgentExecutionPlanEntity>()
            .Where(x => x.ConversationId == request.Id)
            .Select(x => x.Id)
            .ToListAsync();
        if (executionIds.Any())
            await Db.Deleteable<AgentInvocationLogEntity>().Where(x => x.ExecutionPlanId.HasValue && executionIds.Contains(x.ExecutionPlanId.Value)).ExecuteCommandAsync();
        await Db.Deleteable<AgentInvocationLogEntity>().Where(x => x.ConversationId == request.Id).ExecuteCommandAsync();
        await Db.Deleteable<AgentExecutionPlanEntity>().Where(x => x.ConversationId == request.Id).ExecuteCommandAsync();
        await Db.Deleteable<AgentMessageEntity>().Where(x => x.ConversationId == request.Id).ExecuteCommandAsync();
        return await Db.Deleteable<AgentConversationEntity>().Where(x => x.Id == request.Id).ExecuteCommandAsync() > 0;
    }

    /// <summary>
    /// 根据用户意图生成邮件草稿，但不发送邮件。
    /// </summary>
    public async Task<AgentEmailDraftDto?> CreateEmailDraftAsync(AgentEmailDraftRequest request)
    {
        request.UserId = RequireUserId(request.UserId);
        string? content = EmptyToNull(request.Content);
        if (request.SourceToolCallId.HasValue)
        {
            var execution = await Db.Queryable<AgentExecutionPlanEntity>()
                .FirstAsync(x => x.Id == request.SourceToolCallId.Value);
            if (execution != null && !SameUser(execution.UserId, request.UserId)) return null;
            content ??= execution?.ResultJson;
        }

        if (content == null && request.ConversationId.HasValue)
        {
            var conversation = await Db.Queryable<AgentConversationEntity>()
                .FirstAsync(x => x.Id == request.ConversationId.Value);
            if (conversation == null || !SameUser(conversation.UserId, request.UserId)) return null;
            var message = await Db.Queryable<AgentMessageEntity>()
                .Where(x => x.ConversationId == request.ConversationId.Value && x.Role == "assistant")
                .OrderByDescending(x => x.CreateTime)
                .OrderByDescending(x => x.Id)
                .FirstAsync();
            content = message?.Content;
        }

        if (string.IsNullOrWhiteSpace(content)) return null;
        return new AgentEmailDraftDto
        {
            ConversationId = request.ConversationId,
            SourceToolCallId = request.SourceToolCallId,
            Subject = string.IsNullOrWhiteSpace(request.Subject) ? "项目管理 Agent 结果" : request.Subject.Trim(),
            Body = content
        };
    }

    /// <summary>
    /// 发送邮件，并返回是否发送成功。
    /// </summary>
    public async Task<AgentEmailSendResultDto> SendEmailAsync(AgentEmailSendRequest request)
    {
        request.UserId = RequireUserId(request.UserId);
        if (request.ConversationId.HasValue)
        {
            var conversation = await Db.Queryable<AgentConversationEntity>()
                .FirstAsync(x => x.Id == request.ConversationId.Value);
            if (conversation == null || !SameUser(conversation.UserId, request.UserId))
                throw new UnauthorizedAccessException("无权访问该 Agent 会话");
        }
        var result = await _emailService.SendAsync(request);
        if (request.ConversationId.HasValue)
        {
            await AddMessageAsync(
                request.ConversationId.Value,
                "assistant",
                result.Message
            );
            await TouchConversationAsync(request.ConversationId.Value);
        }
        return result;
    }

    /// <summary>
    /// 当用户回复“确认”时，执行待确认计划。
    /// 生产看板的新增、编辑通常由多个连续工具调用组成，因此一次确认会按计划生成顺序执行全部待确认项。
    /// </summary>
    private async Task<AgentResponseDto?> TryConfirmFromConversationAsync(
        AgentConversationEntity conversation,
        AgentSendRequest request)
    {
        // 写操作只能由用户明确回复确认触发，不能由客户端传入的 AutoExecute 参数绕过。
        if (!IsConfirmationMessage(request.Message)) return null;

        var pendingPlans = await Db.Queryable<AgentExecutionPlanEntity>()
            .Where(x => x.ConversationId == conversation.Id && x.Status == "PendingConfirmation")
            .OrderBy(x => x.CreateTime)
            .OrderBy(x => x.Id)
            .ToListAsync();
        if (!pendingPlans.Any()) return null;

        // 保持其他 Agent 原有的“仅确认最近一项”行为，生产看板则一次执行同轮生成的完整配置计划。
        if (!conversation.AgentCode.Equals(AgentCodes.ProductionDashboard, StringComparison.OrdinalIgnoreCase))
            pendingPlans = new List<AgentExecutionPlanEntity> { pendingPlans[^1] };

        var calls = new List<AgentToolCallDto>();
        foreach (var pendingPlan in pendingPlans)
        {
            var call = await _dispatcher.ConfirmAsync(new AgentConfirmRequest
            {
                ToolCallId = pendingPlan.Id,
                UserId = request.UserId,
                Remark = "conversation-confirm"
            });
            if (call != null) calls.Add(call);
        }
        if (!calls.Any()) return null;

        var reply = calls.Count == 1
            ? BuildExecutionReply(calls[0])
            : $"已按确认执行 {calls.Count} 项看板配置：\n" + string.Join("\n", calls.Select(BuildExecutionReply));
        await AddMessageAsync(conversation.Id, "assistant", reply);
        await TouchConversationAsync(conversation.Id);
        return new AgentResponseDto
        {
            ConversationId = conversation.Id,
            Reply = reply,
            ToolCalls = calls,
            RequiresConfirmation = false,
            Suggestions = await _registry.GetSuggestionsAsync(conversation.AgentCode)
        };
    }

    private static bool IsProjectCsvRequest(AgentSendRequest request) =>
        request.AttachmentType?.Equals("project-csv", StringComparison.OrdinalIgnoreCase) == true;

    private static string BuildModelUserMessage(AgentSendRequest request)
    {
        if (!IsProjectCsvRequest(request)) return request.Message;

        if (string.IsNullOrWhiteSpace(request.AttachmentContent))
            throw new InvalidOperationException("CSV 内容不能为空");
        if (!string.IsNullOrWhiteSpace(request.AttachmentName) &&
            !Path.GetExtension(request.AttachmentName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Agent 项目导入仅支持 CSV 文件");
        if (System.Text.Encoding.UTF8.GetByteCount(request.AttachmentContent) > 128 * 1024)
            throw new InvalidOperationException("CSV 文件不能超过 128 KB");

        return $"""
            {request.Message}

            这是用户上传的项目 CSV 文件“{request.AttachmentName ?? "未命名.csv"}”。
            请逐行分析表头和数据，只调用 project.create 工具，把所有可识别项目整理到 projects 参数中。
            不要执行写入，不要声称导入成功；请生成待确认计划，并在回答中清楚列出待写入的项目编号、名称、负责人和周期。
            缺少项目编号、项目名称、开始时间或结束时间的行不要猜测，应在回答中指出问题。

            CSV 内容：
            ```csv
            {request.AttachmentContent}
            ```
            """;
    }

    private async Task<AgentConversationEntity> GetOrCreateConversationAsync(AgentSendRequest request, string agentCode)
    {
        var ownerUserId = RequireUserId(request.UserId);
        AgentConversationEntity? conversation = null;
        if (request.ConversationId.HasValue && request.ConversationId.Value > 0)
            conversation = await Db.Queryable<AgentConversationEntity>().FirstAsync(x => x.Id == request.ConversationId.Value);

        if (conversation != null && !SameUser(conversation.UserId, ownerUserId))
            throw new UnauthorizedAccessException("无权访问该 Agent 会话");
        if (conversation != null &&
            !conversation.AgentCode.Equals(agentCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前会话不属于所选 Agent，请新建会话后重试");

        if (conversation == null)
        {
            conversation = new AgentConversationEntity
            {
                AgentCode = agentCode,
                UserId = ownerUserId,
                Title = LimitText(request.Message.Trim(), 28),
                ScopeProjectName = EmptyToNull(request.ScopeProjectName),
                Mode = NormalizeInteractionMode(request.Mode),
                Status = "Active",
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
            conversation.Id = await Db.Insertable(conversation).ExecuteReturnIdentityAsync();
            return conversation;
        }

        if (!string.IsNullOrWhiteSpace(request.ScopeProjectName))
            conversation.ScopeProjectName = request.ScopeProjectName.Trim();
        conversation.Mode = NormalizeInteractionMode(request.Mode);
        conversation.UpdateTime = DateTime.Now;
        await Db.Updateable(conversation).ExecuteCommandAsync();
        return conversation;
    }

    private async Task<List<AgentMessageDto>> GetRecentMessagesAsync(int conversationId)
    {
        var messages = await Db.Queryable<AgentMessageEntity>()
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreateTime)
            .OrderByDescending(x => x.Id)
            .Take(8)
            .ToListAsync();
        return _mapper.Map<List<AgentMessageDto>>(
            messages.OrderBy(x => x.CreateTime).ThenBy(x => x.Id).ToList());
    }

    private async Task AddMessageAsync(int conversationId, string role, string content)
    {
        await Db.Insertable(new AgentMessageEntity
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
    }

    private async Task TouchConversationAsync(int conversationId)
    {
        await Db.Updateable<AgentConversationEntity>()
            .SetColumns(x => new AgentConversationEntity { UpdateTime = DateTime.Now })
            .Where(x => x.Id == conversationId)
            .ExecuteCommandAsync();
    }

    private static string BuildFallbackReply(List<AgentToolCallDto> calls, string? modelError)
    {
        var messages = calls.Select(x =>
        {
            var message = ReadResultMessage(x.ResponseJson);
            return string.IsNullOrWhiteSpace(message) ? $"{x.ToolTitle}：{x.Status}" : message;
        }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var reply = messages.Any() ? string.Join("\n", messages) : "工具处理完成。";
        if (!string.IsNullOrWhiteSpace(modelError)) reply += $"\n自然语言整理暂不可用：{modelError}";
        return reply;
    }

    private static string BuildExecutionReply(AgentToolCallDto call)
    {
        var message = ReadResultMessage(call.ResponseJson);
        if (!string.IsNullOrWhiteSpace(message)) return message;
        return call.Status switch
        {
            "Success" => $"已执行“{call.ToolTitle}”。",
            "PartialSuccess" => $"“{call.ToolTitle}”已执行，部分数据处理失败，请查看执行结果。",
            "PermissionDenied" => $"“{call.ToolTitle}”执行失败：当前用户没有对应权限。",
            _ => $"“{call.ToolTitle}”执行失败，请查看执行结果。"
        };
    }

    private static string ReadResultMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? string.Empty;
        }
        catch
        {
            // Tool result may be legacy text.
        }
        return string.Empty;
    }

    private static bool IsConfirmationMessage(string message)
    {
        var normalized = message.Trim().Replace(" ", string.Empty);
        return normalized.Equals("确认", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("确认执行", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("同意执行", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("执行", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInteractionMode(string? mode) =>
        mode?.Trim().Equals("Chat", StringComparison.OrdinalIgnoreCase) == true ? "Chat" : "Work";

    private static string ResolveAgentCode(string interactionMode, string? requestedAgentCode)
    {
        if (interactionMode == "Chat") return AgentCodes.Chat;

        var agentCode = AgentToolRegistryService.NormalizeAgentCode(requestedAgentCode);
        if (agentCode.Equals(AgentCodes.Chat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("工作模式必须选择业务域");
        return agentCode;
    }

    private static string LimitText(string value, int length) => value.Length <= length ? value : value[..length];
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string RequireUserId(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? throw new UnauthorizedAccessException("Agent 用户不能为空")
            : userId.Trim();

    private static bool SameUser(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
