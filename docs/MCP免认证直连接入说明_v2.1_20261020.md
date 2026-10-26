# MCP 免认证直连接入说明 v2.1

日期：2026-10-20。本文件为新增迭代说明，不覆盖历史版本。

## 1. 当前模式

MCP 和等价 OpenAPI 数据入口改为内网免认证直连：无需 ClientId、ClientSecret、OAuth 换取 Token 或后台登录 JWT。

访问者可以使用全部已登记的公开业务工具，读取其开放的业务数据，以及提交符合参数规则的写操作。不再按照调用用户、客户端、A/B 系统权限或资源编码白名单过滤。不是开放任意 SQL、任意方法或任意数据库操作；仍由现有工具目录决定能力范围。

**这只是取消认证，不会自动限制访问者来自内网。** 必须通过防火墙、VPN、网关或网络隔离控制可达范围，不要直接暴露到公网。A/B 后台页面原有权限仍有效，但这些开放接口不再提供相同的权限隔离。

## 2. 对接地址

| 用途 | 路径 | 认证 |
| --- | --- | --- |
| 标准 MCP Streamable HTTP，推荐 | `/mcp/v2` | 无 |
| 兼容旧版 JSON-RPC MCP | `POST /mcp` | 无 |
| 等价工具目录 | `GET /api/external/v1/tools` | 无 |
| 等价工具执行 | `POST /api/external/v1/tools/{toolName}` | 无 |

以正式环境后端 IP `10.148.201.223`、端口 `8087` 为例，支持相应配置格式的客户端可使用：

```json
{
  "type": "streamable-http",
  "url": "http://10.148.201.223:8087/mcp/v2"
}
```

不同客户端的外层配置格式可能不同；核心是使用标准 Streamable HTTP、实际后端地址和 `/mcp/v2`。不需要配置认证请求头。如改走前端同域地址或统一网关，必须另外配置 `/mcp/v2` 的转发；仅转发 `/api` 不够。本次不修改 IIS/Nginx 配置。

手工测试标准 V2 时仍须遵守 MCP 协议，例如 POST 设置 `Content-Type: application/json`、`Accept: application/json, text/event-stream`，先执行 `initialize`，后续请求按协商版本发送。免认证不代表取消 JSON-RPC 格式、参数类型或 MCP 协议校验。

## 3. 写操作和审计保留

- 写工具必须显式传入布尔值 `confirm: true`；不接受字符串 `"true"`。
- `confirm` 不能代替必要业务参数，必须符合对应工具定义。读取工具原有的必填参数也不会自动取消；原本支持不传编码的统计/分页工具继续保留该能力。
- 工具白名单、参数验证、已有业务防误改逻辑继续使用。
- 写入执行前仍要求记录 `Attempted` 审计；记录失败则不执行业务写入，后续成功、拒绝、失败状态继续按原逻辑记录。
- HTTP 来源统一记录为 `anonymous-mcp`，保留来源 IP 和请求标识。未认证的情况下不伪造真实用户或 A/B 系统身份，因此无法可靠追溯到具体操作人。
- 客户端撤权、停用、退出系统、令牌失效不再阻断这些开放的数据入口。即使请求附带旧令牌，也统一使用匿名直连上下文。

## 4. 没有取消的后台保护

后台普通业务接口、用户信息、菜单、Agent WebSocket 等继续使用原有登录认证及业务授权。

`/api/external-tool-clients` 和 MCP 服务名称等管理配置仍需后台登录、管理员权限；服务名称修改继续使用原有配置口令。

旧客户端换 Token 和当前用户获取 MCP Token 的接口暂时保留兼容，但直连不再需要调用它们；换 Token 接口自身仍检查提交的客户端凭据，不等于开放任意令牌签发。前端“MCP 接入”按钮已改为直连说明，不再自动请求令牌。

## 5. 本次修改清单（供 diff）

后端修改：

- `calmadmin/Program.cs`：标准 `/mcp/v2` 改为 `AllowAnonymous`，更新服务说明。
- `calmadmin/Controllers/ExternalTools/ExternalToolController.cs`：旧 MCP、OpenAPI 改为 `AllowAnonymous`，统一生成匿名直连上下文。
- `calmadmin/ExternalTools/ExternalMcpV2Handlers.cs`：列工具、调用工具均使用匿名直连上下文。
- `calmadmin/Config/HostBuilderExtend.cs`、`calmadmin/Config/SystemApplicationAccessFilter.cs`：更新认证边界说明；未全局关闭 JWT 或后台权限检查。
- `tests/MultiSystemSmoke/Program.cs`：更新 HTTP 授权预期，补充匿名写入确认、参数检查和数据库审计断言。

后端新增：

- `tests/McpDirectAccessSmoke/McpDirectAccessSmoke.csproj`、`Program.cs`：不依赖数据库的真实 HTTP 协议/认证边界回归测试。
- 本说明文件。

前端修改：

- `src/components/business/system-mcp-access/index.vue`：移除令牌领取与展示，改成匿名连接地址及风险说明。A/B 复用该组件。
- `src/views/agent/external-tool-clients/index.vue`：明确提示匿名读写模式及写入参数要求，使用警告样式提示开放范围。

不修改数据库结构、业务数据、后端运行端口或本地密钥配置；不自动提交或推送 Git。

## 6. 验证与发布

本地已通过后端编译及 `McpDirectAccessSmoke`：真实 V2 SDK、旧 MCP、OpenAPI 的匿名初始化/目录/调用；正常授权接口及 MCP 管理入口拒绝匿名；实际生产代码中的写入确认校验。该测试使用模拟业务服务，不访问真实数据库，不发送微信，不调用模型。

SQL Server 版 `MultiSystemSmoke` 已更新，但本次环境无法连接配置的 SQL Server，未完成真实数据库审计及完整业务链路验证，不能视为已通过。没有为测试修改生产数据库或服务配置。

前端接入组件已通过 ESLint 和 Prettier 检查。

发布时重新构建并部署后端，重启应用后生效；前端重新打包部署后更新接入弹窗。现有运行进程及生产环境不会因源码修改自动切换模式。
