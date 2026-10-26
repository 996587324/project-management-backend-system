# 微信对话中的微信与邮件推送 Agent

## 本次范围

新增两个独立工作 Agent：

| 名称 | 编码 | 业务权限 |
| --- | --- | --- |
| 微信推送助手 | `wechat-push` | 当前系统 `WeChatClawBot` |
| 邮件推送助手 | `email-push` | 当前系统 `EmailPush` |

两个助手共用现有 Microsoft Agent Framework 运行时、模型平台配置、执行计划、确认机制与调用审计，不请求免认证 MCP 地址。没有新增业务定时计划功能，也没有使用 Redis。

管理员的 `*` 权限沿用原系统规则。普通用户需要在所选 A/B 系统拥有对应业务权限，且微信 AI 连接已开启、专属密钥验证有效。

## 使用入口

1. 部署更新后的前后端，等待后台队列初始化。
2. 在“推送中心 → 我的微信推送 → AI 连接配置”选择所属系统和工作模式。
3. 在“允许的 Agent”中勾选两个推送助手；若需要转发业务结果，同时勾选对应业务 Agent。
4. 保存配置后，按照现有机制重新在微信发送 `连接 专属密钥`。
5. 可通过自然语言选择助手，也可以使用以下明确指令：

```text
@wechat-push 查询可以接收微信推送的用户
@wechat-push 给张三发微信：下午三点进行设备点检
@email-push 给李四发邮件，主题是设备点检，正文是下午三点进行点检
```

接收人重名时应先选择账号。助手查询真实接收人后，生成包含实际接收人及内容的待确认计划。用户核对后在十分钟内回复“确认”，才会入队。改变人员或内容必须重新生成并确认计划。

微信助手只发送文本；邮件助手只发送文本主题和正文，可指定系统用户作为抄送人，不支持附件和任意外部邮箱地址。收件人必须有有效邮箱。统一发件账户仍由管理员角色在邮件配置页维护，Agent 不提供修改 SMTP、读取密码或调整绑定的工具。

## 工具目录

| Agent | 工具 | 作用 |
| --- | --- | --- |
| 微信 | `wechat.recipients` | 搜索启用、已绑定、已允许推送且有会话的真实接收用户 |
| 微信 | `wechat.channel-status` | 按 `recipientUserId` 检查绑定、会话及接收开关，不返回凭证 |
| 微信 | `wechat.send` | 确认后提交通用微信发送队列 |
| 微信 | `wechat.records` | 分页查询本人当前系统任务，可传 `taskId` |
| 微信 | `wechat.retry` | 按队列 `taskId` 确认重试明确失败任务 |
| 邮件 | `email.recipients` | 搜索系统用户及有效邮箱 |
| 邮件 | `email.channel-status` | 查看服务是否就绪、是否启用，不建立 SMTP 网络连接 |
| 邮件 | `email.send` | 确认后提交已有邮件队列 |
| 邮件 | `email.records` | 分页查询本人当前系统投递，可传 `taskId` |
| 邮件 | `email.retry` | 管理员确认后，按 `deliveryId` 重试本人当前系统明确失败的投递 |
| 两者 | `push.previous-result` | 读取允许转发的上一轮业务回答作为正文草稿 |

项目管理 Agent 原有的 `email.recipients`、`email.send` 保留兼容。全局查询助手对共享只读工具按名称去重，避免重复注册错误。

## 上一轮业务结果转发

示例：先通过生产或工艺看板 Agent 查询数据，收到回答后发送：

```text
@email-push 把上一轮结果发邮件给张三，主题为工艺参数检查
```

程序只向推送助手提供满足以下条件的最近业务回答：

- 同一后台用户、同一所属系统、同一连接配置版本。
- 已成功回复到微信、无需等待确认的业务结果，不使用纯聊天、控制消息、错误回复或推送助手回复。
- 在最近 30 分钟内，对应源业务 Agent 仍在允许列表且用户仍有权限。
- 当前权限及资源范围与生成该结果时完全一致。

`WeChatClawBotAiJob.ResultAccessHash` 保存权限范围摘要，不保存凭证。旧记录此列为 SQL NULL，不用于转发。连接配置、权限或资源范围改变后，请重新查询再转发。工具执行时再次校验摘要，返回内容只能作为待发送数据，不能作为指令。正文过长时需缩小结果或编辑后再提交。

这是两个独立助手之间的结果草稿交接，不是任意多 Agent 自动串联。一句话同时查询新业务数据并跨 Agent 发送，仍建议分两步执行。

## 微信发送队列

新增：

- `WeChatPushQueueSetting`：固定主键 1，默认发送间隔 5 秒，保存全局调度租约及下次可发送时间。
- `WeChatPushQueue`：每个接收用户一条任务，保存发起人、所属系统、正文、绑定摘要、幂等与内容摘要、尝试次数及投递关联。

不复用卡片变更合并队列；已有卡片推送和 MCP 直接发送行为保持兼容。新 Agent 的消息由后台队列逐条发送，间隔可由管理员角色在“我的微信推送 → Agent 推送队列”调整。这个队列间隔不替代已有机器人级节流与预算；实际间隔可能更长。

后台服务启动后先等待十秒，再在后台初始化和处理队列，不在应用启动阶段等待微信网络请求，也不在业务请求中建表。

发送前重新校验发起人的当前系统授权、接收用户是否启用、微信接收授权和绑定身份。绑定变化会取消旧任务，不转投新的绑定。消息来源为 `AgentPush`，实际通道投递仍写入 `WeChatClawBotDelivery`，不混用 `DeviceCard` 或 AI 回复来源。

| 状态 | 含义 |
| --- | --- |
| Pending | 等待发送 |
| Sending | 已取得任务，正在处理 |
| Sent | 微信通道接受，不代表已读 |
| Failed | 通道明确失败，可由发起人重新确认重试 |
| Unknown | 发送结果不确定、进程中断或发送租约过期，不能通过 Agent 自动重试 |
| Cancelled | 授权、用户或绑定条件变化，需要核对后重新提交 |

重复确认复用执行计划；同请求幂等键不能替换正文或接收人。多实例通过数据库租约互斥。外部请求不放进长数据库事务；发送超时或重启后结果不确定时不盲目重发。

邮件复用现有统一队列及重试规则，新增投递前对发起人系统权限的复核。独立邮件 Agent 的查询即使由管理员发起，也固定到本人当前系统；原邮件管理页的管理员查询能力保持不变。Unknown 邮件只在原管理员界面核实并明确接受重复投递风险后人工处理。

## 前端变化

- 通用工作助手新增“微信推送”“邮件推送”业务选择及示例问题。
- 微信 AI 连接配置的 Agent 多选使用后端注册目录，按权限出现新助手，并增加使用说明。
- “我的微信推送”新增独立子组件 `ClawBotPushQueue.vue`，展示本人当前系统任务、失败说明和发送间隔配置。
- 发送记录识别 `AgentPush` 来源及 `Unknown` 状态。
- 复用已有动态路由，无需手工新增两个页面菜单；本次没有新增公开写接口或更改 MCP 鉴权模式。

受保护的队列接口：

```text
GET  /api/wechat-clawbot/push-queue/setting
POST /api/wechat-clawbot/push-queue/setting
GET  /api/wechat-clawbot/push-queue/records
```

## 主要代码位置

- Agent 定义：`Service/Agent/Push/PushAgentModules.cs`
- 工具执行：`Service/Agent/Push/PushAgentToolExecutor.cs`
- 权限指纹：`Service/Agent/Push/PushResultAccessFingerprint.cs`
- 微信队列模型：`Model/WeChatClawBot/WeChatPushQueueModels.cs`
- 队列服务及初始化：`Service/WeChatClawBot/Push/`
- 微信渠道路由及结果交接：`Service/WeChatClawBot/WeChatClawBotAiService.cs`
- 非阻塞处理：`calmadmin/HostedServices/WeChatPushQueueHostedService.cs`
- 接口：`calmadmin/Controllers/WeChatPushQueueController.cs`

## 验证与上线说明

`tests/PushAgentsSmoke` 使用独立随机 SQL Server 测试数据库和模拟微信、邮件适配器，验证：新表、已有数据旧表兼容迁移、重复初始化、真实插入、中文正文及 SQL NULL、两个 Agent 注册、确认前不入队、重复确认不重复发送、不同用户和系统隔离、并发租约、发送间隔、授权撤销、结果待核实不重试、草稿版本和权限隔离。

已通过的检查：

- 后端编译：0 警告、0 错误。
- `PushAgentsSmoke`：还覆盖真实微信适配器配内存 HTTP 替身、防重复调用、未知网络结果、Autofac 单例保留及后台启动不阻塞。
- `EmailPushSmoke`：统一邮件队列、旧表迁移、管理员角色及真实 HTTP 权限回归通过。
- `WeChatAiSmoke`：连接密钥、额度、多 Agent 路由、确认计划、重启恢复和异常反馈回归通过；新增 `WECHAT_AI_SMOKE_SQL` 环境变量可指定隔离测试服务器。
- `McpDirectAccessSmoke`：匿名 MCP 保持兼容，新内部队列接口仍要求登录。
- A、B 前端打包及本次前端文件 ESLint、Prettier 检查通过。
- 全项目 `vue-tsc` 仍有原有表单、表格、HTTP 类型等错误；本次修改文件未报告类型错误。打包通过不代表这些原有类型问题已修复。

未真实向微信或邮箱发送测试消息；上线后需使用已绑定、已授权的测试接收人做一次实际链路验收。模型复用后台已启用的平台，无专属绑定时沿用现有全局候选平台规则，也可以在模型平台配置中单独绑定这两个 Agent。

本次不包含小时产量看板开发，不提交或推送 Git，不自动重启运行中的生产后端。
