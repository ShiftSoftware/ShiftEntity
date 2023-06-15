namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    public ShiftEntityOptions WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponse)
    {
        this._WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponse;

        return this;
    }

    public HashIdOptions HashId { get; set; }

    public ShiftEntityOptions()
    {
        HashId = new ();
    }
}
