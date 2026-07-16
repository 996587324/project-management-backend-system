using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.User
{
    public class UserAdd
    {
        /// <summary>
        /// 主键
        /// </summary>
        public long Id { get; set; }
        public string Name { get; set; }
        public string MofiyName { get; set; }
        public int UserType {  get; set; }

        public string Password { get; set; }
        public bool IsEnable { get; set; }
        public string Description { get; set; }
    }
}
