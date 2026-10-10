using Model.EdgeGateway;

namespace Interface;

public interface IEdgeDeviceCardPushService
{
    Task<EdgeDeviceCardPushQueueSettingDto> GetQueueSettingAsync();
    Task SaveQueueSettingAsync(EdgeDeviceCardPushQueueSettingDto dto);
    Task<List<EdgeDeviceCardPushRecipientDto>> GetRecipientsAsync();
    Task<EdgePagedResultDto<EdgeDeviceCardPushTaskEntity>> GetTasksAsync(EdgeDeviceCardPushTaskQueryDto query);
    Task<int> QueueTestAsync(EdgeDeviceCardPushTestDto dto);
    Task RetryAsync(long taskId);
    Task DispatchAsync(CancellationToken cancellationToken);
}
