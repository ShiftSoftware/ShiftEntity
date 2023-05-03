using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashId;
using ShiftSoftware.ShiftEntity.Web.Extensions;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ShiftEntityWeb
{
    public class ShiftEntityController<Repository, Entity, ListDTO, DTO> :
        ShiftEntityController<Repository, Entity, ListDTO, DTO, DTO, DTO>
        where Repository : IShiftRepository<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>
        where DTO: ShiftEntityDTO
    {
        public ShiftEntityController(Repository repository) : base(repository)
        {
        }
    }

    public class ShiftEntityController<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ControllerBase
        where Repository : IShiftRepository<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
        where UpdateDTO : ShiftEntityDTO
    {
        public Repository repository { get; set; }

        public ShiftEntityController(Repository repository)
        {
            this.repository = repository;
        }

        [HttpGet]
        [EnableQueryWithHashIdConverter]
        public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get([FromQuery] bool ignoreGlobalFilters = false)
        {
            return Ok(repository.OdataList(ignoreGlobalFilters));
        }

        [HttpGet("{key}")]
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle
            (string key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
        {
            if (asOf.HasValue)
                asOf = TimeZoneService.ReadOffsettedDate(asOf.Value);

            Entity item;

            try
            {
                item = await repository.FindAsync(ShiftEntityHashIds<SelectDTO>.Decode(key), asOf, ignoreGlobalFilters: ignoreGlobalFilters);
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message,
                    Additional = ex.AdditionalData,
                });
            }

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
        [EnableQueryWithHashIdConverter]
        public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
        {
            return Ok(await repository.GetRevisionsAsync(ShiftEntityHashIds<SelectDTO>.Decode(key)));
        }

        [HttpPost]
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
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
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
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

            var item = await repository.FindAsync(ShiftEntityHashIds<SelectDTO>.Decode(key));

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
                if (item.LastSaveDate != TimeZoneService.ReadOffsettedDate(dto.LastSaveDate))
                {
                    throw new ShiftEntityException(
                        new Message("Conflict", "This item has been modified by another process since you last loaded it. Please reload the item and try again."),
                        (int) HttpStatusCode.Conflict
                    );
                }

                repository.Update(item, dto, this.GetUserID());
            }
            catch (ShiftEntityException ex)
            {
                return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
                {
                    Message = ex.Message,
                    Additional = ex.AdditionalData,
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
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key)
        {
            var item = await repository.FindAsync(ShiftEntityHashIds<SelectDTO>.Decode(key));

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
                    Additional = ex.AdditionalData,
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