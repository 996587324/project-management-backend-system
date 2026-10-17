using Model.WeChatClawBot;
using Model.ProjectManage;

namespace Interface;

public interface IWeChatClawBotService
{
    Task<WeChatClawBotDeliveryDto> SendAiMessageAsync(string userId, string content, string idempotencyKey,
        string bindingIdentity, CancellationToken cancellationToken = default);
    Task<WeChatClawBotDeliveryDto> SendSystemMessageAsync(string userId, string content, string idempotencyKey,
        string taskId, CancellationToken cancellationToken = default, string? expectedBindingIdentity = null);
    Task<WeChatClawBotAccountDto> GetMyAccountAsync(string userId, string userName, CancellationToken cancellationToken = default);
    Task<WeChatClawBotBindingDto> CreateBindingAsync(string userId, string userName, CancellationToken cancellationToken = default);
    Task<WeChatClawBotBindingDto> GetBindingStatusAsync(string userId, long sessionId, CancellationToken cancellationToken = default, string? verifyCode = null);
    Task SetMcpPushEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default);
    Task UnbindAsync(string userId, CancellationToken cancellationToken = default);
    Task<PagedResultDto<WeChatClawBotAccountDto>> GetAccountsAsync(string? userName, int current, int size, CancellationToken cancellationToken = default);
    Task SetAdminDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default);
    Task<PagedResultDto<WeChatClawBotDeliveryDto>> GetDeliveryPageAsync(WeChatClawBotDeliveryQueryDto query, bool onlyCurrentUser, string? userId, CancellationToken cancellationToken = default);
    Task<WeChatClawBotDeliveryDto> SendDebugAsync(WeChatClawBotSendDto dto, CancellationToken cancellationToken = default);
    Task<WeChatClawBotDeliveryDto> SendMyTestAsync(string userId, string content, CancellationToken cancellationToken = default);
    Task<PagedResultDto<WeChatClawBotInboundMessageDto>> GetMyInboundMessagesAsync(string userId, int current, int size, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WeChatClawBotRecipientDto>> GetMcpRecipientsAsync(CancellationToken cancellationToken = default);
    Task<object> GetMcpChannelStatusAsync(string? recipientCode, CancellationToken cancellationToken = default);
    Task<object> GetMcpDeliveryStatusAsync(string? recipientCode, string? idempotencyKey, string? status, int current, int size, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WeChatClawBotDeliveryDto>> SendByMcpAsync(WeChatClawBotMcpSendDto dto, CancellationToken cancellationToken = default);
    Task<WeChatClawBotDeliveryDto> RetryDeliveryAsync(long deliveryId, bool requireMcpPushPermission, CancellationToken cancellationToken = default);
    /// <summary>后台接收已绑定用户的消息，并维护消息游标与会话上下文。</summary>
    Task MaintainConversationsAsync(CancellationToken cancellationToken = default);
}
