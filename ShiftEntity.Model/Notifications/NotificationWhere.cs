using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model.Notifications;

/// <summary>
/// Which records a subscribers entry hears about. In JSON it is either the text <c>"all"</c>, or an object with any of
/// <c>company</c>, <c>branch</c>, <c>brand</c>, <c>region</c>, <c>city</c>, <c>country</c> and <c>team</c>, each with
/// one hash ID. Every key given must match the record. A record that lacks the marker for a key, or has no value for
/// it, doesn't match.
/// </summary>
[JsonConverter(typeof(NotificationWhereJsonConverter))]
public sealed class NotificationWhere
{
    /// <summary>The JSON text that means every record.</summary>
    public const string AllText = "all";

    /// <summary>Every record. Set this or the keys, never both.</summary>
    public bool IsAll { get; set; }

    public string? Company { get; set; }
    public string? Branch { get; set; }
    public string? Brand { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Team { get; set; }

    /// <summary>
    /// What the JSON held that this contract doesn't know, such as an unknown key. They are kept so that loading the
    /// list can drop that entry with a clear error. A <c>where</c> that has any never matches.
    /// </summary>
    public List<string> UnreadableParts { get; } = new();

    /// <summary>A new <c>where</c> that means every record.</summary>
    public static NotificationWhere All() => new() { IsAll = true };

    /// <summary>Whether at least one key is set.</summary>
    public bool HasKeys =>
        Company is not null || Branch is not null || Brand is not null || Region is not null ||
        City is not null || Country is not null || Team is not null;

    /// <summary>
    /// A text that is the same for two <c>where</c> values that mean the same thing. It is used to find a second entry
    /// for the same topic and <c>where</c>.
    /// </summary>
    public string GetComparisonKey()
    {
        if (IsAll)
            return AllText;

        return string.Join(";", new[]
        {
            $"branch={Branch}", $"brand={Brand}", $"city={City}", $"company={Company}",
            $"country={Country}", $"region={Region}", $"team={Team}",
        });
    }
}

/// <summary>
/// Reads and writes <see cref="NotificationWhere"/>. Reading never fails on content it doesn't know. It keeps that
/// content in <see cref="NotificationWhere.UnreadableParts"/>, so that loading the list can drop that entry with a clear
/// error, and keep the others.
/// </summary>
public sealed class NotificationWhereJsonConverter : JsonConverter<NotificationWhere>
{
    public override NotificationWhere Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var where = new NotificationWhere();

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();

            if (string.Equals(text, NotificationWhere.AllText, StringComparison.OrdinalIgnoreCase))
                where.IsAll = true;
            else
                where.UnreadableParts.Add($"'{text}'");

            return where;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            where.UnreadableParts.Add("a value that is neither text nor an object");
            reader.TrySkip();
            return where;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();

            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            {
                where.UnreadableParts.Add($"'{key}'");
                reader.TrySkip();
                continue;
            }

            if (!TrySet(where, key, reader.GetString()))
                where.UnreadableParts.Add($"'{key}'");
        }

        return where;
    }

    private static bool TrySet(NotificationWhere where, string key, string? value)
    {
        switch (key.ToLowerInvariant())
        {
            case "company": where.Company = value; return true;
            case "branch": where.Branch = value; return true;
            case "brand": where.Brand = value; return true;
            case "region": where.Region = value; return true;
            case "city": where.City = value; return true;
            case "country": where.Country = value; return true;
            case "team": where.Team = value; return true;
            default: return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, NotificationWhere value, JsonSerializerOptions options)
    {
        if (value.IsAll)
        {
            writer.WriteStringValue(NotificationWhere.AllText);
            return;
        }

        writer.WriteStartObject();
        WriteKey(writer, "company", value.Company);
        WriteKey(writer, "branch", value.Branch);
        WriteKey(writer, "brand", value.Brand);
        WriteKey(writer, "region", value.Region);
        WriteKey(writer, "city", value.City);
        WriteKey(writer, "country", value.Country);
        WriteKey(writer, "team", value.Team);
        writer.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter writer, string key, string? value)
    {
        if (value is not null)
            writer.WriteString(key, value);
    }
}
