using Model.WeChatClawBot;
using Model.SystemApplications;

namespace Interface;

public interface IWeChatClawBotAiService
{
    Task<WeChatClawBotAiStateDto> GetStateAsync(string userId);
    Task SaveAsync(string userId, WeChatClawBotAiSaveDto dto);
    Task<string> ResetSecretAsync(string userId);
    Task DisconnectAsync(string userId);
    Task ClearContextAsync(string userId);
    Task<List<object>> GetOptionsAsync(string userId);
    Task<List<WeChatClawBotAiJobEntity>> GetJobsAsync(string userId, int current, int size);
    Task HandleInboundAsync(WeChatClawBotAccountEntity account, WeChatClawBotInboundMessageEntity message);
    Task<List<string>> GetPendingUsersAsync();
    Task ProcessUserAsync(string userId, IAgentService agent, IWeChatClawBotService sender, CancellationToken cancellationToken);
    Task ReserveSendAsync(WeChatClawBotAccountEntity account, bool reserveNotice, CancellationToken cancellationToken);
    Task<bool> ValidateReplyAsync(string userId, string idempotencyKey);
}
