using Model.ProcessBoard;
using Model.SystemApplications;

namespace Interface;

public interface IProcessBoardService
{
    Task<List<ProcessBoardDefinition>> GetDefinitionsAsync(ApplicationAccessContext access);
    Task<ProcessBoardDefinition> SaveAsync(ApplicationAccessContext access, ProcessBoardDefinition definition);
    Task<object> GetRuntimeAsync(ApplicationAccessContext access, long lineId);
    Task<object> GetHistoryAsync(ApplicationAccessContext access, long cardId, string fieldCode,
        DateTime start, DateTime end, int page, int size, bool events);
    Task<object> GetCurveAsync(ApplicationAccessContext access, long cardId, string fieldCode,
        DateTime start, DateTime end, int points, bool raw);
    Task<ProcessBoardSetting> GetSettingAsync();
    Task SaveSettingAsync(ApplicationAccessContext access, ProcessBoardSetting setting);
    Task CaptureAsync(CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
    Task<object> CreateExportAsync(ApplicationAccessContext access, ProcessExportRequest request);
    Task<object> GetExportAsync(ApplicationAccessContext access, long id);
    Task<string> GetExportFileAsync(ApplicationAccessContext access, long id);
    Task RunExportAsync(CancellationToken cancellationToken);
    Task<object> GetPushTasksAsync(ApplicationAccessContext access, long lineId, int page);
}

public interface IProcessFormulaService
{
    Task<ProcessFormulaResult> EvaluateAsync(ProcessFormulaRequest request, CancellationToken cancellationToken = default);
}
