using System.Text.Json.Serialization;
using System.Text.Json;

namespace ShiftSoftware.ShiftEntity.Model.HashId;

public class JsonHashIdConverter : JsonConverter<string>
{
    private ShiftEntityHashId hashids;

    public JsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if (!HashId.Enabled)
            return id!;

        return this.hashids.Decode(id!).ToString()!;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
            writer.WriteNullValue();
        else
        {
            if (!HashId.Enabled)
                writer.WriteStringValue(value);
            else
                writer.WriteStringValue(this.hashids.Encode(long.Parse(value)));
        }
    }
}

public class JsonHashIdConverterAttribute : JsonConverterAttribute
{
    internal ShiftEntityHashId? Hashids;

    public JsonHashIdConverterAttribute(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (HashId.Enabled)
            Hashids = new ShiftEntityHashId(salt, minHashLength, alphabet);
    }

    public JsonHashIdConverterAttribute(Type dtoType, int minHashLength = 0, string? alphabet = null)
    {
        //HashIds library seems to be taking the first 24 chars of the salt. This is why we're reversing the type name
        if (HashId.Enabled)
            Hashids = new ShiftEntityHashId(new string(dtoType.FullName!.Reverse().ToArray()), minHashLength, alphabet);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert)
    {
        return new JsonHashIdConverter(this.Hashids!);
    }
}

public class JsonHashIdConverterAttribute<T> : JsonHashIdConverterAttribute
{
    public JsonHashIdConverterAttribute(int minHashLength = 0, string? alphabet = null) : base(typeof(T), minHashLength, alphabet)
    {

    }
}