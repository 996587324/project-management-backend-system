using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    public class RolePageData
    {
        public List<SysRole> records { get; set; } = new List<SysRole>();
        public int current { get; set; }
        public int size { get; set; }
        public int total { get; set; }
        public RoleDebugInfo _debug { get; set; } = new RoleDebugInfo();
    }

    public class RoleDebugInfo
    {
        public int totalPages { get; set; }
        public bool isLastPage { get; set; }
        public int currentPageSize { get; set; }
        public int startIndex { get; set; }
    }
}
