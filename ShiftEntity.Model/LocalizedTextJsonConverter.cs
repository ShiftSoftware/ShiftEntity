
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftSoftware.ShiftEntity.Model;
public class LocalizedTextJsonConverter : JsonConverter<string>
{
    /// <summary>
    /// The language for the whole process. <see cref="CurrentLanguage"/> uses it when no language is set for the
    /// current async flow. A Blazor WebAssembly app has one user per process, so it sets this once, when the user
    /// picks a language.
    /// </summary>
    /// <remarks>
    /// A server must not set this per request. Every request in the process shares it, so a request could read the
    /// language of another request. A server sets the language of one request with <see cref="UseLanguage"/>.
    /// </remarks>
    public static string UserLanguage = "en";

    private static readonly AsyncLocal<string?> flowLanguage = new();

    /// <summary>
    /// The language that <see cref="ParseLocalizedText(string)"/> and <see cref="Read"/> use: the language set for
    /// the current async flow with <see cref="UseLanguage"/>, else <see cref="UserLanguage"/>.
    /// </summary>
    public static string CurrentLanguage
    {
        get
        {
            var language = flowLanguage.Value;

            return string.IsNullOrEmpty(language) ? UserLanguage : language!;
        }
    }

    /// <summary>
    /// Sets the language for the current async flow until the returned object is disposed. Then the language the
    /// flow had before applies again. null or an empty string removes the language of the flow, so
    /// <see cref="UserLanguage"/> applies.
    /// </summary>
    /// <remarks>
    /// The language belongs to the async flow, for example one HTTP request. The code that runs after this call in
    /// the same flow sees it, including the methods it awaits. Other requests do not see it. The caller of an async
    /// method that sets it does not see it either. Dispose the returned object in the method that called this one.
    /// </remarks>
    public static IDisposable UseLanguage(string? language)
    {
        var previous = flowLanguage.Value;

        flowLanguage.Value = language;

        return new LanguageScope(previous);
    }

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

    /// <summary>
    /// The text of a localized value in <see cref="CurrentLanguage"/>, by the rules of
    /// <see cref="ParseLocalizedText(string, string)"/>.
    /// </summary>
    public static string ParseLocalizedText(string jsonString)
    {
        return ParseLocalizedText(jsonString, CurrentLanguage);
    }

    /// <summary>
    /// The text of a localized value (<c>{"en":"…","ru":"…"}</c>) in <paramref name="language"/>, else in English,
    /// else an empty string. A value that is not a JSON object (plain text, or malformed) is returned as it is.
    /// </summary>
    public static string ParseLocalizedText(string jsonString, string language)
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

            if (language is not null && localizedText.TryGetValue(language, out var localizedValue))
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

    // Puts back the language the flow had before UseLanguage. Disposing it twice does nothing the second time.
    private sealed class LanguageScope : IDisposable
    {
        private readonly string? previous;
        private bool disposed;

        public LanguageScope(string? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            flowLanguage.Value = previous;
        }
    }
}