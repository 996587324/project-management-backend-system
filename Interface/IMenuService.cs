using Model.Dto;
using Model.Menu;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IMenuService
    {
        /// <summary>
        /// 查询所有菜单（树形结构）
        /// </summary>
        Task<List<sys_menu>> GetMenuTreeAsync();
        /// <summary>
        /// 转换DTO
        /// </summary>
        /// <returns></returns>
        Task<List<FrontMenuDto>> GetFrontMenuTreeAsync();

        /// <summary>
        /// 新增菜单
        /// </summary>
        Task<bool> AddMenuAsync(BackMenuDto menu);

        /// <summary>
        /// 修改菜单
        /// </summary>
        Task<bool> UpdateMenuAsync(BackMenuDto menu);

        /// <summary>
        /// 删除菜单（级联删除子菜单）
        /// </summary>
        Task<bool> DeleteMenuAsync(string menuId);

        /// <summary>
        /// 根据ID查询单个菜单
        /// </summary>
        Task<sys_menu> GetMenuByIdAsync(string menuId);
    }
}
