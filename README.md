# 小型团队项目管理系统后端

基于 ASP.NET Core Web API、.NET 8、SqlSugar、SQL Server 开发的项目管理系统后端服务。系统为前端提供登录认证、用户权限、菜单管理、工作台统计、项目详情、任务管理、文件管理等接口能力。

## 项目简介

本项目是小型团队项目管理系统的后端工程，采用分层结构组织代码：

- `calmadmin`：Web API 启动项目，包含控制器、Swagger、鉴权、配置等。
- `Interface`：服务接口定义。
- `Model`：实体、DTO、配置模型。
- `Service`：业务服务、数据库访问、JWT 生成、项目管理业务逻辑。

当前主要功能：

- 登录认证：账号密码登录、JWT Token 生成。
- 用户管理：账号、密码、角色、权限维护。
- 角色管理：角色增删改查、角色权限维护。
- 菜单管理：系统菜单、路由信息维护。
- 工作台：项目统计、任务统计、项目状态分布、负责人项目统计、近期项目。
- 项目详情：项目分页、新增、编辑、删除、状态维护、负责人信息。
- 任务管理：任务分页、任务树、任务类型、任务状态、任务进度统计。
- 文件管理：文件上传、同名覆盖、下载、在线预览、删除、项目目录文件数量统计。

## 技术栈

- .NET 8
- ASP.NET Core Web API
- SQL Server
- SqlSugarCore
- Autofac
- AutoMapper
- JWT Bearer Authentication
- Newtonsoft.Json
- Swagger / Swashbuckle

## 环境要求

请先安装：

- .NET SDK 8.0+
- SQL Server 2016+ 或兼容版本
- Visual Studio 2022 / Rider / VS Code，任选其一

检查 .NET 版本：

```bash
dotnet --version
```

## 快速开始

进入后端目录：

```bash
cd project-management-backend-system
```

还原依赖：

```bash
dotnet restore
```

编译项目：

```bash
dotnet build calmadmin/calmadmin.csproj -c Release
```

启动服务：

```bash
dotnet run --project calmadmin/calmadmin.csproj
```

默认开发地址：

```text
http://localhost:8089
```

Swagger 地址：

```text
http://localhost:8089/index.html
```

如果部署到服务器，可通过实际地址访问，例如：

```text
http://10.148.201.223:8087/swagger/index.html
```

## 常用命令

```bash
# 还原依赖
dotnet restore

# Debug 编译
dotnet build calmadmin/calmadmin.csproj

# Release 编译
dotnet build calmadmin/calmadmin.csproj -c Release

# 启动 API 服务
dotnet run --project calmadmin/calmadmin.csproj

# 发布到 publish 目录
dotnet publish calmadmin/calmadmin.csproj -c Release -o publish
```

## 配置说明

主要配置文件：

```text
calmadmin/appsettings.json
calmadmin/appsettings.Development.json
```

### 数据库连接

数据库连接配置在 `ConnectionStrings` 节点中。

核心业务库：

```json
{
  "ConnectionStrings": {
    "calmadmin": "Server=127.0.0.1;Database=calmadmin;User ID=sa;Password=123;Encrypt=True;TrustServerCertificate=True;",
    "EdgeGatewayLogs": "Server=127.0.0.1;Database=EdgeGatewayLogs;User ID=sa;Password=123;Encrypt=True;TrustServerCertificate=True;"
  }
}
```

请根据实际环境修改：

- `Server`：数据库地址
- `Database`：数据库名称
- `User ID`：数据库账号
- `Password`：数据库密码

项目管理、用户、角色、菜单等核心功能使用 `calmadmin` 连接。

WebSocket 接收报文使用独立的 `EdgeGatewayLogs` 数据库，按 `EdgeGatewayWebSocketReceiveRecord_yyyyMMdd` 日表保存，默认保留最近 7 个自然日。应用首次启动时会使用 `calmadmin` 账号自动创建该数据库；如果部署配置没有单独提供 `EdgeGatewayLogs`，程序会沿用 `calmadmin` 的服务器和账号，仅切换数据库名。该账号需要具备创建数据库、建表和索引的权限。

### JWT 配置

JWT 配置在 `JWTTokenOptions` 节点中：

```json
{
  "JWTTokenOptions": {
    "Audience": "http://localhost:5294",
    "Issuer": "http://localhost:5294",
    "SecurityKey": "your-security-key"
  }
}
```

生产环境建议更换 `SecurityKey`，并妥善保管。

### Agent 联网模型配置

Agent 联网调用配置在 `AgentOptions` 节点中：

```json
{
  "AgentOptions": {
    "Enabled": true,
    "Provider": "OpenAICompatible",
    "BaseUrl": "https://api.openai.com/v1",
    "EndpointPath": "/chat/completions",
    "ApiKey": "your-api-key",
    "ApiKeyHeader": "Authorization",
    "Model": "gpt-4o-mini",
    "Temperature": 0.2,
    "MaxTokens": 1200,
    "TimeoutSeconds": 60
  }
}
```

`ApiKey` 就是联网模型密钥填写位置。生产环境建议通过环境变量、部署平台密钥配置或服务器上的 `appsettings.Production.json` 注入，不要把真实密钥提交到代码仓库。

内置 Provider 预设：

```json
{
  "AgentOptions": {
    "Enabled": true,
    "Provider": "DeepSeek",
    "ApiKey": "your-deepseek-api-key"
  }
}
```

`DeepSeek` 会默认使用：

- `BaseUrl`: `https://api.deepseek.com`
- `EndpointPath`: `/chat/completions`
- `Model`: `deepseek-chat`

```json
{
  "AgentOptions": {
    "Enabled": true,
    "Provider": "Qwen",
    "ApiKey": "your-dashscope-api-key"
  }
}
```

`Qwen`、`DashScope`、`Tongyi`、`千问`、`通义千问` 都会默认使用：

- `BaseUrl`: `https://dashscope.aliyuncs.com/compatible-mode/v1`
- `EndpointPath`: `/chat/completions`
- `Model`: `qwen-plus`

如果你手动配置了 `BaseUrl` 或 `Model`，系统会优先使用你手动填写的值。

### Agent 工具调用架构

项目管理 Agent 使用统一工具调用链路：

```text
用户对话 -> LLM 自主选择工具 -> 通用调度器校验 -> 业务执行器 -> 项目业务服务 -> Agent 自然语言回复
```

运行时表由 SqlSugar CodeFirst 自动初始化：

- `AgentDefinition`：Agent 定义和系统提示词，可继续注册其他业务 Agent。
- `AgentToolRegistry`：工具元信息、JSON Schema、权限、风险等级和业务服务映射。
- `AgentExecutionPlan`：工具参数、待确认状态、确认人和实际执行结果。
- `AgentInvocationLog`：调度、确认、执行各阶段的请求、结果、耗时和错误日志。
- `AgentConversation` / `AgentMessage`：会话及自然语言消息。

写操作不会由 LLM 直接执行。调度器会先检查工具启用状态、当前用户权限和 `InputSchema`，生成待确认计划；确认时再次校验参数哈希、权限和 Schema，之后才调用业务执行器。项目和任务删除工具未注册，Agent 无法发起删除。

Agent 提问通过 `GET /ws/agent?access_token={JWT}` 升级为 WebSocket，不再使用 HTTP POST。客户端发送 `message` 事件后，服务端会依次推送 `accepted`、`progress`、`heartbeat`、`chunk`、`completed` 或 `error` 事件；会话查询、执行计划确认和邮件发送仍使用普通 HTTP 接口。Agent 项目 CSV 作为 WebSocket 消息附件交给 LLM 分析，LLM 生成 `project.create` 待确认计划，用户确认后才会批量写入数据库，不再提供直接写库的 Agent CSV HTTP 接口。

### 后台管理系统 OAuth 2.0 登录

后台登录页支持 OAuth 2.0 授权码登录，配置位于 `calmadmin/appsettings.json` 的 `OAuth2` 节点。服务端完成授权码交换和用户信息查询，浏览器回到登录页时只携带 60 秒内有效、仅能兑换一次的登录票据，不会把第三方 Access Token 暴露给前端。

OAuth 用户信息会按配置字段同步到 `sys_user_info`：

- 通用 OAuth 模式可通过 `UserNameClaim`、`PasswordClaim`、`NickNameClaim`、`EmailClaim` 映射用户信息。
- `tcl_siam` 模式兼容《应用接入规范 v3.0》：Token 响应为 `access_token=...&expires=...`，Profile 使用 POST 表单提交 `access_token`，账号取 `id`，邮箱取 `attributes` 数组中的 `mail`。
- TCL SIAM Profile 不返回用户密码，`SynchronizePassword=false`：已有账号保留原密码，仅同步昵称、邮箱和时间；新账号生成随机本地密码，只能通过统一认证登录。
- 本地不存在账号时自动新增，默认角色为 `R_USER`，默认权限为 `Dashboard`，可通过 `DefaultRoles`、`DefaultPermissionKeys` 调整。
- 已停用的本地账号仍保持停用，完成信息同步后拒绝登录。

生产环境不要把服务秘钥写入仓库，使用环境变量注入：

```text
OAuth2__ClientSecret=实际服务秘钥
```

TCL SIAM 文档中的 UAT 接口示例为：

```text
AuthorizationEndpoint=https://login-uat.tcl.com/siam/oauth2.0/authorize
TokenEndpoint=https://login-uat.tcl.com/siam/oauth2.0/accessToken
UserInfoEndpoint=https://login-uat.tcl.com/siam/oauth2.0/profile
```

生产环境请以统一身份平台登记的实际域名为准，不要把普通登录页地址当作 `AuthorizationEndpoint`。同时需要在平台登记固定回调地址，例如 `http://localhost:8089/api/auth/oauth2/callback`；确认平台会原样回传 `state` 后，再将 `OAuth2:Enabled` 设置为 `true`。

### Agent 中心：Turing 中转平台

Agent 中心的“模型平台配置”内置 Turing 预置项，使用 OpenAI Chat Completions 兼容协议：

```text
Base URL:      https://live-turing.cn.llm.tcljd.com/api/v1
Endpoint Path: /chat/completions
认证方式:       Authorization: Bearer {API Key}
```

用户首次进入 Agent 中心的模型平台配置页时，系统会为该用户创建禁用状态的 `turing` 预置项，不会写入任何密钥。每位用户填写自己实际开通的模型编码和 API Key 后启用，并为项目管理助手或边缘计算助手设置自己的主平台、备用平台及优先级。

- 密钥在服务端使用 Data Protection 加密保存，查询接口只返回掩码。
- 每个账号的模型平台、密钥和 Agent 绑定均按用户隔离；查询、修改、测试和实际调用只会访问当前用户自己的配置。历史全局配置不会自动继承给任何用户。
- 拥有 Agent 中心权限的角色可以管理自己的配置；默认工程师角色已补齐“模型平台配置”入口权限。
- Turing 调用会附加顶层 `turing_options`，用于设置平台超时、重试次数和可选的模型 Fallback；调用日志保留追踪 ID、实际模型、耗时和 Token 用量，不保存对话正文。
- “测试连接”会发送一次最小请求并产生少量 Token 用量。

模型编码、可用能力和计费以 [Turing 接口文档](https://live-turing-docs.cn.llm.tcljd.com/api-guides/core-apis/choosing-an-endpoint/) 为准。当前 Agent 使用 Chat Completions；如未来需要 GPT-5 的状态化推理/MCP 或 Claude 原生 Messages 能力，再扩展对应协议适配器。

新增其他 Agent 时，可在 `AgentDefinition` 中增加定义，在 `AgentToolRegistry` 中按新的 `AgentCode` 注册工具，并实现 `IAgentBusinessToolExecutor`。通用调度、确认和日志逻辑无需复制。

### 服务端口

开发环境端口可在：

```text
calmadmin/Properties/launchSettings.json
```

中查看或修改：

```json
{
  "applicationUrl": "http://localhost:8089"
}
```

也可以启动时指定：

```bash
dotnet run --project calmadmin/calmadmin.csproj --urls http://0.0.0.0:8089
```

## 数据库说明

后端使用 SqlSugar CodeFirst 初始化部分业务表，例如：

- 项目表
- 任务表
- 文件表
- 任务类型配置表
- 用户权限相关表

首次启动前请确认：

- SQL Server 服务正常运行。
- `appsettings.json` 中数据库连接可用。
- 数据库账号具备建表、查询、插入、更新权限。

## 接口说明

Swagger：

```text
http://localhost:8089/index.html
```

部署环境 Swagger：

```text
http://10.148.201.223:8087/swagger/index.html
```

主要接口分组：

- `/api/auth`：登录认证
- `/api/UserInfo`：用户管理
- `/api/Role`：角色管理
- `/api/Menu`：菜单管理
- `/api/ProjectManage`：项目管理、任务管理、文件管理、工作台

项目管理核心接口：

```text
GET  /api/ProjectManage/dashboard
GET  /api/ProjectManage/projects
POST /api/ProjectManage/projects
POST /api/ProjectManage/projects/{id}
POST /api/ProjectManage/projects/delete
GET  /api/ProjectManage/tasks
GET  /api/ProjectManage/tasks/tree
POST /api/ProjectManage/tasks
POST /api/ProjectManage/tasks/{id}
POST /api/ProjectManage/tasks/delete
GET  /api/ProjectManage/files
POST /api/ProjectManage/files
GET  /api/ProjectManage/files/{id}/download
GET  /api/ProjectManage/files/{id}/preview
POST /api/ProjectManage/files/delete
POST /api/ProjectManage/files/project-counts
```

详细接口文档可查看项目根目录：

```text
../docs/项目管理后端接口文档.md
```

## 文件上传说明

文件管理上传接口：

```text
POST /api/ProjectManage/files
```

文件保存规则：

- 文件保存到 `calmadmin/uploads/{项目名称}/原文件名`。
- 上传后不修改原始文件名。
- 同一项目目录下同名文件会覆盖物理文件。
- 数据库同一项目同名文件会更新原记录，不新增重复记录。
- 删除文件时会同步删除物理文件和软删除数据库记录。

部署时请确认应用目录有 `uploads` 写入权限。

## 项目结构

```text
project-management-backend-system
├── calmadmin
│   ├── Config              # 启动配置、依赖注入、JWT、数据库配置
│   ├── Controllers         # API 控制器
│   ├── Properties          # 启动配置
│   ├── appsettings.json    # 应用配置
│   ├── Program.cs          # 程序入口
│   └── calmadmin.csproj    # Web API 项目
├── Interface               # 服务接口层
├── Model                   # 实体、DTO、配置模型
├── Service                 # 业务服务层
├── .gitignore
└── README.md
```

## 本地联调前端

前端开发环境通过 Vite 代理访问后端。

前端 `.env.development` 示例：

```env
VITE_API_URL = /
VITE_API_PROXY_URL = http://localhost:8089/
```

后端启动后，前端请求：

```text
/api/xxx
```

会被代理到：

```text
http://localhost:8089/api/xxx
```

生产环境部署时，Vite 代理不会生效，需要在 IIS 或 Nginx 中配置 `/api` 反向代理到后端服务。

## 发布部署

发布命令：

```bash
dotnet publish calmadmin/calmadmin.csproj -c Release -o publish
```

发布后将 `publish` 目录部署到服务器。

部署注意事项：

- 修改 `publish/appsettings.json` 中数据库连接。
- 确认服务器已安装 .NET 8 Runtime。
- 确认服务端口已开放。
- 确认应用目录有 `uploads` 写入权限。
- 如前端和后端分开部署，请配置跨域或反向代理。

## IIS 部署建议

1. 安装 .NET 8 Hosting Bundle。
2. 在 IIS 中新建站点或应用程序。
3. 物理路径指向 `publish` 目录。
4. 应用程序池设置为「无托管代码」。
5. 绑定端口，例如 `8087`。
6. 确认站点目录具备读写权限，尤其是 `uploads` 目录。

## Gitee 使用说明

首次提交到 Gitee：

```bash
git init
git add .
git commit -m "init project management backend"
git remote add origin https://gitee.com/你的账号/你的仓库.git
git push -u origin master
```

如果 Gitee 默认分支是 `main`：

```bash
git branch -M main
git push -u origin main
```

建议不要提交以下目录：

```text
bin
obj
.vs
publish
uploads
```

当前 `.gitignore` 已忽略各项目的 `bin`、`obj` 等编译产物。建议根据部署情况继续补充：

```gitignore
publish/
calmadmin/uploads/
*.user
*.suo
.vs/
```

## 常见问题

### 1. Swagger 打不开

确认服务是否启动，并访问：

```text
http://localhost:8089/index.html
```

当前 Swagger UI 的 `RoutePrefix` 为空，因此本地默认不是 `/swagger/index.html`，而是站点根路径 `/index.html`。

部署环境如果配置了其它 Swagger 路径，以实际部署为准。

### 2. 登录后接口返回 401

检查：

- 前端是否携带 `Authorization: Bearer {token}`。
- JWT `Issuer`、`Audience`、`SecurityKey` 是否一致。
- 后端是否调用了 `UseAuthentication()` 和 `UseAuthorization()`。

### 3. 前端请求接口 405

通常是前端生产环境没有正确配置 `/api` 反向代理，请检查 IIS/Nginx 代理规则。

### 4. 数据库连接失败

检查：

- SQL Server 是否启动。
- `appsettings.json` 连接字符串是否正确。
- 数据库账号密码是否正确。
- 服务器防火墙是否放行数据库端口。
- `Encrypt=True;TrustServerCertificate=True;` 是否符合当前 SQL Server 配置。

### 5. 文件上传失败

检查：

- 后端服务账号是否有目录写入权限。
- `uploads` 目录是否可创建。
- 请求是否使用 `multipart/form-data`。
- 是否传入 `projectId` 和 `projectName`。

## 相关文档

- 前端 README：`../project-management-system/README.md`
- 前端操作说明：`../docs/项目管理前端操作说明.md`
- 后端接口文档：`../docs/项目管理后端接口文档.md`
- Swagger 调试地址：`http://10.148.201.223:8087/swagger/index.html`
