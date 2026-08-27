using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoMapper;
using Interface;
using Model.Agent;
using Model.User;
using SqlSugar;

namespace Service;

/// <summary>
/// Agent 工具调度器，负责把模型选择的工具调用保存为执行计划，并在确认后调用业务执行器。
/// </summary>
public class AgentToolDispatcher : IAgentToolDispatcher
{
    private readonly SqlSugarScope _dbScope;
    private readonly IMapper _mapper;
    private readonly IAgentToolRegistryService _registry;
    private readonly IEnumerable<IAgentBusinessToolExecutor> _executors;
    private readonly IEnumerable<IAgentToolAction> _toolActions;
    private ISqlSugarClient Db => _dbScope.GetConnectionScope("calmadmin");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public AgentToolDispatcher(
        SqlSugarScope dbScope,
        IMapper mapper,
        IAgentToolRegistryService registry,
        IEnumerable<IAgentBusinessToolExecutor> executors,
        IEnumerable<IAgentToolAction> toolActions)
    {
        _dbScope = dbScope;
        _mapper = mapper;
        _registry = registry;
        _executors = executors;
        _toolActions = toolActions;
    }

    /// <summary>
    /// 创建工具调用计划。读操作会立即执行，写操作会进入 PendingConfirmation 等待用户确认。
    /// </summary>
    public async Task<AgentToolCallDto> DispatchAsync(AgentToolDispatchRequest request)
    {
        var tool = await _registry.GetToolAsync(request.AgentCode, request.ToolName);
        if (tool == null)
            return await CreateRejectedPlanAsync(request, null, "ToolNotFound", $"工具 {request.ToolName} 未注册");
        if (!tool.Enabled)
            return await CreateRejectedPlanAsync(request, tool, "Disabled", $"工具 {tool.Title} 已禁用");

        var permissionError = await ValidatePermissionAsync(tool, request.UserId);
        if (!string.IsNullOrWhiteSpace(permissionError))
            return await CreateRejectedPlanAsync(request, tool, "PermissionDenied", permissionError);

        var normalizedArguments = NormalizeArguments(request.ArgumentsJson);
        var schemaErrors = AgentJsonSchemaValidator.Validate(tool.InputSchema, normalizedArguments);
        if (schemaErrors.Any())
        {
            request.ArgumentsJson = normalizedArguments;
            return await CreateRejectedPlanAsync(
                request,
                tool,
                "InvalidArguments",
                string.Join("；", schemaErrors.Take(8)));
        }

        var plan = new AgentExecutionPlanEntity
        {
            ConversationId = request.ConversationId,
            ToolId = tool.Id,
            AgentCode = tool.AgentCode,
            ToolName = tool.ToolName,
            ToolTitle = tool.Title,
            UserId = request.UserId,
            ArgumentsJson = normalizedArguments,
            ArgumentsHash = ComputeHash(normalizedArguments),
            Status = tool.IsWriteAction && tool.RequiresConfirmation ? "PendingConfirmation" : "Created",
            IsWriteAction = tool.IsWriteAction,
            RequiresConfirmation = tool.RequiresConfirmation,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CreateTime = DateTime.Now
        };

        if (plan.Status == "PendingConfirmation")
        {
            plan.ResultJson = Serialize(new
            {
                success = true,
                message = $"已生成“{tool.Title}”待确认执行计划，确认后才会写入数据。",
                tool = tool.ToolName,
                arguments = JsonSerializer.Deserialize<object>(normalizedArguments, JsonOptions)
            });
        }

        plan.Id = await Db.Insertable(plan).ExecuteReturnIdentityAsync();
        await WriteLogAsync(tool, plan, "Plan", plan.Status, normalizedArguments, plan.ResultJson, null, 0);

        if (plan.Status != "PendingConfirmation")
            await ExecutePlanAsync(plan, tool, request.UserId, false);

        return _mapper.Map<AgentToolCallDto>(plan);
    }

    /// <summary>
    /// 确认并执行待确认的写操作计划，同时校验权限、工具状态和参数是否被篡改。
    /// </summary>
    public async Task<AgentToolCallDto?> ConfirmAsync(AgentConfirmRequest request)
    {
        var plan = await Db.Queryable<AgentExecutionPlanEntity>().FirstAsync(x => x.Id == request.ToolCallId);
        if (plan == null) return null;
        if (!string.IsNullOrWhiteSpace(plan.UserId) &&
            !string.Equals(plan.UserId, request.UserId, StringComparison.OrdinalIgnoreCase))
            return null;

        if (plan.Status is "Succeeded" or "PartiallySucceeded" or "Failed")
            return _mapper.Map<AgentToolCallDto>(plan);
        if (!plan.RequiresConfirmation)
            return _mapper.Map<AgentToolCallDto>(plan);
        if (plan.Status != "PendingConfirmation")
            return _mapper.Map<AgentToolCallDto>(plan);

        var tool = await _registry.GetToolAsync(plan.AgentCode, plan.ToolName);
        if (tool == null || !tool.Enabled)
        {
            await RejectExistingPlanAsync(plan, tool, "Disabled", "工具未注册或已禁用，确认执行已被拦截");
            return _mapper.Map<AgentToolCallDto>(plan);
        }

        var permissionError = await ValidatePermissionAsync(tool, request.UserId ?? plan.UserId);
        if (!string.IsNullOrWhiteSpace(permissionError))
        {
            await RejectExistingPlanAsync(plan, tool, "PermissionDenied", permissionError);
            return _mapper.Map<AgentToolCallDto>(plan);
        }

        if (!ComputeHash(plan.ArgumentsJson).Equals(plan.ArgumentsHash, StringComparison.OrdinalIgnoreCase))
        {
            await RejectExistingPlanAsync(plan, tool, "ArgumentsChanged", "待确认参数已发生变化，请重新生成执行计划");
            return _mapper.Map<AgentToolCallDto>(plan);
        }

        var schemaErrors = AgentJsonSchemaValidator.Validate(tool.InputSchema, plan.ArgumentsJson);
        if (schemaErrors.Any())
        {
            await RejectExistingPlanAsync(plan, tool, "InvalidArguments", string.Join("；", schemaErrors.Take(8)));
            return _mapper.Map<AgentToolCallDto>(plan);
        }

        var confirmer = request.UserId ?? plan.UserId;
        var affected = await Db.Updateable<AgentExecutionPlanEntity>()
            .SetColumns(x => new AgentExecutionPlanEntity
            {
                Status = "Running",
                ConfirmedBy = confirmer,
                ConfirmTime = DateTime.Now,
                StartTime = DateTime.Now
            })
            .Where(x => x.Id == plan.Id && x.Status == "PendingConfirmation")
            .ExecuteCommandAsync();

        if (affected == 0)
        {
            var current = await Db.Queryable<AgentExecutionPlanEntity>().FirstAsync(x => x.Id == plan.Id);
            return current == null ? null : _mapper.Map<AgentToolCallDto>(current);
        }

        plan.Status = "Running";
        plan.ConfirmedBy = confirmer;
        plan.ConfirmTime = DateTime.Now;
        plan.StartTime = DateTime.Now;
        await ExecutePlanAsync(plan, tool, confirmer, true);
        return _mapper.Map<AgentToolCallDto>(plan);
    }

    /// <summary>
    /// 查询指定会话的工具调用历史，用于前端展示执行状态。
    /// </summary>
    public async Task<List<AgentToolCallDto>> GetConversationExecutionsAsync(int conversationId)
    {
        var plans = await Db.Queryable<AgentExecutionPlanEntity>()
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreateTime)
            .OrderByDescending(x => x.Id)
            .ToListAsync();
        return _mapper.Map<List<AgentToolCallDto>>(plans);
    }

    /// <summary>
    /// 真正执行工具计划，找到对应业务执行器并把执行结果写回计划表。
    /// </summary>
    private async Task ExecutePlanAsync(
        AgentExecutionPlanEntity plan,
        AgentToolRegistryEntity tool,
        string? userId,
        bool alreadyRunning)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!alreadyRunning)
        {
            plan.Status = "Running";
            plan.StartTime = DateTime.Now;
            await Db.Updateable(plan).ExecuteCommandAsync();
        }

        try
        {
            using var arguments = JsonDocument.Parse(plan.ArgumentsJson);
            var context = new AgentBusinessToolContext
            {
                ConversationId = plan.ConversationId,
                AgentCode = plan.AgentCode,
                ToolName = plan.ToolName,
                UserId = userId,
                CorrelationId = plan.CorrelationId
            };
            var action = _toolActions.FirstOrDefault(x =>
                x.Definition.ServiceName.Equals(tool.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                x.Definition.ActionName.Equals(tool.ActionName, StringComparison.OrdinalIgnoreCase));
            AgentBusinessToolResult result;
            if (action != null)
            {
                result = await action.ExecuteAsync(
                    arguments.RootElement,
                    context,
                    cancellationToken: default);
            }
            else
            {
                var executor = _executors.FirstOrDefault(x => x.CanExecute(tool.ServiceName, tool.ActionName));
                if (executor == null)
                    throw new InvalidOperationException($"未找到 {tool.ServiceName}/{tool.ActionName} 的业务执行器");
                result = await executor.ExecuteAsync(
                    tool.ActionName,
                    arguments.RootElement,
                    context);
            }

            plan.Status = result.Success ? "Succeeded" : result.PartialSuccess ? "PartiallySucceeded" : "Failed";
            plan.ResultJson = Serialize(new
            {
                success = result.Success,
                partialSuccess = result.PartialSuccess,
                result.Message,
                data = result.Data
            });
            plan.ErrorMessage = result.ErrorMessage;
        }
        catch (Exception ex)
        {
            plan.Status = "Failed";
            plan.ErrorMessage = ex.Message;
            plan.ResultJson = Serialize(new { success = false, message = ex.Message });
        }
        finally
        {
            stopwatch.Stop();
            plan.EndTime = DateTime.Now;
            await Db.Updateable(plan).ExecuteCommandAsync();
            await WriteLogAsync(
                tool,
                plan,
                "Execute",
                plan.Status,
                plan.ArgumentsJson,
                plan.ResultJson,
                plan.ErrorMessage,
                stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 创建一条被拒绝的工具计划，用于记录工具不存在、权限不足或参数非法等情况。
    /// </summary>
    private async Task<AgentToolCallDto> CreateRejectedPlanAsync(
        AgentToolDispatchRequest request,
        AgentToolRegistryEntity? tool,
        string status,
        string message)
    {
        var arguments = NormalizeArguments(request.ArgumentsJson);
        var plan = new AgentExecutionPlanEntity
        {
            ConversationId = request.ConversationId,
            ToolId = tool?.Id ?? 0,
            AgentCode = tool?.AgentCode ?? request.AgentCode,
            ToolName = tool?.ToolName ?? request.ToolName,
            ToolTitle = tool?.Title ?? request.ToolName,
            UserId = request.UserId,
            ArgumentsJson = arguments,
            ArgumentsHash = ComputeHash(arguments),
            ResultJson = Serialize(new { success = false, message }),
            ErrorMessage = message,
            Status = status,
            IsWriteAction = tool?.IsWriteAction ?? false,
            RequiresConfirmation = false,
            CorrelationId = Guid.NewGuid().ToString("N"),
            CreateTime = DateTime.Now,
            EndTime = DateTime.Now
        };
        plan.Id = await Db.Insertable(plan).ExecuteReturnIdentityAsync();
        await WriteLogAsync(tool, plan, "Dispatch", status, arguments, plan.ResultJson, message, 0);
        return _mapper.Map<AgentToolCallDto>(plan);
    }

    /// <summary>
    /// 将已有执行计划标记为拒绝状态，并记录拒绝原因。
    /// </summary>
    private async Task RejectExistingPlanAsync(
        AgentExecutionPlanEntity plan,
        AgentToolRegistryEntity? tool,
        string status,
        string message)
    {
        plan.Status = status;
        plan.ErrorMessage = message;
        plan.ResultJson = Serialize(new { success = false, message });
        plan.EndTime = DateTime.Now;
        await Db.Updateable(plan).ExecuteCommandAsync();
        await WriteLogAsync(tool, plan, "Confirm", status, plan.ArgumentsJson, plan.ResultJson, message, 0);
    }

    /// <summary>
    /// 校验用户是否拥有调用该工具所需的权限。
    /// </summary>
    private async Task<string?> ValidatePermissionAsync(AgentToolRegistryEntity tool, string? userId)
    {
        if (string.IsNullOrWhiteSpace(tool.PermissionKey)) return null;
        if (string.IsNullOrWhiteSpace(userId)) return "无法识别当前用户，不能执行受保护的 Agent 工具";
        if (!Db.DbMaintenance.IsAnyTable("sys_user_info")) return "用户权限表不存在，不能执行受保护的 Agent 工具";

        var user = await Db.Queryable<SysUserInfo>()
            .FirstAsync(x => x.UserId == userId || x.UserName == userId);
        if (user == null) return "当前用户不存在，不能执行 Agent 工具";
        if (!user.Enabled) return "当前用户已停用，不能执行 Agent 工具";

        var permissions = new HashSet<string>(user.PermissionKeyList, StringComparer.OrdinalIgnoreCase);
        if (user.RoleList.Any(x => x.Equals("R_ADMIN", StringComparison.OrdinalIgnoreCase) ||
                                   x.Equals("R_SUPER", StringComparison.OrdinalIgnoreCase)))
        {
            permissions.Add("*");
        }

        if (Db.DbMaintenance.IsAnyTable("sys_role") && user.RoleList.Any())
        {
            var roleCodes = user.RoleList;
            var roles = await Db.Queryable<SysRole>().Where(x => roleCodes.Contains(x.roleCode) && x.enabled).ToListAsync();
            foreach (var permission in roles.SelectMany(x => x.permissionKeyList))
                permissions.Add(permission);
        }

        return permissions.Contains("*") || permissions.Contains(tool.PermissionKey)
            ? null
            : $"当前用户缺少工具权限：{tool.PermissionKey}";
    }

    /// <summary>
    /// 写入 Agent 调用日志，便于追踪每次计划、确认和执行的输入输出。
    /// </summary>
    private async Task WriteLogAsync(
        AgentToolRegistryEntity? tool,
        AgentExecutionPlanEntity plan,
        string stage,
        string status,
        string? requestJson,
        string? responseJson,
        string? errorMessage,
        long durationMs)
    {
        await Db.Insertable(new AgentInvocationLogEntity
        {
            ExecutionPlanId = plan.Id,
            ConversationId = plan.ConversationId,
            ToolId = tool?.Id ?? plan.ToolId,
            AgentCode = plan.AgentCode,
            ToolName = plan.ToolName,
            UserId = plan.UserId,
            Stage = stage,
            Status = status,
            Method = tool?.Method ?? "POST",
            Route = tool?.Route,
            ServiceName = tool?.ServiceName,
            ActionName = tool?.ActionName,
            RequestJson = requestJson,
            ResponseJson = responseJson,
            ErrorMessage = errorMessage,
            CorrelationId = plan.CorrelationId,
            StartTime = plan.StartTime ?? plan.CreateTime,
            EndTime = DateTime.Now,
            DurationMs = durationMs
        }).ExecuteCommandAsync();
    }

    private static string NormalizeArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "{}";
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.GetRawText();
        }
        catch
        {
            return argumentsJson.Trim();
        }
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
