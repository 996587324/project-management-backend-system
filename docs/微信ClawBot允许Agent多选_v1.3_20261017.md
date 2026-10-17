# 微信 ClawBot 允许 Agent 多选 v1.3

日期：2026-10-17。新增版本说明，不覆盖之前文档。

## 使用方式

「我的微信推送 → AI 连接配置」工作模式的「允许的 Agent」改为可搜索多选。至少选一个；每项都校验当前 A/B 系统权限。切换所属系统时清空工作选择，避免沿用另一个系统的配置。保存后需在微信重新验证原专属密钥，无需重置密钥。

- 单选：直接交给该 Agent，兼容原方式。
- 多选：按当前消息、允许目录和最近使用的 Agent，选择一个最匹配的 Agent。
- 可明确发送 `@edge-gateway 查询连接情况` 或 `@production-dashboard 查询线体状态`，跳过额外的路由模型请求；仍然受允许列表和权限限制。
- 无法明确选择、涉及多个业务或模型返回未允许的编码时，提示用户明确 Agent 或分步提问，不随意执行默认 Agent。
- 直接回复「确认」时不重新分配，只承接上一轮已成功送达且仍有效的计划所属 Agent。

**多选表示多个 Agent 可用，不代表每条消息同时执行全部 Agent。** 自动分配会增加一次无工具识别模型请求，复用后台 chat 模型配置，不执行聊天 Agent 的业务工具。

路由与业务调用合计按一条用户消息占一次正常对话额度，不重复扣次数。密钥、额度、发送队列、权限、数据范围保护和写操作确认规则不变。

## 会话与安全

外部仍是一个用户一个微信对话；内部按 Agent 保存独立业务会话，切回原 Agent 恢复其历史。本轮工具必须属于选定 Agent，不能因为另一个 Agent 也被勾选就跨业务调用。

新的工作请求使该微信连接关联的旧待确认计划过期。修改配置后原连接授权及会话关联失效，不清零当天额度。页面显示当前 Agent，任务记录增加执行 Agent。

## 数据与 API 兼容

保留旧 `AgentCode` 和旧请求方式。以下新字段显式允许 SQL NULL，由 SqlSugar CodeFirst 兼容已有非空表，不用虚假默认值覆盖旧数据：

| 表 | 新字段 | 用途 |
| --- | --- | --- |
| WeChatClawBotAiConnection | AllowedAgentCodesJson | 完整允许列表；NULL 时沿用旧单选 AgentCode |
| WeChatClawBotAiConnection | AgentConversationsJson | Agent 到会话编号的映射 |
| WeChatClawBotAiConnection | ActiveAgentCode | 最近执行的 Agent |
| WeChatClawBotAiJob | AgentCode | 任务执行归属 |

新配置 API 使用 `agentCodes` 数组；旧客户端省略数组时仍接受 `agentCode`。显式空数组不回退默认值。旧会话按原 Agent 兼容读取，不自动扩大允许范围。

## Diff 文件

后端：

- 修改 `Model/WeChatClawBot/WeChatClawBotAiModels.cs`：允许列表、会话映射、当前 Agent、任务归属及 DTO。
- 修改 `Service/WeChatClawBot/WeChatClawBotAiService.cs`：多项校验、分配执行、独立会话、确认锁定及审计。
- 新增 `Service/WeChatClawBot/WeChatClawBotAiAgentRouting.cs`：白名单路由、明确指定、旧配置和会话兼容。
- 修改 `Service/WeChatClawBot/WeChatClawBotAiSchema.cs`：扩展列兼容说明，继续使用 SqlSugar 初始化。
- 扩展 `tests/WeChatAiSmoke/Program.cs`：非空旧表升级、多选、越权、分配、会话恢复、额度及确认。

前端：

- 修改 `src/api/wechat-clawbot-ai.ts`：数组传输、旧响应兼容及任务归属。
- 修改 `src/views/wechat-clawbot/components/ClawBotAiConnection.vue`：多选、校验、系统切换清理、当前 Agent 和任务列。
- 扩展 `tests/wechat-clawbot-ai.mjs`：旧响应、多选保存回读、空列表/越权校验及刷新保留编辑。

## 验证与发布

后端编译、随机隔离 SQL 测试、前端配置与 Tab 回归、前端生产构建通过。验证包括非空旧表重复初始化、原数据与会话保留、新可空列实际读写、路由越界拦截与确认锁定。

本次文件无类型诊断；全量类型检查仍有其他既有的 42 项诊断。模型和微信均使用离线替身，未调用真实模型或发送微信。

需要同时更新前后端并重新启动后端。本次未重启现有服务、未修改真实业务库或用户配置、未提交 Git。
