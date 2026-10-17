# 微信 ClawBot AI 对话与连接授权 v1.0

日期：2026-10-16。本文为新增版本说明，不覆盖之前的需求文档。

## 一、功能与入口

前端现有「微信 ClawBot → 我的微信推送」页面新增「微信 AI 连接配置」及「AI 处理记录」，复用已有动态路由，不另外创建菜单。

微信成为后台现有 Agent 的文字对话入口，不新增计划任务系统、不通过公开 MCP 绕过授权、不要求用户填写模型 API Key。

使用步骤：

1. 登录后台，打开微信 ClawBot 页面并扫码绑定。
2. 在 AI 连接配置中选所属系统、聊天/工作模式及 Agent；开启自动回复并保存。
3. 点击「生成 / 重置密钥」，保存仅本次展示的专属密钥。
4. 在绑定微信发送 `连接 专属密钥`。示例仅为格式，不要把真实密钥放入文档或截图。
5. 收到验证成功后发送文字问题；工作模式可使用授权业务工具。
6. 写操作先查看计划，再在十分钟内回复「确认」。不开放微信删除工具。

默认 AI 关闭、验证有效期 24 小时、每日 50 次正常对话、发送间隔 5 秒、单轮处理超时 120 秒。机器人总发送预算默认 0，表示不启用本地总预算；不是微信平台无限额。

## 二、身份、权限与连接防呆

- 扫码绑定保存后台 UserId 与微信 ILinkUserId、BotId 的对应关系。
- 仅接收 message_type=1 且 from_user_id 与绑定身份匹配的消息；不识别陌生发送者为绑定账号。
- 密钥采用随机 256 bit 高熵值，只保存 SHA256；不使用登录密码或模型 Key。
- `连接 ...` 命令及包含生成密钥前缀的消息入库前脱敏，不进入模型上下文。
- 五次错误验证锁定十五分钟；普通控制提示最多每分钟一次，重复成功验证不会无限回复提示。
- 验证、执行、工具调用、实际发送前均按需要校验最新绑定和权限。
- 保存配置、重置密钥、解绑/重绑、管理员停用微信绑定会撤销连接并断开原 Agent 会话关联。
- 清空 AI 会话同样要求重新验证；保留原 Agent 历史和审计，不物理删除历史业务记录。
- 网页退出不冒充微信退出；微信使用独立有效期授权，不保存、伪造或复用网页 JWT。

权限读取使用真实后台用户及选定系统的 SysUserApplication 授权。A 的旧权限只在显式 UseLegacyPermissions 时合并；不能用用户全局权限绕过 B 的授权。

普通聊天没有业务工具。工作 Agent 遵守 Agent 入口权限和工具 PermissionKey。专用 EdgeGatewayAgent、ProductionDashboardAgent、AgentAssistant 权限仅对应各自业务；通用 Agent / AgentExecutor 权限可选择已注册业务 Agent。

**资源范围限制**：本次不重写所有旧 Agent 工具的数据过滤逻辑。对于还只能查询全量数据的旧工具，AllResources=false 的账号默认拒绝执行，不能靠用户在问题中指定线体就视为安全过滤。后续适配资源级工具后再逐个开放。

## 三、额度与静默

### 用户每日对话额度

- 北京时间每天 00:00 按日期切换额度，不依赖服务器本地时区。
- 每条有效文字任务入队时预占一轮额度，避免连续消息或并发突破限额。
- 一轮 Agent 回答算一次；内部多次工具调用不重复算。
- 图片等未支持消息、验证提示、超限提示、没有有效计划的确认均不调用模型；其中「确认」作为有效文字请求返回说明时仍占一次正常对话额度。
- 超过用户配置额度，仅入队一次限额提示；后续当天消息仍记录，但不生成新回复、不调用模型、不执行工具。
- 次日恢复额度，不恢复已失效的密钥授权。
- 明确尚未执行业务工具的模型失败退还预占额度，并排队发送脱敏异常提示。
- 进程中断、超时或工具/发送结果不确定时保留预占，不自动重执行，以避免重复写数据或重复发消息。
- 配置更新、断开授权不会清零当日用量。
- 已触发当天静默后，不因后来释放预占或调整配置而重新回复；页面显示当天静默及剩余 0，次日恢复。

### 机器人总发送预算与间隔

- 启用 AI 连接配置后，同一 BotId 的 AI、调试、MCP、设备卡片等发送来源共享发送节流和每日预算。
- 多个配置指向同一机器人时，采用正数额度的最小值、发送间隔的最大值。
- 预算 0 表示本地不限制总数；正常对话额度仍生效。
- 正常发送预留最后一次总预算用于限额提示。机器人已满时排队任务不再消耗模型调用。
- 为防止超发，总预算按发送尝试预占；发送失败或结果不确定不会盲目退还总预算。
- 开启前发生、其他应用独立发出的消息无法计入本系统预算，不能保证等同于微信平台真实剩余额度。
- 如果平台提前拒绝发送，限额提示也可能无法送达；查看后台投递记录。

## 四、后台执行与确认

接收长轮询 → 消息及游标事务入库 → 密钥/权限/额度检查 → 持久化任务 → 独立后台 Agent 执行 → 持久化回复 → 节流发送 → 投递审计。

- 新增 AI 后台服务在启动延迟之后工作，不在启动阶段发模型请求。
- 接收线程不等待 LLM，不在数据库事务里调用 LLM。
- 后台最多三个用户并行；同一用户用 SQL Server 会话锁跨进程串行处理。
- 控制状态和每日额度用独立短锁，断开授权不需要等待整轮模型处理。
- 后台 Agent 使用 DatabaseOperationScope 隔离连接，复用现有 Microsoft Agent Framework 和模型平台绑定。
- 待处理/待发送任务可在重启后继续；遗留 Running / Sending 标记 Unknown，不自动重试未知写入或发送。
- 写工具即使注册时未要求确认，在微信渠道也强制先生成待确认计划。
- 「确认」只能承接上一轮成功送达且仍有效的计划；裸确认、重复确认不执行工具。
- 提交新的工作请求会使原会话未确认计划过期；切换系统/Agent 后不复用旧确认上下文。
- 已开始的业务操作不承诺强制回滚；撤权会阻止后续工具和发送，不自动重复未知操作。

任务状态：Pending、Running、ReplyPending、Sending、Sent、Failed、Unknown、Cancelled。记录关联接收记录、Agent 会话及微信投递记录。

首期超过 10000 字符的回复截断并提示到后台会话查看；不自动把一个长答案拆成大量微信消息。

## 五、数据库

新建独立表，不在现有微信业务表上直接追加无默认值的非空列：

| 表 | 用途 | 主键/唯一性 |
| --- | --- | --- |
| WeChatClawBotAiConnection | 用户配置、密钥哈希、授权期限、绑定版本、Agent 会话关联 | UserId |
| WeChatClawBotAiDay | 用户按北京时间日期的用量及限额提示标记 | UserId + Day |
| WeChatClawBotAiJob | 收消息处理、回复内容、执行/发送状态及关联记录 | 自增 Id，InboundId 唯一 |
| WeChatClawBotSendBudget | 同一机器人全来源发送间隔及日预算 | BotKey |

尚未发生的验证时间、锁定时间、会话和投递关联显式允许 SQL NULL。旧微信表原有可空修复继续保留。初始化支持已有账号数据与重复执行。

消息、任务、额度和游标同事务提交；接收失败时保留旧游标，下轮重收。已存在消息 ID 不重复入队。

## 六、新增 API

均使用后台登录身份，不能通过请求参数冒用其他 UserId：

| 方法 | 地址 | 功能 |
| --- | --- | --- |
| GET | /api/wechat-clawbot/ai | 当前用户配置、状态、用量 |
| GET | /api/wechat-clawbot/ai/options | 当前用户获授权的系统与 Agent |
| POST | /api/wechat-clawbot/ai | 保存配置并撤销旧连接 |
| POST | /api/wechat-clawbot/ai/secret/reset | 生成/重置密钥，仅本次展示，禁止缓存 |
| POST | /api/wechat-clawbot/ai/disconnect | 断开授权 |
| POST | /api/wechat-clawbot/ai/context/reset | 清空会话关联并撤销授权 |
| GET | /api/wechat-clawbot/ai/jobs?current=1&size=20 | 当前用户分页任务记录 |

原扫码、收消息、调试发送、MCP 推送接口保留。个人微信接口不再要求全资源范围，但仍校验当前系统微信功能权限；管理员操作仍执行管理员校验。

## 七、代码 Diff 导航

### 后端新增

- `Model/WeChatClawBot/WeChatClawBotAiModels.cs`：四张新表及配置 DTO。
- `Interface/WeChatClawBot/IWeChatClawBotAiService.cs`：微信 AI 渠道接口。
- `Service/WeChatClawBot/WeChatClawBotAiService.cs`：验密、额度、队列、Agent 编排和总发送预算。
- `Service/WeChatClawBot/WeChatClawBotAiPolicy.cs`：密钥、脱敏、北京时间日期和确认命令。
- `Service/WeChatClawBot/WeChatClawBotAiLock.cs`：跨进程 SQL 会话锁。
- `Service/WeChatClawBot/WeChatClawBotAiSchema.cs`：新表初始化。
- `Service/Agent/AgentChannelExecutionScope.cs`：后台渠道权限、取消和确认边界。
- `calmadmin/Controllers/WeChatClawBotAiController.cs`：配置及状态 API。
- `calmadmin/HostedServices/WeChatClawBotAiHostedService.cs`：异步任务消费者。
- `tests/WeChatAiSmoke`：隔离 SQL、模拟模型和模拟微信传输验证。

### 后端修改

- `WeChatClawBotService` / 接口：接收时脱敏并入队、AI 来源发送、统一预算、发送前复查绑定、解绑/重绑撤销授权。
- `WeChatClawBotDatabaseInitializer`：追加独立新表初始化。
- `SystemApplicationService` / 接口：后台渠道读取最新系统授权，不创建网页会话。
- `AgentService`：后台取消令牌、模型失败分类及独立数据库上下文。
- `AgentToolDispatcher`：渠道工具鉴权、写操作强制确认、过期确认拒绝及取消令牌。
- `AgentToolRegistryService`：沿用后台数据库操作上下文。
- `AgentFrameworkModelChatClient`：微信模型失败不直接回传模型错误正文。
- `WeChatClawBotController` / `SystemApplicationAccessFilter`：个人接口按当前系统权限校验，不把全资源授权作为绑定前置。
- `HostBuilderExtend`：注册 AI 服务和独立后台任务。

### 前端新增/修改

- 新增 `src/api/wechat-clawbot-ai.ts`。
- 新增 `src/views/wechat-clawbot/components/ClawBotAiConnection.vue`。
- 修改 `src/views/wechat-clawbot/index.vue`，嵌入配置和任务记录，随原有五秒刷新更新状态，不覆盖未保存表单。
- 新增 `tests/wechat-clawbot-ai.mjs`。

## 八、验证与发布

- 随机独立 SQL 数据库验证新建、旧账号数据升级、重复初始化、真实插入、密钥验证/锁定/脱敏、额度、静默、用户隔离、系统撤权、重启恢复和确认过期。
- 模拟微信 HTTP 传输验证实际 getupdates → 消息入库 → Agent → sendmessage → 发送审计；不向真实微信发送。
- 前端验证 API 大小写兼容、表单、取消确认、一次性密钥展示及刷新不覆盖编辑。
- A / B 两套前端生产构建已验证；全量 TypeScript 检查仍有其他现有文件的 42 项诊断，本次微信相关文件没有类型诊断，不将此报告为全量类型检查通过。
- 发布需要同时更新前后端。AI 默认关闭，首次使用先生成密钥并完成微信验证。
- 需要目标数据库有建表权限；本次未修改真实业务数据库、未重启现有服务、未提交或推送 Git。
- 实际微信账号与真实模型的最终联调需发布后验证；隔离测试不能代表真实平台不会限流。
