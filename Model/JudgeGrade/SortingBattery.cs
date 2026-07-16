using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.JudgeGrade
{
    public class SortingBattery
    {
        /// <summary>
        /// 工单号
        /// </summary>
        public string WorkOrder { get; set; }
        public string FlowName { get; set; } = string.Empty;
        /// <summary>
        /// 电芯条码
        /// </summary>
        public string ContainerName { get; set; }

        /// <summary>
        /// 位置
        /// </summary>
        public int Location { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreateTime { get; set; } = DateTime.Now;

        /// <summary>
        /// OCV1电压
        /// </summary>
        [Sorting("O1Voltage")]
        public decimal O1Voltage { get; set; }

        /// <summary>
        /// OCV1内阻
        /// </summary>
        [Sorting("O1Resistance")]
        public decimal O1Resistance { get; set; }


        /// <summary>
        /// OCV1壳体电压
        /// </summary>
        [Sorting("O1HousingVoltage")]
        public decimal O1HousingVoltage { get; set; }

        /// <summary>
        /// OCV1测试时间
        /// </summary>
        [Sorting("O1TestTime")]
        public DateTime O1TestTime { get; set; } = DateTime.Now;

        /// <summary>
        /// OCV2电压
        /// </summary>
        [Sorting("O2Voltage")]
        public decimal O2Voltage { get; set; }


        /// <summary>
        /// OCV2内阻
        /// </summary>
        [Sorting("O2Resistance")]
        public decimal O2Resistance { get; set; }


        /// <summary>
        /// OCV2壳体电压
        /// </summary>
        [Sorting("O2HousingVoltage")]
        public decimal O2HousingVoltage { get; set; }

        /// <summary>
        /// OCV2测试时间
        /// </summary>
        [Sorting("O2TestTime")]
        public DateTime O2TestTime { get; set; } = DateTime.Now;


        /// <summary>
        /// OCV2K1值
        /// </summary>
        [Sorting("K1")]
        public decimal K1 { get; set; }


        /// <summary>
        /// OCV3电压
        /// </summary>
        [Sorting("O3Voltage")]
        public decimal O3Voltage { get; set; }


        /// <summary>
        /// OCV3内阻
        /// </summary>
        [Sorting("O3Resistance")]
        public decimal O3Resistance { get; set; }


        /// <summary>
        /// OCV3壳体电压
        /// </summary>
        [Sorting("O3HousingVoltage")]
        public decimal O3HousingVoltage { get; set; }

        /// <summary>
        /// OCV3测试时间
        /// </summary>
        [Sorting("O3TestTime")]
        public DateTime O3TestTime { get; set; } = DateTime.Now;


        /// <summary>
        /// OCV3K2值
        /// </summary>
        [Sorting("K2")]
        public decimal K2 { get; set; }


        /// <summary>
        /// OCV4电压
        /// </summary>
        [Sorting("O4Voltage")]
        public decimal O4Voltage { get; set; }


        /// <summary>
        /// OCV4内阻
        /// </summary>
        [Sorting("O4Resistance")]
        public decimal O4Resistance { get; set; }


        /// <summary>
        /// OCV4壳体电压
        /// </summary>
        [Sorting("O4HousingVoltage")]
        public decimal O4HousingVoltage { get; set; }

        /// <summary>
        /// OCV4测试时间
        /// </summary>
        [Sorting("O4TestTime")]
        public DateTime O4TestTime { get; set; } = DateTime.Now;


        /// <summary>
        /// OCV4K3值
        /// </summary>
        [Sorting("K3")]
        public decimal K3 { get; set; }

        /// <summary>
        /// DCIR值
        /// </summary>
        [Sorting("DCIR")]
        public decimal DCIR { get; set; }

        /// <summary>
        /// CDCIR值
        /// </summary>
        [Sorting("CDCIR")]
        public decimal CDCIR { get; set; }

        /// <summary>
        /// 分容容量
        /// </summary>
        [Sorting("PartitionCapacity")]
        public decimal PartitionCapacity { get; set; }

        /// <summary>
        /// 分容补偿容量
        /// </summary>
        [Sorting("PartitionRlCapacity")]
        public decimal PartitionRlCapacity { get; set; }

        /// <summary>
        /// 分容起始电压
        /// </summary>
        [Sorting("PartitionStartVoltage")]
        public decimal PartitionStartVoltage { get; set; }


        /// <summary>
        /// 分容结束电压
        /// </summary>
        [Sorting("PartitionEndVoltage")]
        public decimal PartitionEndVoltage { get; set; }

        /// <summary>
        /// 化成第一次充电容量
        /// </summary>
        [Sorting("HCCDCAP_ONE")]
        public decimal HCCDCAP_ONE { get; set; }

        /// <summary>
        /// 化成第二次充电容量
        /// </summary>
        [Sorting("HCCDCAP_TWO")]
        public decimal HCCDCAP_TWO { get; set; }

        /// <summary>
        /// 化成结束电压
        /// </summary>
        [Sorting("HCENDVOL")]
        public decimal HCENDVOL { get; set; }

        /// <summary>
        /// SOC容量
        /// </summary>
        [Sorting("SOCCapacity")]
        public decimal SOCCapacity { get; set; }

        /// <summary>
        /// SOC开始时间
        /// </summary>
        [Sorting("SOCStartTime")]
        public DateTime SOCStartTime { get; set; } = DateTime.Now;

        /// <summary>
        /// SOC结束时间
        /// </summary>
        [Sorting("SOCEndTime")]
        public DateTime SOCEndTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 一次注液前重
        /// </summary>
        [Sorting("FWeightBeforeFilling")]
        public decimal FWeightBeforeFilling { get; set; }

        /// <summary>
        /// 一次后注液后重
        /// </summary>
        [Sorting("FWeightAfterFilling")]
        public decimal FWeightAfterFilling { get; set; }

        /// <summary>
        /// 一次注液量
        /// </summary>
        [Sorting("FFirstInjectWeight")]
        public decimal FFirstInjectWeight { get; set; }

        /// <summary>
        /// 二次注液前重
        /// </summary>
        [Sorting("SWeightBeforeFilling")]
        public decimal SWeightBeforeFilling { get; set; }

        /// <summary>
        /// 二次注液后重
        /// </summary>
        [Sorting("SWeightAfterFilling")]
        public decimal SWeightAfterFilling { get; set; }

        /// <summary>
        /// 二次注液量
        /// </summary>
        [Sorting("SFirstInjectWeight")]
        public decimal SFirstInjectWeight { get; set; }

        /// <summary>
        /// 总注液量
        /// </summary>
        [Sorting("TotalWeight")]
        public decimal TotalWeight { get; set; }

        /// <summary>
        /// 分选等级
        /// </summary>
        public string SortingGrade { get; set; }

        /// <summary>
        /// 分选规则名
        /// </summary>

        public string SortingRuleName { get; set; }


        /// <summary>
        /// 分选规则详情
        /// </summary>
        public string SortingRuleDetail { get; set; } = "";
    }
}
