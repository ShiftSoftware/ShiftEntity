using System.Collections.Generic;
using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    internal List<Assembly> AutoMapperAssemblies = new List<Assembly>();
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

    public HashIdOptions HashId { get; set; }

    public ShiftEntityOptions()
    {
        HashId = new ();
    }
}
