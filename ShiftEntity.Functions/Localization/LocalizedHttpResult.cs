using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShiftSoftware.ShiftEntity.Model;
using System.Globalization;

namespace ShiftSoftware.ShiftEntity.Functions.Localization;

/// <summary>
/// Wraps an HTTP result so that it is written with the culture and language of the request it answers. See
/// <see cref="RequestLocalizationMiddleware"/>.
/// </summary>
internal static class LocalizedHttpResult
{
    /// <summary>
    /// The wrapper for an <see cref="IActionResult"/> or an <see cref="IResult"/>. null for any other value, and for a
    /// result that is wrapped already.
    /// </summary>
    internal static object? Wrap(object? result, CultureInfo culture, string language)
    {
        return result switch
        {
            LocalizedActionResult or LocalizedResult => null,
            IActionResult actionResult => new LocalizedActionResult(actionResult, culture, language),
            IResult httpResult => new LocalizedResult(httpResult, culture, language),
            _ => null,
        };
    }

    /// <summary>
    /// Sets the culture and the language for the calling method and for what it awaits. The returned object removes
    /// the language again. The wrappers call this from an async method, so the method that runs a wrapper keeps its
    /// own values.
    /// </summary>
    internal static IDisposable Apply(CultureInfo culture, string language)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        return LocalizedTextJsonConverter.UseLanguage(language);
    }
}

/// <summary>
/// Writes an <see cref="IActionResult"/> with the culture and language of the request it answers.
/// </summary>
/// <remarks>
/// After the response is written, the worker also serializes the result into its reply to the Functions host. This
/// class has no public properties, so that copy is an empty object and not the response body a second time.
/// </remarks>
internal sealed class LocalizedActionResult : IActionResult
{
    private readonly IActionResult result;
    private readonly CultureInfo culture;
    private readonly string language;

    public LocalizedActionResult(IActionResult result, CultureInfo culture, string language)
    {
        this.result = result;
        this.culture = culture;
        this.language = language;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        using (LocalizedHttpResult.Apply(culture, language))
        {
            await result.ExecuteResultAsync(context);
        }
    }
}

/// <summary>
/// Writes an <see cref="IResult"/> with the culture and language of the request it answers.
/// </summary>
/// <remarks>
/// After the response is written, the worker also serializes the result into its reply to the Functions host. This
/// class has no public properties, so that copy is an empty object and not the response body a second time.
/// </remarks>
internal sealed class LocalizedResult : IResult
{
    private readonly IResult result;
    private readonly CultureInfo culture;
    private readonly string language;

    public LocalizedResult(IResult result, CultureInfo culture, string language)
    {
        this.result = result;
        this.culture = culture;
        this.language = language;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        using (LocalizedHttpResult.Apply(culture, language))
        {
            await result.ExecuteAsync(httpContext);
        }
    }
}
