using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class ShiftEntityOptions
{
    internal bool _WrapValidationErrorResponseWithShiftEntityResponse;
    public ShiftEntityOptions WrapValidationErrorResponseWithShiftEntityResponse(bool wrapValidationErrorResponse)
    {
        this._WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponse;

        return this;
    }

    public ShiftEntityODataOptions ODataOptions { get; set; }

    public HashIdOptions HashId { get; set; }

    public ShiftEntityOptions()
    {
        ODataOptions = new ();
        HashId = new ();
    }
}
