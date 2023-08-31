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

internal class ShiftEntityControllerService<Repository, Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, SelectDTO, CreateDTO, UpdateDTO>
    where Entity : ShiftEntity<Entity>
    where UpdateDTO : ShiftEntityDTO
    where ListDTO : ShiftEntityDTOBase
{
    private readonly ControllerBase controllerBase;

    public ShiftEntityControllerService(ControllerBase controllerBase)
    {
        this.controllerBase = controllerBase;
    }

    private ActionResult<ShiftEntityResponse<SelectDTO>> HandleException(ShiftEntityException ex)
    {
        return this.controllerBase.StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<SelectDTO>
        {
            Message = ex.Message,
            Additional = ex.AdditionalData,
        });
    }

    public IQueryable<ListDTO> Get(ODataQueryOptions<ListDTO> oDataQueryOptions, [FromQuery] bool showDeletedRows = false, System.Linq.Expressions.Expression<Func<Entity, bool>>? where = null)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

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

    public async Task<List<RevisionDTO>> GetRevisions(string key)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

        return await repository.GetRevisionsAsync(ShiftEntityHashIds.Decode<SelectDTO>(key));
    }

    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> GetSingle(string key, [FromQuery] DateTime? asOf)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

        var timeZoneService = this.controllerBase.HttpContext.RequestServices.GetService<TimeZoneService>();

        if (asOf.HasValue)
            asOf = timeZoneService!.ReadOffsettedDate(asOf.Value);

        Entity item;

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
            return new(this.controllerBase.NotFound(new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Not Found",
                    Body = $"Can't find entity with ID '{key}'"
                },
                Additional = repository.AdditionalResponseData
            }), null);
        }

        return new(this.controllerBase.Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }

    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> Post(CreateDTO dto)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

        if (!this.controllerBase.ModelState.IsValid)
        {
            var response = new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Model Validation Error",
                    SubMessages = this.controllerBase.ModelState.Select(x => new Message
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

            return new(this.controllerBase.BadRequest(response), null);
        }

        Entity newItem;

        try
        {
            newItem = await repository.CreateAsync(dto, this.controllerBase.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        repository.Add(newItem);

        await repository.SaveChangesAsync();

        return new(this.controllerBase.Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(newItem))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), newItem);
    }

    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> Put(string key, UpdateDTO dto)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

        if (!this.controllerBase.ModelState.IsValid)
        {
            var response = new ShiftEntityResponse<SelectDTO>
            {
                Message = new Message
                {
                    Title = "Model Validation Error",
                    SubMessages = this.controllerBase.ModelState.Select(x => new Message
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

            return new(this.controllerBase.BadRequest(response), null);
        }

        var item = await repository.FindAsync(ShiftEntityHashIds.Decode<SelectDTO>(key));

        if (item == null)
            return new(this.controllerBase.NotFound(new ShiftEntityResponse<SelectDTO>
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
                    new Message("Conflict", "This item has been modified by another process since you last loaded it. Please reload the item and try again."),
                    (int)HttpStatusCode.Conflict
                );
            }

            await repository.UpdateAsync(item, dto, this.controllerBase.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        await repository.SaveChangesAsync();

        return new(this.controllerBase.Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData,
        }), item);
    }

    public async Task<(ActionResult<ShiftEntityResponse<SelectDTO>> ActionResult, Entity? Entity)> Delete(string key, bool isHardDelete = false)
    {
        var repository = this.controllerBase.HttpContext.RequestServices.GetRequiredService<Repository>();

        var item = await repository.FindAsync(ShiftEntityHashIds.Decode<SelectDTO>(key));

        if (item == null)
            return new(this.controllerBase.NotFound(new ShiftEntityResponse<SelectDTO>
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
            await repository.DeleteAsync(item, isHardDelete, this.controllerBase.GetUserID());
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        await repository.SaveChangesAsync();

        if (item.ReloadAfterSave)
            item = await repository.FindAsync(item.ID);

        return new(this.controllerBase.Ok(new ShiftEntityResponse<SelectDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }
}
