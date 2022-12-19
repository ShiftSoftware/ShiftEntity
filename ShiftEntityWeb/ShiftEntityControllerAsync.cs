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
        private DbContext db { get; set; }
        private DbSet<Entity> dbSet { get; set; }
        public Repository repository { get; set; }

        public ShiftEntityControllerAsync(DbContext db, Repository repository, DbSet<Entity> dbSet)
        {
            this.db = db;
            this.dbSet = dbSet;
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
            var item = await repository.FindAsync(dbSet, key, asOf);

            if (item == null)
                return NotFound();

            return Ok(await repository.ViewAsync(item));
        }

        [HttpGet]
        public async Task<IActionResult> GetRevisions(Guid key)
        {
            return Ok(await repository.GetRevisionsAsync(dbSet, key));
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

            dbSet.Add(newItem);

            await db.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<DTO>(await repository.ViewAsync(newItem)));
        }

        [HttpPut("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> Put(Guid key, [FromBody] DTO dto)
        {
            var item = await repository.FindAsync(dbSet, key);

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

            db.SaveChanges();

            return Ok(new ShiftEntityResponse<DTO>(await repository.ViewAsync(item)));
        }

        [HttpDelete("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(dbSet, key);

            if (item == null)
                return NotFound();

            await repository.DeleteAsync(item);

            db.SaveChanges();

            return Ok(await repository.ViewAsync(item));
        }
    }
}
