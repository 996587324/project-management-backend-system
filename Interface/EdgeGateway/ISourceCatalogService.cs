using Model.EdgeGateway;
using Model.SystemApplications;

namespace Interface;

public interface ISourceCatalogService
{
    Task<SourceCatalogPage<SourceCatalogModel>> ModelsAsync(ApplicationAccessContext access, SourceCatalogQuery query);
    Task<SourceCatalogPage<SourceCatalogDevice>> DevicesAsync(ApplicationAccessContext access, SourceCatalogQuery query);
    Task<SourceCatalogDevice?> ResolveAsync(ApplicationAccessContext access, SourceCatalogQuery query);
}
