using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Attributes;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System.Threading.Tasks;
using System;

namespace ShiftSoftware.ShiftEntity.Web
{
    public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO> :
        ControllerBase
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
        public Repository repository { get; set; }

        public ShiftEntityControllerAsync(Repository repository)
        {
            this.repository = repository;
        }

        [HttpGet]
        public IActionResult Get()
        {
            return Ok(repository.OdataList());
        }

        [HttpGet("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> GetSingle(Guid key, [FromHeader] DateTime? asOf)
        {
            var item = await repository.FindAsync(key, asOf);

            if (item == null)
                return NotFound();

            return Ok(await repository.ViewAsync(item));
        }

        [HttpGet]
        public async Task<IActionResult> GetRevisions(Guid key)
        {
            return Ok(await repository.GetRevisionsAsync(key));
        }

        [HttpPost]
        [ODataIgnored]
        public async Task<IActionResult> Post([FromBody] DTO dto)
        {
            Entity newItem;

            try
            {
                newItem = await repository.CreateAsync(dto);
            }
            catch (ShiftEntityException ex)
            {
                return BadRequest(new ShiftEntityResponse<DTO>
                {
                    Message = ex.Message
                });
            }

            repository.Add(newItem);

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<DTO>(await repository.ViewAsync(newItem)));
        }

        [HttpPut("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> Put(Guid key, [FromBody] DTO dto)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            try
            {
                await repository.UpdateAsync(item, dto);
            }
            catch (ShiftEntityException ex)
            {
                return BadRequest(new ShiftEntityResponse<DTO>
                {
                    Message = ex.Message
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<DTO>(await repository.ViewAsync(item)));
        }

        [HttpDelete("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            await repository.DeleteAsync(item);

            await repository.SaveChangesAsync();

            return Ok(await repository.ViewAsync(item));
        }
    }
}
