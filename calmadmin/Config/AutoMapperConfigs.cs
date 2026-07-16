using AutoMapper;
using Model.User;
using Model;
using System.Data;
using Model.Entitys;
using Model.Menu;
using Newtonsoft.Json;
using Model.Dto;
using calmadmin.Config;

namespace 上传MES.Config
{
    /// <summary>
    /// Dto的映射配置
    /// </summary>
    public class AutoMapperConfigs : Profile
    {
        public AutoMapperConfigs()
        {
            //用户
            CreateMap<UserAdd, UserRes>().ReverseMap();
            CreateMap<UserEdit, UserRes>().ReverseMap();
          
            CreateMap<Users, UserRes>();
            CreateMap<UserAdd, Users>();
            CreateMap<UserEdit, Users>();

            // 1. 核心映射：SysMenu → FrontMenuDto
            CreateMap<sys_menu, FrontMenuDto>()
                // 普通字段映射（名称不同的手动指定，名称一致的自动映射）
                .ForMember(dest => dest.id, opt => opt.MapFrom(src => src.MenuId))
                .ForMember(dest => dest.name, opt => opt.MapFrom(src => src.MenuName))
                .ForMember(dest => dest.path, opt => opt.MapFrom(src => src.Path))
                .ForMember(dest => dest.component, opt => opt.MapFrom(src => src.Component))
                // 嵌套对象：Meta字段映射
                .ForMember(dest => dest.meta, opt => opt.MapFrom(src => src))
                // 子菜单递归映射
                .ForMember(dest => dest.children, opt => opt.MapFrom(src => src.Children));

            // 2. 嵌套映射：SysMenu → FrontMenuMetaDto（Meta字段单独映射）
            CreateMap<sys_menu, FrontMenuMetaDto>()
                .ForMember(dest => dest.title, opt => opt.MapFrom(src => src.MetaTitle))
                .ForMember(dest => dest.icon, opt => opt.MapFrom(src => src.MetaIcon))
                .ForMember(dest => dest.keepAlive, opt => opt.MapFrom(src => src.MetaKeepAlive))
                .ForMember(dest => dest.fixedTab, opt => opt.MapFrom(src => src.MetaFixedTab))
                .ForMember(dest => dest.isHide, opt => opt.MapFrom(src => src.MetaIsHide))
                .ForMember(dest => dest.isHideTab, opt => opt.MapFrom(src => src.MetaIsHideTab))
                .ForMember(dest => dest.isFullPage, opt => opt.MapFrom(src => src.MetaIsFullPage))
                    // 自定义转换：逗号分隔的字符串 → List<string>（角色）
                .ForMember(dest => dest.roles, opt => opt.MapFrom(src =>
                MenuFieldConverter.ConvertStringToList(src.MetaRoles)))
                // 自定义转换：JSON字符串 → List<FrontMenuAuthItemDto>（按钮权限）
                .ForMember(dest => dest.authList, opt => opt.MapFrom(src =>
                    string.IsNullOrEmpty(src.MetaAuthList)
                        ? new List<FrontMenuAuthItemDto>()
                        : JsonConvert.DeserializeObject<List<FrontMenuAuthItemDto>>(src.MetaAuthList)));

            // 3. 按钮权限映射（如果有数据库实体的话，这里直接匹配字段）
            CreateMap<MenuAuthItem, FrontMenuAuthItemDto>()
                .ForMember(dest => dest.title, opt => opt.MapFrom(src => src.Title))
                .ForMember(dest => dest.authMark, opt => opt.MapFrom(src => src.AuthMark));



            // 商品管理Dto → Entity
            CreateMap<BusinessBillCreateDto, BusinessBill>();

            CreateMap<BusinessBillUpdateDto, BusinessBill>();
        }
    }
}
