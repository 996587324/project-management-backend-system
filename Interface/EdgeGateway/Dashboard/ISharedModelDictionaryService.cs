using Model.EdgeGateway;

namespace Interface;

/// <summary>平台共享映射，与 A/B、页面布局和报警规则的作用范围无关。</summary>
public interface ISharedModelDictionaryService
{
    Task<List<EdgeDashboardModelDisplayMappingOutputDto>> GetModelsAsync();
    Task<EdgeDashboardModelDisplayMappingOutputDto> SaveModelAsync(EdgeDashboardModelDisplayMappingSaveDto dto);
    Task DeleteModelAsync(long id);
    Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> GetFieldsAsync(string modelMark);
    Task<List<EdgeDashboardModelFieldDisplayMappingOutputDto>> SaveFieldsAsync(EdgeDashboardModelFieldDisplayMappingListSaveDto dto);
}
