using Google.Apis.Auth.OAuth2;
using Google.Apis.Firebaseappcheck.v1beta;
using Google.Apis.Firebaseappcheck.v1beta.Data;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ShiftSoftware.ShiftEntity.Functions.AppCheck;

public class AntiBotService
{
    private readonly AntiBotOptions antiBotOptions;
    private readonly ILogger<AntiBotService> logger;

    public AntiBotService(AntiBotOptions antiBotOptions, ILogger<AntiBotService> logger)
    {
        this.antiBotOptions = antiBotOptions;
        this.logger = logger;
    }

    public async ValueTask<bool> IsItBot(string token, Platforms? platform = null)
    {
        if (platform == Platforms.Android || platform == Platforms.IOS)
        {
            GoogleCredential googleCredentials = GoogleCredential.FromJson(antiBotOptions.AppCheckServiceAccount).CreateScoped(new string[] { FirebaseappcheckService.Scope.Firebase });
            
            var appCheck = new FirebaseappcheckService(initializer: new()
            {
                HttpClientInitializer = googleCredentials,
            });

            try
            {
                var appCheckResult = await appCheck.Projects.VerifyAppCheckToken(body: new GoogleFirebaseAppcheckV1betaVerifyAppCheckTokenRequest
                {
                    AppCheckToken = token,

                }, project: $"projects/{antiBotOptions.AppCheckProjectNumber}").ExecuteAsync();
                if (appCheckResult != null)
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, $"App Check Token Verification Error, Token: {token}");
                return true;
            }

            return true;
        }
        else if (platform == Platforms.Huawei)
        {
            var accessToken = await GetHMSAccessToken();
            var client = new HttpClient();

            var response = await client.PostAsJsonAsync(
                $"https://hirms.cloud.huawei.asia//rms/v1/userRisks/verify?appId={antiBotOptions.HMSAppId}",
                new
                {
                    accessToken = accessToken["access_token"]!.ToString(),
                    response = token
                });
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var responseBody = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                if ((bool)responseBody["success"]! == true)
                {
                    return false;
                }
                return true;
            }
        }
        return true;
    }

    private async Task<JsonNode> GetHMSAccessToken()
    {
        using var client = new HttpClient();

        var authPayload = new List<KeyValuePair<string, string>>()
            {
                KeyValuePair.Create("grant_type", "client_credentials"),
                KeyValuePair.Create("client_id", antiBotOptions.HMSClientID),
                KeyValuePair.Create("client_secret", antiBotOptions.HMSClientSecret),
            };
        var encodedPaylod = new FormUrlEncodedContent(authPayload);
        encodedPaylod.Headers.ContentType = new("application/x-www-form-urlencoded");

        var authResponse = await client.PostAsync(
            "https://oauth-login.cloud.huawei.com/oauth2/v3/token",
           encodedPaylod
        );
        if (authResponse.StatusCode == HttpStatusCode.OK)
        {
            return JsonNode.Parse(await authResponse.Content.ReadAsStringAsync());
        }
        return null;
    }
}
