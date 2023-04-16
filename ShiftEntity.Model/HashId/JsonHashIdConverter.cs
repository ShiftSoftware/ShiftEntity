using System.Text.Json.Serialization;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Model.HashId;

public class JsonHashIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if (HashId.hashids == null)
            return id!;

        return HashId.Decode(id!).ToString();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (HashId.hashids == null || string.IsNullOrWhiteSpace(value))
            writer.WriteStringValue(value);
        else
            writer.WriteStringValue(HashId.Encode(long.Parse(value)));
    }
}
