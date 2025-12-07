using Google.Apis.Auth.OAuth2;
using Google.Apis.Firebaseappcheck.v1beta;
using Google.Apis.Firebaseappcheck.v1beta.Data;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ShiftSoftware.ShiftEntity.Functions.AppCheck;

public class AntiBotService
{
    private readonly AntiBotOptions antiBotOptions;
    private readonly ILogger<AntiBotService> logger;
    private readonly IHttpClientFactory httpClientFactory;

    public AntiBotService(AntiBotOptions antiBotOptions, ILogger<AntiBotService> logger, IHttpClientFactory httpClientFactory)
    {
        this.antiBotOptions = antiBotOptions;
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
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
            var accessToken = GetHMSAccessToken();
            var client = httpClientFactory.CreateClient();
            try
            {
                var response = await client.PostAsJsonAsync(
                    $"https://hirms.cloud.huawei.eu/rms/v1/userRisks/verify?appId={antiBotOptions.HMSAppId}",
                    new HMSUserDetectRequestBody
                    {
                        AccessToken = accessToken,
                        Response = token
                    }
                );

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var userDetectResult = await response.Content.ReadFromJsonAsync<HMSUserDetectResponseBody>();
                    if (userDetectResult is not null && userDetectResult.Success)
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HMS Bot Detection Failed");
                return true;
            }

        }
        return true;
    }

    private string GetHMSAccessToken()
    {

        using var rsa = RSA.Create();
        var keyBytes = Convert.FromBase64String(antiBotOptions.HMSPrivateKey);
        rsa.ImportPkcs8PrivateKey(keyBytes, out _);

        var signingCredentials = new SigningCredentials(
            new RsaSecurityKey(rsa)
            {
                KeyId = antiBotOptions.HMSKeyId
            },
            SecurityAlgorithms.RsaSha256);

        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + 3600;

        var header = new JwtHeader(signingCredentials);
        header["kid"] = antiBotOptions.HMSKeyId; // ensure kid is in header

        var payload = new JwtPayload
        {
            { "iss", antiBotOptions.HMSIssuer },
            { "aud", "https://oauth-login.cloud.huawei.com/oauth2/v3/token" },
            { "iat", iat },
            { "exp", exp }
        };

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
