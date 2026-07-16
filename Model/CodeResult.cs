using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model
{
    public class CodeResult<T>
    {
        public int code { get; set; }

        public string Msg { get; set; }

        public List<T>? Data { get; set; }
    }
}
