
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

            try
            {
                var json = JsonDocument.Parse(jsonString);

                var localizedText = json.Deserialize<Dictionary<string, string>>()!;

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
        }

        return string.Empty;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}