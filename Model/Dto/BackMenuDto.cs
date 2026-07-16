using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.Dto
{
    public class BackMenuDto
    {
        /// <summary>
        /// 菜单类型 menu-菜单 button-按钮
        /// </summary>
        public string MenuType { get; set; } = "menu";

        /// <summary>
        /// 菜单ID
        /// </summary>
        public int Id { get; set; } = 0;

        /// <summary>
        /// 路由名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 路由路径
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 菜单显示名称
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// 组件路径
        /// </summary>
        public string Component { get; set; } = string.Empty;

        /// <summary>
        /// 菜单图标
        /// </summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsEnable { get; set; } = true;

        /// <summary>
        /// 排序号
        /// </summary>
        public int Sort { get; set; } = 1;

        /// <summary>
        /// 是否为菜单
        /// </summary>
        public bool IsMenu { get; set; } = true;

        /// <summary>
        /// 是否缓存页面
        /// </summary>
        public bool KeepAlive { get; set; } = true;

        /// <summary>
        /// 是否隐藏菜单
        /// </summary>
        public bool IsHide { get; set; } = false;

        /// <summary>
        /// 是否隐藏标签页
        /// </summary>
        public bool IsHideTab { get; set; } = false;

        /// <summary>
        /// 外链地址
        /// </summary>
        public string Link { get; set; } = string.Empty;

        /// <summary>
        /// 是否为iframe外链
        /// </summary>
        public bool IsIframe { get; set; } = false;

        /// <summary>
        /// 是否显示徽标
        /// </summary>
        public bool ShowBadge { get; set; } = false;

        /// <summary>
        /// 徽标文字
        /// </summary>
        public string ShowTextBadge { get; set; } = string.Empty;

        /// <summary>
        /// 是否固定标签页
        /// </summary>
        public bool FixedTab { get; set; } = false;

        /// <summary>
        /// 高亮菜单路径
        /// </summary>
        public string ActivePath { get; set; } = string.Empty;

        /// <summary>
        /// 角色权限列表
        /// </summary>
        public List<string> Roles { get; set; } = new List<string>();

        /// <summary>
        /// 是否全屏页面
        /// </summary>
        public bool IsFullPage { get; set; } = false;

        /// <summary>
        /// 权限标识名称
        /// </summary>
        public string AuthName { get; set; } = string.Empty;

        /// <summary>
        /// 权限显示名称
        /// </summary>
        public string AuthLabel { get; set; } = string.Empty;

        /// <summary>
        /// 权限图标
        /// </summary>
        public string AuthIcon { get; set; } = string.Empty;

        /// <summary>
        /// 权限排序
        /// </summary>
        public int AuthSort { get; set; } = 1;
    }
}
