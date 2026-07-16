# 项目管理系统后端

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
