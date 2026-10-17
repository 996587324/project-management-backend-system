using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.Dto
{
    /// <summary>
    /// 前端菜单展示DTO（匹配第一个JSON结构）
    /// </summary>
    public class FrontMenuDto
    {
        /// <summary>
        /// 菜单ID（对应数据库MenuId）
        /// </summary>
        public string id { get; set; }

        /// <summary>
        /// 菜单名称（对应数据库MenuName）
        /// </summary>
        public string name { get; set; }

        /// <summary>
        /// 路由路径（对应数据库Path）
        /// </summary>
        public string path { get; set; }

        /// <summary>
        /// 前端组件路径（对应数据库Component）
        /// </summary>
        public string component { get; set; }

        /// <summary>
        /// 菜单元信息（嵌套DTO）
        /// </summary>
        public FrontMenuMetaDto meta { get; set; }

        /// <summary>
        /// 子菜单（递归嵌套）
        /// </summary>
        public List<FrontMenuDto> children { get; set; } = new List<FrontMenuDto>();
    }

    /// <summary>
    /// 前端菜单元信息DTO（匹配meta嵌套结构）
    /// </summary>
    public class FrontMenuMetaDto
    {
        public bool isEnable { get; set; } = true;
        public int sort { get; set; }
        public string permissionKey { get; set; } = string.Empty;
        /// <summary>
        /// 菜单标题（对应数据库MetaTitle）
        /// </summary>
        public string title { get; set; }

        /// <summary>
        /// 菜单图标（对应数据库MetaIcon）
        /// </summary>
        public string icon { get; set; }

        /// <summary>
        /// 是否缓存页面（对应数据库MetaKeepAlive）
        /// </summary>
        public bool? keepAlive { get; set; }

        /// <summary>
        /// 是否固定标签页（对应数据库MetaFixedTab）
        /// </summary>
        public bool? fixedTab { get; set; }

        /// <summary>
        /// 是否隐藏菜单（对应数据库MetaIsHide）
        /// </summary>
        public bool? isHide { get; set; }

        /// <summary>
        /// 是否隐藏标签页（对应数据库MetaIsHideTab）
        /// </summary>
        public bool? isHideTab { get; set; }

        /// <summary>
        /// 是否全屏页面（对应数据库MetaIsFullPage）
        /// </summary>
        public bool? isFullPage { get; set; }

        /// <summary>
        /// 角色权限列表（数据库MetaRoles逗号分隔转列表）
        /// </summary>
        public List<string> roles { get; set; } = new List<string>();

        /// <summary>
        /// 按钮权限列表（数据库MetaAuthList JSON字符串转列表）
        /// </summary>
        public List<FrontMenuAuthItemDto> authList { get; set; } = new List<FrontMenuAuthItemDto>();
    }

    /// <summary>
    /// 前端按钮权限DTO（匹配authList嵌套结构）
    /// </summary>
    public class FrontMenuAuthItemDto
    {
        /// <summary>
        /// 权限标题
        /// </summary>
        public string title { get; set; }

        /// <summary>
        /// 权限标识
        /// </summary>
        public string authMark { get; set; }
    }

}
