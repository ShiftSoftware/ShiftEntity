using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Functions.AppCheck;
using ShiftSoftware.ShiftEntity.Functions.Localization;
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
    /// <summary>
    /// 
    /// </summary>
    /// <param name="appCheckProjectNumber">Firebase Project Number</param>
    /// <param name="appCheckServiceAccount">a Google Cloud Service Account's Json Key as String with proper permssion for Firebase AppCheck API</param>
    public static IFunctionsWorkerApplicationBuilder AddFirebaseAppCheck(this IFunctionsWorkerApplicationBuilder builder, string appCheckProjectNumber,
        string appCheckServiceAccount, string hmsAppId, string hmsIssuer, string hmsKeyId, string hmsPrivateKey, string headerKey = "Verification-Token")
    {
        builder.Services.AddTransient<AntiBotService>();
        builder.Services.AddScoped(x => new AntiBotOptions
        {
            HeaderKey = headerKey,
            AppCheckProjectNumber = appCheckProjectNumber,
            AppCheckServiceAccount = appCheckServiceAccount,
            HMSAppId = hmsAppId,
            HMSIssuer = hmsIssuer,
            HMSKeyId = hmsKeyId,
            HMSPrivateKey = hmsPrivateKey
        });

        builder.UseWhen<AppCheckMiddleware>(context =>
            {
                bool hasCheckAppCheckAttribute = context.GetTargetFunctionMethod().CustomAttributes.FirstOrDefault(x => x.AttributeType.Name.Equals(nameof(CheckAppCheckAttribute))) != null;

                return context.FunctionDefinition.InputBindings.Any(binding => binding.Value.Type == "httpTrigger") && hasCheckAppCheckAttribute;
            }
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

    public static IFunctionsWorkerApplicationBuilder UseRequestLocalization(this IFunctionsWorkerApplicationBuilder builder)
    {

        builder.UseWhen<RequestLocalizationMiddleware>(context =>
            context.FunctionDefinition.InputBindings.Any(binding => binding.Value.Type == "httpTrigger")
        );

        return builder;
    }
}