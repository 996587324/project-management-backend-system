using Model.Menu;
using SqlSugar;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Service;

/// <summary>启动时补齐缺失的业务菜单，不在读取路由时建表，也不覆盖已有菜单配置。</summary>
internal sealed class SystemMenuInitializer
{
    private readonly ISqlSugarClient _db;
    private readonly ILogger _logger;

    public SystemMenuInitializer(ISqlSugarClient db, ILogger logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RunStepAsync("Agent", EnsureAgentMenuAsync);
        await RunStepAsync("WeChatClawBot", EnsureWeChatClawBotMenuAsync);
        cancellationToken.ThrowIfCancellationRequested();
        await RunStepAsync("PDA", EnsurePdaMenuAsync);
        await RunStepAsync("EdgeGateway", EnsureEdgeGatewayMenuAsync);
        cancellationToken.ThrowIfCancellationRequested();
        await RunStepAsync("ProductionDashboard", EnsureProductionDashboardMenuAsync);
        await RunStepAsync("UserActivity", EnsureUserActivityMenuAsync);
    }

    private async Task RunStepAsync(string module, Func<Task> initialize)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await initialize();
        }
        finally
        {
            _logger.LogInformation("System menu initialization step {Module} took {ElapsedMilliseconds} ms.",
                module, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>生产环境由后端菜单动态注册，确保管理员可进入用户行为日志页面。</summary>
    private async Task EnsureUserActivityMenuAsync()
    {
        var db = _db;
        var systemRoot = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == "0" && (x.MenuName == "System" || x.Path == "/system"));
        if (systemRoot == null) return;
        await EnsureAgentChildMenuAsync(
            db,
            systemRoot.MenuId,
            "UserActivity",
            "user-activity",
            "/system/user-activity",
            "用户行为日志",
            90,
            "R_SUPER,R_ADMIN");
    }

    private async Task EnsureAgentMenuAsync()
    {
        var db = _db;

        var rootMenu = await db.Queryable<sys_menu>()
            .FirstAsync(x => x.Path == "/agent" || x.MenuName == "Agent");
        if (rootMenu == null)
        {
            var rootMenus = await db.Queryable<sys_menu>().Where(x => x.ParentId == "0").ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            rootMenu = new sys_menu
            {
                MenuId = (maxRootId + 1).ToString(),
                ParentId = "0",
                MenuName = "Agent",
                Path = "/agent",
                Component = "/index/index",
                MetaTitle = "Agent 中心",
                MetaIcon = "ri:robot-2-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 98,
                Status = true,
                CreateTime = DateTime.Now
            };
            await db.Insertable(rootMenu).ExecuteCommandAsync();
        }

        // Agent 统一从智能助手入口进入；旧路由仅为已打开页面兼容而保留并隐藏。
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "AgentAssistant",
            "assistant",
            "/agent/assistant",
            "智能助手",
            1);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "AgentExecutor",
            "executor",
            "/agent/executor",
            "项目智能助手",
            2,
            isHide: true);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "EdgeGatewayAgent",
            "edge-gateway",
            "/agent/edge-gateway",
            "边缘计算助手",
            3,
            isHide: true);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "ProductionDashboardAgent",
            "production-dashboard",
            "/agent/production-dashboard",
            "生产看板助手",
            4,
            isHide: true);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "ProcessBoardAgent",
            "process-board",
            "/agent/process-board",
            "产线工艺看板助手",
            8,
            isHide: true);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "AgentModelProvider",
            "model-providers",
            "/agent/model-provider",
            "模型平台配置",
            5,
            "R_SUPER,R_ADMIN");
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "ExternalToolClients",
            "external-tool-clients",
            "/agent/external-tool-clients",
            "外部 MCP 客户端",
            6,
            "R_SUPER,R_ADMIN");
        // 微信管理入口由 PushCenterMenus 统一整理，不能在重启后重新补到 Agent 中心。
    }

    /// <summary>
    /// 确保微信 ClawBot 的个人绑定入口可由后端动态菜单返回。
    /// 管理入口放在 Agent 中心；个人入口独立展示，避免普通用户看到无权限的管理功能。
    /// </summary>
    private async Task EnsureWeChatClawBotMenuAsync()
    {
        var db = _db;

        var rootMenu = await db.Queryable<sys_menu>()
            .FirstAsync(x => x.Path == "/wechat-clawbot" || x.MenuName == "WeChatClawBotRoot");
        if (rootMenu == null)
        {
            var rootMenus = await db.Queryable<sys_menu>()
                .Where(x => x.ParentId == "0")
                .ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();

            rootMenu = new sys_menu
            {
                MenuId = (maxRootId + 1).ToString(),
                ParentId = "0",
                MenuName = "WeChatClawBotRoot",
                Path = "/wechat-clawbot",
                Component = "/index/index",
                MetaTitle = "微信推送",
                MetaIcon = "ri:wechat-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER,R_USER",
                MetaAuthList = "[]",
                Sort = 94,
                Status = true,
                CreateTime = DateTime.Now
            };
            await db.Insertable(rootMenu).ExecuteCommandAsync();
        }

        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "WeChatClawBot",
            "binding",
            "/wechat-clawbot",
            "我的微信推送",
            1,
            "R_SUPER,R_ADMIN,R_ENGINEER,R_USER",
            icon: "ri:wechat-line");
    }

    private async Task EnsurePdaMenuAsync()
    {
        var db = _db;

        var rootMenu = await db.Queryable<sys_menu>()
            .FirstAsync(x => x.Path == "/pda" || x.MenuName == "Pda");
        if (rootMenu == null)
        {
            var rootMenus = await db.Queryable<sys_menu>().Where(x => x.ParentId == "0").ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            rootMenu = new sys_menu
            {
                MenuId = (maxRootId + 1).ToString(),
                ParentId = "0",
                MenuName = "Pda",
                Path = "/pda",
                Component = "/index/index",
                MetaTitle = "PDA 管理",
                MetaIcon = "ri:barcode-box-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 96,
                Status = true,
                CreateTime = DateTime.Now
            };
            await db.Insertable(rootMenu).ExecuteCommandAsync();
        }

        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "PdaScanLog",
            "scan-logs",
            "/pda/scan-log",
            "扫码记录",
            1);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "PdaDevice",
            "devices",
            "/pda/device",
            "PDA 设备",
            2);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "PdaBarcodeRule",
            "barcode-rules",
            "/pda/barcode-rule",
            "条码规则",
            3);
    }

    /// <summary>
    /// 确保中转管理及数据追溯入口在后端菜单模式中可见。
    /// 静态路由只负责开发模式；生产环境的菜单由此处自动补齐。
    /// </summary>
    private async Task EnsureEdgeGatewayMenuAsync()
    {
        var db = _db;

        var rootMenu = await db.Queryable<sys_menu>()
            .FirstAsync(x => x.Path == "/edge-gateway" || x.MenuName == "EdgeGateway");
        if (rootMenu == null)
        {
            var rootMenus = await db.Queryable<sys_menu>().Where(x => x.ParentId == "0").ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            rootMenu = new sys_menu
            {
                MenuId = (maxRootId + 1).ToString(),
                ParentId = "0",
                MenuName = "EdgeGateway",
                Path = "/edge-gateway",
                Component = "/index/index",
                MetaTitle = "中转管理",
                MetaIcon = "ri:router-line",
                MetaKeepAlive = true,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 95,
                Status = true,
                CreateTime = DateTime.Now
            };
            await db.Insertable(rootMenu).ExecuteCommandAsync();
        }

        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "EdgeGatewayConsole",
            "console",
            "/edge-gateway/console",
            "运行中心",
            1);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "EdgeDataTrace",
            "data-trace",
            "/edge-gateway/data-trace",
            "数据追溯",
            2);
        await EnsureAgentChildMenuAsync(
            db,
            rootMenu.MenuId,
            "EdgeDataTraceDetail",
            "data-trace/detail",
            "/edge-gateway/data-trace/detail",
            "参数详情",
            3,
            isHide: true);
    }

    /// <summary>确保一个 Agent 子菜单存在，菜单编号按当前根菜单子项顺序生成。</summary>
    private static async Task EnsureAgentChildMenuAsync(
        ISqlSugarClient db,
        string rootId,
        string menuName,
        string path,
        string component,
        string title,
        int sort,
        string roles = "R_SUPER,R_ADMIN,R_ENGINEER",
        bool isHide = false,
        string icon = "ri:robot-2-line")
    {
        var exists = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == rootId && (x.Path == path || x.MenuName == menuName));
        if (exists != null)
        {
            // 已有菜单的权限、图标、排序和显示方式由管理员维护，初始化不覆盖。
            return;
        }

        var children = await db.Queryable<sys_menu>()
            .Where(x => x.ParentId == rootId)
            .ToListAsync();
        var suffix = 1;
        string menuId;
        do
        {
            menuId = $"{rootId}{suffix:00}";
            suffix++;
        } while (children.Any(x => x.MenuId == menuId));

        await db.Insertable(new sys_menu
        {
            MenuId = menuId,
            ParentId = rootId,
            MenuName = menuName,
            Path = path,
            Component = component,
            MetaTitle = title,
            MetaIcon = icon,
            MetaKeepAlive = true,
            MetaFixedTab = false,
            MetaIsHide = isHide,
            MetaIsHideTab = false,
            MetaIsFullPage = false,
            MetaRoles = roles,
            MetaAuthList = "[]",
            Sort = sort,
            Status = true,
            CreateTime = DateTime.Now
        }).ExecuteCommandAsync();
    }

    /// <summary>
    /// 确保后端权限模式能够返回产线看板及布局编辑器路由。
    /// 前端使用 backend 访问模式时，只有写入 sys_menu 的路由才会被动态注册。
    /// </summary>
    private async Task EnsureProductionDashboardMenuAsync()
    {
        var db = _db;

        var rootMenu = await db.Queryable<sys_menu>()
            .Where(x => x.Path == "/production-dashboard" || x.MenuName == "ProductionDashboard")
            .FirstAsync();
        if (rootMenu == null)
        {
            var rootMenus = await db.Queryable<sys_menu>()
                .Where(x => x.ParentId == "0")
                .ToListAsync();
            var maxRootId = rootMenus
                .Select(x => int.TryParse(x.MenuId, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
            var now = DateTime.Now;
            rootMenu = new sys_menu
            {
                MenuId = (maxRootId + 1).ToString(),
                ParentId = "0",
                MenuName = "ProductionDashboard",
                Path = "/production-dashboard",
                Component = "/index/index",
                MetaTitle = "产线看板",
                MetaIcon = "ri:dashboard-3-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 97,
                Status = true,
                CreateTime = now
            };
            await db.Insertable(rootMenu).ExecuteCommandAsync();
        }

        var boardMenu = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == rootMenu.MenuId &&
            (x.Path == "board" || x.MenuName == "ProductionDashboardBoard"));
        if (boardMenu == null)
        {
            await db.Insertable(new sys_menu
            {
                MenuId = $"{rootMenu.MenuId}01",
                ParentId = rootMenu.MenuId,
                MenuName = "ProductionDashboardBoard",
                Path = "board",
                Component = "/production-dashboard/board",
                MetaTitle = "生产监控",
                MetaIcon = "ri:dashboard-3-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 1,
                Status = true,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }

        // 看板配置是可见的管理入口；后端菜单模式下由这里补齐，避免每个环境手工录入。
        var configMenu = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == rootMenu.MenuId &&
            (x.Path == "config" || x.MenuName == "ProductionDashboardConfig"));
        if (configMenu == null)
        {
            var preferredMenuId = $"{rootMenu.MenuId}03";
            var menuIdExists = await db.Queryable<sys_menu>()
                .AnyAsync(x => x.MenuId == preferredMenuId);
            await db.Insertable(new sys_menu
            {
                // 保持现有子菜单编号习惯；极端情况下避免覆盖管理员已创建的菜单。
                MenuId = menuIdExists ? $"{rootMenu.MenuId}-config" : preferredMenuId,
                ParentId = rootMenu.MenuId,
                MenuName = "ProductionDashboardConfig",
                Path = "config",
                Component = "/production-dashboard/config-center",
                MetaTitle = "产线看板配置",
                MetaIcon = "ri:settings-3-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 2,
                Status = true,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }

        // 设备卡片看板与生产监控分开：前者只展示通用设备实时字段和可选追溯记录。
        var deviceCardBoardMenu = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == rootMenu.MenuId &&
            (x.Path == "device-cards" || x.MenuName == "ProductionDashboardDeviceCards"));
        if (deviceCardBoardMenu == null)
        {
            var preferredMenuId = $"{rootMenu.MenuId}04";
            var menuIdExists = await db.Queryable<sys_menu>().AnyAsync(x => x.MenuId == preferredMenuId);
            await db.Insertable(new sys_menu
            {
                MenuId = menuIdExists ? $"{rootMenu.MenuId}-device-cards" : preferredMenuId,
                ParentId = rootMenu.MenuId,
                MenuName = "ProductionDashboardDeviceCards",
                Path = "device-cards",
                Component = "/production-dashboard/device-card-board",
                MetaTitle = "设备卡片看板",
                MetaIcon = "ri:computer-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 3,
                Status = true,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }

        var deviceCardConfigMenu = await db.Queryable<sys_menu>().FirstAsync(x =>
            x.ParentId == rootMenu.MenuId &&
            (x.Path == "device-card-config" || x.MenuName == "ProductionDashboardDeviceCardConfig"));
        if (deviceCardConfigMenu == null)
        {
            var preferredMenuId = $"{rootMenu.MenuId}05";
            var menuIdExists = await db.Queryable<sys_menu>().AnyAsync(x => x.MenuId == preferredMenuId);
            await db.Insertable(new sys_menu
            {
                MenuId = menuIdExists ? $"{rootMenu.MenuId}-device-card-config" : preferredMenuId,
                ParentId = rootMenu.MenuId,
                MenuName = "ProductionDashboardDeviceCardConfig",
                Path = "device-card-config",
                Component = "/production-dashboard/device-card-config",
                MetaTitle = "设备卡片配置",
                MetaIcon = "ri:settings-4-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = false,
                MetaIsHideTab = false,
                MetaIsFullPage = false,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 4,
                Status = true,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }

        var editorExists = await db.Queryable<sys_menu>().AnyAsync(x =>
            x.ParentId == rootMenu.MenuId &&
            (x.Path == "layout-editor" || x.MenuName == "ProductionDashboardLayoutEditor"));
        if (!editorExists)
        {
            await db.Insertable(new sys_menu
            {
                MenuId = $"{rootMenu.MenuId}02",
                ParentId = rootMenu.MenuId,
                MenuName = "ProductionDashboardLayoutEditor",
                Path = "layout-editor",
                Component = "/production-dashboard/layout-editor",
                MetaTitle = "布局编辑",
                MetaIcon = "ri:layout-grid-line",
                MetaKeepAlive = false,
                MetaFixedTab = false,
                MetaIsHide = true,
                MetaIsHideTab = false,
                MetaIsFullPage = true,
                MetaRoles = "R_SUPER,R_ADMIN,R_ENGINEER",
                MetaAuthList = "[]",
                Sort = 2,
                Status = true,
                CreateTime = DateTime.Now
            }).ExecuteCommandAsync();
        }
    }

}
