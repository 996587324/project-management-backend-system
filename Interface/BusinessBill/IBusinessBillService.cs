using Model.Dto;
using Model.Entitys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interface
{
    public interface IBusinessBillService
    {
        Task<long> CreateAsync(BusinessBillCreateDto dto);
        Task<bool> ModifyAsync(BusinessBillUpdateDto dto);
        Task<bool> RemoveAsync(long id);
        Task<BusinessBill> GetDetailAsync(long id);
        Task<List<BusinessBill>> GetAllAsync();
        Task<(List<BusinessBill> list, int total)> GetPageAsync(PagedDto dto);
    }
}
