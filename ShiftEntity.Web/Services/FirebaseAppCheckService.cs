using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web.Services
{
    public class FirebaseAppCheckService
    {
        // The public endpoint where Google publishes the App Check public keys
        private const string JwksEndpoint = "https://firebaseappcheck.googleapis.com/v1beta/jwks";

        private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
        private readonly FirebaseAppCheckOptions options;

        public FirebaseAppCheckService(FirebaseAppCheckOptions options)
        {
            this.options = options;
            // This manager handles fetching and caching the public keys automatically
            var documentRetriever = new HttpDocumentRetriever { RequireHttps = true };
            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                JwksEndpoint,
                new OpenIdConnectConfigurationRetriever(),
                documentRetriever
            );
        }

        public async Task<bool> VerifyTokenAsync(string appCheckToken)
        {
            if (string.IsNullOrEmpty(appCheckToken)) return false;

            try
            {
                // 1. Fetch the cached public keys from Google
                var discoveryDocument = await _configurationManager.GetConfigurationAsync(CancellationToken.None);
                var signingKeys = discoveryDocument.SigningKeys;

                var validationParameters = new TokenValidationParameters
                {
                    // 2. Verify the token's cryptographic signature
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = signingKeys,

                    // 3. Verify the Issuer (must match your project number)
                    ValidateIssuer = true,
                    ValidIssuer = $"https://firebaseappcheck.googleapis.com/{ProjectNumber}",

                    // 4. Verify the Audience (must match your project number)
                    ValidateAudience = true,
                    ValidAudience = $"projects/{ProjectNumber}",

                    // 5. Verify the token hasn't expired
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                var tokenHandler = new JwtSecurityTokenHandler();

                // This will throw an exception if the signature, issuer, audience, or lifetime is invalid
                var principal = tokenHandler.ValidateToken(appCheckToken, validationParameters, out var validatedToken);

                // Optional: If you want to restrict requests to a specific App ID (e.g., just your iOS app)
                // var appId = principal.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                // if (appId != "YOUR_APP_ID") return false;

                return true;
            }
            catch (SecurityTokenException ex)
            {
                // Token is invalid (expired, tampered with, wrong project, etc.)
                Console.WriteLine($"App Check token validation failed: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during verification: {ex.Message}");
                return false;
            }
        }
    }

    public class FirebaseAppCheckOptions
    {
        public string ProjectNumber { get; set; } = default!;
        public string ServiceAccount { get; set; } = default!;
    }
}
