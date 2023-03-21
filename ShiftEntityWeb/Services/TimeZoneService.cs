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

    internal static TimeSpan GetTimeZoneOffset()
    {
        return TimeSpan.Parse(TimeZoneService.httpContextAccessor!.HttpContext!.Request!.Headers!["timezone-offset"]!);
    }

    internal static DateTime ReadOffsettedDate(DateTimeOffset dateTime)
    {
        return new DateTime(dateTime.Subtract(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Utc);
    }

    internal static DateTime WriteOffsettedDate(DateTimeOffset dateTime)
    {
        return new DateTime(dateTime.Add(TimeZoneService.GetTimeZoneOffset()).Ticks, DateTimeKind.Unspecified);
    }
}

class JsonDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dateTime = reader.GetDateTime();

        //Substracting or adding may result in invalid dates.
        //For example if the value is Datetime.Min or Datetime.Max
        try
        {
            dateTime = TimeZoneService.ReadOffsettedDate(dateTime);
        }
        catch
        {

        }

        return dateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        if (value == default)
        {
            writer.WriteStringValue(DateTime.MinValue);
            return;
        }

        DateTimeOffset dateTime = value;

        //Substracting or adding may result in invalid dates.
        //For example if the value is Datetime.Min or Datetime.Max
        try
        {
            dateTime = new DateTimeOffset(TimeZoneService.WriteOffsettedDate(dateTime), TimeZoneService.GetTimeZoneOffset());
        }

        catch
        {

        }

        writer.WriteStringValue(dateTime);
    }
}

class JsonTimeConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        //Adding or substracting from TimeSpan does not rotate the time of day correctly.
        //For example substracting 12 from 10:10:00 results in (-01:50:00). Instead of rotating to the previous day's 22:10:00


        //So we use a date time instead

        var timeSpan = TimeSpan.Parse(reader.GetString()!);

        var dateTime = new DateTime(2020, 01, 15).Add(timeSpan);

        dateTime = TimeZoneService.ReadOffsettedDate(dateTime);

        return dateTime.TimeOfDay;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        //Adding or substracting from TimeSpan does not rotate the time of day correctly.
        //For example substracting 12 from 10:10:00 results in (-01:50:00). Instead of rotating to the previous day's 22:10:00


        //So we use a date time instead
        var dateTime = new DateTime(2020, 01, 15).Add(value);

        dateTime = dateTime.Add(TimeZoneService.GetTimeZoneOffset());

        writer.WriteStringValue(dateTime.TimeOfDay.ToString("hh\\:mm\\:ss\\.fffffff"));
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
            var dateTime = (DateTimeOffset)property.Value;

            if (dateTime != default)
            {
                //Substracting or adding may result in invalid dates.
                //For example if the value is Datetime.Min or Datetime.Max
                try
                {
                    dateTime = new DateTimeOffset(TimeZoneService.WriteOffsettedDate(dateTime), TimeZoneService.GetTimeZoneOffset());
                }
                catch
                {

                }

                property.Value = dateTime;
            }
        }

        return property;
    }
}