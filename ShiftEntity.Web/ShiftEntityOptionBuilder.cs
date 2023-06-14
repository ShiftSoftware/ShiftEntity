namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptionBuilder
{
    private ShiftEntityOptions _shiftEntityOptions { get; set; }

    public ShiftEntityOptionBuilder()
    {
        this._shiftEntityOptions = new ShiftEntityOptions();
    }

    public ShiftEntityOptionBuilder WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponseWithShiftEntityResponse)
    {
        this._shiftEntityOptions.WrapValidationErrorResponseWithShiftEntityResponse(wrapValidationErrorResponseWithShiftEntityResponse);

        return this;
    }

    public ShiftEntityOptionBuilder SetupOdata(ShiftEntityODataOptions shiftEntityODataOptions = null)
    {
        if (shiftEntityODataOptions == null)
        {
            this._shiftEntityOptions.ODataOptions.DefaultOptions();
        }
        else
        {
            this._shiftEntityOptions.ODataOptions = shiftEntityODataOptions;
        }

        return this;
    }

    public ShiftEntityOptionBuilder OdataEntitySet<T>(string name) where T : class
    {
        this._shiftEntityOptions.ODataOptions.ODataConvention.EntitySet<T>(name);

        return this;
    }

    public ShiftEntityOptionBuilder RegisterHashId(bool acceptUnencodedIds)
    {
        this._shiftEntityOptions.HashId.RegisterHashId(acceptUnencodedIds);

        return this;
    }

    public ShiftEntityOptionBuilder RegisterUserIdsHasher(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        this._shiftEntityOptions.HashId.RegisterUserIdsHasher(salt, minHashLength, alphabet);

        return this;
    }

    public ShiftEntityOptions Build()
    {
        return this._shiftEntityOptions;
    }
}
