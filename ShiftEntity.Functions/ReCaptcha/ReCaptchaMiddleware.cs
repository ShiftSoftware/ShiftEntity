﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
            var httpContext = context.GetHttpContext()!;

            var reCaptchaToken = httpContext.Request.Headers
                .LastOrDefault(x => x.Key.ToLower().Equals(options.HeaderKey, StringComparison.InvariantCultureIgnoreCase))
                .Value.LastOrDefault();

            if (string.IsNullOrWhiteSpace(reCaptchaToken))
            {
                await new UnauthorizedResult().ExecuteResultAsync(new ActionContext
                {
                    HttpContext = httpContext
                });
                return;
            }

            var minScopre = attribute.HasValue ? attribute.Value.attribute?.MinScore ?? options.MinScore : options.MinScore;

            var reCaptchaResponse = await googleReCaptchaService.VerifyAsync(reCaptchaToken);

            if (reCaptchaResponse is null || reCaptchaResponse.Score < minScopre || !reCaptchaResponse.Success)
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