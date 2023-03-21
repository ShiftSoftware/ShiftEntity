using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Formatter.Serialization;
using Microsoft.OData.Edm;
using Microsoft.OData;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Converters;
using System;

namespace ShiftSoftware.ShiftEntity.Web.Services;
public static class TimeZoneService
{
    public static DateTime ReceiveDateFromClient(this DateTime dateTime)
    {
        return new DateTime(dateTime.Subtract(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Utc);
    }

    public static void RegisterTimeZoneConverters(this Microsoft.AspNetCore.Mvc.JsonOptions options)
    {
        options.JsonSerializerOptions.Converters.Add(new JsonDateTimeConverter());
        options.JsonSerializerOptions.Converters.Add(new JsonTimeConverter());
    }

    public static void RegisterTimeZoneConverters(this IServiceCollection services)
    {
        services.AddSingleton<IODataSerializerProvider>(serviceProvider =>
        {
            return new ODataDatetimeSerializerProvider(serviceProvider);
        });
    }

    private static IHttpContextAccessor? httpContextAccessor;

    public static void Register(IHttpContextAccessor httpContextAccessor)
    {
        TimeZoneService.httpContextAccessor = httpContextAccessor;
    }

    public static TimeSpan GetTimeZoneOffset()
    {
        return TimeSpan.Parse(TimeZoneService.httpContextAccessor!.HttpContext!.Request!.Headers!["timezone-offset"]!);
    }
}

class JsonDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDateTime();//.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(new DateTimeOffset(new DateTime(value.Add(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Unspecified), TimeZoneService.GetTimeZoneOffset()));
    }
}

class JsonTimeConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return TimeSpan.Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        //writer.WriteStringValue(new DateTimeOffset(new DateTime(value.Add(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Unspecified), TimeZoneService.GetTimeZoneOffset()));
        writer.WriteStringValue(value.Add(TimeZoneService.GetTimeZoneOffset()).ToString("hh\\:mm\\:ss\\.fffffff"));
    }
}

class ODataDatetimeSerializerProvider : ODataSerializerProvider
{
    public ODataDatetimeSerializerProvider(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    public override IODataEdmTypeSerializer GetEdmTypeSerializer(IEdmTypeReference edmType)
    {
        if (edmType.Definition.TypeKind == EdmTypeKind.Entity)
            return new ODataDatetimeResourceSerializer(this);
        else
            return base.GetEdmTypeSerializer(edmType);
    }
}

class ODataDatetimeResourceSerializer : ODataResourceSerializer
{
    public ODataDatetimeResourceSerializer(ODataSerializerProvider serializerProvider) : base(serializerProvider)
    {
    }

    public override ODataProperty? CreateStructuralProperty(IEdmStructuralProperty structuralProperty, ResourceContext resourceContext)
    {
        ODataProperty property = base.CreateStructuralProperty(structuralProperty, resourceContext);

        if (property?.Value?.GetType() == typeof(DateTimeOffset))
        {
            var dateValue = (DateTimeOffset)property.Value;

            property.Value = new DateTimeOffset(new DateTime(dateValue.Add(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Unspecified), TimeZoneService.GetTimeZoneOffset());
        }

        return property;
    }
}