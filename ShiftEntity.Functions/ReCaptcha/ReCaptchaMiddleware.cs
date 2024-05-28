using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using ShiftSoftware.ShiftEntity.Functions.Utilities;
using System.Net;

namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

internal class ReCaptchaMiddleware : IFunctionsWorkerMiddleware
{
    private readonly GoogleReCaptchaService googleReCaptchaService;
    private readonly ReCaptchaOptions options;

    public ReCaptchaMiddleware(GoogleReCaptchaService googleReCaptchaService, ReCaptchaOptions options)
    {
        this.googleReCaptchaService = googleReCaptchaService;
        this.options = options;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var methodInfo = context.GetTargetFunctionMethod();

        var attribute = AttributeUtility.GetAttribute<CheckReCaptchaAttribute>(methodInfo);

        if (attribute is null)
        {
            await next(context);
        }
        else
        {
            var request = await context.GetHttpRequestDataAsync();

            var reCaptchaToken = request?
                .Headers?
                .LastOrDefault(x => x.Key.ToLower().Equals(options.HeaderKey, StringComparison.InvariantCultureIgnoreCase))
                .Value?
                .LastOrDefault();

            if (string.IsNullOrWhiteSpace(reCaptchaToken))
            {
                var response = request?.CreateResponse(HttpStatusCode.Unauthorized);
                context.GetInvocationResult().Value = response;
                return;
            }

            var minScopre = attribute.HasValue ? attribute.Value.attribute?.MinScore ?? options.MinScore : options.MinScore;

            var reCaptchaResponse = await googleReCaptchaService.VerifyAsync(reCaptchaToken);

            if (reCaptchaResponse is null || reCaptchaResponse.Score < minScopre || !reCaptchaResponse.Success)
            {
                var response = request?.CreateResponse(HttpStatusCode.Forbidden);
                context.GetInvocationResult().Value = response;
                return;
            }

            await next(context);
        }
    }
}