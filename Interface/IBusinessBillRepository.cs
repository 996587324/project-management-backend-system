using Model.Entitys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IBusinessBillRepository
    {
        Task<long> AddAsync(BusinessBill entity);
        Task<bool> UpdateAsync(BusinessBill entity);
        Task<bool> DeleteAsync(long id);
        Task<BusinessBill> GetByIdAsync(long id);
        Task<List<BusinessBill>> GetListAsync();
        Task<(List<BusinessBill>, int)> GetPageListAsync(int pageIndex, int pageSize);
    }
}
