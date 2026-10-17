using Interface;
using Model.EdgeGateway;

namespace Service;

/// <summary>
/// 第一阶段复用已发布的表和数据。production-dashboard 仅为旧表的兼容存储分区，
/// 不再从调用系统或看板选择中文映射。禁止把 A/B 编码作为字典作用范围。
/// </summary>
public sealed class SharedModelDictionaryService(IEdgeDashboardService dashboard) : ISharedModelDictionaryService
{
    public const string StorageScope = "production-dashboard";
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public Task<List<EdgeDashboardModelDisplayMappingOutputDto>> GetModelsAsync() =>
        dashboard.GetModelDisplayMappingsAsync(StorageScope);

    public Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> GetFieldsAsync(string modelMark) =>
        dashboard.GetModelFieldDisplayMappingsAsync(StorageScope, modelMark);

    public async Task<EdgeDashboardModelDisplayMappingOutputDto> SaveModelAsync(EdgeDashboardModelDisplayMappingSaveDto dto)
    {
        await WriteGate.WaitAsync();
        try
        {
            dto.DashboardCode = StorageScope;
            return await dashboard.SaveModelDisplayMappingAsync(dto);
        }
        finally { WriteGate.Release(); }
    }

    public async Task DeleteModelAsync(long id)
    {
        await WriteGate.WaitAsync();
        try { await dashboard.DeleteModelDisplayMappingAsync(id); }
        finally { WriteGate.Release(); }
    }

    public async Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> SaveFieldsAsync(EdgeDashboardModelFieldDisplayMappingListSaveDto dto)
    {
        await WriteGate.WaitAsync();
        try
        {
            dto.DashboardCode = StorageScope;
            return await dashboard.SaveModelFieldDisplayMappingsAsync(dto);
        }
        finally { WriteGate.Release(); }
    }
}
