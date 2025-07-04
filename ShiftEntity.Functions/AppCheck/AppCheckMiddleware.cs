﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using ShiftSoftware.ShiftEntity.Functions.Utilities;

namespace ShiftSoftware.ShiftEntity.Functions.AppCheck
{
    internal class AppCheckMiddleware : IFunctionsWorkerMiddleware
    {
        async Task IFunctionsWorkerMiddleware.Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var methodInfo = context.GetTargetFunctionMethod();
            var attribute = AttributeUtility.GetAttribute<CheckAppCheckAttribute>(methodInfo);

            if (attribute is null)
            {
                await next(context);
            }
            else
            {
                var httpContext = context.GetHttpContext()!;

                var serviceProvider = context.InstanceServices;

                var antiBotService = serviceProvider.GetRequiredService<AntiBotService>();
                var options = serviceProvider.GetRequiredService<AntiBotOptions>();

                var verificationToken = httpContext.Request.Headers
                    .LastOrDefault(x => x.Key.ToLower().Equals(options.HeaderKey, StringComparison.InvariantCultureIgnoreCase))
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
                Platforms platform;

                Enum.TryParse(platformHeader, out platform);

                var isBot = await antiBotService.IsItBot(verificationToken, platform);

                if (isBot)
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
