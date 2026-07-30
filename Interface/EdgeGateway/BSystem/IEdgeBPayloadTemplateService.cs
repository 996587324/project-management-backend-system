using Model.EdgeGateway;

namespace Interface;

/// <summary>B 系统 payload 模板管理接口，负责模板校验、持久化和软删除。</summary>
public interface IEdgeBPayloadTemplateService
{
    /// <summary>查询全部未删除的 B 系统 payload 模板。</summary>
    Task<List<EdgeBPayloadTemplateOutputDto>> GetListAsync();

    /// <summary>新增或修改可复用的 B 系统 payload 模板。</summary>
    Task<int> SaveAsync(EdgeBPayloadTemplateSaveDto dto);

    /// <summary>软删除模板，已经保存到映射中的快照继续有效。</summary>
    Task<bool> DeleteAsync(int id);
}
