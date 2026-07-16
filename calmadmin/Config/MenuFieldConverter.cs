using Model.Dto;
using Newtonsoft.Json;

namespace calmadmin.Config
{
    /// <summary>
    /// 菜单字段转换静态工具（避开可选参数问题）
    /// </summary>
    public static class MenuFieldConverter
    {
        /// <summary>
        /// 逗号分隔字符串 → List<string>（无可选参数）
        /// </summary>
        public static List<string> ConvertStringToList(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return new List<string>();
            }
            // 显式指定所有参数，避免可选参数
            return source.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>
        /// JSON字符串 → List<FrontMenuAuthItemDto>（无可选参数）
        /// </summary>
        public static List<FrontMenuAuthItemDto> ConvertJsonToAuthList(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return new List<FrontMenuAuthItemDto>();
            }
            // 显式调用无默认参数的重载
            return JsonConvert.DeserializeObject<List<FrontMenuAuthItemDto>>(source);
        }
    }
}
