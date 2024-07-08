using System.Net.Http.Json;

namespace ShiftSoftware.ShiftEntity.Functions.ReCaptcha;

public class GoogleReCaptchaService
{
    private readonly HttpClient httpClient;
    private readonly ReCaptchaOptions options;

    public GoogleReCaptchaService(HttpClient httpClient, ReCaptchaOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public virtual async Task<GoogleRcaptchaResponse?> VerifyAsync(string token)
    {
        var content = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("secret", options.SecretKey),
            new KeyValuePair<string, string>("response", token) }
        );

        var response = await httpClient.PostAsync($"https://www.google.com/recaptcha/api/siteverify", content);

        return await response.Content.ReadFromJsonAsync<GoogleRcaptchaResponse>();
    }
}