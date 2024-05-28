using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        builder.UseMiddleware<ReCaptchaMiddleware>();

        return builder;
    }
}