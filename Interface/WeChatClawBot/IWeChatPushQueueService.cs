using Model.EmailPush;
using Model.SystemApplications;
using Model.WeChatClawBot;
using Model.ExternalTools;

namespace Interface;

public interface IWeChatPushQueueService
{
    Task<EmailPage<WeChatClawBotRecipientDto>> GetRecipientsAsync(ApplicationAccessContext access, string? keyword, int current, int size);
    Task<object> GetChannelStatusAsync(ApplicationAccessContext access, string recipientUserId);
    Task<object> EnqueueAsync(ApplicationAccessContext access, WeChatPushEnqueueDto dto);
    Task<EmailPage<WeChatPushQueueRecordDto>> GetRecordsAsync(ApplicationAccessContext access, long? taskId, int current, int size);
    Task RetryAsync(ApplicationAccessContext access, long id);
    Task DispatchAsync(CancellationToken cancellationToken);
    Task<object> EnqueueWorkTaskAsync(string deliveryId);
    Task<int> GetIntervalAsync(ApplicationAccessContext access);
    Task<bool> CanConfigureAsync(ApplicationAccessContext access);
    Task SetIntervalAsync(ApplicationAccessContext access, int seconds);
    Task<object> EnqueueByMcpAsync(ExternalToolCallerContext caller, WeChatPushEnqueueDto dto);
    Task<EmailPage<WeChatPushQueueRecordDto>> GetMcpRecordsAsync(ExternalToolCallerContext caller, WeChatPushQueueQuery query);
    Task RetryByMcpAsync(ExternalToolCallerContext caller, long? taskId, long? deliveryId, bool confirm);
}

public interface IWeChatPushQueueInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
