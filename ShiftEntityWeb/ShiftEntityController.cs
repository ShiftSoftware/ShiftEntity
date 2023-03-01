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
            (long key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
        {
            var item = await repository.FindAsync(key, asOf, ignoreGlobalFilters: ignoreGlobalFilters);

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

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item))
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
                newItem = repository.Create(dto, this.GetUserID());
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

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(newItem))
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
                repository.Update(item, dto, this.GetUserID());
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

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item))
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
                repository.Delete(item, this.GetUserID());
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

            return Ok(new ShiftEntityResponse<SelectDTO>(repository.View(item))
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