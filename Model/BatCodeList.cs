using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model
{
    public class BatCodeList
    {
        public string Batcode { get; set; }

        public double? Ocv4Vol { get; set; }

        public double? Ocv4Acir { get; set; }

        public double? Ocv4ShellVol { get; set; }

        public double? Ocv3Vol { get; set; }

        public double? Ocv3Acir { get; set; }

        public double? Ocv3ShellVol { get; set; }

        public double? Capacity { get; set; }

        public double? Ocv2Vol { get; set; }

        public double? Ocv2Acir { get; set; }

        public double? R { get; set; }

        public double? K12 { get; set; }

        public double? Ocv2ShellVol { get; set; }

        public double? Ocv1Vol { get; set; }

        public double? Ocv1Acir { get; set; }

        public double? Ocv1ShellVol { get; set; }

        public DateTime? OCV1Time { get; set; }

        public DateTime? OCV2Time { get; set; }

        public DateTime? OCV3Time { get; set; }

        public DateTime? OCV4Time { get; set; }

    }
    
}
