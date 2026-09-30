using System.Text.Json;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Localization;

/// <summary>
/// Tests that change <see cref="LocalizedTextJsonConverter.UserLanguage"/>, which every test in the process can read.
/// They run alone, after the tests that run in parallel.
/// </summary>
[CollectionDefinition(nameof(ProcessWideLanguageCollection), DisableParallelization = true)]
public sealed class ProcessWideLanguageCollection;

/// <summary>
/// Pins which language <see cref="LocalizedTextJsonConverter"/> reads. A language set for the current async flow (one
/// request on a server) takes precedence. Without one, the process-wide <see cref="LocalizedTextJsonConverter.UserLanguage"/>
/// applies, as before: a Blazor WebAssembly app sets only that one.
/// </summary>
[Collection(nameof(ProcessWideLanguageCollection))]
public class LocalizedTextLanguageTests
{
    private const string Title = """{"en":"Spring service campaign","ru":"Весенняя сервисная кампания"}""";

    private const string TitleEn = "Spring service campaign";
    private const string TitleRu = "Весенняя сервисная кампания";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Without_a_language_for_the_flow_the_process_wide_language_applies()
    {
        using var processLanguage = new ProcessLanguageScope("ru");

        Assert.Equal("ru", LocalizedTextJsonConverter.CurrentLanguage);
        Assert.Equal(TitleRu, LocalizedTextJsonConverter.ParseLocalizedText(Title));
        Assert.Equal(TitleRu, ReadSelectText(Title));
    }

    [Fact]
    public void A_language_for_the_flow_takes_precedence_until_its_scope_ends()
    {
        using var processLanguage = new ProcessLanguageScope("ru");

        using (LocalizedTextJsonConverter.UseLanguage("en"))
        {
            Assert.Equal("en", LocalizedTextJsonConverter.CurrentLanguage);
            Assert.Equal(TitleEn, LocalizedTextJsonConverter.ParseLocalizedText(Title));
            Assert.Equal(TitleEn, ReadSelectText(Title));

            // null removes the language of the flow for the inner scope, so the process-wide one applies there.
            using (LocalizedTextJsonConverter.UseLanguage(null))
            {
                Assert.Equal("ru", LocalizedTextJsonConverter.CurrentLanguage);
            }

            Assert.Equal("en", LocalizedTextJsonConverter.CurrentLanguage);
        }

        Assert.Equal("ru", LocalizedTextJsonConverter.CurrentLanguage);
    }

    [Fact]
    public async Task Concurrent_flows_each_read_their_own_language()
    {
        using var processLanguage = new ProcessLanguageScope("en");
        using var bothSet = new Barrier(2);

        // Each flow sets its language and then waits until the other has set its own, before it reads.
        string Read(string language)
        {
            using (LocalizedTextJsonConverter.UseLanguage(language))
            {
                Assert.True(bothSet.SignalAndWait(Timeout), "The other flow did not set its language.");

                return LocalizedTextJsonConverter.ParseLocalizedText(Title);
            }
        }

        var texts = await Task.WhenAll(
            Task.Run(() => Read("ru"), TestContext.Current.CancellationToken),
            Task.Run(() => Read("en"), TestContext.Current.CancellationToken));

        Assert.Equal([TitleRu, TitleEn], texts);

        // Neither flow changed the language of this one.
        Assert.Equal("en", LocalizedTextJsonConverter.CurrentLanguage);
    }

    [Fact]
    public void An_explicit_language_ignores_the_language_of_the_flow_and_of_the_process()
    {
        using var processLanguage = new ProcessLanguageScope("ru");

        using (LocalizedTextJsonConverter.UseLanguage("ru"))
        {
            Assert.Equal(TitleEn, LocalizedTextJsonConverter.ParseLocalizedText(Title, "en"));
        }

        // A language the value does not have falls back to English. A value that is not a JSON object is returned
        // as it is.
        Assert.Equal(TitleEn, LocalizedTextJsonConverter.ParseLocalizedText(Title, "de"));
        Assert.Equal("Plain text", LocalizedTextJsonConverter.ParseLocalizedText("Plain text", "ru"));
    }

    // What a server does with a request body: ShiftEntitySelectDTO.Text is read by LocalizedTextJsonConverter.
    private static string? ReadSelectText(string text)
    {
        var json = JsonSerializer.Serialize(new { Value = "1", Text = text });

        return JsonSerializer.Deserialize<ShiftEntitySelectDTO>(json)!.Text;
    }

    // Sets the process-wide language for one test, and puts the previous one back.
    private sealed class ProcessLanguageScope : IDisposable
    {
        private readonly string previous = LocalizedTextJsonConverter.UserLanguage;

        public ProcessLanguageScope(string language)
        {
            LocalizedTextJsonConverter.UserLanguage = language;
        }

        public void Dispose()
        {
            LocalizedTextJsonConverter.UserLanguage = previous;
        }
    }
}
