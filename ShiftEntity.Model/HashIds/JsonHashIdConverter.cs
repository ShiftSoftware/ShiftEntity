using System.Text.Json.Serialization;
using System.Text.Json;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Model.HashIds;

public class StringJsonHashIdConverter : JsonConverter<string>
{
    private readonly ShiftEntityHashId? hashids;
    private readonly IHashIdServiceReader? hashIdService;
    private readonly string? configurationName;

    public StringJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    private readonly string? configurationName;

    public NullableLongJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    private readonly string? configurationName;

    public LongJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    private readonly string? configurationName;

    public ShiftEntitySelectDTOJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    private readonly string? configurationName;

    public ShiftEntitySelectDTOEnumerableJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    private readonly string? configurationName;

    public ShiftEntitySelectDTOListJsonHashIdConverter(ShiftEntityHashId? hashids, string? configurationName = null, IHashIdServiceReader? hashIdService = null)
    {
        this.hashids = hashids;
        this.configurationName = configurationName;
        this.hashIdService = hashIdService;
    }

    private bool IsEnabled() => HashIdConverterRuntime.IsEnabled(this.configurationName, this.hashIdService);

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
    bool IsConfigurationRegistered(string configurationName);
}

internal static class HashIdConverterRuntime
{
    internal static bool IsEnabled(string? configurationName, IHashIdServiceReader? service)
    {
        var name = configurationName ?? JsonHashIdConverterAttribute.DefaultConfigurationName;

        if (service is not null)
            return service.IsConfigurationRegistered(name);

#pragma warning disable CS0618
        return name == JsonHashIdConverterAttribute.IdentityConfigurationName
            ? HashId.IdentityHashIdEnabled
            : HashId.Enabled;
#pragma warning restore CS0618
    }
}
public class JsonHashIdConverterAttribute : JsonConverterAttribute
{
    public const string IdentityConfigurationName = "Identity";
    public const string DefaultConfigurationName  = "Default";

    internal ShiftEntityHashId? Hashids;

    public string? Salt { get; set; }
    public int MinHashLength { get; set; }
    public string? Alphabet { get; set; }
    public Type? DtoType { get; set; }

    // When set, the attribute resolves its hasher from the named entry in HashIdOptions.Configurations
    // at type-info build time. Wins over the literal Salt/MinHashLength/Alphabet properties.
    public string? ConfigurationName { get; set; }

    public JsonHashIdConverterAttribute(
        string? salt = null,
        int minHashLength = 0,
        string? alphabet = null,
        Type? dtoType = null,
        string? configurationName = null)
    {
        Salt = salt;
        MinHashLength = minHashLength;
        Alphabet = alphabet;
        DtoType = dtoType;
        ConfigurationName = configurationName;
    }

    public override JsonConverter? CreateConverter(Type typeToConvert)
    {
        EnsureLegacyHasher();

        if (typeToConvert == typeof(string))
            return new StringJsonHashIdConverter(this.Hashids, this.ConfigurationName);
        if (typeToConvert == typeof(long))
            return new LongJsonHashIdConverter(this.Hashids, this.ConfigurationName);
        if (typeToConvert == typeof(long?))
            return new NullableLongJsonHashIdConverter(this.Hashids, this.ConfigurationName);
        else if (typeToConvert == typeof(ShiftEntitySelectDTO))
            return new ShiftEntitySelectDTOJsonHashIdConverter(this.Hashids, this.ConfigurationName);
        else if (typeToConvert == typeof(IEnumerable<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOEnumerableJsonHashIdConverter(this.Hashids, this.ConfigurationName);
        else if (typeToConvert == typeof(List<ShiftEntitySelectDTO>))
            return new ShiftEntitySelectDTOListJsonHashIdConverter(this.Hashids, this.ConfigurationName);

        throw new Exception($"No JsonHashIdConverter for type ({typeToConvert.Name}) is available");
    }

    // Legacy static path only — DI hosts go through HashIdJsonTypeInfoResolverModifier which
    // replaces the converter with a DI-aware one before this fallback is consulted. Named-config
    // attributes deliberately don't materialize a hasher here; they require DI.
    private void EnsureLegacyHasher()
    {
        if (Hashids is not null) return;
        if (ConfigurationName is not null) return;
#pragma warning disable CS0618
        if (!HashId.Enabled) return;
        var saltWithType = DtoType is null
            ? Salt ?? string.Empty
            : (Salt ?? string.Empty) + new string(DtoType.FullName!.Reverse().ToArray());
        Hashids = new ShiftEntityHashId(saltWithType, MinHashLength, Alphabet);
#pragma warning restore CS0618
    }
}

public class JsonHashIdConverterAttribute<T> : JsonHashIdConverterAttribute
{
    public JsonHashIdConverterAttribute(
        int minHashLength = 0,
        string? salt = null,
        string? alphabet = null,
        string? configurationName = null)
        : base(salt, minHashLength, alphabet, typeof(T), configurationName)
    {
    }
}
