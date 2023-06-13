using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Extensions;

public static class MvcJsonOptionsExtensions
{
    public static JsonOptions RegisterTimeZoneConverters(this JsonOptions options, TimeZoneService timeZoneService)
    {
        options.JsonSerializerOptions.Converters.Add(new JsonDateTimeConverter(timeZoneService));
        options.JsonSerializerOptions.Converters.Add(new JsonTimeConverter(timeZoneService));

        return options;
    }
}
