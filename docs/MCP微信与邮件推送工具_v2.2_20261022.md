# MCP 微信与邮件推送工具 v2.2

日期：2026-10-22。本文件新增迭代说明，不覆盖 v2.1 或微信 Agent 文档。

## 1. 本次变化

新增邮件的接收人查询、服务状态、发送、投递结果及失败重试工具；微信 MCP 发送改为提交通用发送队列，并新增队列查询。

以下三个入口共享同一个工具目录和业务实现：

- 标准 Streamable HTTP：`/mcp/v2`。
- 兼容 JSON-RPC：`POST /mcp`。
- 等价 OpenAPI：`GET /api/external/v1/tools`，`POST /api/external/v1/tools/{toolName}`。

当前仍按既定设计免认证直连，不需要 OAuth、ClientId、ClientSecret 或登录 JWT。正式环境连接示例：

```json
{
  "type": "streamable-http",
  "url": "http://10.148.201.223:8087/mcp/v2"
}
```

测试环境可改为 `http://10.148.75.230:8089/mcp/v2`。地址只有部署后才具备本次功能；修改源码不会更新已经运行的进程。标准 MCP 初始化、协议协商及请求头规则仍按现有 SDK 执行。

**免认证并不能确保只有内网访问，也不能确认操作者身份。** 请通过防火墙、VPN 或网关限制可达范围。`confirm: true` 是程序防误操作参数，不是用户认证，不证明真人已经确认。

## 2. 工具清单

| 工具 | 用途 | 必要参数 |
| --- | --- | --- |
| `email.recipients.list` | 搜索启用的系统用户及邮箱有效性 | 无；可传 `keyword/current/size` |
| `email.channel-status` | 返回邮件队列就绪和服务启用状态，不返回 SMTP 配置 | 无 |
| `email.send` | 使用统一发件账户提交邮件队列 | `recipientUserIds/subject/body/idempotencyKey/confirm` |
| `email.delivery-status` | 查询当前 MCP 来源邮件投递结果 | 无；可传 `taskId/deliveryId/keyword/status/startTime/endTime/current/size` |
| `email.retry` | 重试当前 MCP 来源明确失败的邮件 | `deliveryId/confirm` |
| `clawbot.recipients.list` | 保留原可推送微信用户查询 | 无 |
| `clawbot.channel-status` | 保留原微信通道状态查询 | 可传 `recipientCode` |
| `clawbot.send` | **改为提交微信队列，不同步发送** | `recipientCodes/content/idempotencyKey/confirm` |
| `clawbot.queue-status` | 新增微信任务查询，包含未发送的任务 | 无；可传 `taskId/recipientCode/idempotencyKey/status/current/size` |
| `clawbot.delivery-status` | 保留原实际微信通道投递查询 | 可传原有筛选参数 |
| `clawbot.retry` | 改为将当前 MCP 来源失败队列任务重新入队 | `confirm`，以及 `taskId` 或 `deliveryId` 二选一 |

全部写工具必须传布尔值 `confirm: true`。字符串 `"true"`、缺少幂等键、超长字段、错误类型、超量数组或额外的身份/配置字段都会拒绝；不会偷偷截取前 100 人后继续执行。

邮件单次最多 100 个收件及抄送用户，主题最多 160 字，正文最多 100000 字。微信单次最多 100 位接收人，正文最多 10000 字。接收人使用真实系统用户编码，不开放任意外部邮箱或任意微信 ID，不能由模型编造。

## 3. 邮件示例

### 3.1 搜索接收人

```json
{
  "name": "email.recipients.list",
  "arguments": {
    "keyword": "张三",
    "current": 1,
    "size": 20
  }
}
```

返回分页用户及 `UserId/UserName/Email/Available`。姓名重名时应让使用者确定账号；只能发送给启用且邮箱有效的系统用户。

### 3.2 提交邮件

以下用户编码仅为示例，请替换为实际查询结果：

```json
{
  "name": "email.send",
  "arguments": {
    "recipientUserIds": ["10002"],
    "ccUserIds": ["10003"],
    "subject": "产线检查结果",
    "body": "A线注氟工序产出低于目标，请安排检查。",
    "idempotencyKey": "line-report-20261022-1500",
    "confirm": true
  }
}
```

返回任务编号及 `Pending` 等队列状态，**不表示邮件已送达**。同一业务批次重试应沿用幂等键；新的定时执行批次应使用包含本次时间或批次编号的新键。相同键替换人员、主题或正文会被拒绝。

### 3.3 查询结果

```json
{
  "name": "email.delivery-status",
  "arguments": {
    "taskId": 123,
    "current": 1,
    "size": 20
  }
}
```

结果按实际收件邮箱分别记录，`Id` 为投递编号，`TaskId` 为任务编号。

- `Pending/Processing/Retrying`：等待、处理或按原队列规则重试。
- `Sent`：SMTP 服务接受，不代表收件人已读，也不保证最终投递到收件箱。
- `Failed`：明确失败，可通过 `email.retry` 携带对应 `deliveryId` 和 `confirm: true` 重试。
- `Unknown`：可能已发送，不能通过 MCP 重试；需管理员在原邮件管理页核实后人工处理。
- `Cancelled`：用户停用、邮箱变化或任务条件不符。

MCP 不能查询或重试普通后台用户的私有邮件任务，也不能修改 SMTP、发件邮箱、密码、加密密钥或发送间隔。统一邮件账户仍由管理员角色配置。没有启用邮件服务时发送会明确拒绝。

## 4. 微信示例与兼容变化

原 `clawbot.send` 参数保留：

```json
{
  "name": "clawbot.send",
  "arguments": {
    "recipientCodes": ["10002"],
    "content": "A线注氟工序需要检查，请确认。",
    "source": "external-model",
    "externalTaskId": "line-check-20261022-1500",
    "idempotencyKey": "wechat-line-check-20261022-1500",
    "confirm": true
  }
}
```

新返回形态示例：

```json
{
  "message": "消息已提交后台队列，请按任务编号查询结果；不代表已送达",
  "tasks": [
    {
      "taskId": 456,
      "RecipientUserId": "10002",
      "Status": "Pending"
    }
  ]
}
```

**原来把 `clawbot.send` 返回当成同步已发送投递列表的客户端，需要调整。** 现在先接收任务编号，再查询：

```json
{
  "name": "clawbot.queue-status",
  "arguments": {
    "taskId": 456
  }
}
```

也可用提交时的 `idempotencyKey` 查询整批接收人结果。队列返回每位接收人的 `Id`（队列任务编号）、`Status`、`DeliveryId`（实际通道投递编号）、错误及发送时间。

`source`、`externalTaskId` 只作为来源标签和外部关联元数据保存于队列表，不决定权限或真实发送来源。两列支持 Unicode，长度分别为 32、128 字。实际微信投递来源固定标记为 `MCP`，实际投递的 `ExternalTaskId` 使用本系统队列任务编号，便于与队列表关联。`AI` 仍是保留来源标签，不能冒用。

新 MCP 微信消息与 Agent 微信消息共享 `WeChatPushQueue`、全局租约及可配置发送间隔（默认 5 秒）。已有机器人级预算与节流仍然生效。MCP 查询工具不能修改这些配置。

发送前会再次检查系统用户启用、微信绑定、管理员停用状态、接收授权和会话。关闭绑定页“Agent / MCP 推送”开关后不接收这两类推送；绑定变化不会将旧任务转投新绑定。

`Sent` 只表示通道接受；`Unknown` 不自动重发。明确失败任务可发送：

```json
{
  "name": "clawbot.retry",
  "arguments": {
    "taskId": 456,
    "confirm": true
  }
}
```

`deliveryId` 仍可使用，但只能关联新队列产生的失败投递，且与 `taskId` 不可同时传。旧版直接发送且没有队列关联的历史记录不能借此同步重发，应先人工核实，再以新的 `clawbot.send` 任务提交。原通道历史查询保留，不删除历史记录。

## 5. 匿名来源与权限边界

- HTTP MCP 当前统一为匿名直连，审计保留 `anonymous-mcp`、来源 IP 和请求标识，不伪造真实用户或管理员。
- 邮件及微信队列中的外部任务使用独立 `MCP` 来源分区及 `mcp:` 摘要标识，不借用 A/B 用户身份。后台 Agent 原有 A/B 用户权限检查保持不变。
- **所有匿名调用者共享这一 MCP 来源分区，不能据此提供不同匿名客户端之间的身份隔离。** 不要把自填 `source`、请求头或客户端展示名当作认证。
- 若内部兼容入口使用已验证客户端上下文，则工具还检查 `email.read/email.write` 或 `clawbot.read/clawbot.write` 以及独立推送对象授权；线体／项目资源授权不会自动开放人员推送。
- 写入前必须成功记录 `Attempted` 审计，记录失败不入队。后续继续记录成功、拒绝或失败，参数只记摘要。
- 不开放删除、任意 SQL、任意 SMTP 地址、账户密码读取或修改工具。

## 6. 修改清单（供 diff）

新增：

- `Service/ExternalTools/McpPushAccess.cs`：外部来源标识与推送范围校验。
- `Service/ExternalTools/ExternalToolService.Push.cs`：邮件 MCP 分发及推送参数 Schema。
- `Service/Email/EmailPushService.Mcp.cs`：邮件的显式 MCP 业务入口，不构造管理员上下文。
- `Service/WeChatClawBot/Push/WeChatPushQueueService.Mcp.cs`：微信 MCP 入队、查询、失败重试。
- `tests/McpPushSmoke/`：真实 V2 SDK、旧 MCP、OpenAPI 与隔离 SQL 队列联调。
- 本 v2.2 说明文件。

修改：

- `ExternalToolService.cs`：注册 5 个邮件工具和微信队列查询，微信写入改走队列，推送参数增加完整类型／长度／额外字段校验。
- `ExternalMcpV2Handlers.cs`：推送写工具使用外部副作用提示；无需改协议地址或重新实现 MCP。
- `IEmailPushService`、`IWeChatPushQueueService`：增加专用 MCP 方法。
- 邮件提交／记录查询抽出共用核心；邮件 worker 区分真实用户任务与外部来源任务，保留原用户验证。
- 微信入队／记录查询抽出共用核心；worker 区分来源并共享节流，不再同步调用原 MCP 群发。
- `WeChatPushQueueModels.cs`：新增显式可空 `ExternalTaskId/SourceLabel`，CodeFirst 可兼容已有数据表，保留主键、索引和历史任务。
- `IWeChatClawBotService`、`WeChatClawBotService`：队列适配只允许 `AgentPush/MCP` 真实来源，保留不确定结果分类。
- 前端邮件投递记录来源筛选增加 `MCP 推送`，无新增动态路由。

没有修改认证模式、端口、生产配置文件或运行中的服务，不自动提交／推送 Git。

## 7. 验证与部署

`McpPushSmoke` 在随机隔离 SQL Server 数据库中验证实际工具目录、写入审计、确认门禁、Schema 校验、幂等、队列分发、节流、MCP 来源隔离、私有邮件拦截、失败重试、Unknown 禁重试，以及已有数据表添加可空元数据列后重复初始化。

测试使用内存微信 HTTP 与 SMTP 替身，不连接真实微信、邮箱或模型。实际部署后，请由管理员准备有效统一邮件配置及已绑定接收人，先做一次小范围真实投递验收。

本次已通过后端编译（最终增量检查 0 警告、0 错误）、`McpPushSmoke`、`PushAgentsSmoke`、`EmailPushSmoke`、`McpDirectAccessSmoke`。也验证了审计表不可写时禁止入队、超过人数上限不截断执行、32／128 字中文元数据真实入库不截断。前端本次来源筛选文件通过 ESLint 和 Prettier；未将前期已有的全项目类型错误视为已修复。

部署新版后端并重启应用，等待后台队列初始化；外部客户端重新连接或刷新工具目录即可发现新增工具。前端来源筛选的小调整需要重新打包部署。保持现有模型工具调用循环及外部定时任务不变，但按第四节适配微信的异步返回。
