﻿using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Core;
using System.Threading.Tasks;
using System;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using ShiftSoftware.ShiftEntity.Core.Dtos;
using ShiftSoftware.ShiftEntity.Web.Extensions;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace ShiftSoftware.ShiftEntity.Web
{
    public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO> :
        ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO, DTO, DTO>
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
    {
        public ShiftEntityControllerAsync(Repository repository) : base(repository)
        {
        }
    }

    public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ControllerBase
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
    {
        public Repository repository { get; set; }

        public ShiftEntityControllerAsync(Repository repository)
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
            (long key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
        {
            if (asOf.HasValue)
                asOf = TimeZoneService.ReadOffsettedDate(asOf.Value);

            var item = await repository.FindAsync(key, asOf, ignoreGlobalFilters: ignoreGlobalFilters);

            if (item == null)
                return NotFound(new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Not Found",
                        Body = $"Can't find entity with ID '{key}'"
                    },
                    Additional = repository.AdditionalResponseData
                });

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            });
        }

        [HttpGet]
        [EnableQuery]
        public virtual async Task<IActionResult> GetRevisions(long key)
        {
            return Ok(await repository.GetRevisionsAsync(key));
        }

        [HttpPost]
        public virtual async Task<IActionResult> Post([FromBody] CreateDTO dto)
        {
            if (!ModelState.IsValid)
            {
                var response = new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Model Validation Error",
                        SubMessages = ModelState.Select(x => new Message
                        {
                            Title = x.Key,
                            SubMessages = x.Value.Errors.Select(y => new Message
                            {
                                Title = y.ErrorMessage
                            }).ToList()
                        }).ToList()
                    },
                    Additional = repository.AdditionalResponseData,
                };

                return BadRequest(response);
            }

            Entity newItem;

            try
            {
                newItem = await repository.CreateAsync(dto, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message,
                    Additional = ex.AdditionalData,
                });
            }

            repository.Add(newItem);

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(newItem))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            });
        }

        [HttpPut("{key}")]
        public virtual async Task<IActionResult> Put(long key, [FromBody] UpdateDTO dto)
        {
            if (!ModelState.IsValid)
            {
                var response = new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Model Validation Error",
                        SubMessages = ModelState.Select(x => new Message
                        {
                            Title = x.Key,
                            SubMessages = x.Value.Errors.Select(y => new Message
                            {
                                Title = y.ErrorMessage
                            }).ToList()
                        }).ToList()
                    },
                    Additional = repository.AdditionalResponseData,
                };

                return BadRequest(response);
            }

            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound(new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Not Found",
                        Body = $"Can't find entity with ID '{key}'"
                    },
                    Additional = repository.AdditionalResponseData
                });

            try
            {
                await repository.UpdateAsync(item, dto, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message,
                    Additional = repository.AdditionalResponseData,
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData,
            });
        }

        [HttpDelete("{key}")]
        public virtual async Task<IActionResult> Delete(long key)
        {
            var item = await repository.FindAsync(key);

            if (item == null)
                return NotFound(new ShiftEntityResponse<SelectDTO>
                {
                    Message = new Message
                    {
                        Title = "Not Found",
                        Body = $"Can't find entity with ID '{key}'",
                    },
                    Additional = repository.AdditionalResponseData,
                });

            try
            {
                await repository.DeleteAsync(item, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message,
                    Additional = repository.AdditionalResponseData,
                });
            }

            await repository.SaveChangesAsync();

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            });
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
