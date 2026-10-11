using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShiftSoftware.ShiftEntity.Functions.Extensions;
using ShiftSoftware.ShiftEntity.Functions.Utilities;

namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

internal class ReCaptchaMiddleware : IFunctionsWorkerMiddleware
{
    private enum ReCaptchaOutcome
    {
        Passed,
        MissingToken,
        NoResponse,
        VerificationFailed,
        ScoreTooLow,
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var attribute = AttributeUtility.GetAttribute<CheckReCaptchaAttribute>(context.GetTargetFunctionMethod());

        if (attribute is null)
        {
            await next(context);
            return;
        }

        var httpContext = context.GetHttpContext()!;
        var serviceProvider = context.InstanceServices;

        var options = serviceProvider.GetRequiredService<ReCaptchaOptions>();
        var functionName = context.FunctionDefinition.Name;

        var minScore = attribute.Value.attribute?.MinScore ?? options.MinScore;

        var logger = context.GetLogger<ReCaptchaMiddleware>();

        // Logging the verification result is opt-in per function through CheckReCaptchaAttribute.LogResult.
        var logResult = attribute.Value.attribute?.LogResult ?? false;

        // Request headers are case-insensitive, so this matches the header key in any casing.
        httpContext.Request.Headers.TryGetValue(options.HeaderKey, out var tokenValues);
        var reCaptchaToken = tokenValues.LastOrDefault();

        if (string.IsNullOrWhiteSpace(reCaptchaToken))
        {
            if (logResult)
                LogResult(logger, ReCaptchaOutcome.MissingToken, functionName, minScore, null);
            await WriteStatusCodeAsync(httpContext, StatusCodes.Status401Unauthorized);
            return;
        }

        var googleReCaptchaService = serviceProvider.GetRequiredService<GoogleReCaptchaService>();

        GoogleRcaptchaResponse? reCaptchaResponse;

        try
        {
            reCaptchaResponse = await googleReCaptchaService.VerifyAsync(reCaptchaToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReCaptcha verification for {FunctionName} threw an exception.", functionName);
            throw;
        }

        var outcome = Evaluate(reCaptchaResponse, minScore);

        if (logResult)
            LogResult(logger, outcome, functionName, minScore, reCaptchaResponse);

        if (outcome != ReCaptchaOutcome.Passed)
        {
            await WriteStatusCodeAsync(httpContext, StatusCodes.Status403Forbidden);
            return;
        }

        await next(context);
    }

    private static ReCaptchaOutcome Evaluate(GoogleRcaptchaResponse? response, double minScore)
    {
        if (response is null)
            return ReCaptchaOutcome.NoResponse;

        if (!response.Success)
            return ReCaptchaOutcome.VerificationFailed;

        if (response.Score < minScore)
            return ReCaptchaOutcome.ScoreTooLow;

        return ReCaptchaOutcome.Passed;
    }

    private static void LogResult(ILogger logger, ReCaptchaOutcome outcome, string functionName, double minScore, GoogleRcaptchaResponse? response)
    {
        var level = outcome == ReCaptchaOutcome.Passed ? LogLevel.Information : LogLevel.Warning;

        if (!logger.IsEnabled(level))
            return;

        logger.Log(
            level,
            "ReCaptcha {ReCaptchaOutcome} for {FunctionName}. Success: {ReCaptchaSuccess}, Score: {ReCaptchaScore}, MinScore: {ReCaptchaMinScore}, " +
            "Action: {ReCaptchaAction}, HostName: {ReCaptchaHostName}, ChallengeTimestamp: {ReCaptchaChallengeTimestamp}, ErrorCodes: {ReCaptchaErrorCodes}",
            outcome,
            functionName,
            response?.Success,
            response?.Score,
            minScore,
            response?.Action,
            response?.HostName,
            response?.Challenge_Ts,
            response?.ErrorCodes is { Length: > 0 } errorCodes ? string.Join(", ", errorCodes) : null);
    }

    private static Task WriteStatusCodeAsync(HttpContext httpContext, int statusCode)
    {
        return new StatusCodeResult(statusCode).ExecuteResultAsync(new ActionContext
        {
            HttpContext = httpContext
        });
    }
}
