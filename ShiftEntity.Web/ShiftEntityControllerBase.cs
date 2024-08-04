using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityControllerBase<Repository, Entity, ListDTO, ViewAndUpsertDTO> : ControllerBase
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
    where Entity : ShiftEntity<Entity>, new()
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
    where ListDTO : ShiftEntityDTOBase
{
    [NonAction]
    private ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> HandleException(ShiftEntityException ex)
    {
        return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse<ViewAndUpsertDTO>
        {
            Message = ex.Message,
            Additional = ex.AdditionalData,
        });
    }

    [NonAction]
    public IQueryable<ListDTO> GetOdataListing(ODataQueryOptions<ListDTO> oDataQueryOptions, System.Linq.Expressions.Expression<Func<Entity, bool>>? where = null)
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

        var queryable = repository.GetIQueryable();

        if (where is not null)
            queryable = queryable.Where(where);

        var data = repository.OdataList(queryable);

        if (!isFilteringByIsDeleted)
            data = data.Where(x => x.IsDeleted == false);

        return data;
    }

    [NonAction]
    public async Task<List<RevisionDTO>> GetRevisionListing(string key)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        return await repository.GetRevisionsAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key));
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> GetSingle(string key, DateTimeOffset? asOf, Action<Entity>? beforeGetValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        //var timeZoneService = HttpContext.RequestServices.GetService<TimeZoneService>();

        //if (asOf.HasValue)
        //    asOf = timeZoneService!.ReadOffsettedDate(asOf.Value);

        Entity? item;

        try
        {
            item = await repository.FindAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key), asOf);
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        if (item == null)
        {
            return new(NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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

        var isTemporal = item.GetType().GetCustomAttributes(typeof(TemporalShiftEntity)).Any();

        if (isTemporal)
        {
            Response.Headers.Append(Constants.HttpHeaderVersioning, "Temporal");
        }

        return new(Ok(new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PostItem(ViewAndUpsertDTO dto, Action<Entity>? beforeCommitValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        if (!ModelState.IsValid)
        {
            var response = new ShiftEntityResponse<ViewAndUpsertDTO>
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

        Guid? idempotencyKey = null;

        try
        {
            if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasIdempotencyKey<Entity>))))
                idempotencyKey = Guid.Parse(HttpContext.Request.Headers["Idempotency-Key"].ToString());

            newItem = await repository.CreateAsync(dto, this.GetUserID(), idempotencyKey);
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
        catch (DbUpdateException dbUpdateException)
        {
            var sqlException = dbUpdateException.InnerException as SqlException;

            //2601: This error occurs when you attempt to put duplicate index values into a column or columns that have a unique index.
            //Message looks something like: {"Cannot insert duplicate key row in object 'dbo.Countries' with unique index 'IX_Countries_IdempotencyKey'. The duplicate key value is (88320ba8-345f-410a-9aa4-3e8a7112c040)."}

            if (sqlException != null && sqlException.Errors.OfType<SqlError>().Any(se => se.Number == 2601 && se.Message.Contains(idempotencyKey!.ToString()!)))
            {
                var existingItem = await repository.FindByIdempotencyKeyAsync(idempotencyKey!.Value);

                var existingDto = await repository.ViewAsync(existingItem!);

                return new(Ok(new ShiftEntityResponse<ViewAndUpsertDTO>(existingDto)
                {
                    Message = repository.ResponseMessage,
                    Additional = repository.AdditionalResponseData
                }), existingItem);
            }
            else
            {
                throw;
            }
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        var createdDto = await repository.ViewAsync(newItem);

        return new(CreatedAtAction(nameof(GetSingle), new { key = createdDto.ID }, new ShiftEntityResponse<ViewAndUpsertDTO>(createdDto)
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), newItem);
    }

    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PutItem(string key, ViewAndUpsertDTO dto, Action<Entity>? beforeSaveValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        if (!ModelState.IsValid)
        {
            var response = new ShiftEntityResponse<ViewAndUpsertDTO>
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

        var item = await repository.FindAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key));

        if (item == null)
            return new(NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        return new(Ok(new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData,
        }), item);
    }
    
    [NonAction]
    public async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> DeleteItem(string key, bool isHardDelete, Action<Entity>? beforeSaveValidation)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var item = await repository.FindAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key), null);

        if (item == null)
            return new(NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        if (item.ReloadAfterSave)
            item = await repository.FindAsync(item.ID);

        return new(Ok(new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), item);
    }

    [NonAction]
    public async Task<ActionResult> Print(string key)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        try
        {
            return new FileStreamResult(await repository.PrintAsync(key), "application/pdf");
        }
        catch (ShiftEntityException ex)
        {
            return StatusCode(ex.HttpStatusCode, new ShiftEntityResponse
            {
                Message = ex.Message,
                Additional = ex.AdditionalData,
            });
        }
    }
}
