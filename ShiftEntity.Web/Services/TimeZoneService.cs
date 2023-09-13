﻿using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using System;

namespace ShiftSoftware.ShiftEntity.Web.Services;
public class TimeZoneService
{
    private IHttpContextAccessor httpContextAccessor;

    public TimeZoneService(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public TimeSpan GetTimeZoneOffset()
    {
        if (this.httpContextAccessor != null && this.httpContextAccessor.HttpContext!.Request!.Headers.ContainsKey("timezone-offset"))
            return TimeSpan.Parse(this.httpContextAccessor!.HttpContext!.Request!.Headers!["timezone-offset"]!);

        return TimeSpan.Zero;
    }

    public DateTime ReadOffsettedDate(DateTimeOffset dateTime)
    {
        return new DateTime(dateTime.Subtract(this.GetTimeZoneOffset()).Ticks, DateTimeKind.Utc);
    }

    public DateTime WriteOffsettedDate(DateTimeOffset dateTime)
    {
        return new DateTime(dateTime.Add(this.GetTimeZoneOffset()).Ticks, DateTimeKind.Unspecified);
    }
}

class JsonDateTimeConverter : JsonConverter<DateTime>
{
    private readonly TimeZoneService timeZoneService;

    public JsonDateTimeConverter(TimeZoneService timeZoneService)
    {
        this.timeZoneService = timeZoneService;
    }

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dateTime = reader.GetDateTime();

        //Substracting or adding may result in invalid dates.
        //For example if the value is Datetime.Min or Datetime.Max
        try
        {
            dateTime = timeZoneService.ReadOffsettedDate(dateTime);
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
            dateTime = new DateTimeOffset(timeZoneService.WriteOffsettedDate(dateTime), timeZoneService.GetTimeZoneOffset());
        }

        catch
        {

        }

        writer.WriteStringValue(dateTime);
    }
}

class JsonTimeConverter : JsonConverter<TimeSpan>
{
    private readonly TimeZoneService timeZoneService;

    public JsonTimeConverter(TimeZoneService timeZoneService)
    {
        this.timeZoneService = timeZoneService;
    }

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        //Adding or substracting from TimeSpan does not rotate the time of day correctly.
        //For example substracting 12 from 10:10:00 results in (-01:50:00). Instead of rotating to the previous day's 22:10:00


        //So we use a date time instead

        var timeSpan = TimeSpan.Parse(reader.GetString()!);

        var dateTime = new DateTime(2020, 01, 15).Add(timeSpan);

        dateTime = timeZoneService.ReadOffsettedDate(dateTime);

        return dateTime.TimeOfDay;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        //Adding or substracting from TimeSpan does not rotate the time of day correctly.
        //For example substracting 12 from 10:10:00 results in (-01:50:00). Instead of rotating to the previous day's 22:10:00


        //So we use a date time instead
        var dateTime = new DateTime(2020, 01, 15).Add(value);

        dateTime = dateTime.Add(timeZoneService.GetTimeZoneOffset());

        writer.WriteStringValue(dateTime.TimeOfDay.ToString("hh\\:mm\\:ss\\.fffffff"));
    }
}