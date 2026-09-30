using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Azure.Functions.Worker;
using ShiftSoftware.ShiftEntity.Model;
using System.Globalization;


namespace ShiftSoftware.ShiftEntity.Functions.Localization
{
    /// <summary>
    /// Applies the language a request asks for in its Accept-Language header. It sets
    /// <see cref="CultureInfo.CurrentCulture"/>, <see cref="CultureInfo.CurrentUICulture"/> and the language of
    /// <see cref="LocalizedTextJsonConverter"/> for the function and for the writing of its response.
    /// </summary>
    /// <remarks>
    /// Nothing process-wide is set. The three values belong to the async flow of the request, so concurrent requests
    /// do not see each other's language.
    /// <para>
    /// With the ASP.NET Core integration, the worker writes an <see cref="IActionResult"/> or an <see cref="IResult"/>
    /// in its own middleware. That middleware runs before this one, and it writes the result only after this one has
    /// returned, where the values set here no longer apply. So this middleware wraps the result, and the wrapper sets
    /// the same values again while the result is written. This covers the value a function returns, and the
    /// <c>[HttpResult]</c> property of a return type with several outputs.
    /// </para>
    /// </remarks>
    public class RequestLocalizationMiddleware : IFunctionsWorkerMiddleware
    {
        private const string DefaultLanguage = "en";

        // Only the first entries of Accept-Language are tried, as in ASP.NET Core, so a long header cannot make each
        // request try many cultures.
        private const int MaxLanguagesToTry = 3;

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var httpContext = context.GetHttpContext();

            string? acceptLanguage;

            if (httpContext is not null)
            {
                acceptLanguage = httpContext.Request.Headers.AcceptLanguage;
            }
            else
            {
                // The built-in HTTP model has no HttpContext.
                var request = await context.GetHttpRequestDataAsync();

                if (request is null)
                {
                    await next(context);
                    return;
                }

                acceptLanguage = request.Headers.TryGetValues("Accept-Language", out var values)
                    ? string.Join(",", values)
                    : null;
            }

            var culture = ResolveCulture(acceptLanguage);
            var language = LanguageOf(culture);

            // This method is async, so the values apply to the function and to everything this method awaits. The
            // caller of this method keeps its own values.
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            using (LocalizedTextJsonConverter.UseLanguage(language))
            {
                await next(context);
            }

            // In the built-in HTTP model the function writes its HttpResponseData itself, with the values set above.
            if (httpContext is not null)
                LocalizeHttpResult(context, culture, language);
        }

        /// <summary>
        /// The culture for an Accept-Language value, else English. The header carries whatever the client sends: a
        /// list with quality values ("ru-RU,ru;q=0.9,en;q=0.8"), a wildcard, or junk. A culture constructed from it
        /// without these checks threw, and the request failed.
        /// </summary>
        /// <remarks>
        /// The entries are tried by quality, highest first. Entries with the same quality keep their order. The
        /// wildcard and entries with quality 0 are skipped. Only cultures the platform has data for are used. When it
        /// has no data for a language and region, the language alone is tried ("ru-XX" gives "ru"). So a client
        /// cannot make the process create and keep a new culture for every name it sends.
        /// </remarks>
        internal static CultureInfo ResolveCulture(string? acceptLanguage)
        {
            if (!string.IsNullOrWhiteSpace(acceptLanguage))
            {
                foreach (var tag in LanguageTags(acceptLanguage))
                {
                    var culture = FindCulture(tag);

                    if (culture is not null)
                        return culture;
                }
            }

            // In globalization-invariant mode no culture can be created, not even English.
            return FindCulture(DefaultLanguage) ?? CultureInfo.InvariantCulture;
        }

        /// <summary>
        /// The language that <see cref="LocalizedTextJsonConverter"/> uses for <paramref name="culture"/>: its
        /// two-letter language name, or English for the invariant culture.
        /// </summary>
        internal static string LanguageOf(CultureInfo culture)
        {
            return culture.Name.Length == 0 ? DefaultLanguage : culture.TwoLetterISOLanguageName;
        }

        // Wraps the result that the ASP.NET Core integration writes. It looks where the integration looks, in the same
        // order: first the value the function returned, then the output binding of type "http". For a return type with
        // several outputs, the worker has moved the [HttpResult] property into that binding before this runs.
        private static void LocalizeHttpResult(FunctionContext context, CultureInfo culture, string language)
        {
            var invocationResult = context.GetInvocationResult();

            if (LocalizedHttpResult.Wrap(invocationResult.Value, culture, language) is { } localizedResult)
            {
                invocationResult.Value = localizedResult;
                return;
            }

            var httpOutput = context.GetOutputBindings<object>()
                .FirstOrDefault(binding => string.Equals(binding.BindingType, "http", StringComparison.OrdinalIgnoreCase));

            if (httpOutput is not null && LocalizedHttpResult.Wrap(httpOutput.Value, culture, language) is { } localizedOutput)
                httpOutput.Value = localizedOutput;
        }

        // The language tags of an Accept-Language value, highest quality first.
        private static IEnumerable<string> LanguageTags(string acceptLanguage)
        {
            return acceptLanguage
                .Split(',', MaxLanguagesToTry + 1)
                .Take(MaxLanguagesToTry)
                .Select(ParseEntry)
                .Where(entry => entry.Quality > 0 && entry.Tag.Length > 0 && entry.Tag != "*")
                // OrderByDescending is a stable sort, so entries with the same quality keep their order.
                .OrderByDescending(entry => entry.Quality)
                .Select(entry => entry.Tag);
        }

        // "ru;q=0.9" gives ("ru", 0.9). A missing or unreadable quality counts as 1.
        private static (string Tag, double Quality) ParseEntry(string entry)
        {
            var parts = entry.Split(';');
            var quality = 1d;

            foreach (var parameter in parts.Skip(1))
            {
                var trimmed = parameter.Trim();

                if (trimmed.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(trimmed.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                {
                    quality = value;
                }
            }

            return (parts[0].Trim(), quality);
        }

        // The culture for a tag when the platform has data for it, else the culture for the language of the tag
        // ("ru-XX" gives "ru"), else null.
        private static CultureInfo? FindCulture(string tag)
        {
            var culture = PredefinedCulture(tag);

            var dash = tag.IndexOf('-');

            if (culture is null && dash > 0)
                culture = PredefinedCulture(tag.Substring(0, dash));

            return culture;
        }

        private static CultureInfo? PredefinedCulture(string name)
        {
            try
            {
                // predefinedOnly refuses a name the platform has no data for, such as "xx". Without it,
                // GetCultureInfo makes up a culture for such a name and keeps it for the life of the process.
                return CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }
    }
}
