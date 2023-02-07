using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using ShiftSoftware.ShiftEntity.Web.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShiftEntityWeb
{
    public class ShiftEntityController<Repository, Entity, ListDTO, DTO> :
        ShiftEntityController<Repository, Entity, ListDTO, DTO, DTO, DTO>
        where Repository : IShiftRepository<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
        public ShiftEntityController(Repository repository) : base(repository)
        {
        }
    }

    public class ShiftEntityController<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ControllerBase
        where Repository : IShiftRepository<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
    {
        public Repository repository { get; set; }

        public ShiftEntityController(Repository repository)
        {
            this.repository = repository;
        }

        [HttpGet]
        [EnableQuery]
        public virtual IActionResult Get([FromQuery] bool ignoreGlobalFilters = false)
        {
            return Ok(repository.OdataList(ignoreGlobalFilters));
        }

        [HttpGet("{key}")]
        public virtual async Task<IActionResult> GetSingle
            (Guid key, [FromHeader] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
        {
            var item = await repository.FindAsync(key, asOf, ignoreGlobalFilters: ignoreGlobalFilters);

            if (item == null)
                return NotFound(new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Not Found",
                        Body = $"Can't find entity with ID '{key}'"
                    }
                });

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item)));
        }

        [HttpGet]
        [EnableQuery]
        public virtual async Task<IActionResult> GetRevisions(Guid key)
        {
            return Ok(await repository.GetRevisionsAsync(key));
        }

        [HttpPost]
        public virtual async Task<IActionResult> Post([FromBody] CreateDTO dto)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Select(x => new { x.Key, x.Value?.Errors })
                .ToDictionary(x => x.Key, x => x.Errors);
                var response = new ShiftEntityResponse<SelectDTO>
                {
                    Additional = errors.ToDictionary(x => x.Key, x => (object)x.Value?.Select(s => s.ErrorMessage)!)
                };
                return BadRequest(response);
            }


            Entity newItem;

            try
            {
                newItem = repository.Create(dto, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message
                });
            }

            repository.Add(newItem);

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(newItem)));
        }

        [HttpPut("{key}")]
        public virtual async Task<IActionResult> Put(Guid key, [FromBody] UpdateDTO dto)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Select(x => new { x.Key, x.Value?.Errors })
                .ToDictionary(x => x.Key, x => x.Errors);
                var response = new ShiftEntityResponse<SelectDTO>
                {
                    Additional = errors.ToDictionary(x => x.Key, x => (object)x.Value?.Select(s => s.ErrorMessage)!)
                };
                return BadRequest(response);
            }

            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            try
            {
                repository.Update(item, dto, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item)));
        }

        [HttpDelete("{key}")]
        public virtual async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            try
            {
                repository.Delete(item, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item)));
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