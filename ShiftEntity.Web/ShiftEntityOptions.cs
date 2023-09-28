using ShiftSoftware.ShiftEntity.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    internal List<Assembly> AutoMapperAssemblies = new List<Assembly>();
    internal List<AzureStorageOption> azureStorageOptions = new List<AzureStorageOption>();

    /// <summary>
    /// If not set, it gets the entry assembly
    /// </summary>
    public Assembly? RepositoriesAssembly { get; set; }

    public ShiftEntityOptions WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponse)
    {
        this._WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponse;

        return this;
    }

    public ShiftEntityOptions AddAutoMapper(params Assembly[] assemblies)
    {
        this.AutoMapperAssemblies.AddRange(assemblies);
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
        HashId = new ();
    }
}
