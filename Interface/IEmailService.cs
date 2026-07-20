using Model.Agent;

namespace Interface;

public interface IEmailService
{
    Task<AgentEmailSendResultDto> SendAsync(AgentEmailSendRequest request);
}
