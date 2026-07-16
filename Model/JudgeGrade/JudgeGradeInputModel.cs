using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.JudgeGrade
{
    public class JudgeGradeInputModel
    {
        /// <summary>
        /// 托盘码
        /// </summary>
        public string TrayName { get; set; }
        /// <summary>
        /// 机台ID
        /// </summary>
        public string PCID { get; set; }

        /// <summary>
        /// 电芯集合
        /// </summary>

        public List<SortingBattery> ContainerNames { get; set; }

        /// <summary>
        /// 工艺名称
        /// </summary>
        public string RecipeName { get; set; }

        /// <summary>
        /// 本地还是Mes获取标志
        /// </summary>
        public bool IsMesOnline { get; set; }

        /// <summary>
        /// 时间
        /// </summary>
        public DateTime RecordTime { get; set; }
    }
}
