using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Web.Extensions;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityControllerBase<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO> : ControllerBase
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    [NonAction]
    private ActionResult<ShiftEntityResponse<SelectDTO>> HandleException(ShiftEntityException ex)
    {
        return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
        {
            Message = ex.Message,
            Additional = ex.AdditionalData,
        });
    }

    [NonAction]
    public IQueryable<ListDTO> GetOdataListing(ODataQueryOptions<ListDTO> oDataQueryOptions, bool showDeletedRows = false, System.Linq.Expressions.Expression<Func<Entity, bool>>? where = null)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        bool isFilteringByIsDeleted = false;

        FilterClause? filterClause = oDataQueryOptions.Filter?.FilterClause;

        if (filterClause is not null)
        {
            var visitor = new SoftDeleteQueryNodeVisitor();

            var visited = filterClause.Expression.Accept(visitor);

            isFilteringByIsDeleted = visitor.IsFilteringByIsDeleted;
        }

        var queryable = repository.GetIQueryableForOData(showDeletedRows);

        if (where is not null)
            queryable = queryable.Where(where);

        var data = repository.OdataList(showDeletedRows, queryable);

        if (!isFilteringByIsDeleted)
            data = data.Where(x => x.IsDeleted == false);

        return data;
    }

    [NonAction]
    public async Task<List<RevisionDTO>> GetRevisionListing(string key)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        return await repository.GetRevisionsAsync(ShiftEntityHashIds.Decode<SelectDTO>(key));
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> GetSingleItem(string key, DateTime? asOf, Action<Entity>? beforeGetValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var timeZoneService = HttpContext.RequestServices.GetService<TimeZoneService>();

        if (asOf.HasValue)
            asOf = timeZoneService!.ReadOffsettedDate(asOf.Value);

        Entity? item;

        try
        {
            item = await repository.FindAsync(ShiftEntityHashIds.Decode<SelectDTO>(key), asOf);
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        if (item == null)
        {
            return new(NotFound(new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Not Found",
                    Body = $"Can't find entity with ID '{key}'"
                },
                Additional = repository.AdditionalResponseData
            }), null);
        }

        if (beforeGetValidation != null)
        {
            try
            {
                beforeGetValidation(item);
            }
            catch (ShiftEntityException ex)
            {
                return new(HandleException(ex), null);
            }
        }

        return new(Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> PostItem(CreateDTO dto, Action<Entity>? beforeCommitValidation)
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
                        SubMessages = x.Value is null ? new List<Message>() : x.Value.Errors.Select(y => new Message
                        {
                            Title = y.ErrorMessage
                        }).ToList()
                    }).ToList()
                },
                Additional = repository.AdditionalResponseData,
            };

            return new(BadRequest(response), null);
        }

        Entity newItem;

        try
        {
            newItem = await repository.CreateAsync(dto, this.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        repository.Add(newItem);

        newItem.BeforeCommitValidation = beforeCommitValidation;

        try
        {
            await repository.SaveChangesAsync(true);
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        return new(Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(newItem))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), newItem);
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> PutItem(string key, UpdateDTO dto, Action<Entity>? beforeSaveValidation)
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
                        SubMessages = x.Value is null ? new List<Message>() : x.Value.Errors.Select(y => new Message
                        {
                            Title = y.ErrorMessage
                        }).ToList()
                    }).ToList()
                },
                Additional = repository.AdditionalResponseData,
            };

            return new(BadRequest(response), null);
        }

        var item = await repository.FindAsync(ShiftEntityHashIds.Decode<SelectDTO>(key));

        if (item == null)
            return new(NotFound(new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Not Found",
                    Body = $"Can't find entity with ID '{key}'"
                },
                Additional = repository.AdditionalResponseData
            }), null);

        try
        {
            if (item.LastSaveDate != dto.LastSaveDate)
            {
                throw new ShiftEntityException(
                    new Message("Conflict", $"The submitted item version ({dto.LastSaveDate}) has been modified by another process. It does not match the loaded item version ({item.LastSaveDate}). Please reload the item and try again."),
                    (int)HttpStatusCode.Conflict
                );
            }

            await repository.UpdateAsync(item, dto, this.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        if (beforeSaveValidation != null)
        {
            try
            {
                beforeSaveValidation(item);
            }
            catch (ShiftEntityException ex)
            {
                return new(HandleException(ex), null);
            }
        }

        await repository.SaveChangesAsync();

        return new(Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData,
        }), item);
    }
    
    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> DeleteItem(string key, bool isHardDelete, Action<Entity>? beforeSaveValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var item = await repository.FindAsync(ShiftEntityHashIds.Decode<SelectDTO>(key), null);

        if (item == null)
            return new(NotFound(new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Not Found",
                    Body = $"Can't find entity with ID '{key}'",
                },
                Additional = repository.AdditionalResponseData,
            }), null);

        try
        {
            await repository.DeleteAsync(item, isHardDelete, this.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        if (beforeSaveValidation != null)
        {
            try
            {
                beforeSaveValidation(item);
            }
            catch (ShiftEntityException ex)
            {
                return new(HandleException(ex), null);
            }
        }

        await repository.SaveChangesAsync();

        if (item.ReloadAfterSave)
            item = await repository.FindAsync(item.ID);

        return new(Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }
}
