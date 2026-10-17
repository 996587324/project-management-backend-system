using Model.Dto;
using Model.Menu;
using Model.SystemApplications;

namespace Interface;

public interface ISystemApplicationMenuService
{
    Task<List<FrontMenuDto>> GetTreeAsync(ApplicationAccessContext access, bool includeDisabled = false);
    Task<sys_menu?> GetMenuAsync(ApplicationAccessContext access, int id);
    Task SaveMenuAsync(ApplicationAccessContext access, BackMenuDto dto);
    Task DeleteMenuAsync(ApplicationAccessContext access, int id);
    Task DeleteButtonAsync(ApplicationAccessContext access, int menuId, string authMark);
    Task ConfigureSourcesAsync(string appCode, List<string> sourceMenuIds);
}
