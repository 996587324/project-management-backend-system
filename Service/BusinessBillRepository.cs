using Interface;
using Model.Entitys;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class BusinessBillRepository : IBusinessBillRepository
    {
        private readonly ISqlSugarClient _db;

        public BusinessBillRepository(ISqlSugarClient db)
        {
            _db = db;
        }

        public async Task<long> AddAsync(BusinessBill entity)
        {
            _db.CodeFirst.SetStringDefaultLength(200).InitTables(typeof(BusinessBill));
            return await _db.Insertable(entity).ExecuteReturnBigIdentityAsync();
        }

        public async Task<bool> UpdateAsync(BusinessBill entity)
        {
            return await _db.Updateable(entity).ExecuteCommandAsync() > 0;
        }

        public async Task<bool> DeleteAsync(long id)
        {
            return await _db.Deleteable<BusinessBill>().In(id).ExecuteCommandAsync() > 0;
        }

        public async Task<BusinessBill> GetByIdAsync(long id)
        {
            return await _db.Queryable<BusinessBill>().In(id).FirstAsync();
        }

        public async Task<List<BusinessBill>> GetListAsync()
        {
            return await _db.Queryable<BusinessBill>().OrderByDescending(x => x.Id).ToListAsync();
        }

        public async Task<(List<BusinessBill>, int)> GetPageListAsync(int pageIndex, int pageSize)
        {
            var total = 0;
            var list = await _db.Queryable<BusinessBill>()
                .OrderByDescending(x => x.Id)
                .ToPageListAsync(pageIndex, pageSize, total);

            return (list, total);
        }
    }
}
