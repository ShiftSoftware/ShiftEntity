using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using ShiftSoftware.ShiftEntity.Functions.Localization;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Tests.Localization.Scenario;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.Localization;

/// <summary>
/// Pins the request localization of Azure Functions hosts. The middleware used to write the language of each request
/// into a static that every request in the process shares, and to set the culture only for the function. With the
/// ASP.NET Core integration, the worker writes the function's result in its own middleware, which runs first and writes
/// the result only after this middleware has returned. So a response body was serialized with the language that the
/// latest request had written, and without the culture of its own request. These tests run the real middleware against
/// a hand-built FunctionContext, and then write the result the way the worker does: after the middleware has returned,
/// in a flow that has not seen the request.
/// </summary>
public class RequestLocalizationMiddlewareTests
{
    private const string Title = """{"en":"Spring service campaign","ru":"Весенняя сервисная кампания"}""";

    private const string TitleEn = "Spring service campaign";
    private const string TitleRu = "Весенняя сервисная кампания";
    private const string StatusEn = "Delivered";
    private const string StatusRu = "Доставлено";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly ResourceManager Strings = new(
        "ShiftSoftware.ShiftEntity.Tests.Localization.RequestLocalizationStrings", typeof(RequestLocalizationMiddlewareTests).Assembly);

    /// <summary>
    /// A response body that reads the values of its request while it is serialized: a localized text in the current
    /// language of the framework (as a host's converter reads it), a .resx string through the current UI culture (as an
    /// enum display name is read), and the current culture.
    /// </summary>
    [JsonConverter(typeof(ProbeWriter))]
    public sealed class Probe
    {
        public string Title { get; init; } = RequestLocalizationMiddlewareTests.Title;

        // Holds the writing of this body until the other response is being written too.
        public Barrier? Rendezvous { get; init; }
    }

    public sealed class ProbeWriter : JsonConverter<Probe>
    {
        public override Probe Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Probe value, JsonSerializerOptions options)
        {
            if (value.Rendezvous is not null)
                Assert.True(value.Rendezvous.SignalAndWait(Timeout), "The other response was not being written at the same time.");

            writer.WriteStartObject();
            writer.WriteString("Title", LocalizedTextJsonConverter.ParseLocalizedText(value.Title));
            writer.WriteString("Status", Strings.GetString("Delivered"));
            writer.WriteString("Culture", CultureInfo.CurrentCulture.Name);
            writer.WriteString("UICulture", CultureInfo.CurrentUICulture.Name);
            writer.WriteEndObject();
        }
    }

    [Fact]
    public async Task Concurrent_requests_write_their_responses_in_their_own_language()
    {
        using var outside = new InvariantCultureScope();

        var english = new HttpInvocation("en-US");
        var russian = new HttpInvocation("ru-RU,ru;q=0.9,en;q=0.8");

        // Each function waits until both have started, so both requests have set their language before either
        // function returns.
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writing = new Barrier(2);

        async Task Function(FunctionContext context)
        {
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.SetResult();

            await bothStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            context.GetInvocationResult().Value = new OkObjectResult(new Probe { Rendezvous = writing });
        }

        var middleware = new RequestLocalizationMiddleware();

        await Task.WhenAll(middleware.Invoke(english.Context, Function), middleware.Invoke(russian.Context, Function));

        // Both responses are written at the same time, after the middleware has returned. Each one waits inside its
        // serialization until the other one has started, so both have set their language before either reads it.
        var responses = await Task.WhenAll(
            Task.Run(english.WriteResponseAsync, TestContext.Current.CancellationToken),
            Task.Run(russian.WriteResponseAsync, TestContext.Current.CancellationToken));

        AssertResponse(responses[0], TitleEn, StatusEn, "en-US");
        AssertResponse(responses[1], TitleRu, StatusRu, "ru-RU");
    }

    [Fact]
    public async Task A_result_written_after_the_middleware_returned_gets_the_culture_and_language_of_its_request()
    {
        using var outside = new InvariantCultureScope();

        var invocation = new HttpInvocation("ru-RU");
        (string Culture, string UICulture, string Language) inFunction = default;

        await new RequestLocalizationMiddleware().Invoke(invocation.Context, context =>
        {
            inFunction = (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name, LocalizedTextJsonConverter.CurrentLanguage);
            context.GetInvocationResult().Value = new OkObjectResult(new Probe());
            return Task.CompletedTask;
        });

        Assert.Equal(("ru-RU", "ru-RU", "ru"), inFunction);

        // The caller of the middleware keeps its own values, before and after the response is written.
        AssertOutside();

        AssertResponse(await invocation.WriteResponseAsync(), TitleRu, StatusRu, "ru-RU");

        AssertOutside();
    }

    [Fact]
    public async Task An_IResult_is_written_with_the_culture_and_language_of_its_request()
    {
        using var outside = new InvariantCultureScope();

        var invocation = new HttpInvocation("ru");

        await new RequestLocalizationMiddleware().Invoke(invocation.Context, context =>
        {
            context.GetInvocationResult().Value = Results.Ok(new Probe());
            return Task.CompletedTask;
        });

        AssertResponse(await invocation.WriteResponseAsync(), TitleRu, StatusRu, "ru");
    }

    [Fact]
    public async Task The_HttpResult_property_of_a_return_type_with_several_outputs_is_written_with_the_language_of_its_request()
    {
        using var outside = new InvariantCultureScope();

        var invocation = new HttpInvocation("ru-RU", outputBindings: [("HttpResponse", "http"), ("Message", "queue")]);

        await new RequestLocalizationMiddleware().Invoke(invocation.Context, _ =>
        {
            // The worker has moved the properties of the returned object into their output bindings, and cleared the
            // return value, before the middleware continues.
            invocation.SetOutputBinding("HttpResponse", new OkObjectResult(new Probe()));
            invocation.SetOutputBinding("Message", "queued");
            return Task.CompletedTask;
        });

        Assert.Null(invocation.InvocationResult);
        Assert.Equal("queued", invocation.OutputBinding("Message"));

        AssertResponse(await invocation.WriteResponseAsync(), TitleRu, StatusRu, "ru-RU");
    }

    [Fact]
    public async Task In_the_built_in_HTTP_model_the_function_gets_the_language_and_its_result_is_left_as_it_is()
    {
        using var outside = new InvariantCultureScope();

        var invocation = new HttpInvocation("ru-RU", aspNetCoreIntegration: false);
        var result = new OkObjectResult(new Probe());
        (string UICulture, string Language) inFunction = default;

        await new RequestLocalizationMiddleware().Invoke(invocation.Context, context =>
        {
            inFunction = (CultureInfo.CurrentUICulture.Name, LocalizedTextJsonConverter.CurrentLanguage);
            context.GetInvocationResult().Value = result;
            return Task.CompletedTask;
        });

        // The header came from the HttpRequestData. Nothing writes the result after the middleware in this model.
        Assert.Equal(("ru-RU", "ru"), inFunction);
        Assert.Same(result, invocation.InvocationResult);
    }

    [Theory]
    [InlineData(null, "en", "en")]
    [InlineData("", "en", "en")]
    [InlineData("   ", "en", "en")]
    [InlineData("*", "en", "en")]
    [InlineData("ru;q=0.9", "ru", "ru")]
    [InlineData("ru-RU,ru;q=0.9,en;q=0.8", "ru-RU", "ru")]
    [InlineData("RU-ru", "ru-RU", "ru")]
    // The entry with the highest quality wins, not the first one. Quality 0 means "not this language".
    [InlineData("en;q=0.5, ru", "ru", "ru")]
    [InlineData("ru;q=0, en-GB", "en-GB", "en")]
    [InlineData("*, ru;q=0.5", "ru", "ru")]
    // No platform has data for the private-use region XX, so the language alone is used.
    [InlineData("ru-XX", "ru", "ru")]
    // No platform has data for the private-use language qaa.
    [InlineData("qaa", "en", "en")]
    [InlineData("not a language", "en", "en")]
    [InlineData("@@@", "en", "en")]
    [InlineData(";q=0.9", "en", "en")]
    [InlineData(",,,", "en", "en")]
    // A quality that cannot be read counts as 1.
    [InlineData("ru;q=abc", "ru", "ru")]
    // Only the first three entries are tried.
    [InlineData("qaa, qab, qac, ru", "en", "en")]
    public async Task Accept_language_resolves_without_throwing(string? acceptLanguage, string culture, string language)
    {
        var invocation = new HttpInvocation(acceptLanguage);
        (string Culture, string UICulture, string Language) inFunction = default;

        await new RequestLocalizationMiddleware().Invoke(invocation.Context, _ =>
        {
            inFunction = (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name, LocalizedTextJsonConverter.CurrentLanguage);
            return Task.CompletedTask;
        });

        Assert.Equal((culture, culture, language), inFunction);
    }

    [Fact]
    public void The_invariant_culture_takes_English_for_localized_text()
    {
        // What a request gets in globalization-invariant mode, where no other culture can be created.
        Assert.Equal("en", RequestLocalizationMiddleware.LanguageOf(CultureInfo.InvariantCulture));
    }

    private static void AssertResponse(JsonElement response, string title, string status, string culture)
    {
        Assert.Equal(
            (title, status, culture, culture),
            (response.GetProperty("Title").GetString(), response.GetProperty("Status").GetString(),
                response.GetProperty("Culture").GetString(), response.GetProperty("UICulture").GetString()));
    }

    // The flow that calls the middleware and writes the response. It runs with the invariant culture, so it differs
    // from every request culture in these tests, and it has no language of its own.
    private static void AssertOutside()
    {
        Assert.Equal(
            ("", "", LocalizedTextJsonConverter.UserLanguage),
            (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name, LocalizedTextJsonConverter.CurrentLanguage));
    }

    // Runs the test with the invariant culture, whatever the culture of the machine is, and puts the culture back.
    private sealed class InvariantCultureScope : IDisposable
    {
        private readonly CultureInfo culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;

        public InvariantCultureScope()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
