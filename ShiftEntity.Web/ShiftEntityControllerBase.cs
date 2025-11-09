using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.ModelBuilder;
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
using System.Linq.Expressions;
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
    internal async Task<ODataDTO<ListDTO>> GetOdataListingNonAction(ODataQueryOptions<ListDTO> oDataQueryOptions, System.Linq.Expressions.Expression<Func<Entity, bool>>? where = null)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var queryable = await repository.GetIQueryable();

        if (where is not null)
            queryable = queryable.Where(where);

        var data = await repository.OdataList(queryable);

        return await data.ToOdataDTO(oDataQueryOptions, Request);
    }

    [NonAction]
    internal async Task<ODataDTO<RevisionDTO>> GetRevisionListingNonAction(string key, ODataQueryOptions<RevisionDTO> oDataQueryOptions)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var data = repository.GetRevisionsAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key));

        return await data.ToOdataDTO(oDataQueryOptions, Request);
    }

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> GetSingleNonAction(string key, DateTimeOffset? asOf)
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
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PostItemNonAction(ViewAndUpsertDTO dto, string getActionName)
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

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (DuplicateIdempotencyKeyException)
        {
            var existingItem = await repository.FindByIdempotencyKeyAsync(idempotencyKey!.Value);

            var existingDto = await repository.ViewAsync(existingItem!);

            return new(Ok(new ShiftEntityResponse<ViewAndUpsertDTO>(existingDto)
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            }), existingItem);
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

        var createdDto = await repository.ViewAsync(newItem);

        return new(CreatedAtAction(getActionName, new { key = ShiftEntityHashIdService.Encode<ViewAndUpsertDTO>(newItem.ID) }, new ShiftEntityResponse<ViewAndUpsertDTO>(createdDto)
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        }), newItem);
    }

    [NonAction]
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> PutItemNonAction(string key, ViewAndUpsertDTO dto)
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

        Entity? item = null;

        try
        {
            item = await repository.FindAsync(ShiftEntityHashIdService.Decode<ViewAndUpsertDTO>(key));
        }
        catch (ShiftEntityException ex)
        {
            return new(HandleException(ex), null);
        }

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
    internal async Task<(ActionResult<ShiftEntityResponse<ViewAndUpsertDTO>> ActionResult, Entity? Entity)> DeleteItemNonAction(string key, bool isHardDelete)
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
    internal async Task<ActionResult> PrintNonAction(string key)
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

    private async Task<IQueryable<Entity>> GetQueryForSelectionAsync(
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters
    )
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var query = await repository.GetIQueryable(
            disableDefaultDataLevelAccess: disableDefaultDataLevelAccess,
            disableGlobalFilters: disableGlobalFilters
        );

        query = query.Where(x => !x.IsDeleted);

        return query;
    }

    private async Task<List<ListDTO>> GetListDTOForSelectionAsync(List<string?>? selectedIds, bool disableDefaultDataLevelAccess, bool disableGlobalFilters)
    {
        var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

        var query = await this.GetQueryForSelectionAsync(disableDefaultDataLevelAccess, disableGlobalFilters);

        var odataList = await repository.OdataList(query);

        if (odataList is not null)
        {
            if (selectedIds is not null)
                odataList = odataList.Where(x => selectedIds.Contains(x.ID));

            return await odataList.ToListAsync();
        }

        return new List<ListDTO>();
    }

    private ODataQueryOptions<ListDTO> GetODataQueryOptionsFromSelectStateDTO(SelectStateDTO<ListDTO> ids)
    {
        var builder = new ODataConventionModelBuilder();

        builder.EntitySet<ListDTO>("ListDTOs");

        var model = builder.GetEdmModel();

        this.Request.QueryString = this.Request.QueryString.Add("$filter", ids.Filter!);

        ODataQueryContext oDataQueryContext = new ODataQueryContext(model, typeof(ListDTO), new());

        ODataQueryOptions<ListDTO> modifiedODataQueryOptions = new(oDataQueryContext, this.Request);

        return modifiedODataQueryOptions;
    }

    internal async Task<List<ListDTO>> GetSelectedListDTOsAsyncBase(
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false
    )
    {
        if (oDataQueryOptions?.Filter is not null)
        {
            var modifiedFilterNode = oDataQueryOptions.Filter.FilterClause.Expression.Accept(new HashIdQueryNodeVisitor());

            FilterClause modifiedFilterClause = new FilterClause(modifiedFilterNode, oDataQueryOptions.Filter.FilterClause.RangeVariable);

            ODataUriParser parser = new ODataUriParser(oDataQueryOptions.Context.Model, new Uri("", UriKind.Relative));

            var odataUri = parser.ParseUri();

            odataUri.Filter = modifiedFilterClause;

            var updatedUrl = odataUri.BuildUri(parser.UrlKeyDelimiter).ToString();

            var newQueryString = new Microsoft.AspNetCore.Http.QueryString(updatedUrl.Substring(updatedUrl.IndexOf("?")));

            this.Request.QueryString = newQueryString;

            ODataQueryOptions<ListDTO> modifiedODataQueryOptions2 = new(oDataQueryOptions.Context, this.Request);

            var repository = HttpContext.RequestServices.GetRequiredService<Repository>();

            var query = await this.GetQueryForSelectionAsync(disableDefaultDataLevelAccess, disableGlobalFilters);

            var odataList = await repository.OdataList(query);

            odataList = modifiedODataQueryOptions2.Filter.ApplyTo(odataList, new ODataQuerySettings()) as IQueryable<ListDTO>;

            return await odataList!.ToListAsync();
        }

        return await this.GetListDTOForSelectionAsync(null, disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    internal async Task<List<ListDTO>> GetSelectedListDTOsAsyncBase(
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false
    )
    {
        if (ids.All && !string.IsNullOrWhiteSpace(ids.Filter))
        {
            return await this.GetSelectedListDTOsAsyncBase(
                this.GetODataQueryOptionsFromSelectStateDTO(ids),
                disableDefaultDataLevelAccess,
                disableGlobalFilters
            );
        }

        return await this.GetListDTOForSelectionAsync(
            ids.All ? null : ids?.Items?.Select(x => x.ID)?.ToList(),
            disableDefaultDataLevelAccess,
            disableGlobalFilters
        );
    }

    internal async Task<List<Entity>> GetSelectedEntitiesAsyncBase(
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false
    )
    {
        var query = await this.GetQueryForSelectionAsync(
            disableDefaultDataLevelAccess,
            disableGlobalFilters
        );

        if (oDataQueryOptions.Filter is not null)
        {
            var listDTOData = await this.GetSelectedListDTOsAsyncBase(
                oDataQueryOptions,
                disableDefaultDataLevelAccess,
                disableGlobalFilters
            );

            var filteredIds = listDTOData!.Select(x => x.ID!.ToLong()).ToList();

            query = query.Where(x => filteredIds.Contains(x.ID));
        }

        if (query != null)
            return await query.ToListAsync();

        return new List<Entity>();
    }

    internal async Task<List<Entity>> GetSelectedEntitiesAsyncBase(
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false
    )
    {
        if (ids.All && !string.IsNullOrWhiteSpace(ids.Filter))
        {
            return await this.GetSelectedEntitiesAsyncBase(
                this.GetODataQueryOptionsFromSelectStateDTO(ids),
                disableDefaultDataLevelAccess,
                disableGlobalFilters
            );
        }
        else
        {
            var query = await this.GetQueryForSelectionAsync(
                disableDefaultDataLevelAccess,
                disableGlobalFilters
            );

            if (!ids.All)
            {
                var longIds = ids.Items.Select(x => x.ID!.ToLong());

                query = query.Where(x => longIds.Contains(x.ID));
            }

            return await query
                .ToListAsync();
        }
    }
}