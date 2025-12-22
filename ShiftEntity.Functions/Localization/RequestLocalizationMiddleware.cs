using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Azure.Functions.Worker;
using ShiftSoftware.ShiftEntity.Model;
using System.Globalization;


namespace ShiftSoftware.ShiftEntity.Functions.Localization
{
    public class RequestLocalizationMiddleware : IFunctionsWorkerMiddleware
    {
        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var req = await context.GetHttpRequestDataAsync();

            if (req != null)
            {
                IEnumerable<string>? langHeader;
                req.Headers.TryGetValues("Accept-Language", out langHeader);

                string cultureCode = "en";
                if (langHeader is not null && langHeader.Any())
                {
                    // Default to English if no Accept-Language header is present
                    cultureCode = langHeader?.FirstOrDefault()?.Split(',')?.FirstOrDefault() ?? "en";
                }

                var culture = new CultureInfo(cultureCode);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                LocalizedTextJsonConverter.UserLanguage = culture.TwoLetterISOLanguageName;
            }

            await next(context);
        }
    }
}
