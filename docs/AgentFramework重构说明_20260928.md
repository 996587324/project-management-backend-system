# Agent Framework 重构说明

**改造日期：** 2026-09-28
**适用项目：** `project-management-backend-system`
**改造范围：** 后端 Agent 中心的运行与工具编排核心
**前端、数据库、MCP：** 本次不修改接口契约和表结构

## 1. 改造结论

系统 Agent 的运行核心已由 `AgentService` 内部的手工“模型选工具 → 循环调用工具 → 再请求模型整理回答”流程，改为基于 **Microsoft Agent Framework** 的标准 Agent 运行流程。

本次不是替换模型平台，也不是重写现有的项目管理、生产看板、设备卡片或边缘计算业务工具。模型平台和领域业务工具保留，只将它们接入标准的 Agent 框架。

| 项目 | 改造前 | 改造后 |
| --- | --- | --- |
| Agent 编排 | `AgentService` 手工选择并循环执行工具 | `ChatClientAgent` 负责工具调用循环 |
| 模型接入 | `IAgentModelClient` 直接由应用服务调用 | 通过自定义 `IChatClient` 接入 Agent Framework |
| 工具定义 | AgentService 读取工具后自行分发 | 动态转换为标准 `AIFunction` 后由框架调度 |
| 工具实际执行 | `AgentToolDispatcher` | 保持不变 |
| 权限、参数校验、审计 | `AgentToolDispatcher` | 保持不变 |
| 写操作确认 | 生成 `PendingConfirmation` 后用户回复“确认” | 保持不变，框架不能绕过确认 |
| Turing / DeepSeek | 原有模型配置、日志、回退 | 保持不变 |
| 前端 Agent 接口 | 现有会话、进度、工具调用接口 | 保持不变 |

## 2. 当前业务框架

```text
前端 Agent 中心
  │  发送消息 / 查看进度 / 回复“确认”
  ▼
AgentService
  │  会话、历史消息、Agent 定义、模式判断
  ▼
MicrosoftAgentFrameworkRuntime
  │
  ├─ ChatClientAgent（Microsoft Agent Framework）
  │    ├─ Chat 模式：直接生成自然语言回答
  │    └─ Work 模式：解析工具调用，自动管理工具调用轮次
  │
  ├─ AgentFrameworkModelChatClient（IChatClient 适配器）
  │    └─ IAgentModelClient
  │         ├─ 模型配置中心
  │         ├─ Turing / DeepSeek 调用
  │         ├─ DSML 兼容
  │         ├─ 调用日志
  │         └─ 故障回退
  │
  └─ AgentFrameworkToolFunction（动态 AIFunction）
       ▼
     AgentToolDispatcher
       ├─ 工具是否注册、启用
       ├─ 用户权限校验
       ├─ JSON Schema 参数校验
       ├─ 审计与执行计划记录
       ├─ 读操作：立即执行
       └─ 写操作：PendingConfirmation，等待用户回复“确认”
            ▼
          现有领域工具执行器
            ├─ 项目管理
            ├─ 生产看板与线体、设备、机械手、图表配置
            ├─ 设备卡片看板
            ├─ 边缘计算中心
            └─ 其他已登记 Agent 工具
```

## 3. 实际执行流程

### 3.1 聊天模式

1. 前端发送聊天消息。
2. `AgentService` 保存用户消息、读取最近会话历史。
3. 运行时创建 `ChatClientAgent`，但不传入业务工具。
4. 适配器继续使用当前模型配置中心指定的 Turing 或 DeepSeek 生成回答。
5. 保存助手回答并返回前端。

聊天模式仍是开放聊天，不会调用业务写工具。

### 3.2 工作模式（查询、配置、编辑）

1. `AgentService` 按 Agent 编码读取当前启用的工具及其 JSON Schema。
2. 每项工具动态转为 Microsoft Agent Framework 的 `AIFunction`。
3. 模型根据自然语言请求选择工具和参数。
4. `ChatClientAgent` 调用对应函数；函数仍调用本项目的 `AgentToolDispatcher`。
5. 查询类工具直接执行；写类工具只创建待确认执行计划。
6. 框架把工具结果再次交给模型生成自然语言说明。
7. 用户回复“确认”时，现有确认逻辑执行待确认计划，并记录审计日志。

### 3.3 进度推送

原有前端进度事件继续保留：

| 阶段 | 含义 |
| --- | --- |
| `selecting_tools` | 正在理解问题、选择工具 |
| `executing_tool` | 某个工具已被框架决定调用，正在进入业务处理 |
| `generating_reply` | 工具处理结束，正在组织自然语言回复 |
| `saving_result` | 正在保存会话消息 |

## 4. 变更文件

### 新增

| 文件 | 职责 |
| --- | --- |
| `Interface/Agent/IAgentFrameworkRuntime.cs` | 定义 Agent 框架运行时接口 |
| `Model/Agent/Runtime/AgentFrameworkRuntimeDtos.cs` | 运行时输入、输出和进度回调模型 |
| `Service/Agent/Framework/MicrosoftAgentFrameworkRuntime.cs` | 创建并运行 `ChatClientAgent` |
| `Service/Agent/Framework/AgentFrameworkModelChatClient.cs` | 将既有模型客户端适配成 `IChatClient` |
| `Service/Agent/Framework/AgentFrameworkToolFunction.cs` | 将数据库工具定义适配成 `AIFunction` |
| `Service/Agent/Framework/AgentFrameworkExecutionContext.cs` | 保存单次请求上下文、工具结果与进度通知 |

### 修改

| 文件 | 修改内容 |
| --- | --- |
| `Service/Agent/AgentService.cs` | 改为调用 `IAgentFrameworkRuntime`，移除手工工具循环和手工最终回答逻辑 |
| `calmadmin/Config/HostBuilderExtend.cs` | 注册 `IAgentFrameworkRuntime` 的依赖注入实现 |
| `Service/Service.csproj` | 增加 `Microsoft.Agents.AI` 1.22.0 依赖 |
| `calmadmin/Program.cs` | 删除未使用的 Microsoft Graph 命名空间 |
| `calmadmin/calmadmin.csproj` | 删除未使用的 Microsoft Graph 包，避免旧 Kiota/JSON 依赖限制与新框架冲突 |

## 5. 本次明确未修改的业务

- Agent 工具注册数据、工具名称、参数 Schema 与权限编码。
- `AgentToolDispatcher` 的权限校验、执行计划、参数哈希校验、操作审计。
- 用户回复“确认”后再执行写操作的规则。
- 项目管理 Agent 的 CSV 导入待确认流程。
- 生产看板、设备卡片、边缘计算中心等既有领域工具实现。
- 模型配置中心中的 Turing、DeepSeek 配置和模型调用日志。
- 前端 Agent 中心页面、WebSocket 消息格式、HTTP API 路径。
- MCP V2 协议、外部工具开放能力。
- 数据库表结构与历史数据。

## 6. 兼容性与风险控制

### 6.1 写操作不会被自动落库

Microsoft Agent Framework 默认会自动调用已提供的函数。本项目在函数内部仍使用 `AgentToolDispatcher`，而调度器对写工具会返回 `PendingConfirmation`，并不直接调用领域写操作。因此框架不能跳过人工确认。

### 6.2 模型兼容策略

没有直接使用 OpenAI 专属 Agent Provider，而是通过 `IChatClient` 适配既有 `IAgentModelClient`。这样 Turing 的 DSML 返回结构、DeepSeek 兼容调用、模型平台配置和调用日志均不会因重构丢失。

### 6.3 删除旧 Graph 依赖

检查发现 `Microsoft.Graph` 5.12.0 在项目中只有一个未使用的 `using`，没有实际业务调用。该包会带入旧版 Kiota 与 `System.Text.Json` 版本限制；删除后，框架依赖还原不再出现该类冲突警告。

## 7. 验证结果

已执行：

```powershell
dotnet restore calmadmin/calmadmin.csproj
dotnet build calmadmin/calmadmin.csproj --no-restore
```

结果：**还原成功，编译成功，0 个错误。**

当前仍存在项目原有的 AutoMapper 安全公告和部分可空引用提示；它们不由本次 Agent Framework 重构引入。

## 8. 上线后建议的验收场景

1. Chat 模式：发送普通问题，确认模型平台可返回自然语言结果。
2. Work 查询：查询一个项目、生产看板或设备数据，确认工具立即执行并返回结果。
3. Work 写入：要求新增或编辑数据，确认只生成待确认计划而不立即修改数据。
4. 回复“确认”：确认同一会话中待确认计划执行成功，并可在 Agent 工具调用记录中查询审计数据。
5. 无权限账户：调用受限工具，确认得到权限拒绝结果且没有业务数据变更。
6. 模型不可用：确认返回可读错误信息，服务本身不阻塞或崩溃。
