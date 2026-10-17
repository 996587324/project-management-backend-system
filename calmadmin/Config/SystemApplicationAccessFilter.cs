using Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Model.SystemApplications;

namespace calmadmin.Config;

/// <summary>
/// 老接口尚未实现按资源分页的部分只允许显式全部资源授权且拥有对应业务权限的 A/B 用户。
/// 默认拒绝，不能把“隐藏菜单”当成后端数据授权。
/// </summary>
public sealed class SystemApplicationAccessFilter(ISystemApplicationService applications) : IAsyncActionFilter, IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var status = context.Exception is UnauthorizedAccessException ? 403 : 400;
            context.Result = new ObjectResult(new { code = status, msg = context.Exception.Message, data = new { } }) { StatusCode = status };
            context.ExceptionHandled = true;
        }
        return Task.CompletedTask;
    }
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try
        {
            var action = (ControllerActionDescriptor)context.ActionDescriptor;
            if (!IsInfrastructure(action, context))
            {
                if (context.HttpContext.User.Identity?.IsAuthenticated != true)
                {
                    context.Result = new UnauthorizedObjectResult(new { code = 401, msg = "登录已失效，请重新登录", data = new { } });
                    return;
                }
                var access = await applications.GetAccessAsync(context.HttpContext.User,
                    action.ControllerName != "SystemApplications");
                context.HttpContext.Items[typeof(ApplicationAccessContext)] = access;
                if (!IsScopedEndpoint(action))
                {
                    if (access.AppCode is not ("A" or "B") || !access.AllResources)
                        throw new UnauthorizedAccessException("该旧接口尚未适配当前系统的数据范围，请使用授权数据接口");
                    if (IsSharedDictionaryMutation(action))
                    {
                        if (!access.PlatformAdministrator && !access.Permissions.Contains("shared-dictionary.write"))
                            throw new UnauthorizedAccessException("共享映射修改需要平台级配置授权");
                    }
                    else if (!GetPermissions(action).Any(access.HasPermission))
                        throw new UnauthorizedAccessException("没有当前业务操作权限");
                }
            }
            await next();
        }
        catch (UnauthorizedAccessException exception)
        {
            context.Result = new ObjectResult(new { code = 403, msg = exception.Message, data = new { } }) { StatusCode = 403 };
        }
        catch (ArgumentException exception)
        {
            context.Result = new BadRequestObjectResult(new { code = 400, msg = exception.Message, data = new { } });
        }
    }

    private static bool IsInfrastructure(ControllerActionDescriptor action, ActionExecutingContext context)
    {
        // 外部工具控制器有独立认证方案，不能再走后台系统令牌校验。
        if (action.ControllerName is "ExternalTool" or "ExternalMcp" or "ExternalToolToken") return true;
        if (action.ControllerName is "auth" or "OAuth2" or "PdaAuth" or "EdgeDashboardPublic") return true;
        if (action.ControllerName == "Pda" &&
            context.HttpContext.User.FindFirst("LoginSource")?.Value == "PDA") return true;
        if (action.ControllerName == "SystemApplications" && action.ActionName is "Redeem" or "EntryInformation") return true;
        if (action.ControllerName == "EdgeRealtimeData" && action.ActionName == "Receive") return true;
        return action.ControllerName == "EdgeGateway" && action.ActionName is
            "Heartbeat" or "ReceiveMessages" or "ReportEvent" or "ReportWebSocketSendRecord" or "GetEffectiveConfig";
    }

    private static bool IsScopedEndpoint(ControllerActionDescriptor action) =>
        action.ControllerName == "UserProfile" && action.ActionName is "GetAvatarInfo" or "GetAvatar" or "UploadAvatar" or "ResetAvatar" ||
        action.ControllerName is "SystemApplications" or "SharedData" or "ProcessBoard" or "WeChatClawBotAi" or "WeChatClawBot" ||
        action.ControllerName == "UserInfo" && action.ActionName == "GetUserInfo" ||
        action.ControllerName == "Menu" && action.ActionName == "GetMenuTree" ||
        action.ControllerName == "UserActivity" && action.ActionName is "Heartbeat" or "Logout" or "RecordOperation";

    private static bool IsSharedDictionaryMutation(ControllerActionDescriptor action) =>
        action.ControllerName == "EdgeDashboard" && action.ActionName is
            "SaveModelDisplayMapping" or "DeleteModelDisplayMapping" or "SaveModelFieldDisplayMappings" or "ImportModelConfiguration";

    private static string[] GetPermissions(ControllerActionDescriptor action)
    {
        var name = action.ActionName;
        return action.ControllerName switch
        {
            "Menu" => ["Menu"],
            "Role" => ["Role"],
            "UserInfo" => name == "ChangePassword" ? ["Dashboard"] : ["User"],
            "UserActivity" => ["UserActivity"],
            "AgentModelProvider" => ["AgentModelProvider"],
            "ExternalToolClient" => ["ExternalToolClients"],
            "Agent" => ["Agent", "AgentAssistant", "AgentExecutor", "EdgeGatewayAgent", "ProductionDashboardAgent", "ProcessBoardAgent"],
            "WeChatClawBot" => ["WeChatClawBot", "WeChatClawBotManagement"],
            "Pda" => name.Contains("Rule") ? ["PdaBarcodeRule"] : name.Contains("Device") ? ["PdaDevice"] : ["Pda", "PdaScan", "PdaScanLog"],
            "ProjectManage" => name == "GetDashboard" ? ["Dashboard", "ProjectDashboard"] :
                name.Contains("File") ? ["ProjectFile"] : name.Contains("Task") ? ["TaskBoard"] : ["ProjectLedger"],
            "BusinessBill" => ["ProjectLedger"],
            "EdgeRealtimeData" => ["EdgeGatewayConsole"],
            "EdgeGateway" => name.Contains("DataTrace") ? ["EdgeDataTrace", "EdgeDataTraceDetail"] : ["EdgeGatewayConsole"],
            "EdgeDeviceCardPush" => ["ProductionDashboardDeviceCardConfig"],
            "EdgeDashboardExport" => ["ProductionDashboardBoard", "ProductionDashboardConfig"],
            "EdgeDashboard" => name.Contains("DeviceCard") ?
                (name.StartsWith("Save") || name.StartsWith("Delete") ? ["ProductionDashboardDeviceCardConfig"] :
                    ["ProductionDashboardDeviceCards", "ProductionDashboardDeviceCardConfig"]) :
                name.StartsWith("Save") || name.StartsWith("Delete") || name.StartsWith("Copy") || name.StartsWith("Change") || name.StartsWith("Import") ?
                    ["ProductionDashboardConfig", "ProductionDashboardLayoutEditor"] :
                    ["ProductionDashboardBoard", "ProductionDashboardConfig", "ProductionDashboardLayoutEditor", "ProductionDashboardDeviceCardConfig"],
            _ => ["__unregistered_operation__"]
        };
    }
}
