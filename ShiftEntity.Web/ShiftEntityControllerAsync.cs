using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.OData.Client;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ShiftSoftware.ShiftEntity.Web
{
    public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO> :
        ShiftEntityControllerAsync<Repository, Entity, ListDTO, DTO, DTO, DTO>
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, DTO>
        where Entity : ShiftEntity<Entity>, new()
        where DTO : ShiftEntityDTO
    {
    }

    public class ShiftEntityControllerAsync<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> :
        ControllerBase
        where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
        where Entity : ShiftEntity<Entity>
        where UpdateDTO : ShiftEntityDTO
    {
        [HttpGet]
        [EnableQueryWithHashIdConverter]
        public virtual ActionResult<ODataDTO<IQueryable<ListDTO>>> Get([FromQuery] bool ignoreGlobalFilters = false)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

            return Ok(repository.OdataList(ignoreGlobalFilters));
        }

        [HttpGet("{key}")]
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> GetSingle
            (string key, [FromQuery] DateTime? asOf, [FromQuery] bool ignoreGlobalFilters = false)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

            var timeZoneService = HttpContext.RequestServices.GetService<TimeZoneService>();

            if (asOf.HasValue)
                asOf = timeZoneService.ReadOffsettedDate(asOf.Value);

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
        [EnableQueryWithHashIdConverter]
        public virtual async Task<ActionResult<ODataDTO<List<RevisionDTO>>>> GetRevisions(string key)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

            return Ok(await repository.GetRevisionsAsync(ShiftEntityHashIds<SelectDTO>.Decode(key)));
        }

        [HttpPost]
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Post([FromBody] CreateDTO dto)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

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
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Put(string key, [FromBody] UpdateDTO dto)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

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
                if (item.LastSaveDate != dto.LastSaveDate)
                {
                    throw new ShiftEntityException(
                        new Message("Conflict", "This item has been modified by another process since you last loaded it. Please reload the item and try again."),
                        (int) HttpStatusCode.Conflict
                    );
                }

                await repository.UpdateAsync(item, dto, this.GetUserID());
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

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData,
            });
        }

        [HttpDelete("{key}")]
        public virtual async Task<ActionResult<ShiftEntityResponse<SelectDTO>>> Delete(string key, [FromQuery] bool isHardDelete = false)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

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
                await repository.DeleteAsync(item, isHardDelete, this.GetUserID());
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

            if (item.ReloadAfterSave)
                item = await repository.FindAsync(item.ID);

            return Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            });
        }

        [NonAction]
        public virtual async Task<List<ListDTO>> GetSelectedItemsAsync(ODataQueryOptions<ListDTO> oDataQueryOptions)
        {
            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

            var list = repository.OdataList();

            if (oDataQueryOptions.Filter != null)
                list = oDataQueryOptions.Filter.ApplyTo(list, new()) as IQueryable<ListDTO>;

            if (list != null)
                return await list.ToListAsync();

            return new List<ListDTO>();
        }
    }
}
