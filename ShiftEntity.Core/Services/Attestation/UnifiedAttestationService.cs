using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.ComponentModel;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Core.Services.Attestation
{
    public class UnifiedAttestationService : IUnifiedAttestationService
    {
        private readonly FirebaseAppCheckService firebaseAppCheckService;
        private readonly HMSUserDetectService hmsUserDetectService;

        public UnifiedAttestationService(FirebaseAppCheckService firebaseAppCheckService, HMSUserDetectService hmsUserDetectService)
        {
            this.firebaseAppCheckService = firebaseAppCheckService;
            this.hmsUserDetectService = hmsUserDetectService;
        }

        /// <summary>
        /// Routes the attestation token to the appropriate verification service based on the client's platform.
        /// </summary>
        /// <param name="token">The attestation token provided by the client request mainly Firebase App Check Token for iOS and Android, HMS UserDetect Response Token for HMS/Huawei.</param>
        /// <param name="platform">The OS or attestation provider the token originated from.</param>
        /// <param name="withReplayProtection">
        /// If true, performs a strict, one-time-use network verification for Firebase App Check Token only to prevent replay attacks. 
        /// Defaults to false (which uses fast, offline JWT validation where supported).
        /// </param>
        /// <returns>True if the token is valid, trusted, otherwise, false.</returns>
        public async ValueTask<bool> VerifyTokenAsync(string token, AttestationPlatform platform, bool? withReplayProtection = false)
        {
            if (platform is (AttestationPlatform.Android or AttestationPlatform.iOS))
            {
                if (withReplayProtection is true)
                    return await firebaseAppCheckService.VerifyTokenWithReplayProtectionAsync(token);

                return await firebaseAppCheckService.VerifyTokenAsync(token);
            }
            else if (platform is AttestationPlatform.Huawei) { 
                return await hmsUserDetectService.VerifyTokenAsync(token);
            }
            return false;
        }
    }
    public class AttestationOptions
    {
        public string HeaderKey { get; set; } = "Verification-Token";
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum AttestationPlatform
    {
        [Description("iOS OS")]
        iOS = 1,
        [Description("Normal Android")]
        Android = 2,
        [Description("HMS Android")]
        Huawei = 3,
    }
}
