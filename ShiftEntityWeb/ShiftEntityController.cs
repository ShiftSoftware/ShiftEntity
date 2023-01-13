using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftEntityWeb
{
    public class ShiftEntityController<Repository, Entity, ListDTO, DTO> :
        ControllerBase
        where Repository : IShiftRepository<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
        public Repository repository { get; set; }

        public ShiftEntityController(Repository repository)
        {
            this.repository = repository;
        }

        [HttpGet]
        [EnableQuery]
        public virtual IActionResult Get()
        {
            return Ok(repository.OdataList());
        }

        [HttpGet("{key}")]
        public virtual async Task<IActionResult> GetSingle(Guid key, [FromHeader] DateTime? asOf)
        {
            var item = await repository.FindAsync(key, asOf);

            if (item == null)
                return NotFound();

            return Ok(repository.View(item));
        }

        [HttpGet]
        [EnableQuery]
        public virtual async Task<IActionResult> GetRevisions(Guid key)
        {
            return Ok(await repository.GetRevisionsAsync(key));
        }

        [HttpPost]
        public virtual async Task<IActionResult> Post([FromBody] DTO dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            Entity newItem;

            try
            {
                newItem = repository.Create(dto);
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

            return Ok(new ShiftEntityResponse<DTO>(repository.View(newItem)));
        }

        [HttpPut("{key}")]
        public virtual async Task<IActionResult> Put(Guid key, [FromBody] DTO dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            try
            {
                repository.Update(item, dto);
            }
            catch (ShiftEntityException ex)
            {
                return BadRequest(new ShiftEntityResponse<DTO>
                {
                    Message = ex.Message
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<DTO>(repository.View(item)));
        }

        [HttpDelete("{key}")]
        public virtual async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            repository.Delete(item);

            await repository.SaveChangesAsync();

            return Ok(repository.View(item));
        }

        [NonAction]
        public virtual async Task<List<ListDTO>> GetSelectedItemsAsync(ODataQueryOptions<ListDTO> oDataQueryOptions)
        {
            var list = repository.OdataList();

            if (oDataQueryOptions.Filter != null)
                list = oDataQueryOptions.Filter.ApplyTo(list, new()) as IQueryable<ListDTO>;

            if (list != null)
                return await list.ToListAsync();

            return new List<ListDTO>();
        }
    }
}