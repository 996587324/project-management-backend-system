using Model.WorkTasks;

namespace Interface;

public interface IWorkTaskService
{
    Task<object> BootstrapAsync(WorkActor actor);
    Task<object> CandidatesAsync(WorkActor actor, WorkCandidateQuery query);
    Task<object> DispatchGroupsAsync(WorkActor actor);
    Task<object> OverviewAsync(WorkActor actor, WorkOverviewQuery query);
    Task<object> ListAsync(WorkActor actor, WorkQuery query);
    Task<object> DetailAsync(WorkActor actor, string id);
    Task<WorkCommandResult> CommandAsync(WorkActor actor, string? id, WorkTaskCommand command);
    Task<object> OperationAsync(WorkActor actor, string id);
    Task<object> AdministrationAsync(WorkActor actor);
    Task<object> SaveGroupAsync(WorkActor actor, WorkGroup group);
    Task<object> SaveMemberAsync(WorkActor actor, WorkMember member);
    Task<object> SaveGrantAsync(WorkActor actor, WorkGrant grant);
    Task<object> SaveRuleAsync(WorkActor actor, WorkNotificationRule rule);
    Task<object> KeysAsync(WorkActor actor);
    Task<object> KeyActivityAsync(WorkActor actor);
    Task RecordMcpUseAsync(WorkActor actor, string tool, bool success);
    Task<object> CreateKeyAsync(WorkActor actor, WorkKeyRequest request);
    Task RevokeKeyAsync(WorkActor actor, string id);
    Task<WorkActor> AuthenticateKeyAsync(string token);
    Task<object> BeginUploadAsync(WorkActor actor, WorkUploadRequest request);
    Task UploadAsync(string id, string ticket, Stream stream, CancellationToken cancellationToken);
    Task<object> CompleteUploadAsync(WorkActor actor, string id);
    Task<(string Path, string Name, string ContentType)> DownloadAsync(WorkActor actor, string id);
    Task<object> InboxAsync(WorkActor actor);
    Task MarkReadAsync(WorkActor actor, string id);
    Task RetryDeliveryAsync(WorkActor actor, string id);
    Task HandoverAsync(WorkActor actor, WorkHandoverRequest request);
    Task TickAsync(CancellationToken cancellationToken);
}
public interface IWorkTaskInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}
