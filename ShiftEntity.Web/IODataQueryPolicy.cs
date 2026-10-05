using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.UriParser;

namespace ShiftSoftware.ShiftEntity.Web;

/// <summary>Checks ordering and transforms a parsed filter before count, paging or selection.</summary>
public interface IODataQueryPolicy
{
    SingleValueNode? Apply<T>(ODataQueryOptions<T> options, SingleValueNode? filter) where T : class;
}
