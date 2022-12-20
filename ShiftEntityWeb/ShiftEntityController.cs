﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Attributes;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using System;
using System.Threading.Tasks;

namespace ShiftEntityWeb
{
    //[EnableQuery]
    public class ShiftEntityController<Repository, Entity, ListDTO, DTO> :
        ControllerBase
        where Repository : IShiftRepository<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
        private DbContext db { get; set; }
        private DbSet<Entity> dbSet { get; set; }
        public Repository repository { get; set; }

        public ShiftEntityController(DbContext db, Repository repository, DbSet<Entity> dbSet)
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
            var item = await repository.FindAsync(key, asOf);

            if (item == null)
                return NotFound();

            return Ok(repository.View(item));
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
                newItem = repository.Create(dto);
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

            return Ok(new ShiftEntityResponse<DTO>(repository.View(newItem)));
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
                repository.Update(item, dto);
            }
            catch (ShiftEntityException ex)
            {
                return BadRequest(new ShiftEntityResponse<DTO>
                {
                    Message = ex.Message
                });
            }

            db.SaveChanges();

            return Ok(new ShiftEntityResponse<DTO>(repository.View(item)));
        }

        [HttpDelete("{key}")]
        [ODataIgnored]
        public async Task<IActionResult> Delete(Guid key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound();

            repository.Delete(item);

            db.SaveChanges();

            return Ok(repository.View(item));
        }
    }
}