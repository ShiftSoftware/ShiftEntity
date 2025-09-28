using System.Text.Json.Serialization;
using System.Text.Json;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class StringJsonHashIdConverter : JsonConverter<string>
{
    private ShiftEntityHashId hashids;

    public StringJsonHashIdConverter(ShiftEntityHashId hashids)
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

public class NullableLongJsonHashIdConverter : JsonConverter<long?>
{
    private ShiftEntityHashId hashids;

    public NullableLongJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
        {
            return this.hashids.Decode(id!)!;
        }

        return long.Parse(id!);
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
        {
            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
                writer.WriteStringValue(this.hashids.Encode(value.Value));
            else
                writer.WriteStringValue(value.ToString());
        }
    }
}

public class LongJsonHashIdConverter : JsonConverter<long>
{
    private ShiftEntityHashId hashids;

    public LongJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
        {
            return this.hashids.Decode(id!)!;
        }

        return long.Parse(id!);
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        if (value == 0)
            writer.WriteNullValue();
        else
        {
            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
                writer.WriteStringValue(this.hashids.Encode(value));
            else
                writer.WriteStringValue(value.ToString());
        }
    }
}

public class ShiftEntitySelectDTOJsonHashIdConverter : JsonConverter<ShiftEntitySelectDTO>
{
    private ShiftEntityHashId hashids;

    public ShiftEntitySelectDTOJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override ShiftEntitySelectDTO Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        //var dto = new ShiftEntitySelectDTO();

        var dto = JsonSerializer.Deserialize<ShiftEntitySelectDTO>(ref reader, options)!;

        //if (obj.TryGetProperty(nameof(ShiftEntitySelectDTO.Value), out var idProperty))
        {
            //var id = idProperty.GetString();

            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
            {
                dto.Value = this.hashids.Decode(dto.Value!).ToString();
            }
        }

        //if (obj.TryGetProperty(nameof(ShiftEntitySelectDTO.Text), out var textProperty))
        //{
        //    dto.Text = textProperty.Deserialize<string>(options);
        //}

        return dto;
    }

    public override void Write(Utf8JsonWriter writer, ShiftEntitySelectDTO value, JsonSerializerOptions options)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Value))
            writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();

            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids.Encode(long.Parse(value.Value)));
            }
            else
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), value.Value);
            }

            if (!string.IsNullOrWhiteSpace(value?.Text))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Text), value!.Text);
            }

            writer.WriteEndObject();
        }
    }
}

public class ShiftEntitySelectDTOEnumerableJsonHashIdConverter : JsonConverter<IEnumerable<ShiftEntitySelectDTO>>
{
    private ShiftEntityHashId hashids;

    public ShiftEntitySelectDTOEnumerableJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override IEnumerable<ShiftEntitySelectDTO> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        //var dtoList = new List<ShiftEntitySelectDTO>();

        var dtoList = JsonSerializer.Deserialize<IEnumerable<ShiftEntitySelectDTO>>(ref reader, options)!;

        //if (obj.ValueKind == JsonValueKind.Array)
        {
            foreach (var dto in dtoList)
            {
                //var dto = new ShiftEntitySelectDTO();

                //if (element.TryGetProperty(nameof(ShiftEntitySelectDTO.Value), out var idProperty))
                {
                    //var id = idProperty.GetString();

                    if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
                    {
                        dto.Value = this.hashids.Decode(dto.Value!).ToString();
                    }
                    //else
                    //{
                    //    dto.Value = id!;
                    //}
                }

                //if (element.TryGetProperty(nameof(ShiftEntitySelectDTO.Text), out var textProperty))
                //{
                //    dto.Text = textProperty.GetString();
                //}

                //dtoList.Add(dto);
            }
        }

        return dtoList;
    }

    public override void Write(Utf8JsonWriter writer, IEnumerable<ShiftEntitySelectDTO> value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var dto in value)
        {
            writer.WriteStartObject();

            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids.Encode(long.Parse(dto.Value)));
            }
            else
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), dto.Value);
            }

            if (!string.IsNullOrWhiteSpace(dto.Text))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Text), dto.Text);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

public class ShiftEntitySelectDTOListJsonHashIdConverter : JsonConverter<List<ShiftEntitySelectDTO>>
{
    private ShiftEntityHashId hashids;

    public ShiftEntitySelectDTOListJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public override List<ShiftEntitySelectDTO> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        //var dtoList = new List<ShiftEntitySelectDTO>();

        var dtoList = JsonSerializer.Deserialize<List<ShiftEntitySelectDTO>>(ref reader, options)!;

        //if (obj.ValueKind == JsonValueKind.Array)
        {
            foreach (var dto in dtoList)
            {
                //var dto = new ShiftEntitySelectDTO();

                //if (element.TryGetProperty(nameof(ShiftEntitySelectDTO.Value), out var idProperty))
                {
                    //var id = idProperty.GetString();

                    if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
                    {
                        dto.Value = this.hashids.Decode(dto.Value!).ToString();
                    }
                    //else
                    //{
                    //    dto.Value = id!;
                    //}
                }

                //if (element.TryGetProperty(nameof(ShiftEntitySelectDTO.Text), out var textProperty))
                //{
                //    dto.Text = textProperty.GetString();
                //}

                //dtoList.Add(dto);
            }
        }

        return dtoList;
    }

    public override void Write(Utf8JsonWriter writer, List<ShiftEntitySelectDTO> value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var dto in value)
        {
            writer.WriteStartObject();

            if ((HashId.Enabled && !(this.hashids?.IsIdentityHasher ?? true)) || (HashId.IdentityHashIdEnabled && (this.hashids?.IsIdentityHasher ?? false)))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids.Encode(long.Parse(dto.Value)));
            }
            else
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), dto.Value);
            }

            if (!string.IsNullOrWhiteSpace(dto.Text))
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Text), dto.Text);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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
        if (typeToConvert == typeof(string))
            return new StringJsonHashIdConverter(this.Hashids!);
        if (typeToConvert == typeof(long))
            return new LongJsonHashIdConverter(this.Hashids!);
        if (typeToConvert == typeof(long?))
            return new NullableLongJsonHashIdConverter(this.Hashids!);
        else if (typeToConvert == typeof(ShiftEntitySelectDTO))
            return new ShiftEntitySelectDTOJsonHashIdConverter(this.Hashids!);
        else if (typeToConvert == typeof(IEnumerable<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOEnumerableJsonHashIdConverter(this.Hashids!);
        else if (typeToConvert == typeof(List<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOListJsonHashIdConverter(this.Hashids!);

        throw new Exception($"No JsonHashIdConverter for type ({typeToConvert.Name}) is available");
    }
}

public class JsonHashIdConverterAttribute<T> : JsonHashIdConverterAttribute
{
    public JsonHashIdConverterAttribute(int minHashLength = 0, string? salt = null, string? alphabet = null, bool isIdentityHasher = false) : base(typeof(T), salt, minHashLength, alphabet, isIdentityHasher)
    {

    }
}