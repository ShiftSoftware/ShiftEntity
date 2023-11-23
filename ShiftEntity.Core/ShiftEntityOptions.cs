﻿using ShiftSoftware.ShiftEntity.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Core;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    internal List<Assembly> AutoMapperAssemblies = new List<Assembly>();
    internal List<AzureStorageOption> azureStorageOptions = new List<AzureStorageOption>();

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

    public ShiftEntityOptions AddAzureStorage(params AzureStorageOption[] azureStorageOptions)
    {
        this.azureStorageOptions = azureStorageOptions.ToList();

        return this;
    }

    public HashIdOptions HashId { get; set; }

    public ShiftEntityOptions()
    {
        HashId = new();
    }
}
