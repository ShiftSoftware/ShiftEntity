﻿using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using System.Threading.Tasks;
using System;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;

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

            return Ok(await repository.ViewAsync(item));
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
        public virtual async Task<IActionResult> Put(Guid key, [FromBody] DTO dto)
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
        public virtual async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            await repository.DeleteAsync(item);

            await repository.SaveChangesAsync();

            return Ok(await repository.ViewAsync(item));
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
