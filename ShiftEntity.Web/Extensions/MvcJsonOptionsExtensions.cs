using ShiftSoftware.ShiftEntity.Core.Services;
using ShiftSoftware.ShiftEntity.Web.Services;

namespace Microsoft.AspNetCore.Mvc;

public static class MvcJsonOptionsExtensions
{
    public static JsonOptions RegisterTimeZoneConverters(this JsonOptions options, TimeZoneService timeZoneService)
    {
        options.JsonSerializerOptions.Converters.Add(new JsonDateTimeConverter(timeZoneService));
        options.JsonSerializerOptions.Converters.Add(new JsonTimeConverter(timeZoneService));

        return options;
    }

    public static JsonOptions RegisterAzureStorageServiceConverters(this JsonOptions options, AzureStorageService? azureStorageService)
    {
        if (azureStorageService == null)
            return options;

        options.JsonSerializerOptions.Converters.Add(new JsonShiftFileDTOConverter(azureStorageService));

        return options;
    }
}
