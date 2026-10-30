using Model.EmailPush;
using Model.SystemApplications;
using Model.ExternalTools;

namespace Interface;

public interface IEmailPushService
{
    Task<bool> IsAdministratorAsync(ApplicationAccessContext access);
    Task<object> GetAvailabilityAsync(ApplicationAccessContext access);
    Task<EmailSettingDto> GetSettingAsync(ApplicationAccessContext access);
    Task<EmailSettingDto> SaveSettingAsync(ApplicationAccessContext access, EmailSettingSaveDto dto);
    Task TestConnectionAsync(ApplicationAccessContext access, CancellationToken cancellationToken);
    Task<EmailPage<EmailRecipientDto>> GetRecipientsAsync(ApplicationAccessContext access, string? keyword, int current, int size);
    Task<EmailEnqueueResult> EnqueueAsync(ApplicationAccessContext access, EmailEnqueueDto dto, string source = "Manual");
    Task<EmailPage<EmailRecordDto>> GetRecordsAsync(ApplicationAccessContext access, EmailRecordQuery query);
    Task RetryAsync(ApplicationAccessContext access, long deliveryId, EmailRetryDto dto);
    Task DispatchAsync(CancellationToken cancellationToken);
    Task<EmailEnqueueResult> EnqueueWorkTaskAsync(string deliveryId);
    Task<EmailPage<EmailRecipientDto>> GetMcpRecipientsAsync(ExternalToolCallerContext caller, string? keyword, int current, int size);
    Task<object> GetMcpAvailabilityAsync(ExternalToolCallerContext caller);
    Task<EmailEnqueueResult> EnqueueByMcpAsync(ExternalToolCallerContext caller, EmailEnqueueDto dto);
    Task<EmailPage<EmailRecordDto>> GetMcpRecordsAsync(ExternalToolCallerContext caller, EmailRecordQuery query);
    Task RetryByMcpAsync(ExternalToolCallerContext caller, long deliveryId, bool confirm);
}

public interface IEmailPushDatabaseInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IEmailSecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IEmailTransport
{
    Task TestAsync(EmailPushSettingEntity setting, CancellationToken cancellationToken);
    Task<EmailTransportResult> SendAsync(EmailPushSettingEntity setting, EmailPushTaskEntity task,
        EmailPushDeliveryEntity delivery, CancellationToken cancellationToken);
}
