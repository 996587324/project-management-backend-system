using AutoMapper;
using Interface;
using Model.Dto;
using Model.Entitys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class BusinessBillService : IBusinessBillService
    {
        private readonly IBusinessBillRepository _repository;
        private readonly IMapper _mapper;

        public BusinessBillService(IBusinessBillRepository repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }

        /// <summary>
        /// 新增
        /// </summary>
        public async Task<long> CreateAsync(BusinessBillCreateDto dto)
        {
            // AutoMapper 映射 DTO → Entity
            var entity = _mapper.Map<BusinessBill>(dto);
            entity.CreateTime = DateTime.Now;

            return await _repository.AddAsync(entity);
        }

        /// <summary>
        /// 修改
        /// </summary>
        public async Task<bool> ModifyAsync(BusinessBillUpdateDto dto)
        {
            var entity = await _repository.GetByIdAsync(dto.Id);
            if (entity == null)
                return false;

            // AutoMapper 把 dto 的值覆盖到 entity
            _mapper.Map(dto, entity);

            return await _repository.UpdateAsync(entity);
        }

        /// <summary>
        /// 删除
        /// </summary>
        public async Task<bool> RemoveAsync(long id)
        {
            return await _repository.DeleteAsync(id);
        }

        /// <summary>
        /// 详情
        /// </summary>
        public async Task<BusinessBill> GetDetailAsync(long id)
        {
            return await _repository.GetByIdAsync(id);
        }

        /// <summary>
        /// 全部列表
        /// </summary>
        public async Task<List<BusinessBill>> GetAllAsync()
        {
            return await _repository.GetListAsync();
        }

        /// <summary>
        /// 分页
        /// </summary>
        public async Task<(List<BusinessBill> list, int total)> GetPageAsync(PagedDto dto)
        {
            return await _repository.GetPageListAsync(dto.PageIndex, dto.PageSize);
        }
    }
}
