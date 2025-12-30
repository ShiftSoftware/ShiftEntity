using ShiftSoftware.ShiftEntity.Core.Services;
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
    internal int MaxTop;

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

    public ShiftEntityOptions AddAzureStorage(params AzureStorageOption[] azureStorageOptions)
    {
        this.azureStorageOptions = azureStorageOptions.ToList();

        return this;
    }

    public void SetMaxTop(int maxTop)
    {
        if (maxTop <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(maxTop), "MaxTop must be greater than zero.");
        MaxTop = maxTop;
    }

    public HashIdOptions HashId { get; set; }
    public JsonNamingPolicy? JsonNamingPolicy { get; set; } = null;

    public ShiftEntityOptions()
    {
        HashId = new();
    }
}