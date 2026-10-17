using Model.EdgeGateway;
using Model.SystemApplications;

namespace Interface;

public interface IEdgeModelFieldCatalogService
{
    Task<List<string>> GetModelsAsync();
    Task<EdgeModelFieldCatalogPage> GetPageAsync(EdgeModelFieldCatalogQuery query);
    Task<List<EdgeModelFieldReference>> GetReferencesAsync(string modelMark, long id);
    Task<EdgeModelFieldCatalogChangeResult> ChangeAsync(EdgeModelFieldCatalogChange input, ApplicationAccessContext actor);
}
