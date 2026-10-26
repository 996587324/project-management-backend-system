using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interface;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Model.ProjectManage;
using Model.WeChatClawBot;
using SqlSugar;

namespace Service;

/// <summary>
/// 微信 ClawBot 的唯一服务端适配层。
/// 二维码、bot token、会话游标均不经由前端或 MCP 返回；调试和 MCP 均复用此服务投递。
/// </summary>
public sealed class WeChatClawBotService : IWeChatClawBotService
{
    private static readonly SemaphoreSlim InitializeGate = new(1, 1);
    private static readonly SemaphoreSlim SystemSendGate = new(1, 1);
    private static bool _initialized;
    private readonly SqlSugarScope _dbScope;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WeChatClawBotOptions _options;
    private readonly IDataProtector _tokenProtector;
    private readonly IWeChatClawBotAiService? _ai;

    private ISqlSugarClient Db => DatabaseOperationScope.GetConnection(_dbScope, "calmadmin");

    public WeChatClawBotService(
        SqlSugarScope dbScope,
        IHttpClientFactory httpClientFactory,
        IOptions<WeChatClawBotOptions> options,
        IDataProtectionProvider dataProtectionProvider,
        IWeChatClawBotAiService? ai = null)
    {
        _dbScope = dbScope;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _tokenProtector = dataProtectionProvider.CreateProtector("CalmAdmin.WeChatClawBot.BotToken.v1");
        _ai = ai;
    }

    public async Task<WeChatClawBotAccountDto> GetMyAccountAsync(
        string userId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>()
            .FirstAsync(x => x.UserId == userId);
        if (account == null)
        {
            return new WeChatClawBotAccountDto
            {
                UserId = userId,
                UserName = userName,
                Status = "Unbound",
                McpPushEnabled = true
            };
        }

        await RefreshUserNameAsync(account, userName);
        var result = ToAccountDto(account);
        var conversation = await Db.Queryable<WeChatClawBotConversationEntity>()
            .FirstAsync(x => x.UserId == userId);
        result.HasConversationContext = !string.IsNullOrWhiteSpace(conversation?.ContextToken);
        result.LastInboundTime = conversation?.LastInboundTime;
        return result;
    }

    public async Task<WeChatClawBotBindingDto> CreateBindingAsync(
        string userId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        EnsureEnabled();

        var response = await SendAnonymousAsync(
            HttpMethod.Post,
            "/ilink/bot/get_bot_qrcode?bot_type=3",
            new { local_token_list = Array.Empty<string>() },
            cancellationToken);
        using var document = JsonDocument.Parse(response);
        var qrcode = FindString(document.RootElement, "qrcode", "qr_code", "qrcode_id");
        if (string.IsNullOrWhiteSpace(qrcode))
            throw new InvalidOperationException("微信 ClawBot 未返回二维码，请稍后重试。");

        var now = DateTime.Now;
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
        if (account == null)
        {
            await Db.Insertable(new WeChatClawBotAccountEntity
            {
                UserId = userId,
                UserName = Trim(userName, 128) ?? userId,
                Status = "Unbound",
                CreateTime = now,
                UpdateTime = now
            }).ExecuteCommandAsync();
        }
        else
        {
            await RefreshUserNameAsync(account, userName);
        }
        var session = new WeChatClawBotBindingSessionEntity
        {
            UserId = userId,
            Qrcode = Trim(qrcode, 512)!,
            QrcodeUrl = Trim(FindString(document.RootElement, "qrcode_img_content", "qrcode_url", "qr_code_url"), 1024),
            PollBaseUrl = _options.BaseUrl.TrimEnd('/'),
            Status = "Waiting",
            ExpireTime = now.AddMinutes(Math.Clamp(_options.QrcodeExpireMinutes, 1, 30)),
            CreateTime = now,
            UpdateTime = now
        };
        session.Id = await Db.Insertable(session).ExecuteReturnBigIdentityAsync();
        return ToBindingDto(session);
    }

    public async Task<WeChatClawBotBindingDto> GetBindingStatusAsync(
        string userId,
        long sessionId,
        CancellationToken cancellationToken = default,
        string? verifyCode = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        var session = await Db.Queryable<WeChatClawBotBindingSessionEntity>()
            .FirstAsync(x => x.Id == sessionId && x.UserId == userId)
            ?? throw new InvalidOperationException("二维码绑定会话不存在或无权访问。");
        if (session.Status is "Confirmed" or "Expired" or "Failed" or "Blocked") return ToBindingDto(session);
        if (verifyCode != null && (verifyCode.Length is < 1 or > 16 || verifyCode.Any(character => character is < '0' or > '9')))
        {
            throw new InvalidOperationException("请输入手机微信显示的数字验证码。");
        }
        if (session.ExpireTime <= DateTime.Now)
        {
            session.Status = "Expired";
            session.UpdateTime = DateTime.Now;
            await Db.Updateable(session).ExecuteCommandAsync();
            return ToBindingDto(session);
        }

        try
        {
            var path = $"/ilink/bot/get_qrcode_status?qrcode={Uri.EscapeDataString(session.Qrcode)}";
            if (!string.IsNullOrWhiteSpace(verifyCode))
            {
                path += $"&verify_code={Uri.EscapeDataString(verifyCode)}";
            }

            using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollTimeout.CancelAfter(TimeSpan.FromSeconds(35));
            var response = await SendAnonymousAsync(
                HttpMethod.Get,
                path,
                null,
                pollTimeout.Token,
                session.PollBaseUrl);
            using var document = JsonDocument.Parse(response);
            var botToken = FindString(document.RootElement, "bot_token");
            var status = FindString(document.RootElement, "status", "qrcode_status", "state") ?? "Waiting";
            session.LastPollTime = DateTime.Now;
            session.UpdateTime = DateTime.Now;
            session.LastError = null;

            if (!string.IsNullOrWhiteSpace(botToken))
            {
                await ConfirmBindingAsync(session, document.RootElement, botToken, cancellationToken);
                session.Status = "Confirmed";
            }
            else if (IsExpiredStatus(status))
            {
                session.Status = "Expired";
            }
            else if (status.Equals("need_verifycode", StringComparison.OrdinalIgnoreCase))
            {
                session.Status = "NeedVerifyCode";
                if (verifyCode != null) session.LastError = "验证码不匹配，请重新输入手机显示的数字。";
            }
            else if (status.Equals("verify_code_blocked", StringComparison.OrdinalIgnoreCase))
            {
                session.Status = "Blocked";
            }
            else if (status.Equals("scaned_but_redirect", StringComparison.OrdinalIgnoreCase))
            {
                session.PollBaseUrl = NormalizePollingBaseUrl(FindString(document.RootElement, "redirect_host"));
                session.Status = "Scanned";
            }
            else if (status.Equals("binded_redirect", StringComparison.OrdinalIgnoreCase))
            {
                var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
                session.Status = account?.Status == "Bound" && !string.IsNullOrWhiteSpace(account.BotToken)
                    ? "Confirmed"
                    : "Failed";
                if (session.Status == "Failed") session.LastError = "微信提示已绑定，但本系统没有可用凭证，请重新生成二维码。";
            }
            else if (status.Equals("confirmed", StringComparison.OrdinalIgnoreCase))
            {
                session.Status = "Failed";
                session.LastError = "微信已确认，但未返回有效绑定凭证，请重新生成二维码。";
            }
            else if (IsScannedStatus(status))
            {
                session.Status = "Scanned";
            }
            else
            {
                session.Status = "Waiting";
            }

            await Db.Updateable(session).ExecuteCommandAsync();
            return ToBindingDto(session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 正常的长轮询超时：保持当前二维码，前端继续查询。
            session.LastPollTime = DateTime.Now;
            session.UpdateTime = DateTime.Now;
            await Db.Updateable(session).ExecuteCommandAsync();
            return ToBindingDto(session);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            session.LastPollTime = DateTime.Now;
            session.LastError = Trim(exception.Message, 1000);
            session.UpdateTime = DateTime.Now;
            await Db.Updateable(session).ExecuteCommandAsync();
            var result = ToBindingDto(session);
            result.Message = "暂时无法查询微信扫码状态，正在重试。";
            return result;
        }
    }

    public async Task SetMcpPushEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId)
            ?? throw new InvalidOperationException("请先绑定微信 ClawBot 后再设置推送权限。");
        account.McpPushEnabled = enabled;
        account.UpdateTime = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
    }

    public async Task UnbindAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (_ai != null) await _ai.DisconnectAsync(userId);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
        if (account == null) return;
        account.BotToken = null;
        account.BotId = null;
        account.ILinkUserId = null;
        account.Status = "Unbound";
        account.LastError = null;
        account.UpdateTime = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
        await Db.Deleteable<WeChatClawBotConversationEntity>().Where(x => x.UserId == userId).ExecuteCommandAsync();
    }

    public async Task<PagedResultDto<WeChatClawBotAccountDto>> GetAccountsAsync(
        string? userName,
        int current,
        int size,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        current = Math.Max(1, current);
        size = Math.Clamp(size, 1, 200);
        RefAsync<int> total = 0;
        var accounts = await Db.Queryable<WeChatClawBotAccountEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(userName), x => x.UserName.Contains(userName!))
            .OrderByDescending(x => x.UpdateTime)
            .ToPageListAsync(current, size, total);
        return new PagedResultDto<WeChatClawBotAccountDto>
        {
            Current = current,
            Size = size,
            Total = total,
            Records = accounts.Select(ToAccountDto).ToList()
        };
    }

    public async Task SetAdminDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (_ai != null && disabled) await _ai.DisconnectAsync(userId);
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId)
            ?? throw new InvalidOperationException("未找到该用户的 ClawBot 绑定。");
        account.AdminDisabled = disabled;
        account.Status = disabled ? "Disabled" : string.IsNullOrWhiteSpace(account.BotToken) ? "Unbound" : "Bound";
        account.UpdateTime = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
    }

    public async Task<PagedResultDto<WeChatClawBotDeliveryDto>> GetDeliveryPageAsync(
        WeChatClawBotDeliveryQueryDto query,
        bool onlyCurrentUser,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var current = Math.Max(1, query.Current);
        var size = Math.Clamp(query.Size, 1, 200);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<WeChatClawBotDeliveryEntity>()
            .WhereIF(onlyCurrentUser && !string.IsNullOrWhiteSpace(userId), x => x.UserId == userId)
            .WhereIF(!string.IsNullOrWhiteSpace(query.UserName), x => x.UserName.Contains(query.UserName!))
            .WhereIF(!string.IsNullOrWhiteSpace(query.Source), x => x.Source == query.Source)
            .WhereIF(!string.IsNullOrWhiteSpace(query.Status), x => x.Status == query.Status)
            .WhereIF(query.StartTime.HasValue, x => x.CreateTime >= query.StartTime!.Value)
            .WhereIF(query.EndTime.HasValue, x => x.CreateTime <= query.EndTime!.Value)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(current, size, total);
        return new PagedResultDto<WeChatClawBotDeliveryDto>
        {
            Current = current,
            Size = size,
            Total = total,
            Records = records.Select(ToDeliveryDto).ToList()
        };
    }

    public async Task<WeChatClawBotDeliveryDto> SendDebugAsync(WeChatClawBotSendDto dto, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dto.UserId)) throw new InvalidOperationException("请选择接收用户。");
        return await SendOneAsync(dto.UserId, dto.Content, "Debug", null, null, cancellationToken, false);
    }

    public Task<WeChatClawBotDeliveryDto> SendMyTestAsync(string userId, string content, CancellationToken cancellationToken = default)
    {
        return SendOneAsync(userId, content, "PersonalTest", null, null, cancellationToken, false);
    }

    /// <summary>系统卡片消息复用微信适配和审计，不借用外部 MCP 的授权开关。</summary>
    public async Task<WeChatClawBotDeliveryDto> SendSystemMessageAsync(string userId, string content,
        string idempotencyKey, string taskId, CancellationToken cancellationToken = default, string? expectedBindingIdentity = null)
    {
        await SystemSendGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var existing = await Db.Queryable<WeChatClawBotDeliveryEntity>()
                .FirstAsync(x => x.UserId == userId && x.Source == "DeviceCard" && x.IdempotencyKey == idempotencyKey);
            if (existing != null) return ToDeliveryDto(existing);
            return await SendOneAsync(userId, content, "DeviceCard", idempotencyKey, taskId,
                cancellationToken, false, classifyUncertainResult: true, expectedBindingIdentity: expectedBindingIdentity);
        }
        finally
        {
            SystemSendGate.Release();
        }
    }

    public async Task<WeChatClawBotDeliveryDto> SendQueuedMessageAsync(string userId, string content,
        string idempotencyKey, string taskId, string bindingIdentity, CancellationToken cancellationToken, string source = "AgentPush")
    {
        if (source is not ("AgentPush" or "MCP")) throw new ArgumentException("不支持的队列来源");
        await SystemSendGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            var existing = await Db.Queryable<WeChatClawBotDeliveryEntity>().FirstAsync(x =>
                x.UserId == userId && x.Source == source && x.IdempotencyKey == idempotencyKey);
            if (existing != null) return ToDeliveryDto(existing);
            return await SendOneAsync(userId, content, source, idempotencyKey, taskId, cancellationToken,
                true, classifyUncertainResult: true, expectedBindingIdentity: bindingIdentity);
        }
        finally
        {
            SystemSendGate.Release();
        }
    }

    public async Task<PagedResultDto<WeChatClawBotInboundMessageDto>> GetMyInboundMessagesAsync(
        string userId,
        int current,
        int size,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        current = Math.Max(1, current);
        size = Math.Clamp(size, 1, 100);
        RefAsync<int> total = 0;
        var messages = await Db.Queryable<WeChatClawBotInboundMessageEntity>()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Id)
            .ToPageListAsync(current, size, total);
        return new PagedResultDto<WeChatClawBotInboundMessageDto>
        {
            Current = current,
            Size = size,
            Total = total,
            Records = messages.Select(message => new WeChatClawBotInboundMessageDto
            {
                Id = message.Id,
                WeChatMessageId = message.WeChatMessageId,
                SenderId = message.SenderId,
                MessageType = message.MessageType,
                Content = message.Content,
                MessageTime = message.MessageTime,
                ReceivedTime = message.ReceivedTime
            }).ToList()
        };
    }

    public async Task<IReadOnlyList<WeChatClawBotRecipientDto>> GetMcpRecipientsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return (await Db.Queryable<WeChatClawBotAccountEntity>()
                .Where(x => x.Status == "Bound" && x.McpPushEnabled && !x.AdminDisabled && x.BotToken != null)
                .OrderBy(x => x.UserName)
                .ToListAsync())
            .Select(x => new WeChatClawBotRecipientDto { RecipientCode = x.UserId, UserName = x.UserName, Status = x.Status })
            .ToList();
    }

    public async Task<object> GetMcpChannelStatusAsync(string? recipientCode, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var accounts = await Db.Queryable<WeChatClawBotAccountEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(recipientCode), x => x.UserId == recipientCode)
            .OrderBy(x => x.UserName)
            .ToListAsync();
        return new
        {
            total = accounts.Count,
            bound = accounts.Count(x => x.Status == "Bound" && !x.AdminDisabled),
            channels = accounts.Select(x => new
            {
                recipientCode = x.UserId,
                userName = x.UserName,
                status = x.Status,
                mcpPushEnabled = x.McpPushEnabled,
                adminDisabled = x.AdminDisabled,
                lastOnlineTime = x.LastOnlineTime,
                lastSendTime = x.LastSendTime,
                lastError = x.LastError
            })
        };
    }

    public async Task<object> GetMcpDeliveryStatusAsync(
        string? recipientCode,
        string? idempotencyKey,
        string? status,
        int current,
        int size,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        current = Math.Max(1, current);
        size = Math.Clamp(size, 1, 100);
        RefAsync<int> total = 0;
        var records = await Db.Queryable<WeChatClawBotDeliveryEntity>()
            .WhereIF(!string.IsNullOrWhiteSpace(recipientCode), x => x.UserId == recipientCode)
            .WhereIF(!string.IsNullOrWhiteSpace(idempotencyKey), x => x.IdempotencyKey == idempotencyKey)
            .WhereIF(!string.IsNullOrWhiteSpace(status), x => x.Status == status)
            .OrderByDescending(x => x.CreateTime)
            .ToPageListAsync(current, size, total);
        return new PagedResultDto<WeChatClawBotDeliveryDto>
        {
            Current = current,
            Size = size,
            Total = total,
            Records = records.Select(ToDeliveryDto).ToList()
        };
    }

    public async Task<IReadOnlyList<WeChatClawBotDeliveryDto>> SendByMcpAsync(
        WeChatClawBotMcpSendDto dto,
        CancellationToken cancellationToken = default)
    {
        if (dto.RecipientCodes.Count == 0) throw new InvalidOperationException("至少指定一位接收用户。");
        if (string.IsNullOrWhiteSpace(dto.IdempotencyKey)) throw new InvalidOperationException("idempotencyKey 不能为空。");
        var source = Trim(dto.Source, 32) ?? "MCP";
        if (source.Equals("AI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI 为系统保留消息来源，外部推送不能使用该来源。");
        var results = new List<WeChatClawBotDeliveryDto>();
        foreach (var userId in dto.RecipientCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var duplicate = await Db.Queryable<WeChatClawBotDeliveryEntity>()
                .FirstAsync(x => x.UserId == userId && x.Source == source && x.IdempotencyKey == dto.IdempotencyKey);
            if (duplicate != null)
            {
                results.Add(ToDeliveryDto(duplicate));
                continue;
            }
            results.Add(await SendOneAsync(userId, dto.Content, source, dto.IdempotencyKey, dto.ExternalTaskId, cancellationToken, true));
        }
        return results;
    }

    public async Task<WeChatClawBotDeliveryDto> RetryDeliveryAsync(
        long deliveryId,
        bool requireMcpPushPermission,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var original = await Db.Queryable<WeChatClawBotDeliveryEntity>().InSingleAsync(deliveryId)
            ?? throw new InvalidOperationException("投递记录不存在。");
        if (original.Source == "AI")
            throw new InvalidOperationException("AI 回复不支持脱离原授权任务重试，请在微信重新发送请求。");
        if (original.Status != "Failed") throw new InvalidOperationException("仅失败的投递记录可以重试。");
        var retryKey = $"retry:{original.Id}:{original.AttemptCount + 1}";
        return await SendOneAsync(
            original.UserId,
            original.Content,
            original.Source,
            retryKey,
            original.ExternalTaskId,
            cancellationToken,
            requireMcpPushPermission,
            original.Id);
    }

    public async Task MaintainConversationsAsync(CancellationToken cancellationToken = default)
    {
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        await EnsureInitializedAsync(cancellationToken);
        if (!_options.Enabled || !_options.MonitorEnabled) return;

        var accounts = await Db.Queryable<WeChatClawBotAccountEntity>()
            .Where(x => x.Status == "Bound" && !x.AdminDisabled && x.BotToken != null)
            .ToListAsync();
        using var gate = new SemaphoreSlim(3, 3);
        var tasks = accounts.Select(async account =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await RefreshConversationAsync(account, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task RefreshConversationAsync(WeChatClawBotAccountEntity account, CancellationToken cancellationToken)
    {
        // Task.WhenAll 的每个账号必须持有独立连接和事务。
        using var operation = DatabaseOperationScope.Begin(_dbScope);
        try
        {
            var conversation = await Db.Queryable<WeChatClawBotConversationEntity>()
                .FirstAsync(x => x.UserId == account.UserId)
                ?? new WeChatClawBotConversationEntity { UserId = account.UserId, UpdateTime = DateTime.Now };
            var response = await SendAuthenticatedAsync(account, "/ilink/bot/getupdates", new
            {
                get_updates_buf = conversation.UpdateCursor ?? string.Empty,
                base_info = GetBaseInfo()
            }, cancellationToken);
            using var document = JsonDocument.Parse(response);
            EnsureSuccessfulWeChatResponse(document.RootElement);

            var nextCursor = FindString(document.RootElement, "get_updates_buf");
            var contextToken = FindLatestMessageContextToken(document.RootElement, account.ILinkUserId);
            var now = DateTime.Now;
            if (!string.IsNullOrWhiteSpace(nextCursor)) conversation.UpdateCursor = nextCursor;
            if (!string.IsNullOrWhiteSpace(contextToken))
            {
                conversation.ContextToken = contextToken;
                conversation.LastInboundTime = now;
            }
            conversation.UpdateTime = now;
            var transaction = await Db.Ado.UseTranAsync(async () =>
            {
                // 长轮询期间用户可能解绑或重新绑定，旧响应不能写回新会话。
                var accountId = account.Id;
                var botToken = account.BotToken;
                var stillBound = await Db.Queryable<WeChatClawBotAccountEntity>().AnyAsync(x =>
                    x.Id == accountId && x.Status == "Bound" && !x.AdminDisabled && x.BotToken == botToken);
                if (!stillBound) return;

                await SaveInboundMessagesAsync(account, document.RootElement, now);
                if (conversation.Id == 0)
                    conversation.Id = await Db.Insertable(conversation).ExecuteReturnBigIdentityAsync();
                else
                    await Db.Updateable(conversation).ExecuteCommandAsync();

                // 消息、上下文和游标一起提交，保存失败时保留旧游标，下次重新接收。
                await Db.Updateable<WeChatClawBotAccountEntity>()
                    .SetColumns(x => new WeChatClawBotAccountEntity
                    {
                        LastOnlineTime = now,
                        LastError = null,
                        UpdateTime = now
                    })
                    .Where(x => x.Id == account.Id)
                    .ExecuteCommandAsync();
            });
            if (!transaction.IsSuccess)
                throw transaction.ErrorException ?? new InvalidOperationException("保存微信接收消息失败。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // 收消息长轮询超时属于正常等待，不把它当成账号失效。
        }
        catch (Exception exception)
        {
            var error = Trim(exception.Message, 1000);
            var now = DateTime.Now;
            await Db.Updateable<WeChatClawBotAccountEntity>()
                .SetColumns(x => new WeChatClawBotAccountEntity { LastError = error, UpdateTime = now })
                .Where(x => x.Id == account.Id)
                .ExecuteCommandAsync();
        }
    }

    private async Task SaveInboundMessagesAsync(WeChatClawBotAccountEntity account, JsonElement response, DateTime receivedTime)
    {
        foreach (var message in GetUserMessages(response, account.ILinkUserId))
        {
            var messageId = FindString(message, "message_id");
            var identity = messageId ?? FindString(message, "seq") ?? message.GetRawText();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.BotId}:{identity}")));
            if (await Db.Queryable<WeChatClawBotInboundMessageEntity>().AnyAsync(x =>
                    x.UserId == account.UserId && x.MessageKey == key))
            {
                continue;
            }

            var (messageType, content) = GetMessageContent(message);
            DateTime? messageTime = null;
            if (long.TryParse(FindString(message, "create_time_ms"), out var milliseconds) &&
                milliseconds >= -62135596800000L && milliseconds <= 253402300799999L)
            {
                var parsedTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
                // SQL Server datetime 的最小值为 1753-01-01。
                if (parsedTime.Year >= 1753) messageTime = parsedTime;
            }

            var inbound = new WeChatClawBotInboundMessageEntity
            {
                UserId = account.UserId,
                MessageKey = key,
                WeChatMessageId = Trim(messageId, 128),
                SenderId = account.ILinkUserId!,
                MessageType = messageType,
                Content = content,
                MessageTime = messageTime,
                ReceivedTime = receivedTime
            };
            if (_ai != null)
                await _ai.HandleInboundAsync(account, inbound);
            else
            {
                inbound.Content = WeChatClawBotAiPolicy.Redact(inbound.Content);
                await Db.Insertable(inbound).ExecuteCommandAsync();
            }
        }
    }

    private static IEnumerable<JsonElement> GetUserMessages(JsonElement response, string? senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId) || response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("msgs", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (FindInteger(message, "message_type") == 1 &&
                string.Equals(FindString(message, "from_user_id"), senderId, StringComparison.Ordinal))
            {
                yield return message;
            }
        }
    }

    private static (string MessageType, string Content) GetMessageContent(JsonElement message)
    {
        var types = new HashSet<string>();
        var parts = new List<string>();
        if (message.TryGetProperty("item_list", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var type = FindInteger(item, "type");
                var label = type switch { 1 => "Text", 2 => "Image", 3 => "Voice", 4 => "File", 5 => "Video", _ => "Other" };
                types.Add(label);
                parts.Add(type switch
                {
                    1 => FindString(item, "text") ?? string.Empty,
                    2 => "[图片]",
                    3 => FindString(item, "text") ?? "[语音]",
                    4 => $"[文件] {FindString(item, "file_name") ?? string.Empty}",
                    5 => "[视频]",
                    _ => "[其他消息]"
                });
            }
        }

        var messageType = types.Count == 1 ? types.Single() : types.Count > 1 ? "Mixed" : "Other";
        var content = string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return (messageType, string.IsNullOrWhiteSpace(content) ? "[无文本内容]" : content);
    }

    public async Task<WeChatClawBotDeliveryDto> SendAiMessageAsync(string userId, string content,
        string idempotencyKey, string bindingIdentity, CancellationToken cancellationToken = default)
    {
        await SystemSendGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await Db.Queryable<WeChatClawBotDeliveryEntity>()
                .FirstAsync(x => x.UserId == userId && x.Source == "AI" && x.IdempotencyKey == idempotencyKey);
            if (existing != null) return ToDeliveryDto(existing);
            return await SendOneAsync(userId, content, "AI", idempotencyKey, idempotencyKey,
                cancellationToken, false, classifyUncertainResult: true, expectedBindingIdentity: bindingIdentity);
        }
        finally
        {
            SystemSendGate.Release();
        }
    }

    private async Task<WeChatClawBotDeliveryDto> SendOneAsync(
        string userId,
        string content,
        string source,
        string? idempotencyKey,
        string? externalTaskId,
        CancellationToken cancellationToken,
        bool requireMcpPushPermission,
        long? retryOfDeliveryId = null,
        bool classifyUncertainResult = false,
        string? expectedBindingIdentity = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(content) || content.Length > 10000)
            throw new InvalidOperationException("消息内容不能为空，且不能超过 10000 个字符。");
        content = content.Trim();
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId)
            ?? throw new InvalidOperationException("接收用户尚未绑定微信 ClawBot。");
        if (expectedBindingIdentity != null && expectedBindingIdentity != WeChatClawBotBindingIdentity.Create(account))
            throw new InvalidOperationException("微信绑定已变更，请重新触发推送。");
        if (account.Status != "Bound" || account.AdminDisabled || string.IsNullOrWhiteSpace(account.BotToken))
            throw new InvalidOperationException("该用户当前不允许接收 ClawBot 推送。");
        if (requireMcpPushPermission && !account.McpPushEnabled)
            throw new InvalidOperationException("该用户已关闭外部 MCP 推送授权。");
        if (string.IsNullOrWhiteSpace(account.ILinkUserId))
            throw new InvalidOperationException("该用户尚未建立可用微信会话，请让用户重新扫码绑定后再发送。");

        var conversation = await Db.Queryable<WeChatClawBotConversationEntity>()
            .FirstAsync(x => x.UserId == account.UserId);
        if (string.IsNullOrWhiteSpace(conversation?.ContextToken))
            throw new InvalidOperationException("请先在微信向 ClawBot 发送一条消息，待网页收到后再测试发送。");

        var now = DateTime.Now;
        var delivery = new WeChatClawBotDeliveryEntity
        {
            RetryOfDeliveryId = retryOfDeliveryId,
            UserId = account.UserId,
            UserName = account.UserName,
            Source = Trim(source, 32) ?? "MCP",
            ExternalTaskId = Trim(externalTaskId, 128),
            IdempotencyKey = Trim(idempotencyKey, 128),
            Content = content,
            Status = "Sending",
            ClientMessageId = Guid.NewGuid().ToString("N"),
            AttemptCount = 1,
            CreateTime = now,
            UpdateTime = now
        };
        delivery.Id = await Db.Insertable(delivery).ExecuteReturnBigIdentityAsync();
        var stopwatch = Stopwatch.StartNew();
        var providerAccepted = false;
        try
        {
            var isLimit = source == "AI" && _ai != null && await _ai.ValidateReplyAsync(userId, idempotencyKey!);
            if (_ai != null) await _ai.ReserveSendAsync(account, !isLimit, cancellationToken);
            if (source == "AI" && _ai != null) await _ai.ValidateReplyAsync(userId, idempotencyKey!);
            var currentAccount = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == userId);
            if (currentAccount == null || currentAccount.Status != "Bound" || currentAccount.AdminDisabled ||
                currentAccount.BotToken != account.BotToken ||
                WeChatClawBotBindingIdentity.Create(currentAccount) != WeChatClawBotBindingIdentity.Create(account) ||
                requireMcpPushPermission && !currentAccount.McpPushEnabled)
                throw new InvalidOperationException("微信绑定或发送授权已变更，本次发送已取消。");
            var result = await SendAuthenticatedAsync(account, "/ilink/bot/sendmessage", new
            {
                msg = new
                {
                    from_user_id = string.Empty,
                    to_user_id = account.ILinkUserId,
                    client_id = delivery.ClientMessageId,
                    message_type = 2,
                    message_state = 2,
                    context_token = conversation.ContextToken,
                    item_list = new[] { new { type = 1, text_item = new { text = content } } }
                },
                base_info = GetBaseInfo()
            }, cancellationToken);
            using var document = JsonDocument.Parse(result);
            EnsureSuccessfulWeChatResponse(document.RootElement);
            providerAccepted = true;

            delivery.Status = "Sent";
            delivery.WeChatMessageId = Trim(FindString(document.RootElement, "message_id", "msg_id", "server_id"), 128);
            delivery.SentTime = DateTime.Now;
            var sentTime = delivery.SentTime;
            await Db.Updateable<WeChatClawBotAccountEntity>()
                .SetColumns(x => new WeChatClawBotAccountEntity
                {
                    LastSendTime = sentTime,
                    LastError = null,
                    UpdateTime = DateTime.Now
                })
                .Where(x => x.Id == account.Id)
                .ExecuteCommandAsync();
        }
        catch (Exception exception)
        {
            var uncertain = exception is OperationCanceledException or IOException or JsonException ||
                exception is HttpRequestException http && (http.StatusCode == null ||
                    (int)http.StatusCode >= 500 || http.StatusCode == System.Net.HttpStatusCode.RequestTimeout);
            delivery.Status = classifyUncertainResult && providerAccepted ? "Sent" :
                classifyUncertainResult && uncertain ? "Unknown" : "Failed";
            delivery.ErrorMessage = Trim(exception.Message, 2000);
            var error = Trim(exception.Message, 1000);
            var errorTime = DateTime.Now;
            await Db.Updateable<WeChatClawBotAccountEntity>()
                .SetColumns(x => new WeChatClawBotAccountEntity { LastError = error, UpdateTime = errorTime })
                .Where(x => x.Id == account.Id)
                .ExecuteCommandAsync();
        }
        finally
        {
            delivery.DurationMs = stopwatch.ElapsedMilliseconds;
            delivery.UpdateTime = DateTime.Now;
            await Db.Updateable(delivery).ExecuteCommandAsync();
        }
        return ToDeliveryDto(delivery);
    }

    private async Task ConfirmBindingAsync(
        WeChatClawBotBindingSessionEntity session,
        JsonElement response,
        string botToken,
        CancellationToken cancellationToken)
    {
        var account = await Db.Queryable<WeChatClawBotAccountEntity>().FirstAsync(x => x.UserId == session.UserId)
            ?? new WeChatClawBotAccountEntity { UserId = session.UserId, UserName = session.UserId, CreateTime = DateTime.Now };
        account.BotToken = _tokenProtector.Protect(botToken);
        account.BotId = Trim(FindString(response, "ilink_bot_id", "bot_id"), 128);
        account.ILinkUserId = Trim(FindString(response, "ilink_user_id", "user_id"), 256);
        account.BaseUrl = Trim(FindString(response, "baseurl", "base_url"), 256) ?? _options.BaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(account.BotId) || string.IsNullOrWhiteSpace(account.ILinkUserId))
            throw new InvalidOperationException("微信未返回完整账号信息，请重新扫码绑定。");
        if (_ai != null) await _ai.DisconnectAsync(session.UserId);
        account.Status = account.AdminDisabled ? "Disabled" : "Bound";
        account.LastOnlineTime = DateTime.Now;
        account.LastError = null;
        account.UpdateTime = DateTime.Now;
        var transaction = await Db.Ado.UseTranAsync(async () =>
        {
            if (account.Id == 0)
                account.Id = await Db.Insertable(account).ExecuteReturnBigIdentityAsync();
            else
                await Db.Updateable(account).ExecuteCommandAsync();

            var conversation = await Db.Queryable<WeChatClawBotConversationEntity>()
                .FirstAsync(x => x.UserId == session.UserId);
            if (conversation == null)
            {
                await Db.Insertable(new WeChatClawBotConversationEntity
                {
                    UserId = session.UserId,
                    UpdateTime = DateTime.Now
                }).ExecuteCommandAsync();
            }
            else
            {
                // 重新绑定后旧游标和旧上下文不能用于新的微信会话。
                conversation.UpdateCursor = null;
                conversation.ContextToken = null;
                conversation.LastInboundTime = null;
                conversation.UpdateTime = DateTime.Now;
                await Db.Updateable(conversation).ExecuteCommandAsync();
            }
        });
        if (!transaction.IsSuccess)
            throw transaction.ErrorException ?? new InvalidOperationException("保存微信绑定信息失败。");
    }

    private async Task RefreshUserNameAsync(WeChatClawBotAccountEntity account, string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || account.UserName == userName) return;
        account.UserName = Trim(userName, 128) ?? account.UserName;
        account.UpdateTime = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
    }

    private async Task<string> SendAnonymousAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, string? baseUrl = null)
    {
        using var request = new HttpRequestMessage(method, BuildUri(baseUrl ?? _options.BaseUrl, path));
        if (body != null) request.Content = JsonContent.Create(body);
        if (method == HttpMethod.Get)
        {
            // 状态查询使用官方客户端的应用头，不携带 Bot 鉴权信息。
            AddApplicationHeaders(request);
        }
        return await SendAsync(request, cancellationToken);
    }

    private async Task<string> SendAuthenticatedAsync(WeChatClawBotAccountEntity account, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(account.BaseUrl ?? _options.BaseUrl, path))
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("AuthorizationType", "ilink_bot_token");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UnprotectBotToken(account.BotToken!)}");
        request.Headers.TryAddWithoutValidation("X-WECHAT-UIN", GenerateUin());
        AddApplicationHeaders(request);
        return await SendAsync(request, cancellationToken);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content != null)
        {
            // JsonContent 默认无法预先计算长度，HTTP/1.1 会采用 chunked 传输。
            // iLink 对该传输方式返回 412；预先缓冲并设置实际字节长度，
            // 使二维码申请和业务请求都使用 Content-Length 发送完整 JSON。
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            request.Content.Headers.ContentLength = bodyBytes.LongLength;
        }

        using var client = _httpClientFactory.CreateClient(nameof(WeChatClawBotService));
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"微信 ClawBot 请求失败：{(int)response.StatusCode} {text[..Math.Min(text.Length, 500)]}",
                null,
                response.StatusCode);
        }

        return text;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await InitializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            using var initializationDb = Db.CopyNew();
            await using var schemaLock = await WeChatClawBotAiLock.TryEnterAsync(initializationDb, "ClawBotAi:schema", 10000)
                ?? throw new InvalidOperationException("微信数据表正在初始化，请稍后重试");
            WeChatClawBotDatabaseInitializer.Initialize(initializationDb);
            _initialized = true;
        }
        finally
        {
            InitializeGate.Release();
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw new InvalidOperationException("微信 ClawBot 集成未启用，请联系管理员配置服务。");
    }

    private static WeChatClawBotAccountDto ToAccountDto(WeChatClawBotAccountEntity entity) => new()
    {
        UserId = entity.UserId, UserName = entity.UserName, Status = entity.Status,
        Bound = entity.Status == "Bound" && !string.IsNullOrWhiteSpace(entity.BotToken),
        McpPushEnabled = entity.McpPushEnabled, AdminDisabled = entity.AdminDisabled,
        LastOnlineTime = entity.LastOnlineTime, LastSendTime = entity.LastSendTime,
        LastError = entity.LastError, UpdateTime = entity.UpdateTime
    };

    private static WeChatClawBotBindingDto ToBindingDto(WeChatClawBotBindingSessionEntity entity)
    {
        var showQrcode = entity.Status is "Waiting" or "Scanned" or "NeedVerifyCode";
        var message = entity.Status switch
        {
            "Waiting" => "请使用微信扫码，正在等待扫码结果。",
            "Scanned" => "已扫码，请在手机微信中完成确认。",
            "NeedVerifyCode" => "请输入手机微信显示的数字验证码，完成绑定验证。",
            "Confirmed" => "微信 ClawBot 绑定成功，请先在微信发送一条消息建立会话。",
            "Expired" => "二维码已过期，请重新生成并扫码。",
            "Blocked" => "验证码错误次数过多，请重新生成二维码。",
            _ => "绑定未完成，请重新生成二维码。"
        };
        return new WeChatClawBotBindingDto
        {
            SessionId = entity.Id,
            Status = entity.Status,
            Qrcode = showQrcode ? entity.Qrcode : null,
            QrcodeUrl = showQrcode ? entity.QrcodeUrl : null,
            ExpireTime = entity.ExpireTime,
            Message = entity.LastError == null ? message : $"{message} {entity.LastError}"
        };
    }

    private static WeChatClawBotDeliveryDto ToDeliveryDto(WeChatClawBotDeliveryEntity entity) => new()
    {
        Id = entity.Id, RetryOfDeliveryId = entity.RetryOfDeliveryId, UserId = entity.UserId, UserName = entity.UserName, Source = entity.Source,
        ExternalTaskId = entity.ExternalTaskId, IdempotencyKey = entity.IdempotencyKey, Content = entity.Content,
        Status = entity.Status, WeChatMessageId = entity.WeChatMessageId, AttemptCount = entity.AttemptCount,
        DurationMs = entity.DurationMs, ErrorMessage = entity.ErrorMessage, SentTime = entity.SentTime,
        CreateTime = entity.CreateTime
    };

    private static Uri BuildUri(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}");

    private void AddApplicationHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("iLink-App-Id", "bot");
        request.Headers.TryAddWithoutValidation("iLink-App-ClientVersion", _options.AppVersion);
    }

    private object GetBaseInfo() => new
    {
        channel_version = _options.ChannelVersion,
        bot_agent = _options.BotAgent
    };

    private static string GenerateUin()
    {
        var value = ((uint)Random.Shared.NextInt64(1, (long)uint.MaxValue))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string NormalizePollingBaseUrl(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || !Uri.TryCreate($"https://{host.Trim()}", UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("微信返回的扫码节点无效，请重新生成二维码。");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private string UnprotectBotToken(string protectedValue)
    {
        try
        {
            return _tokenProtector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 兼容极早期尚未加密的本地测试数据；后续重新扫码会自动升级为受保护值。
            return protectedValue;
        }
    }

    private static bool IsExpiredStatus(string status) => status.Contains("expire", StringComparison.OrdinalIgnoreCase) || status is "4" or "Expired";
    private static bool IsScannedStatus(string status) => status.Contains("scan", StringComparison.OrdinalIgnoreCase) || status is "1" or "Scanned";

    private static string? FindString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind != JsonValueKind.Null)
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                var nested = FindString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static int? FindInteger(JsonElement element, params string[] names)
    {
        var value = FindString(element, names);
        return int.TryParse(value, out var result) ? result : null;
    }

    private static void EnsureSuccessfulWeChatResponse(JsonElement response)
    {
        var ret = FindInteger(response, "ret");
        var errorCode = FindInteger(response, "errcode");
        if ((ret.HasValue && ret.Value != 0) || (errorCode.HasValue && errorCode.Value != 0))
        {
            var code = ret.HasValue && ret.Value != 0 ? ret.Value : errorCode!.Value;
            throw new InvalidOperationException(
                FindString(response, "errmsg", "message", "error") ?? $"微信返回错误码 {code}");
        }
    }

    private static string? FindLatestMessageContextToken(JsonElement element, string? senderId)
    {
        string? result = null;
        foreach (var message in GetUserMessages(element, senderId))
        {
            var token = FindString(message, "context_token");
            if (!string.IsNullOrWhiteSpace(token)) result = token;
        }
        return result;
    }

    private static string? Trim(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
