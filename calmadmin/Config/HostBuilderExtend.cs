using Autofac;
using Autofac.Extensions.DependencyInjection;
using calmadmin.HostedServices;
using Interface;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Model.Agent;
using Model.EdgeGateway;
using Model.Other;
using Newtonsoft.Json;
using Service;
using SqlSugar;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            string conn8 = builder.Configuration.GetConnectionString("RoleMenuDb");
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
                    var db = new SqlSugarScope(new List<ConnectionConfig>
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
                            ConfigId = "edge-gateway",
                            ConnectionString = $"Data Source={edgeDatabasePath}",
                            DbType = DbType.Sqlite,
                            // 本地队列只使用一个持续打开的连接，所有访问由单例数据库服务串行执行。
                            IsAutoCloseConnection = false,
                            InitKeyType = InitKeyType.Attribute
                        }
                    },
                    // AOP 配置在 ScopeConfig 回调里
                    db =>
                    {
                        db.Aop.OnLogExecuted = (sql, pars) =>
                        {
                            Console.WriteLine();
                            Console.WriteLine($"{DateTime.Now:yyyyMMdd HH:mm:ss}，Sql语句：{sql}");
                            Console.WriteLine("===========================================================================");
                        };
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
            builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("AgentOptions"));
            // 在 Worker 和自动转发循环启动前，只初始化一次 EdgeGateway 中心数据库结构。
            builder.Services.AddHostedService<EdgeGatewayDatabaseInitializationHostedService>();
            builder.Services.AddHostedService<EdgeGatewayHostedService>();
            // 中心端自动扫描 SQL Server 队列并转发到 B 系统，运行开关和间隔来自数据库。
            builder.Services.AddHostedService<EdgeBAutoForwardHostedService>();
            // 日志中心仅保留最近七天的运行事件，启动时清理一次，之后每天清理一次。
            builder.Services.AddHostedService<EdgeGatewayLogCleanupHostedService>();
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
                     }
                 };
             });
            #endregion
            //添加跨域策略
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy", opt =>
                     opt
                     .AllowAnyOrigin()
                     .AllowAnyHeader()
                     .AllowAnyMethod()
                     .WithExposedHeaders("Content‑Disposition")//暴露 Content-Disposition 响应头，便于下载
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
            int listenPort = 8089;
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
            builder.Services.AddScoped<IAgentToolRegistryService, AgentToolRegistryService>();
            builder.Services.AddScoped<IAgentToolDispatcher, AgentToolDispatcher>();
            builder.Services.AddScoped<IAgentBusinessToolExecutor, ProjectManagementAgentToolExecutor>();
            builder.Services.AddScoped<IAgentBusinessToolExecutor, AgentEmailBusinessToolExecutor>();
            builder.Services.AddScoped<IAgentService, AgentService>();
            builder.Services.AddHttpClient<IAgentModelClient, AgentModelClient>(client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            });


        }
    }
}
