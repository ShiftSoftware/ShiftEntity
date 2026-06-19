using ShiftSoftware.ShiftEntity.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    internal List<Assembly> AutoMapperAssemblies = new List<Assembly>();
    internal List<Assembly> DataAssemblies = new List<Assembly>();
    internal List<AzureStorageOption> azureStorageOptions = new List<AzureStorageOption>();

    /// <summary>
    /// Entity + DTO generic-argument sets (each <c>[entity, listDto, viewDto]</c>) for attribute-driven
    /// endpoints whose entities have no repository class for the AutoMapper assembly scanner to discover.
    /// Their default maps are built from these sets (deduped against the repository scan + user profiles).
    /// Populated by <c>RegisterShiftRepositories(...)</c> when it discovers endpoint-attributed entities.
    /// </summary>
    internal List<Type[]> EndpointDefaultMaps = new List<Type[]>();
    //internal int MaxTop;
    //internal Func<IServiceProvider, int?>? MaxTopResolver;

    public ShiftEntityOptions WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponse)
    {
        _WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponse;

        return this;
    }

    public ShiftEntityOptions AddAutoMapper(params Assembly[] assemblies)
    {
        AutoMapperAssemblies.AddRange(assemblies);
        return this;
    }

    public ShiftEntityOptions AddDataAssembly(params Assembly[] assemblies)
    {
        DataAssemblies.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Registers a default entity↔DTO map for an attribute-driven endpoint whose entity has no
    /// repository class. Normally called indirectly by <c>RegisterShiftRepositories(...)</c>.
    /// </summary>
    public ShiftEntityOptions AddEndpointDefaultMap(Type entity, Type listDto, Type viewAndUpsertDto)
    {
        EndpointDefaultMaps.Add(new[] { entity, listDto, viewAndUpsertDto });
        return this;
    }

    public ShiftEntityOptions AddAzureStorage(params AzureStorageOption[] azureStorageOptions)
    {
        this.azureStorageOptions.AddRange(azureStorageOptions);

        return this;
    }

    //public void SetMaxTop(int maxTop)
    //{
    //    if (maxTop <= 0)
    //        throw new System.ArgumentOutOfRangeException(nameof(maxTop), "MaxTop must be greater than zero.");

    //    MaxTop = maxTop;
    //}

    //public void SetMaxTopResolver(Func<IServiceProvider, int?> maxTopResolver)
    //{
    //    MaxTopResolver = maxTopResolver;
    //}

    public HashIdOptions HashId { get; set; }
    public JsonNamingPolicy? JsonNamingPolicy { get; set; } = null;

    public ShiftEntityOptions()
    {
        HashId = new();
    }
}