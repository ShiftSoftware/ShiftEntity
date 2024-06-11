using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Functions.ModelValidation;
using ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

namespace ShiftSoftware.ShiftEntity.Functions.Extensions;

public static class IFunctionsWorkerApplicationBuilderExtension
{

    public static IFunctionsWorkerApplicationBuilder AddGoogleReCaptcha(this IFunctionsWorkerApplicationBuilder builder,
        string secretKey, double minScopre = 0.5, string headerKey = "Recaptcha-Token")
    {
        builder.Services.AddTransient<GoogleReCaptchaService>();
        builder.Services.AddScoped(x => new ReCaptchaOptions
        {
            HeaderKey = headerKey,
            SecretKey = secretKey,
            MinScore = minScopre,

        });

        builder.UseWhen<ReCaptchaMiddleware>(context =>
            context.FunctionDefinition.InputBindings.Any(binding => binding.Value.Type == "httpTrigger")
        );

        return builder;
    }

    public static IFunctionsWorkerApplicationBuilder RequireValidModels(this IFunctionsWorkerApplicationBuilder builder,
        bool wrapValidationErrorResponseWithShiftEntityResponse = false)
    {
        builder.Services.AddScoped(x => new ModelValidatorOptions
        {
            WrapValidationErrorResponseWithShiftEntityResponse = wrapValidationErrorResponseWithShiftEntityResponse
        });

        builder.UseWhen<ModelValidationMiddleware>(context =>
            context.FunctionDefinition.InputBindings.Any(binding => binding.Value.Type == "httpTrigger")
        );

        return builder;
    }
}