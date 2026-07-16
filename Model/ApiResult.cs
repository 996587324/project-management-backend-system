using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model
{
    /// <summary>
    /// 统一返回模型
    /// </summary>
    public class ApiResult
    {
        public bool IsSuccess { get; set; }
        public object Result { get; set; }
        public string Msg { get; set; }
    }
    /// <summary>
    /// 统一返回模型
    /// </summary>
    public class BatCodeResult
    {
        public int code { get; set; }
        public string Msg { get; set; }
        public List<BatCodeList>? BatList { get; set; }
    }
}
