using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OData.Query;
using System.Collections.Generic;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

public static class SwaggerService
{
    public static bool DocInclusionPredicate(string docName, ApiDescription apiDesc)
    {
        //if (apiDesc.RelativePath != null && apiDesc.RelativePath.Contains("odata"))
        //{
        //    return apiDesc.HttpMethod == "GET" &&
        //    !apiDesc.RelativePath.Contains("$count") &&
        //    !apiDesc.RelativePath.Contains("({key})") &&
        //    !apiDesc.RelativePath.Contains("$metadata") &&
        //    !apiDesc.RelativePath.Equals("odata");
        //}

        //var excludedAttributes = new List<System.Type> {
        //    typeof(EnableQueryAttribute),
        //    typeof(EnableQueryWithHashIdConverter),
        //};

        //if (apiDesc.ActionDescriptor.FilterDescriptors.Select(x => x.Filter).Count(x => excludedAttributes.Contains(x.GetType())) > 0)
        //{
        //    return false;
        //}

        return true;
    }
}
