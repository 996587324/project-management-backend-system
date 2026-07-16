using Interface;
using Microsoft.AspNetCore.Mvc;
using Model.Dto;

namespace calmadmin.Controllers
{
    [Route("api/[controller]/[action]")]
    public class BusinessBillController : ControllerBase
    {
        private readonly IBusinessBillService _service;

        public BusinessBillController(IBusinessBillService service)
        {
            _service = service;
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] BusinessBillCreateDto dto)
        {
            var id = await _service.CreateAsync(dto);
            return Ok(new { Id = id });
        }

        [HttpPost]
        public async Task<IActionResult> Update([FromBody] BusinessBillUpdateDto dto)
        {
            return Ok(await _service.ModifyAsync(dto));
        }

        [HttpPost]
        public async Task<IActionResult> Delete([FromBody] long id)
        {
            return Ok(await _service.RemoveAsync(id));
        }

        [HttpGet]
        public async Task<IActionResult> Detail(long id)
        {
            return Ok(await _service.GetDetailAsync(id));
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            return Ok(await _service.GetAllAsync());
        }

        [HttpPost]
        public async Task<IActionResult> Page([FromBody] PagedDto dto)
        {
            var (list, total) = await _service.GetPageAsync(dto);
            return Ok(new { list, total });
        }
    }
}
