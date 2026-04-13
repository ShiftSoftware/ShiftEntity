using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using ShiftSoftware.ShiftEntity.Functions.Services.Attestation;
using ShiftSoftware.ShiftEntity.Functions.Utilities;

namespace ShiftSoftware.ShiftEntity.Functions.AttestationVerification
{
    internal class AttestationMiddleware : IFunctionsWorkerMiddleware
    {
        async Task IFunctionsWorkerMiddleware.Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var methodInfo = context.GetTargetFunctionMethod();
            var attribute = AttributeUtility.GetAttribute<ValidateAttestationAttribute>(methodInfo);

            if (attribute is null)
            {
                await next(context);
            }
            else
            {
                bool withReplayProtection = attribute.Value.attribute?.WithReplayProtection ?? false;

                var httpContext = context.GetHttpContext()!;

                var serviceProvider = context.InstanceServices; 

                var attestationService = serviceProvider.GetRequiredService<IUnifiedAttestationService>();
                var attestationOptions = serviceProvider.GetRequiredService<AttestationOptions>();

                var verificationToken = httpContext.Request.Headers
                    .LastOrDefault(x => x.Key.ToLower().Equals(attestationOptions.HeaderKey, StringComparison.InvariantCultureIgnoreCase))
                    .Value.LastOrDefault();

                var platformHeader = httpContext.Request.Headers.LastOrDefault(x => x.Key.ToLower().Equals("Platform", StringComparison.InvariantCultureIgnoreCase)).Value.LastOrDefault();

                if (string.IsNullOrWhiteSpace(verificationToken) || string.IsNullOrWhiteSpace(platformHeader))
                {
                    await new UnauthorizedResult().ExecuteResultAsync(new ActionContext
                    {
                        HttpContext = httpContext
                    });
                    return;
                }
                AttestationPlatform platform;

                Enum.TryParse(platformHeader, out platform);

                var validToken = await attestationService.VerifyTokenAsync(verificationToken, platform, withReplayProtection);

                if (!validToken)
                {
                    await new StatusCodeResult(StatusCodes.Status403Forbidden).ExecuteResultAsync(new ActionContext
                    {
                        HttpContext = httpContext
                    });
                    return;
                }

                await next(context);
            }
        }
    }
}
