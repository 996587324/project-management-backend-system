using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.JudgeGrade
{
    public class SortingAttribute : Attribute
    {
        public string SortingGradeParamName { get; set; }
        public SortingAttribute(string SortingGradeParamName)
        {

            this.SortingGradeParamName = SortingGradeParamName;
        }
    }
}
