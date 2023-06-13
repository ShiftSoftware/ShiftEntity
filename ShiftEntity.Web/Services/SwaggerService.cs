using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OData.Query;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public static class SwaggerService
{
    public static bool DocInclusionPredicate(string docName, ApiDescription apiDesc)
    {
        if (apiDesc.RelativePath != null && apiDesc.RelativePath.Contains("odata"))
        {
            return apiDesc.HttpMethod == "GET" &&
            !apiDesc.RelativePath.Contains("$count") &&
            !apiDesc.RelativePath.Contains("({key})") &&
            !apiDesc.RelativePath.Contains("$metadata") &&
            !apiDesc.RelativePath.Equals("odata");
        }

        if (apiDesc.ActionDescriptor.FilterDescriptors.Select(x => x.Filter).Count(x => x.GetType() == typeof(EnableQueryAttribute)) > 0)
        {
            return false;
        }

        return true;
    }
}
