using Autofac;
using Autofac.Extensions.DependencyInjection;
using calmadmin.HostedServices;
using Interface;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Model.Agent;
using Model.EdgeGateway;
using Model.Other;
using Model.Pda;
using Model.WeChatClawBot;
using Newtonsoft.Json;
using Service;
using SqlSugar;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Data.Common;
using System.Text;
using DbType = SqlSugar.DbType;

namespace 上传MES.Config
{
    public static class HostBuilderExtend
    {
        public static void Register(this WebApplicationBuilder builder)
        {
            builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
            // 从 appsettings.json 读取连接字符串
            string conn1 = builder.Configuration.GetConnectionString("BatteryTracker");
            string conn2 = builder.Configuration.GetConnectionString("RoleMenuDb");
            string conn3 = builder.Configuration.GetConnectionString("recipe_results_db_hc");
            string conn4 = builder.Configuration.GetConnectionString("recipe_results_db_fr");
            string conn5 = builder.Configuration.GetConnectionString("recipe_results_db_ocv");
            string conn6 = builder.Configuration.GetConnectionString("recipe_results_db_dcir");
            string conn7 = builder.Configuration.GetConnectionString("calmadmin");
            string edgeGatewayLogsConnection = builder.Configuration.GetConnectionString("EdgeGatewayLogs");
            if (string.IsNullOrWhiteSpace(edgeGatewayLogsConnection))
                edgeGatewayLogsConnection = BuildEdgeGatewayLogsConnectionString(conn7!);
            var edgeGatewayBootstrapOptions =
                builder.Configuration.GetSection("EdgeGateway").Get<EdgeGatewayBootstrapOptions>()
                ?? new EdgeGatewayBootstrapOptions();
            var edgeDatabasePath = Path.GetFullPath(
                edgeGatewayBootstrapOptions.DatabasePath,
                AppContext.BaseDirectory);
            builder.Host.ConfigureContainer<ContainerBuilder>(builder =>
            {

                #region 注册 SqlSugarScope（支持多数据库）
                builder.Register<SqlSugarScope>(context =>
                {
                    // AOP 会在后续异步上下文中调用，不能捕获已经结束的 Autofac resolve context。
                    var sqlLogger = context.Resolve<ILoggerFactory>().CreateLogger("DatabaseDiagnostics");
                    var sqlConfiguration = context.Resolve<IConfiguration>();
                    var connectionConfigs = new List<ConnectionConfig>
                    {
                        new ConnectionConfig
                        {
                            ConfigId = "BatteryTracker",
                            ConnectionString = conn1,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        },
                        new ConnectionConfig
                        {
                            ConfigId = "RoleMenuDb",
                            ConnectionString = conn2,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "recipe_results_db_hc",
                            ConnectionString = conn3,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "recipe_results_db_fr",
                            ConnectionString = conn4,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "recipe_results_db_ocv",
                            ConnectionString = conn5,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "recipe_results_db_dcir",
                            ConnectionString = conn6,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "calmadmin",
                            ConnectionString = conn7,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        }
                        ,
                        new ConnectionConfig
                        {
                            ConfigId = "RoleMenuDb",
                            ConnectionString = conn7,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        },
                        new ConnectionConfig
                        {
                            ConfigId = "edge-gateway-logs",
                            ConnectionString = edgeGatewayLogsConnection,
                            DbType = DbType.SqlServer,
                            IsAutoCloseConnection = true,
                            InitKeyType = InitKeyType.Attribute
                        },
                        new ConnectionConfig
                        {
                            ConfigId = "edge-gateway",
                            ConnectionString = $"Data Source={edgeDatabasePath}",
                            DbType = DbType.Sqlite,
                            // 本地队列只使用一个持续打开的连接，所有访问由单例数据库服务串行执行。
                            IsAutoCloseConnection = false,
                            InitKeyType = InitKeyType.Attribute
                        }
                    };
                    var db = new SqlSugarScope(connectionConfigs,
                    // AOP 配置在 ScopeConfig 回调里
                    db =>
                    {
                        // 多租户的每个连接分别配置，不能只配置第一个默认连接。
                        foreach (var config in connectionConfigs)
                        {
                            object configId = config.ConfigId;
                            calmadmin.Config.SqlDiagnosticLogging.Configure(
                                db.GetConnection(configId), sqlLogger, sqlConfiguration);
                        }
                    });

                    return db;
                })
                  .SingleInstance();     // SqlSugarScope 是线程安全的，建议单例
                #endregion

                //注册接口和实现层
                builder.RegisterModule(new AutofacModuleRegister());
            });
            //Automapper映射
            builder.Services.AddAutoMapper(typeof(AutoMapperConfigs));
            // EdgeGateway 仅把连接中心所需的引导参数注册为单例；设备启停状态从数据库动态读取。
            builder.Services.AddSingleton(edgeGatewayBootstrapOptions);
            //注册JWT
            builder.Services.Configure<JWTTokenOptions>(builder.Configuration.GetSection("JWTTokenOptions"));
            builder.Services.Configure<PdaOptions>(builder.Configuration.GetSection("PdaOptions"));
            builder.Services.Configure<OAuth2Options>(builder.Configuration.GetSection("OAuth2"));
            builder.Services.Configure<WeChatClawBotOptions>(builder.Configuration.GetSection("WeChatClawBot"));
            builder.Services.Configure<Model.User.UserAvatarOptions>(builder.Configuration.GetSection("UserAvatar"));
            builder.Services.Configure<Model.ProcessBoard.ProcessFormulaOptions>(builder.Configuration.GetSection("ProcessBoard:Formula"));
            builder.Services.AddSingleton<ProcessBoardInitializationState>();
            builder.Services.AddHostedService<ProcessBoardHostedService>();
            builder.Services.AddHostedService<ProcessBoardExportHostedService>();
            builder.Services.AddMemoryCache();
            // 模型平台密钥通过 Data Protection 加密后保存到数据库，不允许配置管理接口返回明文。
            builder.Services.AddDataProtection().SetApplicationName("CalmAdmin.AgentModelProvider");
            builder.Services.AddSingleton<IAgentModelDatabaseInitializer, AgentModelDatabaseInitializer>();
            builder.Services.AddSingleton<IAgentSecretProtector, AgentSecretProtector>();
            builder.Services.AddSingleton<SystemDatabaseInitializer>();
            builder.Services.AddHostedService<SystemDatabaseInitializationHostedService>();
            // 在 Worker 和自动转发循环启动前，只初始化一次 EdgeGateway 中心数据库结构。
            builder.Services.AddHostedService<EdgeGatewayDatabaseInitializationHostedService>();
            builder.Services.AddHostedService<EdgeRealtimeFieldBackfillHostedService>();
            builder.Services.AddHostedService<EdgeDeviceCardPushHostedService>();
            builder.Services.AddHostedService<EdgeGatewayHostedService>();
            // 中心端自动扫描 SQL Server 队列并转发到 B 系统，运行开关和间隔来自数据库。
            builder.Services.AddHostedService<EdgeBAutoForwardHostedService>();
            // 日志中心仅保留最近七天的运行事件，启动时清理一次，之后每天清理一次。
            builder.Services.AddHostedService<EdgeGatewayLogCleanupHostedService>();
            // 数据追溯参数点按当前保留时长清理，并异步生成大数据导出文件。
            builder.Services.AddHostedService<EdgeDataTraceHostedService>();
            // 每分钟结算失活的后台用户会话，并按保留周期清理登录与操作审计日志。
            builder.Services.AddHostedService<UserActivityCleanupHostedService>();
            // 每十秒推进无新样本机械手的离线时间，统计服务不依赖看板页面是否打开。
            builder.Services.AddHostedService<EdgeDashboardStatisticsHostedService>();
            // 每五分钟按线体生成一次 AI 运行建议；页面只读取结果，不直接调用模型。
            builder.Services.AddHostedService<EdgeDashboardAiInsightHostedService>();
            // 每日按数据库配置的时间为每条启用线体生成独立看板日报。
            builder.Services.AddHostedService<EdgeDashboardExportHostedService>();
            // 延迟维护微信 ClawBot 的会话游标，不能阻塞 Web 服务启动。
            builder.Services.AddHostedService<WeChatClawBotConversationHostedService>();
            builder.Services.AddHostedService<WeChatClawBotAiHostedService>();
            #region JWT校验
            //增加鉴权逻辑
            JWTTokenOptions tokenOptions = new JWTTokenOptions();
            builder.Configuration.Bind("JWTTokenOptions", tokenOptions);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)//Scheme
             .AddJwtBearer(options =>  //这里是配置的鉴权的逻辑
             {
                 options.TokenValidationParameters = new TokenValidationParameters
                 {
                     //JWT有一些默认的属性，就是给鉴权时就可以筛选了
                     ValidateIssuer = true,//是否验证Issuer
                     ValidateAudience = true,//是否验证Audience
                     ValidateLifetime = true,//是否验证失效时间
                     // PDA Token 不设置过期时间；其他来源的 Token 仍必须包含并校验过期时间。
                     RequireExpirationTime = false,
                     ValidateIssuerSigningKey = true,//是否验证SecurityKey
                     ValidAudience = tokenOptions.Audience,//
                     ValidIssuer = tokenOptions.Issuer,//Issuer，这两项和前面签发jwt的设置一致
                     IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SecurityKey)),//拿到SecurityKey 
                 };
                 options.Events = new JwtBearerEvents
                 {
                     OnMessageReceived = context =>
                     {
                         if (context.HttpContext.Request.Path.StartsWithSegments("/ws/agent") &&
                             context.Request.Query.TryGetValue("access_token", out var accessToken))
                         {
                             context.Token = accessToken;
                         }

                         return Task.CompletedTask;
                     },
                     OnTokenValidated = async context =>
                     {
                         var hasExpiration = context.SecurityToken.ValidTo != DateTime.MinValue;
                         var isPdaToken = string.Equals(
                             context.Principal?.FindFirst("LoginSource")?.Value,
                             "PDA",
                             StringComparison.OrdinalIgnoreCase);

                         if (!hasExpiration && !isPdaToken)
                         {
                             context.Fail("无过期时间的 Token 仅允许用于 PDA");
                         }

                         if (!isPdaToken)
                         {
                             try
                             {
                                 var purpose = context.Principal?.FindFirst("TokenPurpose")?.Value;
                                 if (purpose is not ("portal" or "app"))
                                     throw new UnauthorizedAccessException("请使用统一入口重新登录");
                                 context.HttpContext.Items[typeof(Model.SystemApplications.ApplicationAccessContext)] =
                                     await context.HttpContext.RequestServices.GetRequiredService<ISystemApplicationService>()
                                         .GetAccessAsync(context.Principal!);
                             }
                             catch (UnauthorizedAccessException exception) { context.Fail(exception.Message); }
                         }
                     }
                 };
             })
             .AddJwtBearer(ExternalToolAuthenticationDefaults.Scheme, options =>
             {
                 // 外部 MCP/OpenAPI 仅接受服务端签发且 audience 专属的短期机器令牌，
                 // 明确拒绝后台用户登录 JWT，避免把后台会话凭据作为对外接口凭据使用。
                 options.TokenValidationParameters = new TokenValidationParameters
                 {
                     ValidateIssuer = true,
                     ValidateAudience = true,
                     ValidateLifetime = true,
                     RequireExpirationTime = true,
                     ValidateIssuerSigningKey = true,
                     ValidIssuer = tokenOptions.Issuer,
                     ValidAudience = ExternalToolAuthenticationDefaults.Audience,
                     IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tokenOptions.SecurityKey)),
                     ClockSkew = TimeSpan.FromMinutes(1)
                 };
                 options.Events = new JwtBearerEvents
                 {
                     OnTokenValidated = async context =>
                     {
                         if (context.Principal?.FindFirst("TokenPurpose")?.Value == "mcp")
                         {
                             try
                             {
                                 context.HttpContext.Items[typeof(Model.SystemApplications.ApplicationAccessContext)] =
                                     await context.HttpContext.RequestServices.GetRequiredService<ISystemApplicationService>()
                                         .GetAccessAsync(context.Principal, true);
                             }
                             catch (UnauthorizedAccessException exception) { context.Fail(exception.Message); }
                         }
                         else if (string.IsNullOrEmpty(context.Principal?.FindFirst("client_id")?.Value))
                         {
                             context.Fail("不是 MCP 专用令牌");
                         }
                     }
                 };
             });
            builder.Services.AddAuthorization(options => options.AddPolicy("McpAccess", policy =>
                policy.AddAuthenticationSchemes(ExternalToolAuthenticationDefaults.Scheme).RequireAuthenticatedUser()));
            #endregion
            //添加跨域策略
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy", opt =>
                     opt
                     .AllowAnyOrigin()
                     .AllowAnyHeader()
                     .AllowAnyMethod()
                     .WithExposedHeaders("Content?Disposition")//暴露 Content-Disposition 响应头，便于下载
                );
            });
            //设置Json返回的日期格式
            builder.Services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                options.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                         .AllowAnyHeader();
                });
            });

            #region ip监听
            // 步骤1：定义要监听的端口
            int listenPort = 8087;
            // 步骤2：获取本机所有可用的 IPv4 地址
            var availableIps = GetAvailableIPv4Addresses();

            // 步骤3：拼接监听地址（包含 localhost + 所有可用 IP + 0.0.0.0）
            var listenUrls = new List<string>
            {
            // 基础地址：localhost 和 0.0.0.0（监听所有接口）
            $"http://localhost:{listenPort}",
            $"http://0.0.0.0:{listenPort}"
            };
            // 添加本机所有可用 IP 的监听地址
            foreach (var ip in availableIps)
            {
                listenUrls.Add($"http://{ip}:{listenPort}");
            }

            // 步骤4：配置 Web 主机监听地址
         //   builder.WebHost.UseUrls(listenUrls.ToArray());

            // 核心方法：获取本机可用的 IPv4 地址（排除回环、虚拟网卡）
            List<string> GetAvailableIPv4Addresses()
            {
                var ipList = new List<string>();

                // 遍历所有网络接口
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 过滤条件：接口状态为启用、非回环、非虚拟网卡
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    // 遍历接口的 IP 地址信息
                    foreach (UnicastIPAddressInformation ipInfo in ni.GetIPProperties().UnicastAddresses)
                    {
                        // 只保留 IPv4 地址，且排除回环地址（127.x.x.x）
                        if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ipInfo.Address))
                        {
                            ipList.Add(ipInfo.Address.ToString());
                        }
                    }
                }

                // 去重并返回
                return ipList.Distinct().ToList();
            }
            #endregion


            //商品数据注入
            builder.Services.AddScoped<IBusinessBillRepository, BusinessBillRepository>();
            builder.Services.AddScoped<IBusinessBillService, BusinessBillService>();
            builder.Services.AddScoped<IProjectManageService, ProjectManageService>();
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("EmailOptions"));
            builder.Services.AddScoped<IEmailService, EmailService>();
            builder.Services.AddScoped<IWeChatClawBotService, WeChatClawBotService>();
            builder.Services.AddScoped<IWeChatClawBotAiService, WeChatClawBotAiService>();
            builder.Services.AddScoped<IEdgeDeviceCardPushService, EdgeDeviceCardPushService>();
            builder.Services.AddScoped<IEdgeModelFieldCatalogService, EdgeModelFieldCatalogService>();
            var weChatClawBotOptions = builder.Configuration.GetSection("WeChatClawBot").Get<WeChatClawBotOptions>() ?? new WeChatClawBotOptions();
            builder.Services.AddHttpClient(nameof(WeChatClawBotService), client =>
            {
                // 微信扫码和收消息使用约 35 秒的长轮询，客户端超时必须留出余量。
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(weChatClawBotOptions.HttpTimeoutSeconds, 40, 60));
            });
            builder.Services.AddScoped<IAgentToolRegistryService, AgentToolRegistryService>();
            builder.Services.AddScoped<IAgentToolDispatcher, AgentToolDispatcher>();
            builder.Services.AddScoped<IAgentFrameworkRuntime, MicrosoftAgentFrameworkRuntime>();
            builder.Services.AddScoped<IAgentBusinessToolExecutor, ProjectManagementAgentToolExecutor>();
            builder.Services.AddScoped<IAgentBusinessToolExecutor, AgentEmailBusinessToolExecutor>();
            builder.Services.AddScoped<IAgentService, AgentService>();
            builder.Services.AddHttpClient<IAgentModelClient, AgentModelClient>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });


        }

        /// <summary>
        /// 如果部署环境只覆盖了 calmadmin 连接，就沿用同一台 SQL Server 和账号，
        /// 仅将数据库名切换为独立的 EdgeGatewayLogs，避免生产环境漏配第二条连接。
        /// </summary>
        private static string BuildEdgeGatewayLogsConnectionString(string calmadminConnectionString)
        {
            if (string.IsNullOrWhiteSpace(calmadminConnectionString))
                throw new InvalidOperationException("未配置 calmadmin 数据库连接，无法生成 EdgeGatewayLogs 连接。");

            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = calmadminConnectionString
            };
            if (builder.ContainsKey("Initial Catalog"))
                builder["Initial Catalog"] = "EdgeGatewayLogs";
            else
                builder["Database"] = "EdgeGatewayLogs";
            return builder.ConnectionString;
        }
    }
}
