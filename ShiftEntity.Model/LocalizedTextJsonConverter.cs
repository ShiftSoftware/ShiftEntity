
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model;
public class LocalizedTextJsonConverter : JsonConverter<string>
{
    public static string UserLanguage = "en";

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var jsonString = reader.GetString() ?? string.Empty;

            return ParseLocalizedText(jsonString);
        }

        return string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }

    public static string ParseLocalizedText(string jsonString)
    {
        if (string.IsNullOrEmpty(jsonString))
            return string.Empty;

        // Plain (non-JSON) values are common — e.g. rows written before a column was localized.
        // Detect them up front instead of letting JsonDocument.Parse throw: exception-driven
        // control flow per cell is devastating on large lists (especially in Blazor WASM, where
        // a thrown+caught exception costs orders of magnitude more than this check).
        var trimmed = jsonString.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return jsonString;

        try
        {
            var localizedText = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString)!;

            if (localizedText.TryGetValue(UserLanguage, out var localizedValue))
            {
                return localizedValue;
            }

            if (localizedText.TryGetValue("en", out var defaultValue))
            {
                return defaultValue;
            }
        }
        catch
        {
            return jsonString;
        }

        return string.Empty;
    }
}