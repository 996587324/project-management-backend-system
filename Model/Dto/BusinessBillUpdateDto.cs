using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Model.Dto
{
    public class BusinessBillUpdateDto
    {
        public long Id { get; set; }
        public string Date { get; set; }
        public string OrderNo { get; set; }
        public string ProductName { get; set; }
        public string BrandName { get; set; }
        public string Spec { get; set; }
        public string Unit { get; set; }
        public int Quantity { get; set; }
        public string Type { get; set; }
        public decimal PriceUsd { get; set; }
        public decimal PriceKhr { get; set; }
        public string PaymentStatus { get; set; }
        public string Handler { get; set; }
        public string TradeType { get; set; }
        public string CustomerName { get; set; }
        public string Remark { get; set; }
    }
}
