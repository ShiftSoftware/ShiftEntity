using System.Reflection;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    internal Assembly[] AutoMapperAssemblies;

    public ShiftEntityOptions WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponse)
    {
        this._WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponse;

        return this;
    }

    public ShiftEntityOptions AddAutoMapper(params Assembly[] assemblies)
    {
        this.AutoMapperAssemblies = assemblies;
        return this;
    }

    public HashIdOptions HashId { get; set; }

    public ShiftEntityOptions()
    {
        HashId = new ();
    }
}
