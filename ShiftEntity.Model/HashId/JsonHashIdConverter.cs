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

        if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
        {
            return this.hashids.Decode(id!).ToString()!;
        }

        return id!;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
            writer.WriteNullValue();
        else
        {
            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
                writer.WriteStringValue(this.hashids.Encode(long.Parse(value)));
            else
                writer.WriteStringValue(value);
        }
    }
}

public class JsonHashIdConverterAttribute : JsonConverterAttribute
{
    internal ShiftEntityHashId? Hashids;
    internal bool IsIdentityHasher = false;

    public JsonHashIdConverterAttribute(string salt, int minHashLength = 0, string? alphabet = null)
    {
        if (HashId.Enabled && !this.IsIdentityHasher)
            Hashids = new ShiftEntityHashId(salt, minHashLength, alphabet);
    }

    //internal JsonHashIdConverterAttribute(string salt, int minHashLength = 0, string? alphabet = null, bool userIdsHasher = true)
    //{
    //    this.UserIdsHasher = userIdsHasher;

    //    if (HashId.IdentityHashIdEnabled && userIdsHasher)
    //        Hashids = new ShiftEntityHashId(salt, minHashLength, alphabet, userIdsHasher);
    //}

    public JsonHashIdConverterAttribute(Type dtoType, string? salt = null, int minHashLength = 0, string? alphabet = null, bool isIdentityHasher = false)
    {
        //HashIds library seems to be taking the first 24 chars of the salt. This is why we're reversing the type name
        if (HashId.Enabled)
            Hashids = new ShiftEntityHashId(salt + new string(dtoType.FullName!.Reverse().ToArray()), minHashLength, alphabet, isIdentityHasher);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert)
    {
        return new JsonHashIdConverter(this.Hashids!);
    }
}

public class JsonHashIdConverterAttribute<T> : JsonHashIdConverterAttribute
{
    public JsonHashIdConverterAttribute(int minHashLength = 0, string? salt = null, string? alphabet = null, bool isIdentityHasher = false) : base(typeof(T), salt, minHashLength, alphabet, isIdentityHasher)
    {

    }
}