using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SqlSugar;
namespace Model.Menu
{

    /// <summary>
    /// 系统菜单表 Model（适配SqlSugar + SQL Server）
    /// 对应数据库表：dbo.sys_menu
    /// </summary>
    [SugarTable("sys_menu")]
    public class sys_menu
    {
        /// <summary>
        /// 菜单ID（主键）
        /// </summary>
        [SugarColumn(
            ColumnName = "menu_id",
            IsPrimaryKey = true,
            Length = 64,
            IsNullable = false,
            ColumnDescription = "菜单ID（主键）"
        )]
        public string MenuId { get; set; }

        /// <summary>
        /// 父菜单ID（0为顶级）
        /// </summary>
        [SugarColumn(
            ColumnName = "parent_id",
            Length = 64,
            IsNullable = false,
            DefaultValue = "0",
            ColumnDescription = "父菜单ID（0为顶级）"
        )]
        public string ParentId { get; set; } = "0";

        /// <summary>
        /// 菜单名称（对应JSON的name）
        /// </summary>
        [SugarColumn(
            ColumnName = "menu_name",
            Length = 128,
            IsNullable = false,
            ColumnDescription = "菜单名称（对应JSON的name）"
        )]
        public string MenuName { get; set; }

        /// <summary>
        /// 路由路径（对应JSON的path）
        /// </summary>
        [SugarColumn(
            ColumnName = "path",
            Length = 255,
            IsNullable = false,
            ColumnDescription = "路由路径（对应JSON的path）"
        )]
        public string Path { get; set; }

        /// <summary>
        /// 前端组件路径（对应JSON的component）
        /// </summary>
        [SugarColumn(
            ColumnName = "component",
            Length = 255,
            IsNullable = true,
            ColumnDescription = "前端组件路径（对应JSON的component）"
        )]
        public string Component { get; set; }

        /// <summary>
        /// 菜单标题（对应meta.title）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_title",
            Length = 128,
            IsNullable = true,
            ColumnDescription = "菜单标题（对应meta.title）"
        )]
        public string MetaTitle { get; set; }

        /// <summary>
        /// 菜单图标（对应meta.icon）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_icon",
            Length = 64,
            IsNullable = true,
            ColumnDescription = "菜单图标（对应meta.icon）"
        )]
        public string MetaIcon { get; set; }

        /// <summary>
        /// 是否缓存页面（对应meta.keepAlive）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_keep_alive",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "是否缓存页面（对应meta.keepAlive）"
        )]
        public bool? MetaKeepAlive { get; set; } = false;

        /// <summary>
        /// 是否固定标签页（对应meta.fixedTab）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_fixed_tab",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "是否固定标签页（对应meta.fixedTab）"
        )]
        public bool? MetaFixedTab { get; set; } = false;

        /// <summary>
        /// 是否隐藏菜单（对应meta.isHide）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_is_hide",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "是否隐藏菜单（对应meta.isHide）"
        )]
        public bool? MetaIsHide { get; set; } = false;

        /// <summary>
        /// 是否隐藏标签页（对应meta.isHideTab）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_is_hide_tab",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "是否隐藏标签页（对应meta.isHideTab）"
        )]
        public bool? MetaIsHideTab { get; set; } = false;

        /// <summary>
        /// 是否全屏页面（对应meta.isFullPage）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_is_full_page",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "是否全屏页面（对应meta.isFullPage）"
        )]
        public bool? MetaIsFullPage { get; set; } = false;

        /// <summary>
        /// 角色权限（逗号分隔，对应meta.roles）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_roles",
            Length = 512,
            IsNullable = true,
            ColumnDescription = "角色权限（逗号分隔，对应meta.roles）"
        )]
        public string? MetaRoles { get; set; }

        /// <summary>
        /// 按钮权限列表（JSON字符串，对应meta.authList）
        /// </summary>
        [SugarColumn(
            ColumnName = "meta_auth_list",
            Length = 1024,
            IsNullable = true,
            ColumnDescription = "按钮权限列表（JSON字符串，对应meta.authList）"
        )]
        public string MetaAuthList { get; set; }

        /// <summary>
        /// 排序号（控制菜单展示顺序）
        /// </summary>
        [SugarColumn(
            ColumnName = "sort",
            IsNullable = true,
            DefaultValue = "0",
            ColumnDescription = "排序号（控制菜单展示顺序）"
        )]
        public int? Sort { get; set; } = 0;

        /// <summary>
        /// 状态：1=启用，0=禁用
        /// </summary>
        [SugarColumn(
            ColumnName = "status",
            IsNullable = true,
            DefaultValue = "1",
            ColumnDescription = "状态：1=启用，0=禁用"
        )]
        public bool? Status { get; set; } = true;

        /// <summary>
        /// 创建时间
        /// </summary>
        [SugarColumn(
            ColumnName = "create_time",
            IsNullable = true,
            DefaultValue = "GETDATE()",
            ColumnDescription = "创建时间"
        )]
        public DateTime? CreateTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 子菜单（非数据库字段，用于前端树形结构组装）
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public List<sys_menu> Children { get; set; } = new List<sys_menu>();

        #region 拓展：将字符串权限转换为列表（便于业务逻辑处理）
        /// <summary>
        /// 将meta_roles逗号分隔字符串转换为角色列表
        /// </summary>
        public List<string> GetMetaRolesList()
        {
            if (string.IsNullOrEmpty(MetaRoles))
                return new List<string>();
            return MetaRoles.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>
        /// 将meta_auth_list JSON字符串转换为按钮权限列表
        /// </summary>
        public List<MenuAuthItem> GetMetaAuthList()
        {
            if(MetaAuthList != null)
            {
                if (string.IsNullOrEmpty(MetaAuthList.ToString()))
                    return new List<MenuAuthItem>();
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<MenuAuthItem>>(MetaAuthList.ToString());
            }
            else
            {
                return new List<MenuAuthItem>();
            }
          
        }
        #endregion
    }

    /// <summary>
    /// 菜单按钮权限项（解析meta_auth_list用）
    /// </summary>
    public class MenuAuthItem
    {
        /// <summary>
        /// 权限标题
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 权限标识
        /// </summary>
        public string AuthMark { get; set; }
    }
}
