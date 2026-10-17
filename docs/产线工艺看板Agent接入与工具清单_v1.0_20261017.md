# 产线工艺看板 Agent 接入与工具清单 v1.0

日期：2026-10-17

## 1. 入口与接入方式

- Agent 编码：`process-board`，名称：**产线工艺看板 Agent**。
- 网页：**Agent 中心 → 智能助手 → 工作 → 产线工艺看板**。
- 隐藏的独立组件路由：`/agent/process-board`，组件 `/agent/process-board`，权限目录项 `ProcessBoardAgent`。
- 后端菜单初始化时补齐缺失目录项；已有统一助手菜单继续使用，不新增第二套聊天页面。系统 B 的独立菜单仍由管理员选择开放，不能因此自动开放整套业务。
- 微信：在“允许的 Agent”中勾选该 Agent，重新保存连接并按原流程验证密钥后即可使用；多选时也可发送 `@process-board 查询……` 指定。
- 全局悬浮查询助手自动聚合其只读工具；不会获得写工具。
- 模型平台配置页从注册服务发现新 Agent，可为其绑定 Turing / DeepSeek。未配置专属绑定时沿用现有启用平台回退规则，不写入新密钥。

## 2. 业务结构与数据复用

```text
统一助手 / 独立组件 / 已验证微信渠道
  → AgentService
  → Microsoft Agent Framework
  → 工具注册表与 AgentToolDispatcher
  → 写入计划（用户回复“确认”）或只读查询
  → ProcessBoardAgentExecutor
  → 当前系统、用户、业务权限与数据源/工艺线体授权校验
  → 既有 ProcessBoardService / 公式服务 / 共享映射 / 推送队列
  → 既有配置版本审计 + Agent 执行审计
```

不复制配置表、实时表或历史表，不创建第二套工艺业务。执行器拆分为入口授权、查询和命令文件；工具说明和 Schema 独立集中管理。

## 3. 工具清单

共 22 个工具：14 个查询/试算工具，8 个需要确认的写入工具。

| 工具 | 用途 |
| --- | --- |
| `process.overview` | 授权区域、线体、卡片、参数及追溯/曲线/推送数量概览 |
| `process.definitions` | 按类别、父级、关键字、启停分页查找配置摘要、ID、版本 |
| `process.definition` | 完整配置：字段、公式、绑定、上下限、流程、布局、推送规则 |
| `process.runtime` | 按线体、卡片、字段分页查询持久化的实际值、计算值、状态和数据时间；也支持线体级参数 |
| `process.sources` | SQL 分页查找授权设备，支持共享模型中文名搜索 |
| `process.source-fields` | 复用实时/历史字段库、最后实际值及共享中文展示映射 |
| `process.history` | 按字段、时间分页查询采样或变化/越限/恢复事件 |
| `process.curve` | 数据库时间桶聚合，最多 600 个点，支持原始值与计算值 |
| `process.config-history` | 分页查配置审计；指定 version 读取历史定义并校验历史来源授权 |
| `process.push-tasks` | 查看指定线体的推送记录及投递结果，不触发发送 |
| `process.recipients` | 查找已绑定微信接收人，不返回 token 或密钥 |
| `process.setting` | 查询采样间隔、保留小时、共享推送间隔和公式配置 |
| `process.formula-preview` | 使用既有 mXparser 服务试算变量、常量及数学函数，不代签授权声明 |
| `process.export-status` | 查询本人、当前系统的导出进度；完成时返回需登录的相对下载地址 |
| `process.create` | 新建 Region / Line / Card；新建父级确认后才能取得真实 ID 创建子级 |
| `process.update` | 局部修改名称、编码、排序、启停；不修改所属父级 |
| `process.field-upsert` | 新增/编辑单个参数，含来源、固定值/文本、公式变量函数、精度、上下限、追溯曲线、阈值、微信接收人和触发条件 |
| `process.flows-upsert` | 合并新增/修改工艺节点、坐标、关联卡片和连线 |
| `process.devices-bind` | 将真实设备合并绑定到工艺线体 |
| `process.layout-update` | 布局方式、宽高、每行卡片数、每行参数组数、流程/趋势分区及卡片坐标尺寸 |
| `process.setting-update` | 管理员调整采样、保留时间及共享发送间隔；保留公式授权声明不变 |
| `process.export-create` | 确认后创建本人、当前系统的异步采样/事件 CSV 导出 |

## 4. 编辑与确认规则

1. 先查 `definitions` 查找唯一目标，再通过 `definition` 读取 ID、版本和原配置。
2. 修改工具携带 `id + version + patch`。省略属性不覆盖原配置；允许清空的上/下限、固定文本明确传 `null`。
3. 参数按 `code` 更新，不会重复追加已有参数；数据源的局部属性合并时保留其余绑定信息。新增绑定必须匹配实际可用的实时/历史字段路径，不能编造路径。
4. 流程节点按节点 ID 合并、连线按起止节点合并、布局按卡片 ID 合并、设备按租户及设备标识合并；不删除未提交的已有配置。
5. 仍需遵守业务服务对父子关系、字段数量、关联卡片、公式长度、精度、阈值、上下限、接收人及尺寸的校验。
6. 写计划需要用户回复“确认”，沿用确认审计及微信十分钟确认有效期；只生成计划时不写入工艺配置。
7. 旧配置版本会被拒绝，不自动读取新版本强行覆盖。系统级设置要求携带查询时的 expected 原值并再次比对。
8. 涉及不同层级的新增应分阶段确认，不猜测数据库新生成的 ID；同一对象连续修改需使用上次保存返回的新版本。

本次联调同时修正了历史确认映射：过去仅凭有 ConfirmTime 就认为已确认，而旧实体会初始化该时间。现在必须存在实际 ConfirmedBy 才标为已确认，确保新 Agent 的“提出修改 → 回复确认”流程可靠。

## 5. 权限与边界

- 执行器不信任模型提交的用户或系统参数（Schema 不开放这类参数）。网页从当前登录会话读取授权；微信使用已验证渠道传入的真实授权解析器，每次执行/确认重新查询。
- 查看需要 `ProcessBoard` 或 `ProcessBoardConfig`；配置写入、公式试算、接收人及推送记录查询需要 `ProcessBoardConfig`。异步导出按既有业务，只需查看权限，但仍要求确认。
- 修改采样、保留及共享推送间隔还要求平台管理员；共享间隔会同时影响原设备卡片推送。
- 工艺线体资源编码仍为 `process:{lineId}`，来源权限仍为租户与设备组合。已有服务的限制、历史来源复核、导出用户及 A/B 所属校验全部保留。
- 微信仅对已经实现资源级校验的 ProcessBoard 工具放开受限数据范围；其他旧工具继续按原规则拒绝受限资源调用。
- 网页 Agent 的统一接口仍保留当前项目的全部资源授权入口门槛，本次没有放宽其他旧业务的访问权限。不要将隐藏菜单当成资源授权。
- 不开放删除，不直接改写真实采集值、历史采样、越限事件或审计记录，不执行 SQL、JavaScript 或操作系统脚本。
- 本次只消费共享中文映射，不代替平台级映射配置权限。
- 微信只配置推送规则、查记录，不新增自动群发或绕过限频的发送入口。
- 历史和实时查询每页最多 50 条；曲线最多 600 点；大数据使用既有异步导出上限、分页及文件限制。新工具不会在一次模型结果中展开全量历史。

## 6. 新增与修改文件（diff 清单）

后端新增：

- `Service/Agent/ProcessBoard/ProcessBoardAgentModuleProvider.cs`：独立 Agent 定义和业务约束。
- `Service/Agent/ProcessBoard/ProcessBoardAgentToolCatalog.cs`：22 个工具、Schema、说明、示例和风险属性。
- `Service/Agent/ProcessBoard/ProcessBoardAgentExecutor.cs`：授权、参数校验、局部补丁及执行入口。
- `Service/Agent/ProcessBoard/ProcessBoardAgentQueries.cs`：查询、实际值、曲线、审计与导出状态。
- `Service/Agent/ProcessBoard/ProcessBoardAgentCommands.cs`：受控配置写入和版本校验。
- `tests/ProcessBoardAgentSmoke/`：随机隔离 SQL 数据库、真实注册/调度/应用服务链路测试。
- 本说明文档。

后端修改：

- `Model/Agent/Definitions/AgentModuleSeeds.cs`：新 Agent 编码。
- `Service/Agent/AgentChannelExecutionScope.cs`：可选真实业务授权解析器，不伪造 JWT。
- `Service/WeChatClawBot/WeChatClawBotAiService.cs`：微信允许列表、渠道授权、受限资源处理。
- `Service/Agent/Definitions/QueryAssistantAgentModuleProvider.cs`：只读查询范围说明。
- `Service/Agent/AgentReplyFailureFeedback.cs`：兼容实际工具 DTO 的 Success 状态，正确说明查询已完成。
- `calmadmin/Config/AutoMapperConfigs.cs`：确认状态要求真实确认人。
- `calmadmin/Config/SystemApplicationAccessFilter.cs`、`calmadmin/WebSockets/AgentWebSocketHandler.cs`：识别新 Agent 入口权限。
- `Service/System/Initialization/SystemMenuInitializer.cs`：补齐隐藏组件路由目录项。

前端新增：

- `src/views/agent/process-board/index.vue`：独立业务组件（复用统一对话组件）。
- `tests/process-board-agent.mjs`：新组件、统一工作领域与路由编译检查。

前端修改：

- `src/views/agent/executor/index.vue`：新增工作领域、说明、建议问题和回复确认配置。
- `src/views/agent/assistant/index.vue`：识别 process-board 领域链接。
- `src/router/modules/agent.ts`：隐藏独立组件路由。

## 7. 验证与上线

- 随机隔离数据库验证 22 工具自动注册、只读助手聚合、确认前不写入、确认后真实保存、参数补丁、清空上下限、流程布局合并、版本冲突、真实来源路径校验、持久化实时值、曲线/历史、导出、审计、B 系统和数据源授权。
- 使用真实 AgentService 验证“提出修改 → 回复确认”，确认后不重新进行模型工具选择。
- 微信 AI 既有隔离测试及前端组件回归；前端生产构建。
- 全量 Vue 类型检查仍有项目既有的 42 处错误，本次修改的助手、领域组件及路由文件没有类型报错；没有将全量类型检查描述为通过。
- 测试不调用真实大模型，不发送真实微信消息；测试数据库只创建随机名称并严格校验后清理，不修改业务库。

更新前后端并重启后端后，菜单初始化补缺、Agent 注册服务首次访问时同步新模块和工具。没有新增业务表或列迁移。本次没有重启当前服务，没有改动真实模型密钥、授权或业务配置，也没有提交/推送 Git。
