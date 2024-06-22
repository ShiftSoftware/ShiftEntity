using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShiftSoftware.ShiftEntity.Functions.AppCheck;
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

    public static IFunctionsWorkerApplicationBuilder AddFirebaseAppCheck(this IFunctionsWorkerApplicationBuilder builder, string appCheckProjectNumber,
        string appCheckServiceAccountProjectId, string appCheckServiceAccountPrivateKey, string appCheckServiceAccountPrivateKeyId, string appCheckServiceAccountClientEmail, string appCheckServiceAccountClientId, string hmsClientID, string hmsClientSecret, string hmsAppId, string headerKey = "Verification-Token")
    {
        builder.Services.AddTransient<AntiBotService>();
        builder.Services.AddScoped(x => new AntiBotOptions
        {
            HeaderKey = headerKey,
            AppCheckProjectNumber = appCheckProjectNumber,
            AppCheckServiceAccountClientEmail = appCheckServiceAccountClientEmail,
            AppCheckServiceAccountClientId = appCheckServiceAccountClientId,
            AppCheckServiceAccountPrivateKey = appCheckServiceAccountPrivateKey,
            AppCheckServiceAccountPrivateKeyId = appCheckServiceAccountPrivateKeyId,
            AppCheckServiceAccountProjectId = appCheckServiceAccountProjectId,
            HMSClientID = hmsClientID,
            HMSClientSecret = hmsClientSecret,
            HMSAppId = hmsAppId,
        });

        builder.UseWhen<AppCheckMiddleware>(context =>
            {
                /*bool hasCustomerAuthorizeAttribute = context.GetTargetFunctionMethod().CustomAttributes.FirstOrDefault(x => x.AttributeType.Name == "CustomerAuthorizeAttribute") != null;

                bool hasAuhorizationHeader = false;
                if (hasCustomerAuthorizeAttribute)
                {
                    string auhorizationHeader = context.GetHttpContext()!.Request.Headers.Authorization.FirstOrDefault()!;
                    hasAuhorizationHeader = !string.IsNullOrEmpty(auhorizationHeader) && !string.IsNullOrWhiteSpace(auhorizationHeader);
                }*/

                return context.FunctionDefinition.InputBindings.Any(binding => binding.Value.Type == "httpTrigger")/* &&
                 hasAuhorizationHeader == false*/;
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
}