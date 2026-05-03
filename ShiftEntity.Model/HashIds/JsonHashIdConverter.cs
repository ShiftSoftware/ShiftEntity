using System.Text.Json.Serialization;
using System.Text.Json;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class StringJsonHashIdConverter : JsonConverter<string>
{
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public StringJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public StringJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if (IsEnabled())
        {
            return this.hashids!.Decode(id!).ToString()!;
        }

        return id!;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
            writer.WriteNullValue();
        else
        {
            if (IsEnabled())
                writer.WriteStringValue(this.hashids!.Encode(long.Parse(value)));
            else
                writer.WriteStringValue(value);
        }
    }
}

public class NullableLongJsonHashIdConverter : JsonConverter<long?>
{
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public NullableLongJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public NullableLongJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if (IsEnabled())
        {
            return this.hashids!.Decode(id!)!;
        }

        return long.Parse(id!);
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
        {
            if (IsEnabled())
                writer.WriteStringValue(this.hashids!.Encode(value.Value));
            else
                writer.WriteStringValue(value.ToString());
        }
    }
}

public class LongJsonHashIdConverter : JsonConverter<long>
{
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public LongJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public LongJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();

        if (IsEnabled())
        {
            return this.hashids!.Decode(id!)!;
        }

        return long.Parse(id!);
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        if (value == 0)
            writer.WriteNullValue();
        else
        {
            if (IsEnabled())
                writer.WriteStringValue(this.hashids!.Encode(value));
            else
                writer.WriteStringValue(value.ToString());
        }
    }
}

public class ShiftEntitySelectDTOJsonHashIdConverter : JsonConverter<ShiftEntitySelectDTO>
{
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public ShiftEntitySelectDTOJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public ShiftEntitySelectDTOJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override ShiftEntitySelectDTO Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<ShiftEntitySelectDTO>(ref reader, options)!;

        if (IsEnabled())
        {
            dto.Value = this.hashids!.Decode(dto.Value!).ToString();
        }

        return dto;
    }

    public override void Write(Utf8JsonWriter writer, ShiftEntitySelectDTO value, JsonSerializerOptions options)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Value))
            writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();

            if (IsEnabled())
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids!.Encode(long.Parse(value.Value)));
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
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public ShiftEntitySelectDTOEnumerableJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public ShiftEntitySelectDTOEnumerableJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override IEnumerable<ShiftEntitySelectDTO> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dtoList = JsonSerializer.Deserialize<IEnumerable<ShiftEntitySelectDTO>>(ref reader, options)!;

        foreach (var dto in dtoList)
        {
            if (IsEnabled())
            {
                dto.Value = this.hashids!.Decode(dto.Value!).ToString();
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

            if (IsEnabled())
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids!.Encode(long.Parse(dto.Value)));
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
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;

    public ShiftEntitySelectDTOListJsonHashIdConverter(ShiftEntityHashId hashids)
    {
        this.hashids = hashids;
    }

    public ShiftEntitySelectDTOListJsonHashIdConverter(ShiftEntityHashId? hashids, IHashIdServiceReader hashIdService)
    {
        this.hashids = hashids;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.hashids, this.hashIdService);

    public override List<ShiftEntitySelectDTO> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dtoList = JsonSerializer.Deserialize<List<ShiftEntitySelectDTO>>(ref reader, options)!;

        foreach (var dto in dtoList)
        {
            if (IsEnabled())
            {
                dto.Value = this.hashids!.Decode(dto.Value!).ToString();
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

            if (IsEnabled())
            {
                writer.WriteString(nameof(ShiftEntitySelectDTO.Value), this.hashids!.Encode(long.Parse(dto.Value)));
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

/// <summary>
/// Minimal surface the JSON converters need from <c>IHashIdService</c> — factored into its own
/// interface so the converter classes (which live in ShiftEntity.Model) don't need a reference
/// to ShiftEntity.Core. The Core assembly's <c>IHashIdService</c> extends this.
/// </summary>
public interface IHashIdServiceReader
{
    bool Enabled { get; }
    bool IdentityHashIdEnabled { get; }
}

internal static class HashIdConverterRuntime
{
    internal static bool IsEnabled(ShiftEntityHashId? hashids, IHashIdServiceReader? service)
    {
        var isIdentity = hashids?.IsIdentityHasher ?? false;

        if (service is not null)
            return (service.Enabled && !isIdentity) || (service.IdentityHashIdEnabled && isIdentity);

#pragma warning disable CS0618
        return (HashId.Enabled && !isIdentity) || (HashId.IdentityHashIdEnabled && isIdentity);
#pragma warning restore CS0618
    }
}

public class JsonHashIdConverterAttribute : JsonConverterAttribute
{
    internal ShiftEntityHashId? Hashids;
    internal bool IsIdentityHasher = false;

    // Raw construction arguments preserved so HashIdService can (re)build a hasher on demand
    // without depending on the static HashId.Enabled flag that was read at attribute-construction time.
    internal readonly string? RawSalt;
    internal readonly int RawMinHashLength;
    internal readonly string? RawAlphabet;
    internal readonly Type? RawDtoType;
    internal readonly bool RawUsedTypedCtor;

    public JsonHashIdConverterAttribute(string salt, int minHashLength = 0, string? alphabet = null)
    {
        this.RawSalt = salt;
        this.RawMinHashLength = minHashLength;
        this.RawAlphabet = alphabet;
        this.RawUsedTypedCtor = false;

#pragma warning disable CS0618
        if (HashId.Enabled && !this.IsIdentityHasher)
            Hashids = new ShiftEntityHashId(salt, minHashLength, alphabet);
#pragma warning restore CS0618
    }

    public JsonHashIdConverterAttribute(Type dtoType, string? salt = null, int minHashLength = 0, string? alphabet = null, bool isIdentityHasher = false)
    {
        this.RawSalt = salt;
        this.RawMinHashLength = minHashLength;
        this.RawAlphabet = alphabet;
        this.RawDtoType = dtoType;
        this.RawUsedTypedCtor = true;
        this.IsIdentityHasher = isIdentityHasher;

        //HashIds library seems to be taking the first 24 chars of the salt. This is why we're reversing the type name
#pragma warning disable CS0618
        if (HashId.Enabled)
            Hashids = new ShiftEntityHashId(salt + new string(dtoType.FullName!.Reverse().ToArray()), minHashLength, alphabet, isIdentityHasher);
#pragma warning restore CS0618
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
