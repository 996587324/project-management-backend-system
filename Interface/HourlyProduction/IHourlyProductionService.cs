using Model.HourlyProduction;
using Model.SystemApplications;

namespace Interface;

public interface IHourlyProductionService
{
    Task<List<HourlyBoardDefinition>> ListAsync(ApplicationAccessContext access);
    Task<HourlyBoardDefinition> SaveAsync(ApplicationAccessContext access, HourlyBoardDefinition definition);
    Task<List<HourlyRemovedProcess>> RemovedProcessesAsync(ApplicationAccessContext access, string code, string lineCode);
    Task<HourlyBoardRuntime> RuntimeAsync(ApplicationAccessContext? access, string code, bool publicRead = false, bool previous = false);
    Task<object> ReportsAsync(ApplicationAccessContext access, string code, int page);
    Task<object> DispatchesAsync(ApplicationAccessContext access, string code, int page);
    Task RetryDispatchAsync(ApplicationAccessContext access, long id, bool confirm);
    Task<object> PublishedBoardsAsync();
    Task<object> PublishedReportsAsync(string code, int page);
    Task TickAsync(CancellationToken cancellationToken);
    Task RefreshAdviceAsync(CancellationToken cancellationToken);
}

public interface IHourlyProductionInitializer
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IHourlyProductionIngress
{
    Task CaptureRealtimeAsync(string source, string device, string? messageId, DateTime occurredAt,
        IReadOnlyList<string> frames, CancellationToken cancellationToken);
}
