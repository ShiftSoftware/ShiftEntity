using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Azure.Functions.Worker;
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
                string cultureCode = req.Headers.GetValues("Accept-Language")?.FirstOrDefault()?.Split(',')?.FirstOrDefault() ?? "en";

                var culture = new CultureInfo(cultureCode);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }

            await next(context);
        }
    }
}
