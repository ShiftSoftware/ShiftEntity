using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Flags;
using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.HashIds;
using ShiftSoftware.ShiftEntity.Print;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

/// <summary>
/// Framework-agnostic CRUD/revisions/print/selection logic extracted from
/// <see cref="ShiftEntityControllerBase{Repository, Entity, ListDTO, ViewAndUpsertDTO}"/>.
///
/// Consumed by both the controller base (which wraps <see cref="CrudResult"/> as
/// <c>ActionResult</c>) and the minimal-API <c>MapShiftEntityCrud</c> /
/// <c>MapShiftEntitySecureCrud</c> extensions (which wrap it as <c>IResult</c>).
///
/// The handler is stateless and uses the supplied <see cref="HttpContext"/> for DI
/// resolution, user ID, and request headers — no dependency on <c>ControllerBase</c>,
/// <c>ModelState</c>, <c>Url.Action</c>, or any MVC-specific type.
/// </summary>
public class ShiftEntityCrudHandler<Repository, Entity, ListDTO, ViewAndUpsertDTO>
    where Repository : IShiftRepositoryAsync<Entity, ListDTO, ViewAndUpsertDTO>
    where Entity : ShiftEntity<Entity>, new()
    where ViewAndUpsertDTO : ShiftEntityViewAndUpsertDTO
    where ListDTO : ShiftEntityDTOBase
{
    public async Task<ODataDTO<ListDTO>> GetListAsync(
        HttpContext httpContext,
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        Expression<Func<Entity, bool>>? where = null)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();

        var queryable = await repository.GetIQueryable(asOf: null, includes: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        if (where is not null)
            queryable = queryable.Where(where);

        var data = await repository.OdataList(queryable);

        return await data.ToOdataDTO(oDataQueryOptions, httpContext.Request, applyPostODataProcessing: repository.ApplyPostODataProcessing);
    }

    public async Task<ODataDTO<RevisionDTO>> GetRevisionsAsync(
        HttpContext httpContext,
        string key,
        ODataQueryOptions<RevisionDTO> oDataQueryOptions)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();
        var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

        var data = repository.GetRevisionsAsync(hashIdService.Decode<ViewAndUpsertDTO>(key));

        return await data.ToOdataDTO(oDataQueryOptions, httpContext.Request, applySoftDeleteFilter: false);
    }

    public async Task<(CrudResult Result, Entity? Entity)> GetSingleAsync(
        HttpContext httpContext,
        string key,
        DateTimeOffset? asOf)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();
        var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

        Entity? item;

        try
        {
            item = await repository.FindAsync(hashIdService.Decode<ViewAndUpsertDTO>(key), asOf, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        if (item == null)
        {
            return (CrudResult.NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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

        var body = new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        };

        return (CrudResult.Ok(body, isTemporal), item);
    }

    public async Task<(CrudResult Result, Entity? Entity)> PostAsync(
        HttpContext httpContext,
        ViewAndUpsertDTO dto,
        IReadOnlyDictionary<string, string[]>? validationErrors = null)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();

        if (validationErrors is not null && validationErrors.Count > 0)
        {
            return (CrudResult.BadRequest(BuildValidationErrorResponse(validationErrors, repository.AdditionalResponseData)), null);
        }

        Entity newItem;
        Guid? idempotencyKey = null;

        try
        {
            if (typeof(Entity).GetInterfaces().Any(x => x.IsAssignableFrom(typeof(IEntityHasIdempotencyKey<Entity>))))
            {
                var header = httpContext.Request.Headers["Idempotency-Key"].ToString();
                if (!string.IsNullOrWhiteSpace(header))
                    idempotencyKey = Guid.Parse(header);
            }

            newItem = await repository.UpsertAsync(new Entity(), dto, ActionTypes.Insert, httpContext.GetUserID(), idempotencyKey, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        repository.Add(newItem);

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (DuplicateIdempotencyKeyException)
        {
            var existingItem = await repository.FindByIdempotencyKeyAsync(idempotencyKey!.Value, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

            var existingDto = await repository.ViewAsync(existingItem!);

            var existingBody = new ShiftEntityResponse<ViewAndUpsertDTO>(existingDto)
            {
                Message = repository.ResponseMessage,
                Additional = repository.AdditionalResponseData
            };

            return (CrudResult.Ok(existingBody), existingItem);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        var createdDto = await repository.ViewAsync(newItem);

        var createdBody = new ShiftEntityResponse<ViewAndUpsertDTO>(createdDto)
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        };

        var hashIdServiceForKey = httpContext.RequestServices.GetRequiredService<IHashIdService>();
        var createdKey = hashIdServiceForKey.Encode<ViewAndUpsertDTO>(newItem.ID);

        return (CrudResult.Created(createdBody, createdKey), newItem);
    }

    public async Task<(CrudResult Result, Entity? Entity)> PutAsync(
        HttpContext httpContext,
        string key,
        ViewAndUpsertDTO dto,
        IReadOnlyDictionary<string, string[]>? validationErrors = null)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();
        var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

        if (validationErrors is not null && validationErrors.Count > 0)
        {
            return (CrudResult.BadRequest(BuildValidationErrorResponse(validationErrors, repository.AdditionalResponseData)), null);
        }

        Entity? item = null;

        try
        {
            item = await repository.FindAsync(hashIdService.Decode<ViewAndUpsertDTO>(key), asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        if (item == null)
            return (CrudResult.NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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

            await repository.UpsertAsync(item, dto, ActionTypes.Update, httpContext.GetUserID(), idempotencyKey: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        var body = new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData,
        };

        return (CrudResult.Ok(body), item);
    }

    public async Task<(CrudResult Result, Entity? Entity)> DeleteAsync(
        HttpContext httpContext,
        string key,
        bool isHardDelete)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();
        var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

        var item = await repository.FindAsync(hashIdService.Decode<ViewAndUpsertDTO>(key), asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        if (item == null)
            return (CrudResult.NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
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
            await repository.DeleteAsync(item, isHardDelete, httpContext.GetUserID(), disableDefaultDataLevelAccess: false, disableGlobalFilters: false);
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        try
        {
            await repository.SaveChangesAsync();
        }
        catch (ShiftEntityException ex)
        {
            return (HandleException(ex), null);
        }

        if (item.ReloadAfterSave)
            item = await repository.FindAsync(item.ID, asOf: null, disableDefaultDataLevelAccess: false, disableGlobalFilters: false);

        var body = new ShiftEntityResponse<ViewAndUpsertDTO>(await repository.ViewAsync(item!))
        {
            Message = repository.ResponseMessage,
            Additional = repository.AdditionalResponseData
        };

        return (CrudResult.Ok(body), item);
    }

    public async Task<CrudResult> PrintAsync(HttpContext httpContext, string key)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();

        try
        {
            return CrudResult.File(await repository.PrintAsync(key), "application/pdf");
        }
        catch (ShiftEntityException ex)
        {
            return CrudResult.Status(ex.HttpStatusCode, new ShiftEntityResponse
            {
                Message = ex.Message,
                Additional = ex.AdditionalData,
            });
        }
    }

    /// <summary>
    /// Generates a SAS token for the print endpoint. <paramref name="urlDescriptor"/> must
    /// be the same string used by the print endpoint when validating the token (typically
    /// the absolute path of the print-token route).
    /// </summary>
    public async Task<CrudResult> PrintTokenAsync(HttpContext httpContext, string key, string urlDescriptor)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();
        var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

        var found = await repository.FindAsync(
            hashIdService.Decode<ViewAndUpsertDTO>(key),
            asOf: null,
            disableDefaultDataLevelAccess: false,
            disableGlobalFilters: false);

        if (found is null)
            return CrudResult.NotFound(new ShiftEntityResponse<ViewAndUpsertDTO>
            {
                Message = new Message
                {
                    Title = "Not Found",
                    Body = $"Can't find entity with ID '{key}'"
                },
                Additional = repository.AdditionalResponseData
            });

        var options = httpContext.RequestServices.GetRequiredService<ShiftEntityPrintOptions>();

        var (token, expires) = TokenService.GenerateSASToken(
            urlDescriptor,
            key,
            DateTime.UtcNow.AddSeconds(options.TokenExpirationInSeconds),
            options.SASTokenKey);

        return CrudResult.Ok($"expires={expires}&token={token}");
    }

    /// <summary>
    /// Validates a SAS token against <paramref name="urlDescriptor"/> (must match the
    /// descriptor used by <see cref="PrintTokenAsync"/>) and returns true if valid.
    /// </summary>
    public bool ValidatePrintSASToken(HttpContext httpContext, string key, string urlDescriptor, string? expires, string? token)
    {
        if (string.IsNullOrEmpty(expires) || string.IsNullOrEmpty(token))
            return false;

        var options = httpContext.RequestServices.GetRequiredService<ShiftEntityPrintOptions>();

        return TokenService.ValidateSASToken(urlDescriptor, key, expires, token, options.SASTokenKey);
    }

    // ---- Selection helpers (bulk operations) ----

    private async Task<IQueryable<Entity>> GetQueryForSelectionAsync(
        HttpContext httpContext,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();

        var query = await repository.GetIQueryable(
            asOf: null,
            includes: null,
            disableDefaultDataLevelAccess: disableDefaultDataLevelAccess,
            disableGlobalFilters: disableGlobalFilters
        );

        query = query.Where(x => !x.IsDeleted);

        return query;
    }

    private async Task<List<ListDTO>> GetListDTOForSelectionAsync(
        HttpContext httpContext,
        List<string?>? selectedIds,
        bool disableDefaultDataLevelAccess,
        bool disableGlobalFilters)
    {
        var repository = httpContext.RequestServices.GetRequiredService<Repository>();

        var query = await GetQueryForSelectionAsync(httpContext, disableDefaultDataLevelAccess, disableGlobalFilters);

        var odataList = await repository.OdataList(query);

        if (odataList is not null)
        {
            if (selectedIds is not null)
                odataList = odataList.Where(x => selectedIds.Contains(x.ID));

            return await odataList.ToListAsync();
        }

        return new List<ListDTO>();
    }

    public async Task<List<ListDTO>> GetSelectedListDTOsAsync(
        HttpContext httpContext,
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
    {
        if (oDataQueryOptions?.Filter is not null)
        {
            var hashIdService = httpContext.RequestServices.GetRequiredService<IHashIdService>();

            var modifiedFilterNode = oDataQueryOptions.Filter.FilterClause.Expression
                .Accept(new HashIdQueryNodeVisitor<ListDTO>(hashIdService));

            // Build a throwaway ODataQueryOptions carrying the rewritten filter,
            // without mutating the live request (the controller base used to mutate
            // Request.QueryString in place — avoided here).
            var filterClause = new Microsoft.OData.UriParser.FilterClause(
                modifiedFilterNode,
                oDataQueryOptions.Filter.FilterClause.RangeVariable);

            var parser = new Microsoft.OData.UriParser.ODataUriParser(
                oDataQueryOptions.Context.Model,
                new Uri("", UriKind.Relative));

            var odataUri = parser.ParseUri();
            odataUri.Filter = filterClause;

            var updatedUrl = odataUri.BuildUri(parser.UrlKeyDelimiter).ToString();
            var newQueryString = new QueryString(updatedUrl.Substring(updatedUrl.IndexOf("?")));

            // Use a clone of HttpRequest with the new query string to avoid mutating httpContext.Request.
            var originalQueryString = httpContext.Request.QueryString;
            try
            {
                httpContext.Request.QueryString = newQueryString;

                var rebuiltOptions = new ODataQueryOptions<ListDTO>(oDataQueryOptions.Context, httpContext.Request);

                var repository = httpContext.RequestServices.GetRequiredService<Repository>();

                var query = await GetQueryForSelectionAsync(httpContext, disableDefaultDataLevelAccess, disableGlobalFilters);
                var odataList = await repository.OdataList(query);

                odataList = rebuiltOptions.Filter.ApplyTo(odataList, new ODataQuerySettings()) as IQueryable<ListDTO>;

                return await odataList!.ToListAsync();
            }
            finally
            {
                httpContext.Request.QueryString = originalQueryString;
            }
        }

        return await GetListDTOForSelectionAsync(httpContext, null, disableDefaultDataLevelAccess, disableGlobalFilters);
    }

    public async Task<List<ListDTO>> GetSelectedListDTOsAsync(
        HttpContext httpContext,
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
    {
        if (ids.All && !string.IsNullOrWhiteSpace(ids.Filter))
        {
            return await GetSelectedListDTOsAsync(
                httpContext,
                BuildODataQueryOptionsFromFilter(httpContext, ids.Filter!),
                disableDefaultDataLevelAccess,
                disableGlobalFilters);
        }

        return await GetListDTOForSelectionAsync(
            httpContext,
            ids.All ? null : ids?.Items?.Select(x => x.ID)?.ToList(),
            disableDefaultDataLevelAccess,
            disableGlobalFilters);
    }

    public async Task<List<Entity>> GetSelectedEntitiesAsync(
        HttpContext httpContext,
        ODataQueryOptions<ListDTO> oDataQueryOptions,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
    {
        var query = await GetQueryForSelectionAsync(httpContext, disableDefaultDataLevelAccess, disableGlobalFilters);

        if (oDataQueryOptions.Filter is not null)
        {
            var listDTOData = await GetSelectedListDTOsAsync(
                httpContext,
                oDataQueryOptions,
                disableDefaultDataLevelAccess,
                disableGlobalFilters);

            var filteredIds = listDTOData.Select(x => x.ID!.ToLong()).ToList();

            query = query.Where(x => filteredIds.Contains(x.ID));
        }

        if (query != null)
            return await query.ToListAsync();

        return new List<Entity>();
    }

    public async Task<List<Entity>> GetSelectedEntitiesAsync(
        HttpContext httpContext,
        SelectStateDTO<ListDTO> ids,
        bool disableDefaultDataLevelAccess = false,
        bool disableGlobalFilters = false)
    {
        if (ids.All && !string.IsNullOrWhiteSpace(ids.Filter))
        {
            return await GetSelectedEntitiesAsync(
                httpContext,
                BuildODataQueryOptionsFromFilter(httpContext, ids.Filter!),
                disableDefaultDataLevelAccess,
                disableGlobalFilters);
        }

        var query = await GetQueryForSelectionAsync(httpContext, disableDefaultDataLevelAccess, disableGlobalFilters);

        if (!ids.All)
        {
            var longIds = ids.Items.Select(x => x.ID!.ToLong());
            query = query.Where(x => longIds.Contains(x.ID));
        }

        return await query.ToListAsync();
    }

    private static ODataQueryOptions<ListDTO> BuildODataQueryOptionsFromFilter(HttpContext httpContext, string filter)
    {
        var builder = new Microsoft.OData.ModelBuilder.ODataConventionModelBuilder();
        builder.EntitySet<ListDTO>("ListDTOs");
        var model = builder.GetEdmModel();

        httpContext.Request.QueryString = httpContext.Request.QueryString.Add("$filter", filter);

        var oDataQueryContext = new ODataQueryContext(model, typeof(ListDTO), new());
        return new ODataQueryOptions<ListDTO>(oDataQueryContext, httpContext.Request);
    }

    // ---- Helpers ----

    internal static CrudResult HandleException(ShiftEntityException ex)
    {
        return CrudResult.Status(ex.HttpStatusCode, new ShiftEntityResponse<ViewAndUpsertDTO>
        {
            Message = ex.Message,
            Additional = ex.AdditionalData,
        });
    }

    internal static ShiftEntityResponse<ViewAndUpsertDTO> BuildValidationErrorResponse(
        IReadOnlyDictionary<string, string[]> validationErrors,
        Dictionary<string, object>? additionalResponseData)
    {
        return new ShiftEntityResponse<ViewAndUpsertDTO>
        {
            Message = new Message
            {
                Title = "Model Validation Error",
                SubMessages = validationErrors.Select(x => new Message
                {
                    Title = x.Key,
                    For = x.Key,
                    SubMessages = x.Value.Select(e => new Message { Title = e }).ToList()
                }).ToList()
            },
            Additional = additionalResponseData,
        };
    }
}
