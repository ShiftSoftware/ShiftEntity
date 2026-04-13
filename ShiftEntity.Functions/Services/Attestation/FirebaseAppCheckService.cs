using Azure.Security.KeyVault.Certificates;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Firebaseappcheck.v1beta;
using Google.Apis.Firebaseappcheck.v1beta.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Functions.Services.Attestation
{
    public class FirebaseAppCheckService
    {
        // The public endpoint where Google publishes the App Check public keys
        private const string JwksEndpoint = "https://firebaseappcheck.googleapis.com/v1beta/jwks";
        private const string JWKSCacheKey = "APPCHECK_JWKS";

        private readonly string firebaseProject;
        private readonly ILogger<FirebaseAppCheckService> logger;
        private readonly FirebaseappcheckService appCheckService;
        private readonly FirebaseAppCheckOptions appCheckOptions;
        private readonly HttpClient httpClient;
        private readonly IMemoryCache memoryCache;

        public FirebaseAppCheckService(ILogger<FirebaseAppCheckService> logger,
            IOptions<FirebaseAppCheckOptions> options,
            CertificateClient certificateClient,
            IHttpClientFactory httpClientFactory,
            IMemoryCache memoryCache)
        {
            this.appCheckOptions = options.Value;
            this.logger = logger;
            this.httpClient = httpClientFactory.CreateClient(nameof(FirebaseAppCheckService));
            this.memoryCache = memoryCache;

            ArgumentNullException.ThrowIfNull(appCheckOptions);
            ArgumentException.ThrowIfNullOrWhiteSpace(appCheckOptions.FirebaseProjectNumber, nameof(appCheckOptions.FirebaseProjectNumber));
            ArgumentException.ThrowIfNullOrWhiteSpace(appCheckOptions.ServiceAccountEmail, nameof(appCheckOptions.ServiceAccountEmail));
            ArgumentException.ThrowIfNullOrWhiteSpace(appCheckOptions.ServiceAccountKeyVaultCertificate, nameof(appCheckOptions.ServiceAccountKeyVaultCertificate));


            // Downloads certificate WITH private key (requires both certificates/get and secrets/get permissions)
            X509Certificate2 certificate = certificateClient.DownloadCertificate(appCheckOptions.ServiceAccountKeyVaultCertificate);

            ServiceAccountCredential credential = new ServiceAccountCredential(
               new ServiceAccountCredential.Initializer(appCheckOptions.ServiceAccountEmail)
               {
                   Scopes = new[] { FirebaseappcheckService.Scope.Firebase }
               }.FromCertificate(certificate));

            // 2. Initialize the v1beta Firebase App Check Service
            appCheckService = new FirebaseappcheckService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
            });

            firebaseProject = $"projects/{appCheckOptions.FirebaseProjectNumber}";
        }

        /// <summary>
        /// Cryptographically verifies a Firebase App Check token locally using cached Google public keys. 
        /// This method is highly performant (no network latency) but does NOT prevent token replay attacks.
        /// </summary>
        /// <param name="appCheckToken">The raw Firebase App Check token (JWT) from the client.</param>
        /// <returns>True if the token's signature, issuer, audience, and lifetime are cryptographically valid; otherwise, false.</returns>
        public async Task<bool> VerifyTokenAsync(string appCheckToken)
        {
            if (string.IsNullOrEmpty(appCheckToken)) return false;

            var validationParameters = new TokenValidationParameters
            {
                // 2. Verify the token's cryptographic signature
                ValidateIssuerSigningKey = true,

                // 3. Verify the Issuer (must match your project number)
                ValidateIssuer = true,
                ValidIssuer = $"https://firebaseappcheck.googleapis.com/{appCheckOptions.FirebaseProjectNumber}",

                // 4. Verify the Audience (must match your project number)
                ValidateAudience = true,
                ValidAudience = firebaseProject,

                // 5. Verify the token hasn't expired
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            try
            {
                // 1. Fetch the cached public keys from Google
                var jsonWebKeySet = await GetOrRefreshJwksAsync();

                var signingKeys = jsonWebKeySet!.GetSigningKeys();

                validationParameters.IssuerSigningKeys = signingKeys;

                // This will throw an exception if the signature, issuer, audience, or lifetime is invalid
                var principal = tokenHandler.ValidateToken(appCheckToken, validationParameters, out var validatedToken);

                // Optional: If you want to restrict requests to a specific App ID (e.g., just your iOS app)
                // var appId = principal.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
                // if (appId != "YOUR_APP_ID") return false;

                return true;
            }
            catch (SecurityTokenSignatureKeyNotFoundException )
            {
                try
                {
                    var jsonWebKeySet = await GetOrRefreshJwksAsync(true);

                    validationParameters.IssuerSigningKeys = jsonWebKeySet!.GetSigningKeys();

                    // Retry once with fresh keys
                    var principal = tokenHandler.ValidateToken(appCheckToken, validationParameters, out var validatedToken);

                    return true;
                }
                catch (Exception){}
                return false;
            }
            catch (SecurityTokenException ex)
            {
                // Token is invalid (expired, tampered with, wrong project, etc.)
                logger.LogError(ex, $"App Check token validation failed");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"An error occurred during verification");
                return false;
            }
        }

        // <summary>
        /// Verifies a Firebase App Check token by making an authenticated network request to Google's API. 
        /// This strictly prevents Replay Attacks by consuming the token, meaning the same token cannot be used twice.
        /// </summary>
        /// <param name="appCheckToken">The raw Firebase App Check token from the client.</param>
        /// <returns>True if the token is mathematically valid AND has not been previously consumed; otherwise, false.</returns>
        public async Task<bool> VerifyTokenWithReplayProtectionAsync(string appCheckToken)
        {
            if(!(await VerifyTokenAsync(appCheckToken))) return false;

            try
            {
                // Prepare the request body
                var requestBody = new GoogleFirebaseAppcheckV1betaVerifyAppCheckTokenRequest
                {
                    AppCheckToken = appCheckToken,
                };

                // Create the network request targeting the tokens:verify endpoint
                var request = appCheckService.Projects.VerifyAppCheckToken(requestBody, firebaseProject);

                // Execute the request to Google
                var response = await request.ExecuteAsync();

                // 3. Evaluate Replay Protection
                // If it's the FIRST time Google has seen this token, AlreadyConsumed will be null or false.
                if (response.AlreadyConsumed == true)
                {
                    logger.LogInformation("REPLAY DETECTED: This token has already been consumed!");
                    return false;
                }

                // The token is valid, and Google has now marked it as consumed for future requests.
                return true;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // An HTTP 403 means the token itself is fundamentally invalid 
                // (e.g., bad signature, expired, or belongs to a different project).
                logger.LogError(ex, "Token verification failed: Invalid or expired token.");
                return false;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // An HTTP 400 means the attestation provider used for this token 
                // does not currently support Replay Protection via this endpoint.
                logger.LogError(ex, "Token verification failed: Unsupported attestation provider.");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A network or server error occurred");
                return false;
            }
        }

        // <summary>
        /// Gets the JSON Web Key Set (JWKS) from Memory cache if available, otherwise from Google's public endpoint, which contains the public keys used to verify Firebase App Check tokens.
        /// </summary>
        private async Task<JsonWebKeySet> GetOrRefreshJwksAsync(bool forceRefresh = false)
        {
            try
            {
                if (forceRefresh)
                    memoryCache.Remove(JWKSCacheKey);

                var jsonWebKeySet = await memoryCache.GetOrCreateAsync(JWKSCacheKey, async entry =>
                {
                    var jwksJson = await httpClient.GetStringAsync(JwksEndpoint);

                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(5.7);

                    return new JsonWebKeySet(jwksJson);
                });

                return jsonWebKeySet!;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch JWKS from Google and Memory Cache.");
                throw;
            }
        }
    }


    public class FirebaseAppCheckOptions
    {
        /// <summary>
        /// Gets or sets the Firebase project number used to identify the Firebase project.
        /// This value is used to construct the issuer and audience claims during App Check token validation.
        /// </summary>
        /// <remarks>
        /// This must match the numeric project number (not the project ID) found in the Firebase console
        /// under <c>Project Settings &gt; General</c>. It is used to build the <c>projects/{number}</c>
        /// resource path required by the Firebase App Check API.
        /// </remarks>
        public string FirebaseProjectNumber { get; set; } = default!;

        /// <summary>
        /// Gets or sets the email address of the Firebase service account used to authenticate with the Firebase App Check API.
        /// </summary>
        /// <remarks>
        /// This is the <c>client_email</c> field from the Firebase service account JSON key file,
        /// typically in the format <c>{account-name}@{project-id}.iam.gserviceaccount.com</c>.
        /// It is used together with <see cref="ServiceAccountKeyVaultCertificate"/> to create
        /// the <see cref="Google.Apis.Auth.OAuth2.ServiceAccountCredential"/> for API authentication.
        /// </remarks>
        public string ServiceAccountEmail { get; set; } = default!;
        /// <summary>
        /// Gets or sets the name or identifier of the Azure Key Vault certificate containing the Firebase service account credentials.
        /// This certificate is used to authenticate with Firebase App Check API for token verification operations.
        /// </summary>
        /// <remarks>
        /// The certificate should be in PKCS#12 format and contain the private key for the Firebase service account.
        /// This provides a secure alternative to storing service account credentials directly in configuration.
        /// </remarks>
        public string ServiceAccountKeyVaultCertificate { get; set; } = default!;
    }
}